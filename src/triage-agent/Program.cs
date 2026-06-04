using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TriageAgent;

#pragma warning disable MEAI001, OPENAI001

// ── Process-wide Azure.Identity lock ─────────────────────────────────────
// Foundry Hosted Agents authenticate via Workload Identity Federation. The
// runtime injects AZURE_TENANT_ID + AZURE_CLIENT_ID + AZURE_FEDERATED_TOKEN_FILE
// for us. The trap: even if WE construct our project credential as
// `new WorkloadIdentityCredential()` (or DefaultAzureCredential with MI
// excluded), other SDK components in the process — telemetry exporters,
// transitive Azure clients — can still call `new DefaultAzureCredential()`
// with no options, which probes the full chain including ManagedIdentity →
// IMDS. IMDS is broken on Foundry containers (returns HTTP 500) so each
// probe burns ~22s of MSAL exponential backoff before falling through.
// We observed the followup-agent path doing this and turning every comment
// reply into a 3-minute timeout (six failed token attempts inside one
// invoke_agent span; Foundry sees 502, internally re-routes, eventually
// succeeds on retry #5).
//
// Fix: set AZURE_TOKEN_CREDENTIALS=WorkloadIdentityCredential BEFORE any
// Azure SDK initialisation. Azure.Identity ≥ 1.15.0 (we're on 1.21) honours
// this env var inside `DefaultAzureCredentialFactory.CreateCredentialChain()`
// and builds a one-element chain containing only WorkloadIdentityCredential
// — no IMDS probe, anywhere in the process, ever. Confirmed against
// Azure/azure-sdk-for-net source:
//   sdk/core/Azure.Core/src/Identity/DefaultAzureCredentialFactory.cs
//   sdk/core/Azure.Core/src/Identity/Constants.cs
// We only set it if not already set (so the operator can override via
// container env / azure.yaml) and only when AZURE_FEDERATED_TOKEN_FILE is
// present (i.e. we're actually in a Workload Identity environment, not on
// a local `dotnet run` against `az login`).
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"))
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS")))
{
    Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", "WorkloadIdentityCredential");
    Console.WriteLine(
        "[startup] AZURE_TOKEN_CREDENTIALS=WorkloadIdentityCredential set in-process " +
        "(locks every DefaultAzureCredential in the process to WIF, no IMDS probe).");
}

// Early-startup diagnostics — written to stdout BEFORE App Insights is wired so
// we can see boot failures even if telemetry export hasn't initialized.
Console.WriteLine("[startup] triage-agent booting...");
foreach (var name in new[]
{
    "AZURE_AI_PROJECT_ENDPOINT",
    "FOUNDRY_PROJECT_ENDPOINT",
    "TRIAGE_MODEL_DEPLOYMENT",
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

    Console.WriteLine($"[startup] Using project endpoint: {projectEndpoint}");
    Console.WriteLine($"[startup] Using model deployment: {modelDeployment}");

    // Credential selection.
    //
    // Foundry Hosted Agents authenticate via Workload Identity Federation
    // (the AKS pattern): the runtime injects AZURE_TENANT_ID +
    // AZURE_CLIENT_ID + AZURE_FEDERATED_TOKEN_FILE, and Azure.Identity's
    // WorkloadIdentityCredential reads the projected token file and
    // exchanges it directly with Entra ID — no IMDS round-trip required.
    //
    // We use `new WorkloadIdentityCredential()` explicitly (rather than
    // DefaultAzureCredential with MI excluded) because:
    //   1. It's a single credential, no chain, no probing, no startup
    //      latency from EnvironmentCredential probes.
    //   2. It cannot silently fall back to anything else if Foundry's WIF
    //      wiring breaks — we fail loud, which is what we want.
    //   3. Combined with AZURE_TOKEN_CREDENTIALS=WorkloadIdentityCredential
    //      set at the top of this file, ANY downstream
    //      `new DefaultAzureCredential()` in the process (telemetry
    //      exporters, transitive Azure clients) is also locked to WIF only.
    //
    // Outside Foundry (local `dotnet run` against `az login`), we drop to
    // plain DefaultAzureCredential so AzureCliCredential / VisualStudioCredential
    // / etc. can resolve.
    var federatedTokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");
    var inFoundryWorkloadIdentity = !string.IsNullOrWhiteSpace(federatedTokenFile);
    Azure.Core.TokenCredential credential;
    if (inFoundryWorkloadIdentity)
    {
        Console.WriteLine(
            $"[startup] Workload Identity detected (AZURE_FEDERATED_TOKEN_FILE set). " +
            $"Using WorkloadIdentityCredential directly — no chain, no IMDS, no fallback.");
        credential = new Azure.Identity.WorkloadIdentityCredential();
    }
    else
    {
        Console.WriteLine(
            "[startup] No AZURE_FEDERATED_TOKEN_FILE — using full DefaultAzureCredential " +
            "chain (local dev: az login / VS / etc.).");
        credential = new Azure.Identity.DefaultAzureCredential();
    }
    var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

    // NOTE: we deliberately do NOT perform any blocking I/O between here and
    // `app.Run()` below. Earlier revisions did a `WarmUpCredential` token
    // exchange (up to ~67s of retries) and a synchronous PAT-connection
    // lookup before binding Kestrel; both of those run while the container
    // has no port listening, so Foundry's `/readiness` probe gets
    // connection-refused and (since its readiness timeout is ~60s) the
    // gateway returns `424 session_not_ready` to the caller. The "fix" was
    // self-inflicted. Auth is now lazy: the first real request triggers the
    // Workload Identity token exchange (a file read + Entra POST, ~50-200ms);
    // if it fails transiently the workflow's bash retry loop covers it.

    // GitHub identity:
    //   - The workflow mints a per-run GitHub App installation token and
    //     sends it in the JSON body field `github_token`.
    //     TriageInvocationHandler pushes it onto GitHubTokenProvider's
    //     AsyncLocal, and GitHubRestTools reads it for every outbound REST
    //     call so the agent posts/labels as `<app>[bot]`.
    //   - There is no static fallback. CLI invocations without a body
    //     `github_token` will fail with "no GitHub token in scope", which is
    //     the desired behaviour — the alternative (a static PAT in a Foundry
    //     connection) was previously fetched on every startup, blocked
    //     readiness, and is never exercised in production anyway.
    var tokenProvider = new GitHubTokenProvider();

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
