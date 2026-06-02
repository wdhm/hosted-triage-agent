using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
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
    "GITHUB_MCP_CONNECTION_NAME",
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
    var mcpConnectionName = Environment.GetEnvironmentVariable("GITHUB_MCP_CONNECTION_NAME")
        ?? "github-pat-connection";

    Console.WriteLine($"[startup] Using project endpoint: {projectEndpoint}");
    Console.WriteLine($"[startup] Using model deployment: {modelDeployment}");
    Console.WriteLine($"[startup] Using MCP connection: {mcpConnectionName}");

    var credential = new Azure.Identity.DefaultAzureCredential();
    var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

    // Pull the GitHub PAT from the Foundry "Custom keys" project connection
    // (key=Authorization, value=Bearer <PAT>). This keeps the secret entirely
    // inside Foundry — never in env vars, never in code.
    Console.WriteLine($"[startup] Reading connection '{mcpConnectionName}'...");
    var connection = projectClient.Connections
        .GetConnectionAsync(mcpConnectionName, includeCredentials: true)
        .GetAwaiter().GetResult();

    if (connection.Value.Credentials is not AIProjectConnectionCustomCredential customCreds)
    {
        var actualType = connection.Value.Credentials?.GetType().FullName ?? "(null)";
        throw new InvalidOperationException(
            $"Connection '{mcpConnectionName}' credentials type is '{actualType}', expected CustomKeys.");
    }

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
    if (string.IsNullOrWhiteSpace(authHeader))
    {
        throw new InvalidOperationException(
            $"Connection '{mcpConnectionName}' does not contain an 'Authorization' key. Keys present: [{string.Join(", ", customCreds.Keys.Keys)}].");
    }
    // Strip optional "Bearer " prefix — the DynamicAuthHandler re-adds it.
    var fallbackToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authHeader["Bearer ".Length..].Trim()
        : authHeader.Trim();
    Console.WriteLine($"[startup] Fallback PAT loaded from Foundry connection ({fallbackToken.Length} chars).");

    // GitHub identity strategy:
    //   - Preferred path (production): workflow mints a GitHub App installation
    //     token per run and sends it as `X-GitHub-Token` on the invocation POST.
    //     TriageInvocationHandler pushes it onto GitHubTokenProvider's AsyncLocal,
    //     DynamicAuthHandler stamps it on every outbound MCP HTTP call. The
    //     agent then posts/labels as `<app>[bot]` with its own identity.
    //   - Fallback path (CLI / local testing): the PAT from the Foundry
    //     connection above is used. Lets us invoke without a workflow.
    var tokenProvider = new GitHubTokenProvider(fallbackToken);

    // Connect to the GitHub remote MCP server (Streamable HTTP transport). The
    // DynamicAuthHandler injects the per-request token (or fallback PAT) onto
    // every outbound HTTP call — both the initial handshake (uses fallback) and
    // per-tool-invocation calls (use AsyncLocal token from the request).
    Console.WriteLine("[startup] Connecting to GitHub MCP server...");
    var dynamicHandler = new DynamicAuthHandler(
        tokenProvider,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DynamicAuthHandler>.Instance)
    {
        InnerHandler = new HttpClientHandler(),
    };
    var mcpHttpClient = new HttpClient(dynamicHandler);
    var transport = new HttpClientTransport(
        new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://api.githubcopilot.com/mcp/"),
            Name = "github-mcp",
            TransportMode = HttpTransportMode.StreamableHttp,
            // No AdditionalHeaders["Authorization"] — DynamicAuthHandler owns it.
        },
        httpClient: mcpHttpClient,
        loggerFactory: null,
        ownsHttpClient: true);

    var mcpClient = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
    Console.WriteLine("[startup] MCP client connected.");

    var allTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
    Console.WriteLine($"[startup] Server advertises {allTools.Count} tools.");

    // ── Two agents, two tool allowlists ──────────────────────────────────────
    // triageAgent  : posts comment + applies labels (issue.opened path)
    // followupAgent: posts comment only (issue_comment.created path) — narrower
    //                blast radius so a follow-up can't accidentally re-label.

    var triageToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "add_issue_comment",
        "create_issue_comment",
        "issue_write",
        "update_issue",
        "label_write",
        "create_label",
        "get_label",
    };

    var followupToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "add_issue_comment",
        "create_issue_comment",
    };

    var triageTools = allTools.Where(t => triageToolNames.Contains(t.Name))
        .Cast<AITool>().ToList();
    var followupTools = allTools.Where(t => followupToolNames.Contains(t.Name))
        .Cast<AITool>().ToList();

    Console.WriteLine($"[startup] Triage tools ({triageTools.Count}): {string.Join(", ", triageTools.Select(t => t.Name))}");
    Console.WriteLine($"[startup] Follow-up tools ({followupTools.Count}): {string.Join(", ", followupTools.Select(t => t.Name))}");

    // Fail-fast guards — startup should not succeed if the server's tool surface
    // has drifted away from what our prompts assume.
    bool HasAny(IEnumerable<AITool> tools, params string[] names) =>
        tools.Any(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase));

    if (!HasAny(triageTools, "add_issue_comment", "create_issue_comment"))
        throw new InvalidOperationException("GitHub MCP server is missing a comment-post tool (add_issue_comment / create_issue_comment).");
    if (!HasAny(triageTools, "issue_write", "update_issue"))
        throw new InvalidOperationException("GitHub MCP server is missing an issue-update tool (issue_write / update_issue).");
    if (!HasAny(triageTools, "get_label"))
        throw new InvalidOperationException("GitHub MCP server is missing the get_label tool.");
    if (!HasAny(followupTools, "add_issue_comment", "create_issue_comment"))
        throw new InvalidOperationException("Follow-up agent has no comment-post tool wired.");

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

    Console.WriteLine("[startup] Both agents constructed.");

    builder.Services.AddSingleton(tokenProvider);
    builder.Services.AddSingleton(mcpClient);
    builder.Services.AddSingleton(new AgentBundle(triageAgent, followupAgent));
    builder.Services.AddSingleton<FoundrySessionStore>(sp =>
        new FoundrySessionStore(
            // session store binds to followupAgent because both agents share the
            // same underlying Foundry thread model — serialize/deserialize works
            // identically on either, but we pick the narrower agent to make it
            // obvious that the store isn't tool-aware.
            sp.GetRequiredService<AgentBundle>().Followup,
            sp.GetRequiredService<ILogger<FoundrySessionStore>>()));
    builder.Services.AddInvocationsServer();
    builder.Services.AddScoped<InvocationHandler, TriageInvocationHandler>();

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
