using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI;
using OpenAI.Responses;
using TriageAgent;

// Suppress [Experimental] warnings on the OpenAI.Responses MCP surface and the
// Microsoft.Extensions.AI experimental APIs.
#pragma warning disable OPENAI001, MEAI001

// Early-startup diagnostics — these write to stdout BEFORE App Insights is wired
// so we can see boot failures even if telemetry export hasn't initialized.
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

    // Build the GitHub MCP tool. The Foundry runtime executes MCP tool calls
    // server-side using the PAT stored in the Custom-keys project connection
    // (Authorization header = "Bearer <PAT>").
    var mcpTool = ResponseTool.CreateMcpTool(
        serverLabel: "github",
        serverUri: new Uri("https://api.githubcopilot.com/mcp/"),
        allowedTools: new McpToolFilter
        {
            ToolNames =
            {
                // We do NOT include issue_read — title + body are already in the
                // invocation payload, so we save a tool round-trip and shrink the
                // attack surface for prompt-injection.
                "add_issue_comment",
                "issue_write",
                "label_write",
                "get_label",
            },
        },
        toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
            GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));
    mcpTool.ProjectConnectionId = mcpConnectionName;
    Console.WriteLine("[startup] MCP tool built.");

    // Create (or refresh) the persisted agent version that carries the MCP tool.
    // NOTE: each cold start creates a new version. Acceptable for the demo; an
    // M6 cleanup task should prune old versions. See plan.md.
    var agentVersion = projectClient.AgentAdministrationClient
        .CreateAgentVersionAsync(
            "triage-agent",
            new ProjectsAgentVersionCreationOptions(
                new DeclarativeAgentDefinition(model: modelDeployment)
                {
                    Instructions = TriageInvocationHandler.SystemInstruction,
                    Tools = { mcpTool },
                }))
        .GetAwaiter().GetResult();
    Console.WriteLine($"[startup] Agent version created: {agentVersion.Value.Version}");

    AIAgent triageAgent = projectClient.AsAIAgent(agentVersion.Value);
    Console.WriteLine("[startup] AIAgent constructed (FoundryAgent, Responses API).");

    builder.Services.AddSingleton<AIAgent>(triageAgent);
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

#pragma warning restore OPENAI001, MEAI001
