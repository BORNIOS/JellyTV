using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyTV;

/// <summary>
/// Thin wrapper that exposes <see cref="Plugin"/> as an <see cref="ILiveTvService"/>
/// so Jellyfin's DI container can discover it automatically.
/// </summary>
public sealed class JellyTVLiveTvService : ILiveTvService
{
    private static PluginConfiguration Config => Plugin.Instance?.Configuration!;

    /// <inheritdoc />
    public string Name => "JellyTV";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/BORNIOS/JellyTV";

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var list = Config.Channels
            .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Url))
            .Select((c, i) => new ChannelInfo
            {
                Id = c.Id,
                Name = c.Name,
                Number = (i + 1).ToString(),
                ImageUrl = c.LogoUrl,
                ChannelType = ChannelType.TV
            });

        var result = list.ToList();
        PluginLog.Info($"[LiveTV] GetChannelsAsync returned {result.Count} channel(s)");
        return Task.FromResult<IEnumerable<ChannelInfo>>(result);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var ch = Config.Channels.FirstOrDefault(c => c.Id == channelId);
        var url = ch?.Url ?? "http://localhost";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var baseUrl = Plugin.Instance?.KnownBaseUrl ?? "http://localhost:8096";
        return Task.FromResult(new MediaSourceInfo
        {
            Id = channelId,
            Path = $"{baseUrl}/JellyTV/stream?url={encoded}",
            Protocol = MediaProtocol.Http,
            Container = "hls",
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            SupportsTranscoding = false,
            SupportsProbing = false,
            IsInfiniteStream = true,
            ReadAtNativeFramerate = false,
            RequiresOpening = true,
            RequiresClosing = true,
            MediaStreams =
            [
                new() { Type = MediaStreamType.Video, Index = -1, Codec = "h264", IsInterlaced = false },
                new() { Type = MediaStreamType.Audio, Index = -1, Codec = "aac" }
            ]
        });
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var ch = Config.Channels.FirstOrDefault(c => c.Id == channelId)
            ?? throw new KeyNotFoundException($"Channel '{channelId}' not found.");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(ch.Url))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var baseUrl = Plugin.Instance?.KnownBaseUrl ?? "http://localhost:8096";
        // Type 2/3 (token) siempre se sirven via HLS; directo/FAST usan el contenedor natural.
        var isTs = ch.Type is 0 or 1 && ch.Url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new List<MediaSourceInfo>
        {
            new()
            {
                Id = channelId,
                Path = $"{baseUrl}/JellyTV/stream?url={encoded}",
                Protocol = MediaProtocol.Http,
                Container = isTs ? "ts" : "hls",
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
                SupportsTranscoding = false,
                SupportsProbing = false,
                AnalyzeDurationMs = 0,
                IsInfiniteStream = true,
                ReadAtNativeFramerate = true,
                IgnoreDts = true,
                RequiresOpening = true,
                RequiresClosing = true,
                RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
                },
                MediaStreams =
                [
                    new()
                    {
                        Type = MediaStreamType.Video, Index = -1, Codec = "h264", IsInterlaced = false,
                        Width = 1280, Height = 720, AverageFrameRate = 29.97003f, RealFrameRate = 29.97003f,
                        Profile = "Main", Level = 40, PixelFormat = "yuv420p", BitRate = 8000000, BitDepth = 8,
                        ColorRange = "tv", ColorSpace = "bt709", ColorTransfer = "bt709", ColorPrimaries = "bt709",
                        RefFrames = 1, NalLengthSize = "0"
                    },
                    new()
                    {
                        Type = MediaStreamType.Audio, Index = -1, Codec = "aac",
                        Channels = 2, SampleRate = 48000, Profile = "LC", BitRate = 96000,
                        ChannelLayout = "stereo"
                    }
                ]
            }
        });
    }

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<ProgramInfo>());

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<TimerInfo>());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null) => Task.FromResult(new SeriesTimerInfo());

    /// <inheritdoc />
    public Task<TimerInfo> GetDefaultTimer(CancellationToken cancellationToken) => Task.FromResult(new TimerInfo());

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location) => Task.FromResult(new List<NameIdPair>());

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
}
