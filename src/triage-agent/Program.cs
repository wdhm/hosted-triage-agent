using Azure.AI.AgentServer.Invocations;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using TriageAgent;

// Early-startup diagnostics — these write to stdout BEFORE App Insights is wired
// so we can see boot failures even if telemetry export hasn't initialized.
Console.WriteLine("[startup] triage-agent booting...");
foreach (var name in new[] { "AZURE_AI_PROJECT_ENDPOINT", "FOUNDRY_PROJECT_ENDPOINT", "AZURE_OPENAI_ENDPOINT", "TRIAGE_MODEL_DEPLOYMENT", "APPLICATIONINSIGHTS_CONNECTION_STRING" })
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
    var modelDeployment = Environment.GetEnvironmentVariable("TRIAGE_MODEL_DEPLOYMENT") ?? "gpt-5-mini";
    Console.WriteLine($"[startup] Using project endpoint: {projectEndpoint}");
    Console.WriteLine($"[startup] Using model deployment: {modelDeployment}");

    var credential = new Azure.Identity.DefaultAzureCredential();
    var triageAgent = new AIProjectClient(new Uri(projectEndpoint), credential)
        .AsAIAgent(
            model: modelDeployment,
            instructions: TriageInvocationHandler.SystemInstruction,
            name: "triage-agent",
            description: "Classifies and triages software engineering issues into structured JSON.");
    Console.WriteLine("[startup] AIAgent constructed.");

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
