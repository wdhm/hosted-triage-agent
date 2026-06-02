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
    Console.WriteLine($"[startup] PAT loaded from Foundry connection ({authHeader.Length} chars).");

    // Connect to the GitHub remote MCP server (Streamable HTTP transport). The
    // PAT travels only via the Authorization header on outbound HTTP calls.
    Console.WriteLine("[startup] Connecting to GitHub MCP server...");
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri("https://api.githubcopilot.com/mcp/"),
        Name = "github-mcp",
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = authHeader,
        },
    });

    var mcpClient = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
    Console.WriteLine("[startup] MCP client connected.");

    // List tools the server advertises and filter to just the ones we need.
    // The model's allowlist matches what's referenced in the system prompt.
    var allTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
    Console.WriteLine($"[startup] Server advertises {allTools.Count} tools.");

    var allowedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "add_issue_comment",
        "create_issue_comment",   // alternative name on some server versions
        "issue_write",
        "update_issue",            // alternative name on some server versions
        "label_write",
        "create_label",            // alternative name on some server versions
        "get_label",
    };

    var filteredTools = allTools
        .Where(t => allowedToolNames.Contains(t.Name))
        .Cast<AITool>()
        .ToList();

    Console.WriteLine($"[startup] Using {filteredTools.Count} filtered tools: {string.Join(", ", filteredTools.Select(t => t.Name))}");

    if (filteredTools.Count == 0)
    {
        var sample = string.Join(", ", allTools.Take(15).Select(t => t.Name));
        Console.WriteLine($"[startup] WARNING: zero tools matched the allowlist. First advertised tools: {sample}");
    }

    // ChatClientAgent wraps its IChatClient with FunctionInvokingChatClient
    // automatically (UseProvidedChatClientAsIs defaults to false), so tool
    // calls execute through the MCP client without any extra middleware.
    ChatClientAgent triageAgent = projectClient.AsAIAgent(
        model: modelDeployment,
        instructions: TriageInvocationHandler.SystemInstruction,
        name: "triage-agent",
        tools: filteredTools);
    Console.WriteLine("[startup] AIAgent constructed with MCP tools.");

    builder.Services.AddSingleton<AIAgent>(triageAgent);
    builder.Services.AddSingleton(mcpClient);
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
