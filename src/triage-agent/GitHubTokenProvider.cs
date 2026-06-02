using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// Per-request GitHub token provider. The invocation handler pushes the
/// per-call installation token (minted by the GitHub App in the workflow);
/// the MCP HTTP client's <see cref="DynamicAuthHandler"/> reads it and stamps
/// it on outbound MCP requests. Falls back to the static PAT from the Foundry
/// connection if no per-request token is set (direct CLI testing path).
/// </summary>
public sealed class GitHubTokenProvider
{
    private static readonly AsyncLocal<string?> _current = new();
    private readonly string? _fallback;

    /// <param name="fallback">Static fallback token (raw, no "Bearer " prefix).</param>
    public GitHubTokenProvider(string? fallback) => _fallback = fallback;

    /// <summary>Resolve the current token: per-request push wins, then fallback.</summary>
    public string? GetToken() => _current.Value ?? _fallback;

    /// <summary>Push a per-request token. Dispose to restore the prior value.</summary>
    public IDisposable PushToken(string? token)
    {
        var previous = _current.Value;
        _current.Value = token;
        return new TokenScope(() => _current.Value = previous);
    }

    private sealed class TokenScope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>
/// Stamps <c>Authorization: Bearer &lt;token&gt;</c> onto every outbound HTTP
/// request using the current value from <see cref="GitHubTokenProvider"/>.
/// </summary>
public sealed class DynamicAuthHandler(
    GitHubTokenProvider provider,
    ILogger<DynamicAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = provider.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization =
                token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? AuthenticationHeaderValue.Parse(token)
                    : new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            logger.LogWarning("No GitHub token available for outbound MCP request to {Url}", request.RequestUri);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = "";
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 800) body = body[..800] + "...[truncated]";
            }
            catch { /* best-effort */ }
            var tokenKind = string.IsNullOrEmpty(token)
                ? "none"
                : token.StartsWith("ghs_", StringComparison.Ordinal) ? "app-installation(ghs_)"
                : token.StartsWith("ghp_", StringComparison.Ordinal) ? "pat(ghp_)"
                : token.StartsWith("github_pat_", StringComparison.Ordinal) ? "fine-grained-pat"
                : "unknown-prefix";
            Console.WriteLine($"[MCP-ERR] {(int)response.StatusCode} {response.ReasonPhrase} {request.Method} {request.RequestUri} tokenKind={tokenKind} body={body}");
        }
        return response;
    }
}
