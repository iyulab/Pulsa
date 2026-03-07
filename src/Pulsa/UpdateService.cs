using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pulsa;

public class UpdateService(
    IOptions<UpdateOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        try
        {
            await CheckAndApplyAsync(opts, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed, continuing with current version");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CheckAndApplyAsync(UpdateOptions opts, CancellationToken ct)
    {
        var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Pulsa";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{appName}-Updater");

        var release = await FindReleaseAsync(http, opts, ct);
        if (release is null) return;

        var latestVersion = release.TagName;
        if (!string.IsNullOrEmpty(opts.TagPrefix))
            latestVersion = latestVersion[opts.TagPrefix.Length..];
        latestVersion = latestVersion.TrimStart('v');
        var currentVersion = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

        if (!Version.TryParse(latestVersion, out var latest) ||
            !Version.TryParse(currentVersion, out var current))
            return;

        if (latest <= current)
        {
            logger.LogInformation("Up to date: v{Version}", currentVersion);
            return;
        }

        logger.LogInformation("Update available: v{Current} → v{Latest}", currentVersion, latestVersion);

        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name.Equals(opts.AssetPattern, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            logger.LogWarning("No matching release asset: {Pattern}", opts.AssetPattern);
            return;
        }

        // Download
        var tempZip = Path.Combine(Path.GetTempPath(), $"{appName}-{latestVersion}.zip");
        logger.LogInformation("Downloading: {Url}", asset.DownloadUrl);
        var response = await http.GetAsync(asset.DownloadUrl, ct);
        response.EnsureSuccessStatusCode();
        await using (var fs = File.Create(tempZip))
        {
            await response.Content.CopyToAsync(fs, ct);
        }

        // Extract to staging directory
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var updateDir = Path.Combine(appDir, "_update");
        if (Directory.Exists(updateDir))
            Directory.Delete(updateDir, true);
        ZipFile.ExtractToDirectory(tempZip, updateDir);
        File.Delete(tempZip);

        // If zip contains a single sub-folder, use that as the source
        var sourceDir = updateDir;
        var subDirs = Directory.GetDirectories(updateDir);
        if (subDirs.Length == 1 && Directory.GetFiles(updateDir).Length == 0)
            sourceDir = subDirs[0];

        // Create update script (preserves appsettings*.json and logs/)
        var exePath = Environment.ProcessPath
            ?? Path.Combine(appDir, $"{appName}.exe");
        var scriptSlug = appName.ToLowerInvariant().Replace('.', '-');
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptSlug}-update.bat");
        var script = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            robocopy "{sourceDir}" "{appDir}" /s /xf appsettings.json appsettings.*.json /xd logs _update >nul
            rmdir /s /q "{updateDir}" 2>nul
            start "" "{exePath}"
            del "%~f0"
            """;

        await File.WriteAllTextAsync(scriptPath, script, ct);

        logger.LogInformation("Applying update v{Version}, restarting...", latestVersion);
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        lifetime.StopApplication();
    }

    private static async Task<GitHubRelease?> FindReleaseAsync(HttpClient http, UpdateOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(opts.TagPrefix))
        {
            var url = $"https://api.github.com/repos/{opts.Repository}/releases/latest";
            return await http.GetFromJsonAsync<GitHubRelease>(url, ct);
        }

        var listUrl = $"https://api.github.com/repos/{opts.Repository}/releases?per_page=30";
        var releases = await http.GetFromJsonAsync<List<GitHubRelease>>(listUrl, ct);
        return releases?.FirstOrDefault(r =>
            r.TagName.StartsWith(opts.TagPrefix, StringComparison.OrdinalIgnoreCase));
    }
}

internal record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; init; }
}

internal record GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; init; } = "";
}
