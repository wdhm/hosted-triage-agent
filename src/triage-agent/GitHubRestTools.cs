using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// REST-based GitHub tools exposed as <see cref="AITool"/>s for the triage
/// agent. These replace the GitHub MCP server for write operations.
///
/// Why not MCP for writes:
///   1. The hosted MCP server at api.githubcopilot.com/mcp/ returns
///      <c>400 Bad Request: invalid session</c> intermittently when a
///      hosted-agent invocation reuses a session-id across calls — the
///      MCP HTTP transport's session handshake doesn't survive Foundry's
///      opaque process recycling.
///   2. The default /mcp/ endpoint includes a "context" toolset whose
///      system instruction tells the LLM to "Always call get_me first".
///      <c>get_me</c> hits <c>GET /user</c>, which returns 403 for GitHub
///      App installation tokens (that endpoint requires user-scoped auth).
///
/// Calling the REST API directly avoids both problems and still attributes
/// the comment to "&lt;app-slug&gt;[bot]" because GitHub uses the App
/// identity from the <c>ghs_</c> installation token.
/// </summary>
public sealed class GitHubRestTools
{
    /// <summary>
    /// Explicit ActivitySource for GitHub-tool spans. We start one span per
    /// tool call here as a belt-and-braces guarantee: even if the MEAI
    /// function-invocation telemetry path isn't active (e.g. the agent wrap
    /// is misconfigured), the demo trace tree always shows our REST calls.
    /// Registered with the OTel TracerProvider in Program.cs via
    /// <c>AddSource(GitHubRestTools.ActivitySourceName)</c>.
    /// </summary>
    public const string ActivitySourceName = "TriageAgent.Tools";
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    /// <summary>
    /// Hidden HTML-comment marker used to embed the Foundry conversation
    /// pointer (a tiny base64 blob, ~80 bytes) directly into a bot comment.
    /// Wave 2 ("zero-local-state") uses the GitHub issue itself as the
    /// durable store for the pointer — no container-local file, no Azure
    /// Files mount, no Redis. The next turn scans bot comments newest-first
    /// for this marker and resumes the conversation; if no marker is found
    /// it starts fresh. The full conversation history still lives server-side
    /// on the Foundry Responses API; only the ~24-byte pointer rides on GitHub.
    /// </summary>
    public const string FoundrySessionMarkerPrefix = "<!-- foundry-session:";
    public const string FoundrySessionMarkerSuffix = " -->";

    /// <summary>
    /// Hidden HTML-comment marker the LLM emits at the end of every reply
    /// (see TriageInvocationHandler system prompts). Used by the workflow
    /// for loop prevention AND by the issue.opened idempotency check (any
    /// bot comment carrying this marker means the issue has been triaged).
    /// </summary>
    public const string TriageReplyMarker = "<!-- triage-agent-reply -->";

    // Per-invocation success counters. AsyncLocal flows through agent.RunAsync
    // and the tool callbacks the framework triggers, so the handler can read
    // these post-run to verify the model actually completed its work — instead
    // of trusting the model's self-reported "OK: ..." text (which it can lie
    // about or hallucinate).
    private static readonly AsyncLocal<Counters?> _counters = new();

    // Per-invocation trace ID captured by the handler before RunAsync. We
    // intentionally don't read Activity.Current inside the tool because the
    // ambient activity there could be any inner span (chat, execute_tool, ...).
    // Capturing once at the handler entry guarantees the comment footer holds
    // the canonical end-to-end W3C TraceId for the whole run.
    private static readonly AsyncLocal<string?> _traceId = new();

    // Per-invocation ID of the LAST comment AddIssueCommentAsync successfully
    // posted on this turn. Wave 2 ("zero-local-state") reads this after
    // RunAsync to PATCH the comment with the Foundry conversation pointer,
    // so the GitHub issue itself becomes the durable store for conversation
    // continuity — no container-local file or volume needed.
    private static readonly AsyncLocal<long?> _lastCommentId = new();

    private readonly HttpClient _http;
    private readonly GitHubTokenProvider _tokens;
    private readonly ILogger<GitHubRestTools> _logger;

