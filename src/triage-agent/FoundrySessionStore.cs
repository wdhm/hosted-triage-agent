using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace TriageAgent;

/// <summary>
/// Persists the **Foundry conversation pointer** for an issue. NOT a copy of
/// the conversation — the actual messages, system prompts, tool calls, and
/// model responses all live server-side on Foundry's Responses API and are
/// chained via <c>previous_response_id</c>. This file only contains the
/// ~24-byte <c>ConversationId</c> that points at the server-side conversation.
///
/// Why this design matters for the Hosted-Agents pitch:
///   • <b>Scale-to-zero survival</b>. The container can idle for 15 min and
///     get torn down. The actual conversation isn't here — it's on Foundry's
///     server. On cold-start, the pointer is read from <c>$HOME/sessions/</c>
///     (Foundry's persistent container HOME, kept for the session lifetime,
///     up to 30 days) and the next <c>RunAsync</c> resumes the thread with
///     a single hop to the Responses API.
///   • <b>Version-rollout survival</b>. <c>previous_response_id</c> is
///     project-scoped, not version-scoped — when we deploy v22 over v21, the
///     same pointer still resolves and the new agent version keeps talking
///     to the same conversation.
///   • <b>No external memory store</b>. No Redis, no Cosmos, no SQL. On
///     App Service / Container Apps we'd be wiring one of those plus
///     handling reconnects and TTLs ourselves.
///
/// Writes are atomic (write to <c>.tmp</c>, then rename) so a crash mid-write
/// can't corrupt the pointer JSON. Per-issue concurrency is enforced at the
/// workflow level via a GitHub Actions concurrency group; we don't add an
/// in-process lock here.
/// </summary>
public sealed class FoundrySessionStore(
    AIAgent agent,
    ILogger<FoundrySessionStore> logger)
{
    private static readonly string _dir = Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "sessions");

    /// <summary>
    /// Loads the session for <paramref name="sessionId"/> if present, otherwise
    /// creates a fresh one. Returns the session and a flag indicating whether it
    /// was newly created (true) or resumed from disk (false).
    /// </summary>
    public async Task<(AgentSession Session, bool IsNew)> GetOrCreateAsync(
        string sessionId, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(sessionId);

        if (File.Exists(path))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(path, ct);
                var element = JsonSerializer.Deserialize<JsonElement>(raw, JsonSerializerOptions.Web);
                var session = await agent.DeserializeSessionAsync(element, JsonSerializerOptions.Web, ct);
                logger.LogInformation("Resumed session {SessionId} from {Path}", sessionId, path);
                return (session, false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to deserialize session {SessionId} from {Path}; starting fresh",
                    sessionId, path);
            }
        }

        logger.LogInformation("Creating new session {SessionId}", sessionId);
        var fresh = await agent.CreateSessionAsync(ct);
        return (fresh, true);
    }

    /// <summary>
    /// Atomically persists the session JSON. Write goes to a temp file first,
    /// then File.Move replaces the target — so a crash mid-write leaves either
    /// the old file or the new file, never a partial one.
    /// </summary>
    public async Task SaveAsync(string sessionId, AgentSession session, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(sessionId);
        var tmp = path + ".tmp";

        var json = (await agent.SerializeSessionAsync(session, JsonSerializerOptions.Web, ct))
            .GetRawText();

        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
        logger.LogDebug("Saved session {SessionId} to {Path} ({Bytes} bytes)",
            sessionId, path, json.Length);
    }

    /// <summary>
    /// Returns whether a session file exists for the given ID without loading it.
    /// Used to make <c>issue.opened</c> idempotent: if a session already exists,
    /// the issue has already been triaged once and we should skip rather than
    /// destructively reset.
    /// </summary>
    public bool Exists(string sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// Renames a session file to <c>.quarantined-{timestamp}</c> so the next
    /// request starts fresh. Used when a resumed session causes RunAsync to
    /// fail (likely stale ConversationId after a redeploy or thread expiry).
    /// We rename rather than delete so the bad file is still inspectable.
    /// </summary>
    public void Quarantine(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return;
        var quarantinePath = path + $".quarantined-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, quarantinePath, overwrite: true);
    }

    private static string PathFor(string sessionId) =>
        Path.Combine(_dir, $"{SanitizeKey(sessionId)}.json");

    private static string SanitizeKey(string key) =>
        Regex.Replace(key, @"[^a-zA-Z0-9\-_]", "_");
}
