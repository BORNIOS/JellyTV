using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Live stream handle for a source that Jellyfin should open directly.
/// The media bytes remain with the final client; JellyTV does not proxy or transform them.
/// </summary>
public sealed class DirectUrlLiveStream : ILiveStream, IDisposable
{
    private readonly MediaSourceInfo _mediaSource;

    public DirectUrlLiveStream(MediaSourceInfo mediaSource, string streamId)
    {
        _mediaSource = mediaSource;
        OriginalStreamId = streamId;
        UniqueId = $"JellyTV_{mediaSource.Id}_{Guid.NewGuid():N}";
    }

    /// <inheritdoc/>
    public int ConsumerCount { get; set; }

    /// <inheritdoc/>
    public string OriginalStreamId { get; set; }

    /// <inheritdoc/>
    public string TunerHostId => "JellyTV";

    /// <inheritdoc/>
    public bool EnableStreamSharing => false;

    /// <inheritdoc/>
    public MediaSourceInfo MediaSource
    {
        get => _mediaSource;
        set => throw new NotSupportedException("The direct media source is immutable.");
    }

    /// <inheritdoc/>
    public string UniqueId { get; }

    /// <inheritdoc/>
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Close() => Task.CompletedTask;

    /// <inheritdoc/>
    public Stream GetStream() => Stream.Null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
