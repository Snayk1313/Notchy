using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Velopack.Sources;

namespace Notchy;

/// <summary>
/// Every connection Notchy makes. .NET tries a server's addresses one by one, IPv6 first, waiting for each: where IPv6
/// is slow or half working, the first request of each service took over 30 s and often gave up. Here IPv4 goes first
/// and no address gets long.
/// </summary>
internal static class FastHttp
{
    private const int ConnectTimeoutSeconds = 5;

    public static HttpClient Client(TimeSpan timeout) => new(new SocketsHttpHandler { ConnectCallback = ConnectIpv4FirstAsync }) { Timeout = timeout };

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                attempt.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                socket.Dispose(); // the next address
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("No address answered");
    }

#if DEBUG
    /// <summary>Test: how long the first request to each service takes, the old way and this way (each from a cold start).</summary>
    public static async Task DiagnoseAsync(string file)
    {
        string[] urls =
        {
            "https://api.open-meteo.com/v1/forecast?latitude=-8&longitude=-35&current=temperature_2m",
            "https://geocoding-api.open-meteo.com/v1/search?name=Recife&count=1",
            "https://api.spotify.com/v1/",
            "https://lrclib.net/api/search?q=teste",
            "https://api.github.com/repos/Snayk1313/Notchy/releases/latest"
        };
        var log = new System.Text.StringBuilder();
        foreach (string url in urls)
            foreach (var (name, make) in new (string, Func<HttpClient>)[]
                     {
                         ("antes", () => new HttpClient { Timeout = TimeSpan.FromSeconds(12) }),
                         ("agora", () => Client(TimeSpan.FromSeconds(12)))
                     })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var client = make();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy-test");
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    log.AppendLine($"{new Uri(url).Host} {name}: {(int)response.StatusCode} em {clock.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"{new Uri(url).Host} {name}: {ex.GetType().Name} apos {clock.ElapsedMilliseconds} ms");
                }
            }
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var downloader = new FastUpdateDownloader();
            string json = await downloader.DownloadString("https://api.github.com/repos/Snayk1313/Notchy/releases/latest", null, 1);
            log.AppendLine($"atualizador: {json.Length} caracteres em {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            log.AppendLine($"atualizador: {ex.GetType().Name} {ex.Message}");
        }
        File.WriteAllText(file, log.ToString());
    }
#endif
}

/// <summary>The updater's downloads, over the same connections.</summary>
internal sealed class FastUpdateDownloader : HttpClientFileDownloader
{
    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers = null, double timeout = 30)
    {
        var client = FastHttp.Client(TimeSpan.FromMinutes(timeout));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        return client;
    }
}
