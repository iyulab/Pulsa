using Microsoft.Extensions.Options;
using Pulsa;
using PulsaLLM.Providers;

namespace PulsaLLM.Workers;

public class LlmWorker(
    FileQueue queue,
    IOptions<LlmOptions> options,
    ILogger<LlmWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory.StartNew(
            () => RunAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        var promptPath = Path.IsPathRooted(opts.PromptFile)
            ? opts.PromptFile
            : Path.Combine(AppContext.BaseDirectory, opts.PromptFile);

        if (!File.Exists(promptPath))
        {
            logger.LogError("Prompt file not found: {Path}", promptPath);
            return;
        }

        var promptData = await PromptLoader.LoadAsync(promptPath, stoppingToken);
        var providerOptions = PromptLoader.ApplyOverrides(opts.Provider, promptData.Frontmatter);

        logger.LogInformation("LLM worker started. Provider: {Type}, Model: {Model}, Pattern: {Pattern} -> *.{Slug}.md",
            providerOptions.Type, providerOptions.Model, opts.FilePattern, opts.PromptSlug);

        await using var provider = await CreateProviderAsync(providerOptions, stoppingToken);

        await foreach (var filePath in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(provider, filePath, promptData.SystemPrompt, opts, stoppingToken);
            }
            finally
            {
                queue.Complete(filePath);
            }
        }
    }

    private async Task ProcessAsync(ILlmProvider provider, string filePath, string systemPrompt, LlmOptions opts, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("File not found, skipping: {Path}", filePath);
            return;
        }

        var outputPath = opts.ResolveOutputPath(filePath);
        if (File.Exists(outputPath))
        {
            logger.LogDebug("Output already exists, skipping: {Path}", outputPath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var tempPath = outputPath + ".tmp";
        logger.LogInformation("Processing: {Path}", filePath);
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            var result = await provider.GenerateAsync(systemPrompt, content, ct);

            await File.WriteAllTextAsync(tempPath, result, ct);
            File.Move(tempPath, outputPath, overwrite: false);

            logger.LogInformation("Done: {Path}", outputPath);
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM processing failed: {Path}", filePath);
            FileHelper.TryDelete(tempPath);
        }
    }

    private async Task<ILlmProvider> CreateProviderAsync(ProviderOptions opts, CancellationToken ct)
    {
        return opts.Type.ToLowerInvariant() switch
        {
            "openai" or "openai-compatible" => new OpenAiProvider(opts, logger),
            _ => await LmSupplyProvider.CreateAsync(opts, logger, ct),
        };
    }
}
