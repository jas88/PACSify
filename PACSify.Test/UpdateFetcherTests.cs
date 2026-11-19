using System.Collections.Concurrent;
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
        var url = $"http://127.0.0.1:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        var reports = new ConcurrentQueue<string>();
        _ = ServeAsync(listener, reports);

        UpdateFetcher.Update(url);

        // The server records an error report only after a delay, just before it answers,
        // so the report is only there now if Update waited for the answer.
        Assert.That(reports, Has.Count.EqualTo(1));
        Assert.That(reports.Single(), Does.Contain("500"));
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

    /// <summary>
    /// Answers ?op=get with a 500 so that the update fails, and records the body of each
    /// ?op=error report 300ms after receiving it, just before answering it.
    /// </summary>
    private static async Task ServeAsync(HttpListener listener, ConcurrentQueue<string> reports)
    {
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                if (context.Request.QueryString["op"] == "error")
                {
                    await Task.Delay(300);
                    reports.Enqueue(body);
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
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
