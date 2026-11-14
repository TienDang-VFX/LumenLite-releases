using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LumenLite;

public sealed record SpeedResult(double PingMs, double DownloadMbps, double UploadMbps);

/// <summary>
/// Measures latency, download and upload against Cloudflare's public speed-test
/// endpoints (the ones speed.cloudflare.com uses). Runs only when asked.
/// </summary>
internal static class SpeedTest
{
    private const string Base = "https://speed.cloudflare.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<SpeedResult> RunAsync(IProgress<string> progress, CancellationToken cancel)
    {
        progress.Report(L.T("Đang đo độ trễ…"));
        var pings = new double[5];
        for (var i = 0; i < pings.Length; i++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await Http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancel);
            pings[i] = clock.Elapsed.TotalMilliseconds;
        }
        Array.Sort(pings);
        var ping = pings[pings.Length / 2];

        progress.Report(L.T("Đang đo tốc độ tải xuống…"));
        var download = await DownloadAsync(25_000_000, cancel);
        if (download < 40)
        {
            // Fast lines finish 25 MB too quickly to measure well; a larger file evens it out.
            progress.Report(L.T("Đang đo tốc độ tải xuống…"));
            download = Math.Max(download, await DownloadAsync(100_000_000, cancel));
        }

        progress.Report(L.T("Đang đo tốc độ tải lên…"));
        var upload = await UploadAsync(10_000_000, cancel);
        return new SpeedResult(ping, download, upload);
    }

    private static async Task<double> DownloadAsync(long bytes, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        using var response = await Http.GetAsync($"{Base}/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
            total += read;
        return total * 8 / clock.Elapsed.TotalSeconds / 1_000_000;
    }

    private static async Task<double> UploadAsync(int bytes, CancellationToken cancel)
    {
        var payload = new byte[bytes];
        Random.Shared.NextBytes(payload);
        var clock = Stopwatch.StartNew();
        using var response = await Http.PostAsync($"{Base}/__up", new ByteArrayContent(payload), cancel);
        response.EnsureSuccessStatusCode();
        return bytes * 8.0 / clock.Elapsed.TotalSeconds / 1_000_000;
    }
}
