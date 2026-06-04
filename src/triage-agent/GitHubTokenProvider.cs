using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// Per-request GitHub token provider. The invocation handler pushes the
/// per-call installation token (minted by the GitHub App in the workflow)
/// onto the AsyncLocal; <see cref="GitHubRestTools"/> reads it via
/// <see cref="GetToken"/> and stamps it on every outbound REST call to
/// api.github.com. Production has exactly one source of truth — the App
/// installation token in the request body — so there is no static fallback.
/// </summary>
public sealed class GitHubTokenProvider
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>Resolve the current per-request token (null if none pushed).</summary>
    public string? GetToken() => _current.Value;

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

