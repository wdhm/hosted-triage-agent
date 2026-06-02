using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// Per-request GitHub token provider. The invocation handler pushes the
/// per-call installation token (minted by the GitHub App in the workflow);
/// <see cref="GitHubRestTools"/> reads it via <see cref="GetToken"/> and
/// stamps it on every outbound REST call to api.github.com. Falls back to
/// the static PAT from the Foundry connection if no per-request token is
/// set (direct CLI testing path).
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

