using System.ComponentModel;
using System.Text.Json;
using FluxIndex.Extensions.FileVault.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PulsaVault.Tools;

/// <summary>
/// MCP tool for searching the PulsaVault knowledge base.
/// Uses IVault.SearchAsync with vector search over memorized document chunks.
/// </summary>
[McpServerToolType]
public sealed partial class SearchTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchTool> _logger;

    public SearchTool(IServiceScopeFactory scopeFactory, ILogger<SearchTool> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [McpServerTool(Name = "search_knowledge_base")]
    [Description("Search the local knowledge base of memorized documents. Returns the most relevant text chunks ranked by semantic similarity.")]
    public async Task<string> SearchAsync(
        [Description("The search query — a question or keywords to find relevant content")]
        string query,

        [Description("Maximum number of result chunks to return (default 10)")]
        int maxResults = 10,

        [Description("Minimum similarity score threshold 0.0–1.0 (default 0.0)")]
        float minScore = 0.0f,

        [Description("Optional folder or file path to scope the search to (e.g., 'D:/docs/' or 'D:/docs/report.pdf')")]
        string? pathScope = null,

        CancellationToken cancellationToken = default)
    {
        LogSearching(_logger, query, maxResults);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var vault = scope.ServiceProvider.GetRequiredService<IVault>();

            var options = new VaultSearchOptions
            {
                TopK = maxResults,
                MinScore = minScore,
                IncludeContent = true,
                IncludeMetadata = true,
                PathScope = string.IsNullOrWhiteSpace(pathScope)
                    ? []
                    : [pathScope]
            };

            var result = await vault.SearchAsync(query, options, cancellationToken);

            if (!result.IsSuccess)
            {
                LogSearchFailed(_logger, null, query);
                return JsonSerializer.Serialize(new
                {
                    error = "Search failed",
                    message = result.ErrorMessage
                }, s_jsonOptions);
            }

            if (result.Items.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = $"No results found for '{query}'. Try broadening your search terms.",
                    results = Array.Empty<object>()
                }, s_jsonOptions);
            }

            var items = result.Items.Select(item => new
            {
                sourcePath = item.SourcePath,
                fileName = item.FileName,
                chunkIndex = item.ChunkIndex,
                score = item.Score,
                content = item.Content
            });

            return JsonSerializer.Serialize(new
            {
                query,
                totalCount = result.TotalCount,
                documentsSearched = result.DocumentsSearched,
                durationMs = result.Duration.TotalMilliseconds,
                count = result.Items.Count,
                results = items
            }, s_jsonOptions);
        }
        catch (Exception ex)
        {
            LogSearchFailed(_logger, ex, query);
            return JsonSerializer.Serialize(new
            {
                error = "Search failed",
                message = ex.Message
            }, s_jsonOptions);
        }
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Searching knowledge base: {Query} (maxResults={MaxResults})")]
    private static partial void LogSearching(ILogger logger, string query, int maxResults);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Search failed for query: {Query}")]
    private static partial void LogSearchFailed(ILogger logger, Exception? exception, string query);

    #endregion
}
