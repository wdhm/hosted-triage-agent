using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TriageAgent;

/// <summary>
/// REST-based GitHub tools exposed as <see cref="AITool"/>s for the triage
/// agent. These replace the GitHub MCP server for write operations.
///
/// Why not MCP for writes:
///   1. The hosted MCP server at api.githubcopilot.com/mcp/ returns
///      <c>400 Bad Request: invalid session</c> intermittently when a
///      hosted-agent invocation reuses a session-id across calls — the
///      MCP HTTP transport's session handshake doesn't survive Foundry's
///      opaque process recycling.
///   2. The default /mcp/ endpoint includes a "context" toolset whose
///      system instruction tells the LLM to "Always call get_me first".
///      <c>get_me</c> hits <c>GET /user</c>, which returns 403 for GitHub
///      App installation tokens (that endpoint requires user-scoped auth).
///
/// Calling the REST API directly avoids both problems and still attributes
/// the comment to "&lt;app-slug&gt;[bot]" because GitHub uses the App
/// identity from the <c>ghs_</c> installation token.
/// </summary>
public sealed class GitHubRestTools
{
    // Per-invocation success counters. AsyncLocal flows through agent.RunAsync
    // and the tool callbacks the framework triggers, so the handler can read
    // these post-run to verify the model actually completed its work — instead
    // of trusting the model's self-reported "OK: ..." text (which it can lie
    // about or hallucinate).
    private static readonly AsyncLocal<Counters?> _counters = new();

    private readonly HttpClient _http;
    private readonly GitHubTokenProvider _tokens;
    private readonly ILogger<GitHubRestTools> _logger;

    public GitHubRestTools(GitHubTokenProvider tokens, ILogger<GitHubRestTools> logger)
    {
        _tokens = tokens;
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wdhm-triage-agent/1.0");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Reset per-invocation counters at the start of a handler call.</summary>
    public IDisposable BeginInvocation()
    {
        var prev = _counters.Value;
        _counters.Value = new Counters();
        return new Restore(() => _counters.Value = prev);
    }

    /// <summary>Snapshot of tool-call outcomes for the current invocation.</summary>
    public Counters Snapshot() => _counters.Value ?? new Counters();

    /// <summary>Tools exposed on the issue.opened path (comment + relabel).</summary>
    public List<AITool> TriageTools() =>
    [
        AIFunctionFactory.Create(AddIssueCommentAsync,
            name: "add_issue_comment",
            description: "Post a markdown comment on a GitHub issue. Returns the API response JSON (id, html_url, user.login)."),
        AIFunctionFactory.Create(SetIssueLabelsAsync,
            name: "set_issue_labels",
            description: "Replace the entire label set on a GitHub issue with the given list. Any label that does not yet exist in the repo is created automatically with a default color. Returns the API response JSON."),
    ];

    /// <summary>Tools exposed on the issue_comment.created path (comment only).</summary>
    public List<AITool> FollowupTools() =>
    [
        AIFunctionFactory.Create(AddIssueCommentAsync,
            name: "add_issue_comment",
            description: "Post a markdown comment on a GitHub issue. Returns the API response JSON."),
    ];

    [Description("Post a markdown comment on a GitHub issue.")]
    public async Task<string> AddIssueCommentAsync(
        [Description("Repository owner (org or user login).")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int issue_number,
        [Description("Comment body (GitHub-flavored markdown).")] string body,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { body });
        using var req = NewRequest(HttpMethod.Post,
            $"repos/{owner}/{repo}/issues/{issue_number}/comments", payload);
        var result = await SendAsync(req, $"add_issue_comment {owner}/{repo}#{issue_number}", cancellationToken);
        if (result.Success)
        {
            (_counters.Value ??= new Counters()).CommentsPosted++;
        }
        return result.Body;
    }

    [Description("Replace the labels on a GitHub issue with the provided list. Missing labels are auto-created.")]
    public async Task<string> SetIssueLabelsAsync(
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number.")] int issue_number,
        [Description("Final label set. Any label not yet defined in the repo is created on the fly with a default color.")] string[] labels,
        CancellationToken cancellationToken = default)
    {
        // Pre-create any missing labels so PUT /labels doesn't 422 on a name
        // the repo doesn't know yet. 422 from POST /labels = already exists,
        // which is fine.
        foreach (var label in labels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await EnsureLabelExistsAsync(owner, repo, label, cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new { labels });
        using var req = NewRequest(HttpMethod.Put,
            $"repos/{owner}/{repo}/issues/{issue_number}/labels", payload);
        var result = await SendAsync(req, $"set_issue_labels {owner}/{repo}#{issue_number}", cancellationToken);
        if (result.Success)
        {
            (_counters.Value ??= new Counters()).LabelsSet++;
        }
        return result.Body;
    }

    private async Task EnsureLabelExistsAsync(
        string owner, string repo, string name, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { name, color = "bfd4f2" });
        using var req = NewRequest(HttpMethod.Post,
            $"repos/{owner}/{repo}/labels", payload);
        var token = _tokens.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));
        }
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.Created
            && resp.StatusCode != HttpStatusCode.UnprocessableEntity)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine(
                $"[REST-WARN] ensure_label {owner}/{repo} '{name}': HTTP {(int)resp.StatusCode} {Trunc(body, 300)}");
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, string jsonBody) => new(method, path)
    {
        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
    };

    private async Task<RestResult> SendAsync(
        HttpRequestMessage req, string label, CancellationToken cancellationToken)
    {
        var token = _tokens.GetToken();
        if (string.IsNullOrEmpty(token))
        {
            var msg = $"FAILED: {label} — no GitHub token in scope.";
            Console.WriteLine($"[REST-ERR] {msg}");
            return new RestResult(false, msg);
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearer(token));

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine(
                $"[REST-ERR] {label}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {Trunc(body, 800)}");
            return new RestResult(false,
                $"FAILED: {label} returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Trunc(body, 800)}");
        }
        Console.WriteLine($"[REST-OK] {label}: HTTP {(int)resp.StatusCode}");
        return new RestResult(true, Trunc(body, 1500));
    }

    private static string StripBearer(string token) =>
        token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim()
            : token.Trim();

    private static string Trunc(string s, int max) =>
        s.Length > max ? s[..max] + "...[truncated]" : s;

    private sealed record RestResult(bool Success, string Body);

    public sealed class Counters
    {
        public int CommentsPosted;
        public int LabelsSet;
    }

    private sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