    public GitHubRestTools(GitHubTokenProvider tokens, ILogger<GitHubRestTools> logger)
    {
        _tokens = tokens;
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wdhm-triage-agent/1.0");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Reset per-invocation counters at the start of a handler call.</summary>
    public IDisposable BeginInvocation()
    {
        var prev = _counters.Value;
        var prevTrace = _traceId.Value;
        var prevComment = _lastCommentId.Value;
        _counters.Value = new Counters();
        _lastCommentId.Value = null;

        // Capture the canonical end-to-end TraceId once, here. AspNetCore
        // instrumentation has already started the inbound request span by the
        // time the handler runs, so Activity.Current is the request root —
        // exactly the right TraceId for our App Insights deep-link footer.
        var current = Activity.Current?.TraceId.ToString();
        _traceId.Value = IsValidTraceId(current) ? current : null;

        return new Restore(() =>
        {
            _counters.Value = prev;
            _traceId.Value = prevTrace;
            _lastCommentId.Value = prevComment;
        });
    }

    /// <summary>Snapshot of tool-call outcomes for the current invocation.</summary>
    public Counters Snapshot() => _counters.Value ?? new Counters();

    /// <summary>Current invocation's canonical W3C trace ID (null if telemetry isn't initialized).</summary>
    public string? CurrentTraceId() => _traceId.Value;

    /// <summary>
    /// GitHub comment ID of the last comment <see cref="AddIssueCommentAsync"/>
    /// posted on this turn. Used by the handler to PATCH the comment with the
    /// Foundry conversation pointer after RunAsync succeeds.
    /// </summary>
    public long? LastPostedCommentId => _lastCommentId.Value;

    private static bool IsValidTraceId(string? t) =>
        !string.IsNullOrEmpty(t) && t != "00000000000000000000000000000000";

    /// <summary>Tools exposed on the issue.opened path (comment + relabel).</summary>
    public List<AITool> TriageTools() =>
    [
        AIFunctionFactory.Create(AddIssueCommentAsync,
            name: "add_issue_comment",
            description: "Post a markdown comment on a GitHub issue. Returns the API response JSON (id, html_url, user.login)."),
        AIFunctionFactory.Create(SetIssueLabelsAsync,
            name: "set_issue_labels",
            description: "Replace the entire label set on a GitHub issue with the given list. Any label that does not yet exist in the repo is created automatically with a default color. Returns the API response JSON."),
    ];

    /// <summary>Tools exposed on the issue_comment.created path (comment only).</summary>
    public List<AITool> FollowupTools() =>
    [
        AIFunctionFactory.Create(AddIssueCommentAsync,
            name: "add_issue_comment",
            description: "Post a markdown comment on a GitHub issue. Returns the API response JSON."),
    ];

    [Description("Post a markdown comment on a GitHub issue.")]
    public async Task<string> AddIssueCommentAsync(
        [Description("Repository owner (org or user login).")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int issue_number,
        [Description("Comment body (GitHub-flavored markdown).")] string body,
        CancellationToken cancellationToken = default)
    {
        using var activity = s_activitySource.StartActivity("add_issue_comment", ActivityKind.Client);
        activity?.SetTag("github.owner", owner);
        activity?.SetTag("github.repo", repo);
        activity?.SetTag("github.issue_number", issue_number);
        activity?.SetTag("github.comment.body_length", body?.Length ?? 0);

        // Append a demo-relevant footer with the App Insights operation ID
        // (== the W3C trace ID). Pasted into the App Insights Logs blade as
        // `operation_Id == "..."` it shows the full span tree (request →
        // invoke_agent → gen_ai.chat → execute_tool → outbound HTTP) with
        // model + prompt/completion tokens + latency. The trace ID is captured
        // ONCE at handler entry (not read from Activity.Current here) so it's
        // the canonical end-to-end ID, not whichever inner span happens to be
        // current when the tool fires.
        var traceId = _traceId.Value;
        var bodyWithFooter = string.IsNullOrEmpty(traceId)
            ? body
            : body + "\n\n---\n" +
              $"<sub>🔎 <b>Foundry trace</b>: <code>{traceId}</code> — open App Insights Logs and run " +
              $"<code>union requests, dependencies, traces, exceptions | where operation_Id == \"{traceId}\"</code></sub>" +
              $"\n<!-- foundry-trace:{traceId} -->";

        var payload = JsonSerializer.Serialize(new { body = bodyWithFooter });
        using var req = NewRequest(HttpMethod.Post,
            $"repos/{owner}/{repo}/issues/{issue_number}/comments", payload);
        var result = await SendAsync(req, $"add_issue_comment {owner}/{repo}#{issue_number}", cancellationToken);
        activity?.SetTag("http.success", result.Success);
        if (result.Success)
        {
            (_counters.Value ??= new Counters()).CommentsPosted++;
            // Stash the just-created comment ID so the handler can PATCH it
            // with the Foundry session marker after RunAsync returns.
            _lastCommentId.Value = TryReadCommentId(result.Body);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        return result.Body;
    }

    /// <summary>
    /// Best-effort parse of a comment ID from a successful POST /comments
    /// response. The response is the GitHub comment object, which always
    /// includes an integer "id" at the top level. We silently return null
    /// on parse failure — the marker PATCH just gets skipped and the next
    /// turn starts a fresh conversation (no worse than today's behavior).
    /// </summary>
    private static long? TryReadCommentId(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            Console.WriteLine("[REST-DBG] TryReadCommentId: body is null/empty");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"[REST-DBG] TryReadCommentId: root is {doc.RootElement.ValueKind}, head={Trunc(responseBody, 200)}");
                return null;
            }
            if (!doc.RootElement.TryGetProperty("id", out var idEl))
            {
                var keys = string.Join(",", doc.RootElement.EnumerateObject().Take(15).Select(p => p.Name));
                Console.WriteLine($"[REST-DBG] TryReadCommentId: no 'id' field; keys=[{keys}] bodyLen={responseBody.Length}");
                return null;
            }
            if (idEl.ValueKind != JsonValueKind.Number)
            {
                Console.WriteLine($"[REST-DBG] TryReadCommentId: 'id' is {idEl.ValueKind} (raw={Trunc(idEl.GetRawText(), 100)})");
                return null;
            }
            if (!idEl.TryGetInt64(out var id))
            {
                Console.WriteLine($"[REST-DBG] TryReadCommentId: id not Int64 (raw={idEl.GetRawText()})");
                return null;
            }
            return id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[REST-DBG] TryReadCommentId: parse threw {ex.GetType().Name}: {ex.Message}; head={Trunc(responseBody, 200)}");
            return null;
        }
    }

