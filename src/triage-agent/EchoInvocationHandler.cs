// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using Azure.AI.AgentServer.Invocations;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace InvocationsEchoAgent;

/// <summary>
/// An <see cref="InvocationHandler"/> that reads the request body as plain text,
/// passes it to the <see cref="EchoAIAgent"/>, and writes the response back.
/// </summary>
public sealed class EchoInvocationHandler(
    EchoAIAgent agent,
    ILogger<EchoInvocationHandler> logger) : InvocationHandler
{
    /// <inheritdoc/>
    public override async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var reader = new StreamReader(request.Body);
        var input = await reader.ReadToEndAsync(cancellationToken);

        logger.LogInformation(
            "Invocation received: inputLength={InputLength} preview={InputPreview}",
            input.Length,
            input.Length > 80 ? input[..80] + "..." : input);

        var agentResponse = await agent.RunAsync(input, cancellationToken: cancellationToken);

        response.ContentType = "text/plain";
        await response.WriteAsync(agentResponse.Text, cancellationToken);

        stopwatch.Stop();
        logger.LogInformation(
            "Invocation completed: outputLength={OutputLength} elapsedMs={ElapsedMs}",
            agentResponse.Text.Length,
            stopwatch.ElapsedMilliseconds);
    }
}
