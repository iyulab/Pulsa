namespace PulsaPipeline;

/// <summary>
/// Holds pipeline execution state: topic, step results, and template variables.
/// </summary>
public class PipelineContext
{
    public string Topic { get; }

    private readonly Dictionary<string, string> _results = new(StringComparer.OrdinalIgnoreCase);

    public PipelineContext(string topic)
    {
        Topic = topic;
    }

    public void SetResult(string stepName, string result)
    {
        _results[stepName] = result;
    }

    public string? GetResult(string stepName)
    {
        return _results.TryGetValue(stepName, out var result) ? result : null;
    }

    public string GetPreviousResult()
    {
        return _results.Count > 0 ? _results.Values.Last() : "";
    }

    public string ResolveInput(StepDefinition step)
    {
        if (!string.IsNullOrEmpty(step.Input))
        {
            return GetResult(step.Input)
                ?? throw new InvalidOperationException(
                    $"Step '{step.Name}' references input '{step.Input}' which has no result.");
        }
        return GetPreviousResult();
    }

    public Dictionary<string, string> GetVariables(StepDefinition step)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["topic"] = Topic,
            ["previous"] = ResolveInput(step),
        };

        foreach (var (name, result) in _results)
        {
            vars[$"steps.{name}"] = result;
        }

        return vars;
    }
}
