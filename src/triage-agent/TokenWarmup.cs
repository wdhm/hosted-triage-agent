using System.Diagnostics;
using Azure.Core;

namespace TriageAgent;

/// <summary>
/// Background credential pre-warm. Foundry hosted-agent containers commonly
/// take several minutes after first boot before their per-container managed
/// identity is provisioned in the IMDS sidecar (documented Azure behaviour
/// — see <see href="https://learn.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/how-to-troubleshoot-vm-token-request-issues"/>).
/// During that window <c>http://100.64.100.2/msi/token</c> returns HTTP 500 with
/// <c>identity_not_found</c>. Observed in App Insights for issue #89 on v41:
/// ~6 minutes of 500s before the first 200.
///
/// Without pre-warm, EVERY incoming /invocations triggers its own IMDS retry
/// storm (~30-50s of MSAL backoff per call), wasting Foundry's gateway retry
/// budget on the same root cause. We bake in:
/// <list type="bullet">
///   <item>One single retry loop per container, in the background, kicked off
///         at startup AFTER Kestrel has bound (so <c>/readiness</c> still
///         responds 200 quickly — a previous attempt to do this synchronously
///         caused <c>424 session_not_ready</c> because Foundry couldn't reach
///         the readiness endpoint at all).</item>
///   <item>An <c>await ReadyAsync(...)</c> the invocation handler calls before
///         the first LLM token-bearing call. If the warm-up has finished, this
///         completes instantly. If not, the handler blocks gracefully on the
///         shared TCS instead of spawning its own IMDS storm.</item>
/// </list>
/// </summary>
public sealed class TokenWarmup
{
    private static readonly string[] AoaiScope =
    {
        "https://cognitiveservices.azure.com/.default",
    };

    private readonly TokenCredential _credential;
    private readonly TaskCompletionSource<AccessToken> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TokenWarmup(TokenCredential credential)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    /// <summary>Kicks the background warm-up loop. Returns immediately.</summary>
    public void Start()
    {
        _ = Task.Run(WarmupLoopAsync);
    }

    /// <summary>
    /// Awaits until the credential has successfully fetched a token at least
    /// once, or until <paramref name="timeout"/> elapses. If the timeout fires
    /// we return without throwing — the caller can still try the credential
    /// itself; the timeout is just a defensive cap so a stuck warm-up never
    /// hangs an invocation indefinitely.
    /// </summary>
    public async Task ReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_ready.Task.IsCompleted) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await _ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Warm-up didn't finish before the per-invocation cap.
            // Returning lets the caller attempt the credential directly;
            // it'll either succeed (IMDS now up) or hit the same retry path.
        }
    }

    private async Task WarmupLoopAsync()
    {
        var ctx = new TokenRequestContext(AoaiScope);
        var sw = Stopwatch.StartNew();
        var attempt = 0;

        // Hard cap: ~20 minutes (200 attempts × max 6s). Foundry-MI provisioning
        // is typically <8 min; anything beyond that is a real failure and we
        // want it surfaced rather than silently retrying forever.
        const int maxAttempts = 200;

        while (attempt < maxAttempts)
        {
            attempt++;
            try
            {
                var token = await _credential.GetTokenAsync(ctx, CancellationToken.None)
                    .ConfigureAwait(false);
                Console.WriteLine(
                    $"[token-warmup] Acquired AOAI token after {attempt} attempt(s) " +
                    $"in {sw.Elapsed.TotalSeconds:F1}s — expires {token.ExpiresOn:O}.");
                _ready.TrySetResult(token);
                return;
            }
            catch (Exception ex)
            {
                // Exponential backoff capped at 6s. With max 200 attempts that
                // gives up after ~20 minutes of trying.
                var delaySeconds = Math.Min(6, 1 + attempt / 10);
                if (attempt == 1 || attempt % 10 == 0)
                {
                    Console.WriteLine(
                        $"[token-warmup] attempt #{attempt} after {sw.Elapsed.TotalSeconds:F0}s failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
            }
        }

        var msg = $"[token-warmup] FATAL: token never acquired after {maxAttempts} attempts ({sw.Elapsed.TotalMinutes:F1} min).";
        Console.WriteLine(msg);
        _ready.TrySetException(new InvalidOperationException(msg));
    }
}
