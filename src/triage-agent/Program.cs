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

    // Credential — explicit user-assigned ManagedIdentityCredential when the
    // Foundry runtime injects AZURE_CLIENT_ID; plain DefaultAzureCredential
    // otherwise (local dev with `az login`).
    //
    // Why NOT plain `new DefaultAzureCredential()` (as the official samples
    // show in the foundry-samples repo)? Because DAC's *internal*
    // ManagedIdentityCredential sub-credential does **not** auto-read
    // AZURE_CLIENT_ID. `DefaultAzureCredentialOptions.ManagedIdentityClientId`
    // defaults to null, and DAC passes that through to MIC's ctor, so MIC
    // calls IMDS asking for the *system-assigned* MI — which Foundry
    // containers don't have. Every probe times out after ~22-30s of MSAL
    // backoff, costing ~3 minutes across the Foundry gateway's internal
    // retry budget per invocation.
    //
    // Verified live (v40, App Insights MSAL traces during issue #87):
    //   partition `22839df0-..._managed_identity_AppTokenCache`             178 successful hits  ← App Insights export (passes client_id explicitly)
    //   partition `system_assigned_managed_identity_managed_identity_...`    12 failed hits      ← our DAC chain
    //
    // The samples appear to work because they use the Responses protocol
    // (`AddFoundryResponses`), where Foundry's gateway mediates the OpenAI
    // call and the in-container credential is never asked for a model
    // token. Our Invocations protocol path constructs the AIProjectClient +
    // OpenAI chat client in-process, so the LLM call hits BearerTokenPolicy
    // → our credential → MIC. With no client_id, IMDS returns 500 every
    // single time.
    //
    // Fix: pin MIC to the agent identity that azd auto-created and granted
    // `Foundry User` at project scope. AZURE_CLIENT_ID is the discovery
    // mechanism Foundry uses to tell us which identity to authenticate as.
    var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
    Azure.Core.TokenCredential credential;
    if (!string.IsNullOrWhiteSpace(azureClientId))
    {
        Console.WriteLine(
            $"[startup] Using ManagedIdentityCredential pinned to AZURE_CLIENT_ID " +
            $"({azureClientId[..8]}...) — Foundry's per-agent Entra identity.");
        credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(azureClientId));
    }
    else
    {
        Console.WriteLine(
            "[startup] No AZURE_CLIENT_ID — using DefaultAzureCredential (local dev).");
        credential = new DefaultAzureCredential();
    }
    var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

    // Kick off background credential pre-warm. Foundry's per-container MI
    // takes several minutes to provision in the IMDS sidecar after first
    // boot (observed ~6 min for v41 in App Insights), during which IMDS
    // returns HTTP 500. Without pre-warm, every single /invocations triggers
    // its own ~30-50s IMDS retry storm. With pre-warm, the storm happens
    // exactly once per container, in the background, and the handler just
    // awaits the cached token. See TokenWarmup.cs for the full rationale.
    var tokenWarmup = new TokenWarmup(credential);
    tokenWarmup.Start();
    builder.Services.AddSingleton(tokenWarmup);

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
