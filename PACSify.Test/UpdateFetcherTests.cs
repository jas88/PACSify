using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace PACSify.Test;

public class UpdateFetcherTests
{
    [Test]
    public void UpdateWaitsForTheErrorReportToBeSent()
    {
        using var server = new UpdateServer(HttpStatusCode.InternalServerError, []);

        UpdateFetcher.Update(server.Url);

        // The server logs a report only once it is about to answer it, after a delay,
        // so the report is only there now if Update waited for the answer.
        Assert.That(server.Ops, Is.EqualTo("get error"));
        Assert.That(server.Requests.Last().Body, Does.Contain("500"));
    }

    [Test]
    public void UpdateExtractsTheUpdateAndWaitsForTheSuccessReport()
    {
        using var server = new UpdateServer(HttpStatusCode.OK, TarWith("readme.txt", "updated"));
        var workDir = Directory.CreateTempSubdirectory("PACSify.Test-");
        var originalDir = Environment.CurrentDirectory;
        try
        {
            // Update extracts the update into the current directory
            Environment.CurrentDirectory = workDir.FullName;
            UpdateFetcher.Update(server.Url);
            Environment.CurrentDirectory = originalDir;

            Assert.That(File.ReadAllText(Path.Combine(workDir.FullName, "readme.txt")), Is.EqualTo("updated"));
            Assert.That(server.Ops, Is.EqualTo("get success"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            workDir.Delete(true);
        }
    }

    [Test]
    public void UpdateSendsNoReportWhenNoUpdateIsPending()
    {
        // The update server answers 404 when it has no update for this client
        using var server = new UpdateServer(HttpStatusCode.NotFound, []);

        UpdateFetcher.Update(server.Url);

        // Update waits for any report it sends, so an error report would already be logged
        Assert.That(server.Ops, Is.EqualTo("get"));
    }

    [Test]
    public void UpdateDoesNotThrowWhenTheUpdateServerCannotBeReached()
    {
        // Nothing listens on this port, so both the update and its error report fail to connect.
        Assert.DoesNotThrow(() => UpdateFetcher.Update($"http://127.0.0.1:{FreePort()}/"));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static byte[] TarWith(string name, string content)
    {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Ustar, leaveOpen: true))
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
        }
        return archive.ToArray();
    }

    /// <summary>
    /// A stand-in for the update server on a free loopback port. It answers ?op=get with the given
    /// status and body, and logs every request's op and body in order. It logs a report (any other
    /// op) only 300ms after receiving it, just before answering it, so a report is in
    /// <see cref="Requests"/> only if the client waited for the answer.
    /// </summary>
    private sealed class UpdateServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public UpdateServer(HttpStatusCode getStatus, byte[] getBody)
        {
            Url = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync(getStatus, getBody);
        }

        public string Url { get; }

        public ConcurrentQueue<(string Op, string Body)> Requests { get; } = new();

        /// <summary>
        /// The op of each request in <see cref="Requests"/>, separated by spaces.
        /// </summary>
        public string Ops => string.Join(' ', Requests.Select(static r => r.Op));

        public void Dispose() => ((IDisposable)_listener).Dispose();

        private async Task ServeAsync(HttpStatusCode getStatus, byte[] getBody)
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();
                    var op = context.Request.QueryString["op"] ?? string.Empty;
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    if (op == "get")
                    {
                        Requests.Enqueue((op, body));
                        context.Response.StatusCode = (int)getStatus;
                        context.Response.ContentLength64 = getBody.Length;
                        await context.Response.OutputStream.WriteAsync(getBody);
                    }
                    else
                    {
                        await Task.Delay(300);
                        Requests.Enqueue((op, body));
                    }
                    context.Response.Close();
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener was closed at the end of the test.
            }
        }
    }
}