    [Description("Replace the labels on a GitHub issue with the provided list. Missing labels are auto-created.")]
    public async Task<string> SetIssueLabelsAsync(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int issue_number,
        [Description("Final label set. Any label not yet defined in the repo is created on the fly with a default color.")] string[] labels,
        CancellationToken cancellationToken = default)
    {
        using var activity = s_activitySource.StartActivity("set_issue_labels", ActivityKind.Client);
        activity?.SetTag("github.owner", owner);
        activity?.SetTag("github.repo", repo);
        activity?.SetTag("github.issue_number", issue_number);
        activity?.SetTag("github.labels.count", labels?.Length ?? 0);

        // Pre-create any missing labels so PUT /labels doesn't 422 on a name
        // the repo doesn't know yet. 422 from POST /labels = already exists,
        // which is fine.
        foreach (var label in labels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await EnsureLabelExistsAsync(owner, repo, label, cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new { labels });
        using var req = NewRequest(HttpMethod.Put,
            $"repos/{owner}/{repo}/issues/{issue_number}/labels", payload);
        var result = await SendAsync(req, $"set_issue_labels {owner}/{repo}#{issue_number}", cancellationToken);
        activity?.SetTag("http.success", result.Success);
        if (result.Success)
        {
            (_counters.Value ??= new Counters()).LabelsSet++;
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        return result.Body;
    }

    // ── Wave 2: GitHub-as-state-store helpers ─────────────────────────────
    // These are NOT exposed as AITools — the handler calls them directly to
    // load and persist the Foundry conversation pointer via hidden HTML
    // comments on the issue. Keeping them in this class so all GitHub REST
    // I/O (auth header, base URL, error handling) goes through one path.

    /// <summary>
    /// Lists comments on an issue (oldest first per the GitHub API contract).
    /// Pages through up to <paramref name="maxPages"/>×100 comments — for a
    /// triage agent that comments at most once per turn this is plenty.
    /// </summary>
    public async Task<IReadOnlyList<IssueCommentSummary>> ListIssueCommentsAsync(
        string owner, string repo, int issueNumber,
        int maxPages = 5, CancellationToken cancellationToken = default)
    {
        var results = new List<IssueCommentSummary>();
        for (var page = 1; page <= maxPages; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"repos/{owner}/{repo}/issues/{issueNumber}/comments?per_page=100&page={page}");
            var token = _tokens.GetToken();
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine($"[REST-ERR] list_comments {owner}/{repo}#{issueNumber}: no token");
                return results;
            }
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));

