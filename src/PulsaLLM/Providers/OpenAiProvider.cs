using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PulsaLLM.Providers;

public class OpenAiProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly ILogger _logger;

    public OpenAiProvider(ProviderOptions options, ILogger logger)
    {
        _logger = logger;
        _model = options.Model;
        _maxTokens = options.MaxTokens;

        var baseUrl = options.Type.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? "https://api.openai.com"
            : options.Host.TrimEnd('/');

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("User-Agent", "PulsaLLM");

        if (!string.IsNullOrEmpty(options.ApiKey))
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        var request = new
        {
            model = _model,
            max_tokens = _maxTokens,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            }
        };

        _logger.LogDebug("Calling OpenAI API: model={Model}", _model);

        var response = await _http.PostAsJsonAsync("/v1/chat/completions", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private record ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; init; }
    }

    private record Choice
    {
        [JsonPropertyName("message")]
        public MessageContent? Message { get; init; }
    }

    private record MessageContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}
