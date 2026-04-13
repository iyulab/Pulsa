using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PulsaPipeline;

/// <summary>
/// Defines a pipeline loaded from YAML.
/// </summary>
public class PipelineDefinition
{
    public string Name { get; set; } = "";
    public List<StepDefinition> Steps { get; set; } = [];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static PipelineDefinition LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        return Deserializer.Deserialize<PipelineDefinition>(yaml)
            ?? throw new InvalidOperationException($"Failed to parse pipeline file: {path}");
    }

    public static async Task<PipelineDefinition> LoadFromFileAsync(string path, CancellationToken ct = default)
    {
        var yaml = await File.ReadAllTextAsync(path, ct);
        return Deserializer.Deserialize<PipelineDefinition>(yaml)
            ?? throw new InvalidOperationException($"Failed to parse pipeline file: {path}");
    }
}
