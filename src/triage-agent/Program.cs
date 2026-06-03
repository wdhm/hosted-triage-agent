using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry;
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

    var credential = new Azure.Identity.DefaultAzureCredential();
    var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

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
    builder.Services.AddSingleton<FoundrySessionStore>(sp =>
        new FoundrySessionStore(
            // Bind to the unwrapped ChatClientAgent so session (de)serialization
            // is independent of the OpenTelemetry decorator. The bundle holds
            // the wrapped versions for RunAsync; we keep this concern separate.
            followupAgent,
            sp.GetRequiredService<ILogger<FoundrySessionStore>>()));
    builder.Services.AddInvocationsServer();
    builder.Services.AddScoped<InvocationHandler, TriageInvocationHandler>();

    // ── OpenTelemetry → App Insights ───────────────────────────────────────
    // Foundry auto-injects APPLICATIONINSIGHTS_CONNECTION_STRING into the
    // container. Sources registered here MUST match the ones the runtime
    // actually emits on (verified against microsoft/agent-framework + MEAI
    // source). On the IAgentInvocationHandler path:
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
    //   - AddAspNetCoreInstrumentation()          → inbound request span
    //     (this is the root of each end-to-end trace; without it, the
    //     KQL `requests` row is empty for our invocations).
    //   - AddHttpClientInstrumentation()          → outbound HTTP to Azure
    //     OpenAI and api.github.com.
    var aiConnStr = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(aiConnStr))
    {
        Console.WriteLine($"[startup] App Insights connection string present ({aiConnStr.Length} chars) — wiring OpenTelemetry exporter.");
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("triage-agent", serviceVersion: "1.0"))
            .WithTracing(t => t
                .AddSource("Experimental.Microsoft.Agents.AI")
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Azure.AI.AgentServer.Invocations")
                .AddSource(GitHubRestTools.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConnStr));
    }
    else
    {
        Console.WriteLine("[startup] APPLICATIONINSIGHTS_CONNECTION_STRING not set — telemetry export disabled. Spans still emit locally and surface in logs.");
    }

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
