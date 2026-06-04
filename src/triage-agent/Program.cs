using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TriageAgent;

#pragma warning disable MEAI001, OPENAI001

// Early-startup diagnostics — written to stdout BEFORE App Insights is wired so
// we can see boot failures even if telemetry export hasn't initialized.
Console.WriteLine("[startup] triage-agent booting...");
foreach (var name in new[]
{
    "AZURE_AI_PROJECT_ENDPOINT",
    "FOUNDRY_PROJECT_ENDPOINT",
    "TRIAGE_MODEL_DEPLOYMENT",
    "GITHUB_PAT_CONNECTION_NAME",
    "APPLICATIONINSIGHTS_CONNECTION_STRING",
    "HOME",
    // Identity-related — used to choose the correct Azure.Identity credential
    // path below. Logged at startup so we can verify, post-deploy, that we're
    // on the WorkloadIdentity path Foundry Hosted Agents expect and not
    // silently falling back to IMDS.
    "AZURE_CLIENT_ID",
    "AZURE_TENANT_ID",
    "AZURE_FEDERATED_TOKEN_FILE",
    "AZURE_TOKEN_CREDENTIALS",
})
{
    var v = Environment.GetEnvironmentVariable(name);
    Console.WriteLine($"[startup]   env {name}={(string.IsNullOrEmpty(v) ? "(not set)" : v.Length > 80 ? v[..60] + "..." : v)}");
}

