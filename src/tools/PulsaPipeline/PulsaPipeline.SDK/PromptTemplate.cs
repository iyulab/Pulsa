using System.Text.RegularExpressions;

namespace PulsaPipeline;

/// <summary>
/// Replaces {{variable}} placeholders in prompt text.
/// </summary>
public static partial class PromptTemplate
{
    [GeneratedRegex(@"\{\{(\w[\w.]*)\}\}")]
    private static partial Regex VariablePattern();

    public static string Render(string template, Dictionary<string, string> variables)
    {
        return VariablePattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}
