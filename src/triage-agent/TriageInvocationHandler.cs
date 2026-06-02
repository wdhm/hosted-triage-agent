using System.Diagnostics;
using System.Text.Json;
using Azure.AI.AgentServer.Invocations;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// Invocations-protocol handler that runs the LLM-backed triage agent and
/// returns a structured <see cref="TriageOutput"/> as JSON.
/// </summary>
public sealed class TriageInvocationHandler(
    AIAgent agent,
    ILogger<TriageInvocationHandler> logger) : InvocationHandler
{
    public const string SystemInstruction = """
        You are an expert software-engineering triage assistant.

        Given a GitHub issue (title and body, optionally containing app/error logs
        or stack traces), produce a structured triage assessment as JSON conforming
        to the supplied schema.

        Guidelines:
        - Be concrete and specific; avoid hedging like "could be anything".
        - Prefer 'critical' severity ONLY for outages, data loss, or security.
        - If the body lacks signal, set root_cause to "insufficient information"
          and include "needs-repro" in suggested_labels.
        - Suggested labels should be lowercase-kebab-case and useful for routing
          (e.g. "area-auth", "perf", "needs-design").
        - Next actions should be concrete steps a maintainer can take immediately.
        """;

    public override async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var reader = new StreamReader(request.Body);
        var issueText = await reader.ReadToEndAsync(cancellationToken);

        logger.LogInformation(
            "Triage invocation received: inputLength={InputLength}",
            issueText.Length);

        // Per-call ResponseFormat enforces strict JSON-schema output for TriageOutput.
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<TriageOutput>(),
        });

        var rawResponse = await agent.RunAsync(
            message: issueText,
            options: runOptions,
            cancellationToken: cancellationToken);

        TriageOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<TriageOutput>(rawResponse.Text)
                ?? throw new InvalidOperationException("Deserialized triage output was null.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Failed to deserialize triage output. Raw response: {RawResponse}",
                rawResponse.Text);
            response.StatusCode = StatusCodes.Status502BadGateway;
            response.ContentType = "application/json";
            await response.WriteAsJsonAsync(new
            {
                error = "triage_output_invalid",
                message = "Model returned non-conforming JSON.",
                raw = rawResponse.Text,
            }, cancellationToken: cancellationToken);
            return;
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Triage invocation completed: severity={Severity} category={Category} labels={LabelCount} elapsedMs={ElapsedMs}",
            output.Severity, output.Category, output.SuggestedLabels.Length, stopwatch.ElapsedMilliseconds);

        response.ContentType = "application/json";
        await response.WriteAsJsonAsync(output, cancellationToken: cancellationToken);
    }
}
