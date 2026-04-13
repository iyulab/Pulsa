using Microsoft.Extensions.Logging;

namespace PulsaPipeline;

/// <summary>
/// Executes a pipeline definition step by step.
/// </summary>
public class PipelineRunner(
    IReadOnlyDictionary<string, IStepAction> actions,
    ILogger<PipelineRunner> logger)
{
    /// <summary>
    /// Runs the pipeline and writes each step's output to the output directory.
    /// </summary>
    public async Task RunAsync(
        PipelineDefinition definition,
        string topic,
        string pipelineBasePath,
        string outputDir,
        CancellationToken ct = default)
    {
        var context = new PipelineContext(topic);

        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Pipeline '{Name}' started — topic: {Topic}, steps: {Count}",
            definition.Name, topic, definition.Steps.Count);

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = definition.Steps[i];
            var stepNumber = i + 1;
            var outputFileName = $"{stepNumber:D2}-{step.Name}.md";
            var outputPath = Path.Combine(outputDir, outputFileName);

            logger.LogInformation("[{StepNum}/{Total}] Running step '{Name}' (action: {Action})...",
                stepNumber, definition.Steps.Count, step.Name, step.Action);

            if (!actions.TryGetValue(step.Action, out var action))
            {
                throw new InvalidOperationException(
                    $"Unknown action type '{step.Action}' in step '{step.Name}'. " +
                    $"Available actions: {string.Join(", ", actions.Keys)}");
            }

            // Resolve prompt path relative to pipeline file
            var resolvedStep = new StepDefinition
            {
                Name = step.Name,
                Action = step.Action,
                Prompt = Path.IsPathRooted(step.Prompt)
                    ? step.Prompt
                    : Path.Combine(pipelineBasePath, step.Prompt),
                Input = step.Input,
            };

            var result = await action.ExecuteAsync(resolvedStep, "", context, ct);

            context.SetResult(step.Name, result);

            await File.WriteAllTextAsync(outputPath, result, ct);
            logger.LogInformation("[{StepNum}/{Total}] Step '{Name}' complete → {Output}",
                stepNumber, definition.Steps.Count, step.Name, outputPath);
        }

        logger.LogInformation("Pipeline '{Name}' completed — {Count} steps, output: {Dir}",
            definition.Name, definition.Steps.Count, outputDir);
    }
}
