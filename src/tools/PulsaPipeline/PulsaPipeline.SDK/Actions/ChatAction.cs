using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PulsaLLM;

namespace PulsaPipeline.Actions;

/// <summary>
/// Executes a pipeline step by calling an LLM via IChatClient.
/// </summary>
public class ChatAction(
    ChatClientFactory clientFactory,
    ProviderOptions globalProvider,
    ILogger<ChatAction> logger) : IStepAction
{
    public string ActionType => "chat";

    public async Task<string> ExecuteAsync(
        StepDefinition step, string prompt, PipelineContext context, CancellationToken ct)
    {
        // Load prompt file and apply frontmatter overrides
        var promptData = await PromptLoader.LoadAsync(step.Prompt, ct);
        var resolvedProvider = PromptLoader.ApplyOverrides(globalProvider, promptData.Frontmatter);

        // Render the prompt body with pipeline variables
        var variables = context.GetVariables(step);
        var renderedPrompt = PromptTemplate.Render(promptData.SystemPrompt, variables);

        var chatClient = clientFactory.GetOrCreate(resolvedProvider);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, renderedPrompt),
        };

        // If there's previous context, add it as user message
        var input = context.ResolveInput(step);
        if (!string.IsNullOrEmpty(input))
        {
            messages.Add(new(ChatRole.User, input));
        }
        else
        {
            // First step: topic is the user message
            messages.Add(new(ChatRole.User, context.Topic));
        }

        var chatOptions = new ChatOptions();
        if (resolvedProvider.MaxTokens > 0)
            chatOptions.MaxOutputTokens = resolvedProvider.MaxTokens;
        if (resolvedProvider.Temperature.HasValue)
            chatOptions.Temperature = resolvedProvider.Temperature.Value;
        if (!string.IsNullOrEmpty(resolvedProvider.Model))
            chatOptions.ModelId = resolvedProvider.Model;

        logger.LogInformation("[{Step}] Calling LLM (model: {Model})...",
            step.Name, resolvedProvider.Model);

        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
        var result = (response.Text ?? "").Trim();

        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"Step '{step.Name}': LLM returned empty response.");
        }

        logger.LogInformation("[{Step}] LLM response: {Length} chars", step.Name, result.Length);
        return result;
    }
}
