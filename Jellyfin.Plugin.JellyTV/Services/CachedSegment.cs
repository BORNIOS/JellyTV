using System;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// A downloaded media segment kept in memory.
/// </summary>
/// <param name="Data">Segment bytes.</param>
/// <param name="ContentType">Upstream content type.</param>
public sealed record CachedSegment(ReadOnlyMemory<byte> Data, string ContentType);
