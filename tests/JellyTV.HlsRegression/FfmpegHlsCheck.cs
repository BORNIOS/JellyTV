using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyTV.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class FfmpegHlsCheck
{
    public static async Task RunAsync(string ffmpeg, string ffprobe)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "hls-fixture");
        Directory.CreateDirectory(directory);
        var segment = Path.Combine(directory, "video.ts");
        await RunAsync(ffmpeg, "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=160x90:r=10", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "mpegts", segment);
        var media = await File.ReadAllBytesAsync(segment);
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var origin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var shutdown = new CancellationTokenSource();
        var relay = new HlsRewriterService(null!, null!, null!, null!, NullLogger<HlsRewriterService>.Instance);
        const string session = "ffmpeg-regression";
        var fixtureMaster = "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"Spanish\",LANGUAGE=\"es\",URI=\"subtitles.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=300000,SUBTITLES=\"s\"\nvideo.m3u8\n";
        var subtitleRequests = 0;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    var url = context.Request.Url!;
                    var broken = url.AbsolutePath.Contains("broken", StringComparison.Ordinal);
                    var target = url.AbsolutePath;
                    if (target.StartsWith("/JellyTV/segment.", StringComparison.Ordinal))
                    {
                        target = new Uri(relay.DecodeRelayUrl(session, context.Request.QueryString["url"]!)).AbsolutePath;
                    }

                    if (target.EndsWith("subtitles.m3u8", StringComparison.Ordinal) || target.EndsWith(".vtt", StringComparison.Ordinal) || target.EndsWith("bad-subtitle.ts", StringComparison.Ordinal))
                        Interlocked.Increment(ref subtitleRequests);
                    byte[] data;
                    if (target.EndsWith(".ts", StringComparison.Ordinal) && !target.EndsWith("bad-subtitle.ts", StringComparison.Ordinal))
                    {
                        data = media;
                        context.Response.ContentType = "video/mp2t";
                    }
                    else if (target.EndsWith(".vtt", StringComparison.Ordinal) || target.EndsWith("bad-subtitle.ts", StringComparison.Ordinal))
                    {
                        data = Encoding.UTF8.GetBytes("WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:0\n\n00:00:00.000 --> 00:00:01.500\nSubtitle regression\n");
                        context.Response.ContentType = "text/vtt";
                    }
                    else
                    {
                        var content = target.EndsWith("subtitles.m3u8", StringComparison.Ordinal)
                            ? "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nes.m3u8_0000000063.vtt\n#EXT-X-ENDLIST\n"
                            : target.EndsWith("video.m3u8", StringComparison.Ordinal)
                                ? "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nvideo.ts\n#EXT-X-ENDLIST\n"
                                : fixtureMaster;
                        if (broken)
                        {
                            // Old relay fixture: publish VTT with .ts and preserve subtitle declarations.
                            content = content.Replace("subtitles.m3u8", origin + "/broken-subtitles.m3u8", StringComparison.Ordinal)
                                .Replace("video.m3u8", origin + "/broken-video.m3u8", StringComparison.Ordinal)
                                .Replace("es.m3u8_0000000063.vtt", origin + "/bad-subtitle.ts", StringComparison.Ordinal)
                                .Replace("\nvideo.ts", "\n" + origin + "/video.ts", StringComparison.Ordinal);
                        }
                        else
                        {
                            content = relay.RewritePlaylistContent(content, origin + target, session, origin + "/JellyTV");
                        }
                        data = Encoding.UTF8.GetBytes(content);
                        context.Response.ContentType = "application/vnd.apple.mpegurl";
                    }

                    context.Response.ContentLength64 = data.Length;
                    await context.Response.OutputStream.WriteAsync(data);
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        });
        try
        {
            var broken = await RunAsync(ffprobe, "-v", "error", "-show_streams", "-of", "json", origin + "/broken.m3u8");
            if (broken.ExitCode == 0 || !broken.Error.Contains("mismatches allowed extensions", StringComparison.Ordinal))
                throw new InvalidOperationException("Old .ts relay did not reproduce the server failure: " + broken.Error);
            Console.WriteLine("PASS: actual FFmpeg reproduces the WebVTT/.ts mismatch.");
            Interlocked.Exchange(ref subtitleRequests, 0);
            var fixedResult = await RunAsync(ffprobe, "-v", "error", "-show_streams", "-of", "json", origin + "/fixed.m3u8");
            if (fixedResult.ExitCode != 0) throw new InvalidOperationException("Rewritten HLS failed FFmpeg: " + fixedResult.Error);
            using var document = JsonDocument.Parse(fixedResult.Output);
            var codecs = document.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString()).ToArray();
            if (!new[] { "h264", "aac" }.All(codecs.Contains) || codecs.Contains("webvtt") || Volatile.Read(ref subtitleRequests) != 0)
                throw new InvalidOperationException("Video/audio missing or subtitle track was fetched: " + fixedResult.Output);
            var decoded = await RunAsync(ffmpeg, "-hide_banner", "-v", "error", "-i", origin + "/fixed.m3u8", "-map", "0:v", "-map", "0:a", "-f", "null", "-");
            if (decoded.ExitCode != 0 || Volatile.Read(ref subtitleRequests) != 0)
                throw new InvalidOperationException("FFmpeg decoding failed or fetched subtitles: " + decoded.Error);
            Console.WriteLine("PASS: actual FFmpeg probes and decodes H264/AAC without fetching subtitle playlists or VTT segments.");
        }
        finally
        {
            shutdown.Cancel();
            listener.Stop();
            await serving;
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
