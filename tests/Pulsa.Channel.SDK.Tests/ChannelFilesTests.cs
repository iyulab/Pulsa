using System.Text;
using AwesomeAssertions;
using Xunit;
using PulsaChannel;

namespace Pulsa.Channel.SDK.Tests;

public sealed class ChannelFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pulsa-files-" + Guid.NewGuid().ToString("N"));
    private readonly string outside = Path.Combine(Path.GetTempPath(), "pulsa-outside-" + Guid.NewGuid().ToString("N"));

    public ChannelFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "reports"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "reports", "q3.csv"), "a,b\n1,2\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not for sending");
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        Directory.Delete(outside, recursive: true);
    }

    private async Task<(ChannelConnector Connector, FakeChannelProvider Provider)> StartedAsync(string? fileRoot, ChannelFileSupport? files = null)
    {
        var provider = new FakeChannelProvider(files: files);
        var connector = new ChannelConnector(provider, fileRoot: fileRoot);
        await connector.StartAsync(TestContext.Current.CancellationToken);
        return (connector, provider);
    }

    [Fact]
    public async Task A_file_under_the_folder_is_sent_with_its_name_media_type_and_content_and_the_text_as_its_caption()
    {
        var (connector, provider) = await StartedAsync(root);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "The Q3 numbers", File: "reports/q3.csv"), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeTrue();
        var file = provider.SentFiles.Should().ContainSingle().Which;
        file.Name.Should().Be("q3.csv");
        file.MediaType.Should().Be("text/csv");
        Encoding.UTF8.GetString(file.Content).Should().Be("a,b\n1,2\n");
        provider.Sent.Single().Text.Should().Be("The Q3 numbers");
    }

    [Fact]
    public async Task A_connector_given_no_folder_sends_no_files_and_sends_nothing_instead()
    {
        var (connector, provider) = await StartedAsync(null);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "caption", File: "reports/q3.csv"), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.NotSupported);
        provider.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("reports/../../secret.txt")]
    [InlineData("reports\\..\\..\\secret.txt")]
    [InlineData("/etc/hosts")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    [InlineData("reports")]
    [InlineData("reports/missing.csv")]
    [InlineData("empty.txt")]
    public async Task A_path_that_climbs_out_is_absolute_missing_a_folder_or_empty_is_refused_and_nothing_is_sent(string path)
    {
        var (connector, provider) = await StartedAsync(root);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "caption", File: path), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_to_a_file_outside_the_folder_is_refused_whether_the_file_or_a_folder_on_the_way_is_the_link()
    {
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), Path.Combine(outside, "secret.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-folder"), outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // this machine does not let the test make links
        }

        var (connector, provider) = await StartedAsync(root);

        foreach (var path in new[] { "link.txt", "linked-folder/secret.txt" })
        {
            var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "caption", File: path), TestContext.Current.CancellationToken);
            result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest, path);
        }

        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_larger_than_the_service_takes_is_refused()
    {
        var (connector, provider) = await StartedAsync(root, new ChannelFileSupport(MaxBytes: 4, MaxCaptionLength: 100));

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "caption", File: "reports/q3.csv"), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caption_with_its_link_over_the_services_caption_limit_is_refused_even_when_a_plain_message_of_that_length_would_go()
    {
        var (connector, provider) = await StartedAsync(root, new ChannelFileSupport(MaxBytes: 1000, MaxCaptionLength: 10));

        var withFile = await connector.SendAsync(new ChannelSendRequest("conversation-1", new string('a', 11), File: "reports/q3.csv"), TestContext.Current.CancellationToken);
        var plain = await connector.SendAsync(new ChannelSendRequest("conversation-1", new string('a', 11)), TestContext.Current.CancellationToken);

        withFile.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        plain.Delivered.Should().BeTrue();
        provider.SentFiles.Should().BeEmpty();
    }
}