try
{
    var builder = AgentHost.CreateBuilder(args);

    var projectEndpoint =
        Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")
        ?? Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
        ?? throw new InvalidOperationException(
            "Neither AZURE_AI_PROJECT_ENDPOINT nor FOUNDRY_PROJECT_ENDPOINT is set.");
    var modelDeployment = Environment.GetEnvironmentVariable("TRIAGE_MODEL_DEPLOYMENT")
        ?? "gpt-5-mini";
    // Foundry connection holding a fallback PAT (key="Authorization",
    // value="Bearer <pat>"). Used only when no per-request App token is
    // present (e.g. direct CLI invocation without the workflow). Production
    // runs through the workflow which mints a fresh GitHub App installation
    // token per call, so this connection is OPTIONAL — if absent, startup
    // succeeds with a null fallback and direct CLI invocations will simply
    // fail with "no GitHub token in scope" instead of a startup crash.
    var patConnectionName = Environment.GetEnvironmentVariable("GITHUB_PAT_CONNECTION_NAME")
        ?? Environment.GetEnvironmentVariable("GITHUB_MCP_CONNECTION_NAME") // legacy name
        ?? "github-pat-connection";

    Console.WriteLine($"[startup] Using project endpoint: {projectEndpoint}");
    Console.WriteLine($"[startup] Using model deployment: {modelDeployment}");
    Console.WriteLine($"[startup] PAT fallback connection (optional): {patConnectionName}");

    // Credential selection.
    //
    // Foundry Hosted Agents authenticate via **Workload Identity Federation**
    // (the AKS pattern): the runtime injects AZURE_TENANT_ID + AZURE_CLIENT_ID +
    // AZURE_FEDERATED_TOKEN_FILE, and Azure.Identity's WorkloadIdentityCredential
    // reads the projected token file and exchanges it with Entra ID directly —
    // no IMDS round-trip required. This is what the official sample
    // (microsoft-foundry/foundry-samples/.../hello-world/Program.cs) relies on
    // when it does `new AIProjectClient(endpoint, new DefaultAzureCredential())`.
    //
    // PR #67 in this repo previously did `new ManagedIdentityCredential(AZURE_CLIENT_ID)`
    // because we treated AZURE_CLIENT_ID as a signal to use IMDS-only MI. That
    // was wrong: in Workload Identity environments AZURE_CLIENT_ID is set for
    // the WorkloadIdentityCredential constructor, NOT to flag MI. Forcing the
    // IMDS path meant every cold-start request lived or died by whether IMDS
    // happened to be reachable from the Foundry container at that moment, which
    // produced the wall of "ManagedIdentityCredential authentication failed:
    // No response received from the managed identity endpoint" errors we saw.
    //
    // Strategy now (matches the official sample, with one production hardening):
    //   - In Foundry (detected by AZURE_FEDERATED_TOKEN_FILE being set):
    //     DefaultAzureCredential with ExcludeManagedIdentityCredential=true.
    //     This locks the chain to Environment → WorkloadIdentity → (developer
    //     creds, which never resolve in-container). No silent fallback to IMDS,
    //     so if Foundry's federated token wiring ever breaks we fail loud
    //     instead of accidentally trying a path that doesn't apply.
    //   - Outside Foundry (no federated token file, e.g. local `dotnet run`
    //     against `az login`): plain DefaultAzureCredential — full chain
    //     including AzureCliCredential / VisualStudioCredential / etc.
    var federatedTokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");
    var inFoundryWorkloadIdentity = !string.IsNullOrWhiteSpace(federatedTokenFile);
    Azure.Core.TokenCredential credential;
    if (inFoundryWorkloadIdentity)
    {
        Console.WriteLine(
            $"[startup] Workload Identity detected (AZURE_FEDERATED_TOKEN_FILE set). " +
            $"Using DefaultAzureCredential with ManagedIdentityCredential excluded — " +
            $"forces the WorkloadIdentityCredential path, no IMDS fallback.");
        credential = new Azure.Identity.DefaultAzureCredential(
            new Azure.Identity.DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = true,
            });
    }
    else
    {
        Console.WriteLine(
            "[startup] No AZURE_FEDERATED_TOKEN_FILE — using full DefaultAzureCredential " +
            "chain (local dev: az login / VS / etc.).");
        credential = new Azure.Identity.DefaultAzureCredential();
    }
    var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

    // Block startup until the credential actually works. With Workload Identity
    // the token exchange is usually instant (read a file, POST it to Entra),
    // but if the federated token file is missing, unreadable, or stale, or if
    // the trust between the agent identity and the project MI has drifted,
    // we'd rather fail loud right here than serve 502s. The retry budget covers
    // the rare case where the projected token volume isn't fully mounted yet
    // at process start. On final failure we throw, the .NET process exits,
    // and Foundry restarts the container on a clean slate.
    WarmUpCredential(credential);

    static void WarmUpCredential(Azure.Core.TokenCredential credential)
    {
        // Cognitive Services scope — covers all Foundry / Azure OpenAI calls
        // the agent makes downstream. Any working scope proves the credential
        // chain can issue tokens for the resources we actually call.
        var ctx = new Azure.Core.TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" });
        var backoffs = new[] { 2, 5, 10, 20, 30 }; // 5 retries, ~67s budget
        Exception? lastError = null;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt <= backoffs.Length; attempt++)
        {
            try
            {
                var attemptSw = System.Diagnostics.Stopwatch.StartNew();
                var token = credential.GetToken(ctx, default);
                Console.WriteLine(
                    $"[startup] Credential warm-up OK on attempt {attempt + 1} " +
                    $"in {attemptSw.ElapsedMilliseconds}ms (token expires {token.ExpiresOn:O}, " +
                    $"total wall {totalSw.ElapsedMilliseconds}ms).");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                var firstLine = ex.Message.Split('\n', 2)[0];
                if (attempt < backoffs.Length)
                {
                    var delaySec = backoffs[attempt];
                    Console.WriteLine(
                        $"[startup] Credential warm-up attempt {attempt + 1} failed " +
                        $"({ex.GetType().Name}: {firstLine}) — retrying in {delaySec}s.");
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(delaySec));
                }
            }
        }
        Console.WriteLine(
            $"[startup] Credential warm-up FAILED after {backoffs.Length + 1} attempts " +
            $"({totalSw.ElapsedMilliseconds}ms total). Exiting so Foundry restarts the container.");
        throw new InvalidOperationException(
            "Credential warm-up failed; refusing to start agent to avoid serving 502s.",
            lastError);
    }

    string? fallbackToken = null;
    try
    {
        Console.WriteLine($"[startup] Attempting to read fallback PAT from connection '{patConnectionName}'...");
        var connection = projectClient.Connections
            .GetConnectionAsync(patConnectionName, includeCredentials: true)
            .GetAwaiter().GetResult();

        if (connection.Value.Credentials is AIProjectConnectionCustomCredential customCreds)
        {
            Console.WriteLine($"[startup] Connection has {customCreds.Keys.Count} key(s): [{string.Join(", ", customCreds.Keys.Keys)}]");
            string? authHeader = null;
            foreach (var kv in customCreds.Keys)
            {
                if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authHeader = kv.Value;
                    break;
                }
            }
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                fallbackToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader["Bearer ".Length..].Trim()
                    : authHeader.Trim();
                Console.WriteLine($"[startup] Fallback PAT loaded ({fallbackToken.Length} chars).");
            }
            else
            {
                Console.WriteLine($"[startup] Connection has no 'Authorization' key — running without PAT fallback.");
            }
        }
        else
        {
            var actualType = connection.Value.Credentials?.GetType().FullName ?? "(null)";
            Console.WriteLine($"[startup] Connection credentials type is '{actualType}' (expected CustomKeys) — running without PAT fallback.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[startup] PAT connection unavailable ({ex.GetType().Name}: {ex.Message}) — running without PAT fallback. Direct CLI invocations will fail; workflow path is unaffected.");
    }

    // GitHub identity strategy:
    //   - Preferred path (production): workflow mints a GitHub App installation
    //     token per run and sends it in the JSON body field `github_token`.
    //     TriageInvocationHandler pushes it onto GitHubTokenProvider's AsyncLocal.
    //     GitHubRestTools.SendAsync reads it for every outbound GitHub REST call,
    //     so the agent posts/labels as `<app>[bot]` with its own identity.
    //   - Fallback path (CLI / local testing, OPTIONAL): the PAT from the
    //     Foundry connection above. Skipped entirely if the connection isn't
    //     configured.
    var tokenProvider = new GitHubTokenProvider(fallbackToken);

    // GitHub write tools, called via REST (api.github.com) directly. See
    // GitHubRestTools.cs for the full rationale (why not the Copilot MCP
    // server: session-id staleness + the get_me/403 trap for App tokens).
    var restTools = new GitHubRestTools(
        tokenProvider,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubRestTools>.Instance);

    var triageTools = restTools.TriageTools();
    var followupTools = restTools.FollowupTools();

    Console.WriteLine($"[startup] Triage tools ({triageTools.Count}): {string.Join(", ", triageTools.Select(t => t.Name))}");
    Console.WriteLine($"[startup] Follow-up tools ({followupTools.Count}): {string.Join(", ", followupTools.Select(t => t.Name))}");

    ChatClientAgent triageAgent = projectClient.AsAIAgent(
        model: modelDeployment,
        instructions: TriageInvocationHandler.TriageSystemInstruction,
        name: "triage-agent",
        tools: triageTools);

    ChatClientAgent followupAgent = projectClient.AsAIAgent(
        model: modelDeployment,
        instructions: TriageInvocationHandler.FollowupSystemInstruction,
        name: "triage-agent-followup",
        tools: followupTools);

    // Wrap both agents with OpenTelemetry so RunAsync emits invoke_agent spans
    // and the inner MEAI chat client emits gen_ai.chat + execute_tool spans.
    // This is required on the IAgentInvocationHandler path — unlike
    // AddFoundryResponses, this path does NOT auto-wrap. The source name here
    // must match an AddSource("Experimental.Microsoft.Agents.AI") below.
    // EnableSensitiveData stays false (default) — token counts + latency are
    // exported, raw prompts / completions / tool args are NOT.
    AIAgent triageAgentOtel = triageAgent.AsBuilder()
        .UseOpenTelemetry(sourceName: "Experimental.Microsoft.Agents.AI")
        .Build();
    AIAgent followupAgentOtel = followupAgent.AsBuilder()
        .UseOpenTelemetry(sourceName: "Experimental.Microsoft.Agents.AI")
        .Build();

    Console.WriteLine("[startup] Both agents constructed and OpenTelemetry-wrapped.");

    builder.Services.AddSingleton(tokenProvider);
    builder.Services.AddSingleton(restTools);
    builder.Services.AddSingleton(new AgentBundle(triageAgentOtel, followupAgentOtel));
    builder.Services.AddInvocationsServer();
    builder.Services.AddScoped<InvocationHandler, TriageInvocationHandler>();

    // ── OpenTelemetry source registration ─────────────────────────────────
    // Foundry's AgentHostBuilder already wires the Azure Monitor exporter
    // internally (it calls UseAzureMonitor in Build()). We layer source
    // registrations onto the SAME OpenTelemetryBuilder so our spans flow
    // to the same App Insights resource — do NOT call AddAzureMonitorTraceExporter
    // here, that would create a second exporter and double-publish.
    //
    // Sources MUST match what the runtime actually emits on (verified against
    // microsoft/agent-framework + MEAI source). On the IAgentInvocationHandler
    // path:
    //   - "Experimental.Microsoft.Agents.AI"      → invoke_agent spans
    //     (emitted only because we explicitly wrap each agent with
    //     UseOpenTelemetry above — AddFoundryResponses would auto-wrap, the
    //     invocations path does not).
    //   - "Experimental.Microsoft.Extensions.AI"  → per-LLM-call gen_ai.chat
    //     spans + execute_tool <name> spans (model, prompt/completion
    //     tokens, latency; raw text excluded — EnableSensitiveData=false).
    //   - "Azure.AI.AgentServer.Invocations"      → baggage propagation from
    //     the invocations endpoint handler (no span itself in beta.4).
    //   - "TriageAgent.Tools"                     → explicit tool spans we
    //     emit inside GitHubRestTools as a belt-and-braces guarantee that
    //     the demo always shows the REST calls in the trace tree.
    //
    // We do NOT call AddAspNetCoreInstrumentation() or AddHttpClientInstrumentation()
    // ourselves — Foundry's UseAzureMonitor already registers both at the exact
    // package versions its runtime bound to. Pulling in our own
    // OpenTelemetry.Instrumentation.* packages caused FileNotFoundException at
    // AgentHostBuilder.Build() and the container never reached readiness.
    Console.WriteLine("[startup] Layering OpenTelemetry sources onto Foundry's TracerProvider.");
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("triage-agent", serviceVersion: "1.0"))
        .WithTracing(t => t
            .AddSource("Experimental.Microsoft.Agents.AI")
            .AddSource("Experimental.Microsoft.Extensions.AI")
            .AddSource("Azure.AI.AgentServer.Invocations")
            .AddSource(GitHubRestTools.ActivitySourceName));

    builder.RegisterProtocol("invocations", endpoints => endpoints.MapInvocationsServer());

    Console.WriteLine("[startup] Calling builder.Build()...");
    var app = builder.Build();
    Console.WriteLine("[startup] App built. Calling app.Run()...");
    app.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Startup failed: {ex.GetType().FullName}: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    throw;
}

#pragma warning restore MEAI001, OPENAI001

namespace TriageAgent
{
    /// <summary>Holds both agent instances so the handler can pick one per event.</summary>
    public sealed record AgentBundle(AIAgent Triage, AIAgent Followup);
}
