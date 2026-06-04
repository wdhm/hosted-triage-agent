using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Azure.Identity;
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

    // Credential: plain DefaultAzureCredential, matching the official Foundry
    // hosted-agents samples
    // (microsoft-foundry/foundry-samples/samples/csharp/hosted-agents/agent-framework/*).
    //
    // The Foundry runtime injects AZURE_CLIENT_ID + AZURE_TENANT_ID pointing at
    // the agent identity (an Entra service principal that azd auto-creates and
    // grants the `Foundry User` role at project scope — verified via
    // `az role assignment list --assignee $AZURE_CLIENT_ID --all`). DAC reads
    // those env vars and authenticates through ManagedIdentityCredential with
    // the right client_id; no manual wiring needed. Outside Foundry (local
    // `dotnet run`) DAC falls back to AzureCliCredential / VisualStudio etc.
    //
    // We previously had a `new WorkloadIdentityCredential()` branch gated on
    // AZURE_FEDERATED_TOKEN_FILE plus an in-process AZURE_TOKEN_CREDENTIALS
    // env-var lock — that was cargo-culted from an AKS-style WIF setup that
    // Foundry does NOT use. Confirmed by App Insights traces showing zero WIF
    // activity and a working user-assigned MI cache entry for our AZURE_CLIENT_ID.
    var credential = new DefaultAzureCredential();
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
    // internally (it calls UseAzureMonitor in Build()) AND Foundry's
    // Agent365Exporter auto-subscribes to "Experimental.Microsoft.Extensions.AI"
    // for gen_ai.chat / execute_tool spans on the IAgentInvocationHandler path
    // (Microsoft.Agents.AI.Foundry.Hosting ≥ 1.6.1, PR #5750 in agent-framework).
    // We MUST NOT register that source again — Foundry's ExportFormatter
    // crashes with `An item with the same key has already been added.
    // Key: openai.api.type` when the same span attribute set is materialised
    // twice (observed in App Insights on v39, stack lands in
    // Microsoft.Agents.A365.Observability.Runtime.Common.ExportFormatter.MapAttributes).
    //
    // What we DO still need to register:
    //   - "Experimental.Microsoft.Agents.AI"      → invoke_agent spans (we
    //     wrap each agent above with UseOpenTelemetry, which emits on this
    //     source; AddFoundryResponses auto-wraps but the invocations path
    //     does not).
    //   - "Azure.AI.AgentServer.Invocations"      → baggage propagation from
    //     the invocations endpoint handler.
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
