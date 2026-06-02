using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TriageAgent;

/// <summary>
/// Structured triage output emitted by the LLM in strict JSON-schema mode.
/// Property descriptions are surfaced to the model via the JSON schema and
/// directly influence response quality — keep them precise.
/// </summary>
[Description("Triage assessment of a software engineering issue based on its title and body.")]
public sealed class TriageOutput
{
    [JsonPropertyName("severity")]
    [Description("Severity: 'critical' (outage / data loss / security), 'high' (major broken flow), 'medium' (degraded but workaround exists), 'low' (cosmetic / docs).")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("category")]
    [Description("One of: 'bug', 'feature', 'docs', 'question', 'security', 'performance'.")]
    public string Category { get; set; } = "";

    [JsonPropertyName("summary")]
    [Description("One-sentence problem summary, max 140 chars, no markdown.")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("root_cause")]
    [Description("Most likely root cause inferred from logs / stack traces / description. State 'insufficient information' if the body lacks signal.")]
    public string RootCause { get; set; } = "";

    [JsonPropertyName("suggested_labels")]
    [Description("2-5 GitHub labels to apply, lowercase-kebab-case, e.g. 'bug', 'needs-repro', 'area-auth'.")]
    public string[] SuggestedLabels { get; set; } = [];

    [JsonPropertyName("next_actions")]
    [Description("1-3 concrete suggested next actions for the maintainer.")]
    public string[] NextActions { get; set; } = [];
}
