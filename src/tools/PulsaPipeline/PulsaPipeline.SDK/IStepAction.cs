namespace PulsaPipeline;

/// <summary>
/// Executes a pipeline step action.
/// </summary>
public interface IStepAction
{
    /// <summary>
    /// The action type name this handler supports (e.g., "chat").
    /// </summary>
    string ActionType { get; }

    /// <summary>
    /// Executes the step and returns the result text.
    /// </summary>
    Task<string> ExecuteAsync(StepDefinition step, string prompt, PipelineContext context, CancellationToken ct);
}