            using var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine(
                    $"[REST-ERR] list_comments {owner}/{repo}#{issueNumber} page={page}: HTTP {(int)resp.StatusCode} {Trunc(body, 400)}");
                return results;
            }

            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) break;
            var pageCount = 0;
            foreach (var c in arr.EnumerateArray())
            {
                pageCount++;
                var id = c.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0L;
                var author = c.TryGetProperty("user", out var userEl)
                    && userEl.TryGetProperty("login", out var loginEl)
                        ? loginEl.GetString() ?? "" : "";
                var cbody = c.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                results.Add(new IssueCommentSummary(id, author, cbody));
            }
            if (pageCount < 100) break; // last page
        }
        return results;
    }

    /// <summary>
    /// Scans BOT-AUTHORED comments newest-first and yields each base64-decoded
    /// foundry-session payload. The handler iterates these and tries each in
    /// turn — a single damaged or stale marker won't wedge the conversation,
    /// it just falls through to the next-older one.
    ///
    /// Author filter: we only trust comments authored by a GitHub App (login
    /// ends with <c>[bot]</c>). The <c>[bot]</c> suffix is reserved by GitHub
    /// for app installations; users cannot register accounts ending in
    /// <c>[bot]</c>. Without this filter a user could inject a hand-crafted
    /// <c>&lt;!-- foundry-session:...--&gt;</c> in their own comment and hijack
    /// the conversation pointer.
    /// </summary>
    public IEnumerable<string> EnumerateSessionJsonCandidates(IReadOnlyList<IssueCommentSummary> comments)
    {
        for (var i = comments.Count - 1; i >= 0; i--)
        {
            var c = comments[i];
            if (!c.Author.EndsWith("[bot]", StringComparison.Ordinal)) continue;
            var b = c.Body;
            var start = b.IndexOf(FoundrySessionMarkerPrefix, StringComparison.Ordinal);
            if (start < 0) continue;
            var payloadStart = start + FoundrySessionMarkerPrefix.Length;
            var end = b.IndexOf(FoundrySessionMarkerSuffix, payloadStart, StringComparison.Ordinal);
            if (end < 0) continue;
            var b64 = b[payloadStart..end].Trim();
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { continue; }
            yield return decoded;
        }
    }

    /// <summary>True if any BOT-AUTHORED comment carries the triage-reply marker.</summary>
    public bool HasTriageReplyMarker(IReadOnlyList<IssueCommentSummary> comments)
    {
        foreach (var c in comments)
        {
            if (!c.Author.EndsWith("[bot]", StringComparison.Ordinal)) continue;
            if (c.Body.Contains(TriageReplyMarker, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the foundry-session marker line for a serialized AgentSession.
    /// Base64-wrapping the JSON keeps the marker resilient to schema changes
    /// (we don't have to know the inner field names) and prevents the payload
    /// from colliding with HTML-comment terminators.
    /// </summary>
    public static string EncodeSessionMarker(string sessionJson)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionJson));
        return $"{FoundrySessionMarkerPrefix}{b64}{FoundrySessionMarkerSuffix}";
    }

    /// <summary>
    /// PATCHes an existing comment, appending <paramref name="suffix"/> to its
    /// body. The comments PATCH API replaces the body wholesale (no append
    /// primitive), so we GET first, then PATCH — one extra round-trip per
    /// turn, worth it for "no container-local state".
    /// </summary>
    public async Task<bool> AppendToCommentAsync(
        string owner, string repo, long commentId, string suffix,
        CancellationToken cancellationToken = default)
    {
        // Two attempts with a short delay — the marker PATCH is the single
        // point of failure for Wave 2 conversation continuity; a transient
        // 5xx or rate-limit blip here would silently break follow-up memory.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var ok = await TryAppendOnceAsync(owner, repo, commentId, suffix, attempt, cancellationToken);
            if (ok) return true;
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }
        }
        return false;
    }

    private async Task<bool> TryAppendOnceAsync(
        string owner, string repo, long commentId, string suffix, int attempt,
        CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("append_to_comment", ActivityKind.Client);
        activity?.SetTag("github.owner", owner);
        activity?.SetTag("github.repo", repo);
        activity?.SetTag("github.comment_id", commentId);
        activity?.SetTag("github.suffix.length", suffix.Length);
        activity?.SetTag("retry.attempt", attempt);

        var token = _tokens.GetToken();
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine($"[REST-ERR] append_comment {owner}/{repo}#{commentId}: no token");
            activity?.SetStatus(ActivityStatusCode.Error);
            return false;
        }

        using var getReq = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{owner}/{repo}/issues/comments/{commentId}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));
        using var getResp = await _http.SendAsync(getReq, cancellationToken);
        var getBody = await getResp.Content.ReadAsStringAsync(cancellationToken);
        if (!getResp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[REST-ERR] append_comment GET {owner}/{repo}#{commentId}: HTTP {(int)getResp.StatusCode} {Trunc(getBody, 400)}");
            activity?.SetStatus(ActivityStatusCode.Error);
            return false;
        }
        string currentBody;
        using (var doc = JsonDocument.Parse(getBody))
        {
            currentBody = doc.RootElement.TryGetProperty("body", out var bodyEl)
                ? bodyEl.GetString() ?? "" : "";
        }

        var newBody = currentBody + "\n" + suffix;
        var payload = JsonSerializer.Serialize(new { body = newBody });
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch,
            $"repos/{owner}/{repo}/issues/comments/{commentId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));
        using var patchResp = await _http.SendAsync(patchReq, cancellationToken);
        if (!patchResp.IsSuccessStatusCode)
        {
            var patchBody = await patchResp.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"[REST-ERR] append_comment PATCH {owner}/{repo}#{commentId}: HTTP {(int)patchResp.StatusCode} {Trunc(patchBody, 400)}");
            activity?.SetStatus(ActivityStatusCode.Error);
            return false;
        }
        Console.WriteLine($"[REST-OK] append_comment {owner}/{repo}#{commentId}: +{suffix.Length} chars");
        activity?.SetTag("http.success", true);
        return true;
    }

    /// <summary>Minimal comment record used for marker scanning.</summary>
    public sealed record IssueCommentSummary(long Id, string Author, string Body);

    private async Task EnsureLabelExistsAsync(
        string owner, string repo, string name, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { name, color = "bfd4f2" });
        using var req = NewRequest(HttpMethod.Post,
            $"repos/{owner}/{repo}/labels", payload);
        var token = _tokens.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));
        }
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.Created
            && resp.StatusCode != HttpStatusCode.UnprocessableEntity)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine(
                $"[REST-WARN] ensure_label {owner}/{repo} '{name}': HTTP {(int)resp.StatusCode} {Trunc(body, 300)}");
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, string jsonBody) => new(method, path)
    {
        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
    };

    private async Task<RestResult> SendAsync(
        HttpRequestMessage req, string label, CancellationToken cancellationToken)
    {
        var token = _tokens.GetToken();
        if (string.IsNullOrEmpty(token))
        {
            var msg = $"FAILED: {label} — no GitHub token in scope.";
            Console.WriteLine($"[REST-ERR] {msg}");
            return new RestResult(false, msg);
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine(
                $"[REST-ERR] {label}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {Trunc(body, 800)}");
            return new RestResult(false,
                $"FAILED: {label} returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Trunc(body, 800)}");
        }
        Console.WriteLine($"[REST-OK] {label}: HTTP {(int)resp.StatusCode}");
        // Return FULL body on success — Wave 2's TryReadCommentId parses it for
        // the comment ID, and GitHub's comment response routinely exceeds 1500
        // chars once our trace footer + triage template are included.
        return new RestResult(true, body);
    }

    private static string StripBearer(string token) =>
        token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim()
            : token.Trim();

    private static string Trunc(string s, int max) =>
        s.Length > max ? s[..max] + "...[truncated]" : s;

    private sealed record RestResult(bool Success, string Body);

    public sealed class Counters
    {
        public int CommentsPosted;
        public int LabelsSet;
    }

    private sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
