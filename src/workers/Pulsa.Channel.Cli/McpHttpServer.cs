using System.Net;
using System.Text;

namespace PulsaChannel.Cli;

/// <summary>
/// The "Streamable HTTP, JSON replies" transport an enclave's connector registration reaches: <c>POST /mcp</c> with one JSON-RPC
/// message, answered with the JSON reply (or 202 for a notification). A page in a browser must not be able to drive it, so a request
/// that carries an <c>Origin</c> header or is not <c>application/json</c> is refused; with a key set, a request without that bearer
/// key is refused too.
/// </summary>
public sealed class McpHttpServer(McpHandler handler, string prefix, string? bearerKey) : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string path = "/mcp";

    /// <summary>The address actually listened on (the prefix may have asked for any free port).</summary>
    public string Prefix { get; } = prefix;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        listener.Prefixes.Add(Prefix);
        listener.Start();
        using var registration = cancellationToken.Register(listener.Stop);
        var running = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            running.Add(Task.Run(() => ServeAsync(context, cancellationToken), CancellationToken.None));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath != path) { response.StatusCode = 404; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; response.AddHeader("Allow", "POST"); return; }
            if (request.Headers["Origin"] is not null) { response.StatusCode = 403; return; }
            if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { response.StatusCode = 415; return; }
            if (bearerKey is { Length: > 0 } && request.Headers["Authorization"] != "Bearer " + bearerKey) { response.StatusCode = 401; return; }

            string body;
            using (var reader = new StreamReader(request.InputStream, new UTF8Encoding(false)))
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var reply = await handler.HandleAsync(body, cancellationToken).ConfigureAwait(false);
            if (reply is null) { response.StatusCode = 202; return; }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception)
        {
            try { response.StatusCode = 500; } catch { /* the reply had already started */ }
        }
        finally
        {
            try { response.Close(); } catch { /* the client went away */ }
        }
    }

    public void Dispose() => listener.Close();
}
