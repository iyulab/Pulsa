using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulsaPDFDiff;

public class SettingsManager
{
    private readonly string _userSettingsPath;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public SettingsManager(string appRoot)
    {
        _userSettingsPath = Path.Combine(appRoot, "appsettings.user.json");
    }

    public OpenAIOptions GetSettings(IConfiguration config)
    {
        var opts = new OpenAIOptions();
        config.GetSection("OpenAI").Bind(opts);
        return opts;
    }

    public OpenAIOptions GetMaskedSettings(IConfiguration config)
    {
        var opts = GetSettings(config);
        if (!string.IsNullOrEmpty(opts.ApiKey) && opts.ApiKey.Length > 8)
            opts.ApiKey = opts.ApiKey[..4] + "..." + opts.ApiKey[^4..];
        return opts;
    }

    public async Task UpdateAsync(OpenAIOptions newSettings, CancellationToken ct = default)
    {
        JsonObject root;
        if (File.Exists(_userSettingsPath))
        {
            var existing = await File.ReadAllTextAsync(_userSettingsPath, ct);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root["OpenAI"] = JsonSerializer.SerializeToNode(newSettings, JsonOpts);
        await File.WriteAllTextAsync(_userSettingsPath, root.ToJsonString(JsonOpts), ct);
    }
}
