namespace PulsaPipeline;

/// <summary>
/// Defines a single step in a pipeline.
/// </summary>
public class StepDefinition
{
    /// <summary>
    /// Unique name for this step (used for referencing results).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Action type to execute. Default: "chat".
    /// </summary>
    public string Action { get; set; } = "chat";

    /// <summary>
    /// Path to the prompt file (relative to pipeline file directory).
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// Optional: explicit input step name. If omitted, uses previous step's output.
    /// </summary>
    public string? Input { get; set; }
}
