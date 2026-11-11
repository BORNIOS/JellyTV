using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Keeps a live rendition continuous when its provider restarts the playlist counters.
/// </summary>
internal sealed class HlsTimeline
{
    private readonly object _gate = new();
    private Segment[] _previous = [];
    private long _lastRawStart;
    private long _lastRawEnd;
    private long _lastRawDiscontinuity;
    private long _sequenceOffset;
    private long _discontinuityOffset;
    private bool _omitProgramDateTime;
    private string? _lastOutput;

    public string Normalize(string content, string sourceUrl)
    {
        lock (_gate)
        {
            return NormalizeLocked(content, sourceUrl);
        }
    }

    private string NormalizeLocked(string content, string sourceUrl)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)
            || line.Equals("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.OrdinalIgnoreCase)))
        {
            return content;
        }

        var sequenceLine = Array.FindIndex(lines, line => line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase));
        if (sequenceLine < 0 || !TryCounter(lines[sequenceLine], out var rawStart))
        {
            return content;
        }

        var discontinuityLine = Array.FindIndex(lines, line => line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.OrdinalIgnoreCase));
        var rawDiscontinuity = 0L;
        if (discontinuityLine >= 0 && !TryCounter(lines[discontinuityLine], out rawDiscontinuity))
        {
            return content;
        }

        var segments = ParseSegments(lines, sourceUrl, rawStart, rawDiscontinuity);
        if (segments.Count == 0)
        {
            return content;
        }

        var rawEnd = segments[^1].Sequence;
        var addBoundary = false;
        if (_previous.Length > 0)
        {
            // A delayed response may still include the old discontinuity counter within our live window.
            if (rawStart < _lastRawStart && rawEnd >= _lastRawStart && rawEnd <= _lastRawEnd)
            {
                return _lastOutput!;
            }

            var reset = rawDiscontinuity < _lastRawDiscontinuity
                || (rawStart < _lastRawStart && rawEnd < _lastRawStart);
            if (!reset && (rawStart < _lastRawStart || (rawStart == _lastRawStart && rawEnd <= _lastRawEnd)))
            {
                return _lastOutput!;
            }

            if (!reset && discontinuityLine < 0)
            {
                // Infer omitted discontinuity counters from a segment shared with the previous window.
                var inferred = _previous[^1].Discontinuity;
                foreach (var previous in _previous)
                {
                    var index = previous.Sequence - rawStart - _sequenceOffset;
                    if (index >= 0 && index < segments.Count
                        && string.Equals(previous.Identity, segments[(int)index].Identity, StringComparison.Ordinal))
                    {
                        inferred = previous.Discontinuity - segments[(int)index].Discontinuity;
                        break;
                    }
                }

                _discontinuityOffset = Math.Max(0, inferred);
            }

            if (reset)
            {
                var overlap = FindOverlap(_previous, segments);
                if (overlap > 0 && rawDiscontinuity + _previous[^overlap].Discontinuity - segments[0].Discontinuity < 0)
                {
                    overlap = 0;
                }

                if (overlap > 0)
                {
                    var matched = _previous[^overlap];
                    _sequenceOffset = matched.Sequence - rawStart;
                    _discontinuityOffset = matched.Discontinuity - segments[0].Discontinuity;
                }
                else
                {
                    // There is no shared segment to join: begin a fresh timestamp epoch after the old one.
                    _sequenceOffset = _previous[^1].Sequence + 1 - rawStart;
                    var leadingBoundaries = segments[0].Discontinuity - rawDiscontinuity;
                    var nextDiscontinuity = Math.Max(_previous[^1].Discontinuity + 1, leadingBoundaries);
                    _discontinuityOffset = nextDiscontinuity - segments[0].Discontinuity;
                    addBoundary = leadingBoundaries == 0;
                }

                // The observed restart also changes PDT for identical segments; retain the media clock instead.
                _omitProgramDateTime = true;
                PluginLog.Warn($"[HLS] Timeline reset: media {_lastRawStart}->{rawStart}, published={rawStart + _sequenceOffset}; discontinuity {_lastRawDiscontinuity}->{rawDiscontinuity}, published={segments[0].Discontinuity + _discontinuityOffset}; overlap={overlap}");
            }
        }

        var output = new StringBuilder(content.Length + 256);
        var nextSegment = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == sequenceLine)
            {
                line = "#EXT-X-MEDIA-SEQUENCE:" + (rawStart + _sequenceOffset).ToString(CultureInfo.InvariantCulture);
                if (discontinuityLine < 0)
                {
                    output.AppendLine("#EXT-X-DISCONTINUITY-SEQUENCE:" + (rawDiscontinuity + _discontinuityOffset - (addBoundary ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (i == discontinuityLine)
            {
                line = "#EXT-X-DISCONTINUITY-SEQUENCE:" + (rawDiscontinuity + _discontinuityOffset - (addBoundary ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
            }
            else if (_omitProgramDateTime && line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (nextSegment < segments.Count && segments[nextSegment].Line == i)
            {
                var segment = segments[nextSegment];
                if (addBoundary && nextSegment == 0)
                {
                    output.AppendLine("#EXT-X-DISCONTINUITY");
                }

                // AES-128 without IV derives it from the provider's counter, never our published counter.
                if (_sequenceOffset != 0 && segment.ImplicitIvKey is { } key)
                {
                    output.AppendLine(key + ",IV=0x" + segment.Sequence.ToString("x32", CultureInfo.InvariantCulture));
                }

                nextSegment++;
            }

            output.Append(line);
            if (i < lines.Length - 1)
            {
                output.AppendLine();
            }
        }

        _lastRawStart = rawStart;
        _lastRawEnd = rawEnd;
        _lastRawDiscontinuity = rawDiscontinuity;
        _previous = segments.TakeLast(128)
            .Select(segment => segment with { Sequence = segment.Sequence + _sequenceOffset, Discontinuity = segment.Discontinuity + _discontinuityOffset })
            .ToArray();
        _lastOutput = output.ToString();
        return _lastOutput;
    }

    private static List<Segment> ParseSegments(string[] lines, string sourceUrl, long sequence, long discontinuity)
    {
        var result = new List<Segment>();
        string? key = null;
        string? map = null;
        string? range = null;
        string? previousUri = null;
        long? previousRangeEnd = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
            {
                discontinuity++;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                key = line;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                map = line;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                range = line["#EXT-X-BYTERANGE:".Length..];
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                var uri = new Uri(new Uri(sourceUrl), line).AbsoluteUri;
                var effectiveRange = range ?? string.Empty;
                if (range is not null)
                {
                    var parts = range.Split('@', 2);
                    var offset = parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var explicitOffset)
                        ? explicitOffset
                        : previousUri == uri ? previousRangeEnd : null;
                    if (offset is { } start && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    {
                        effectiveRange = length.ToString(CultureInfo.InvariantCulture) + "@" + start.ToString(CultureInfo.InvariantCulture);
                        previousRangeEnd = start + length;
                    }
                    else
                    {
                        previousRangeEnd = null;
                    }
                }
                else
                {
                    previousRangeEnd = null;
                }

                var implicitIv = key is not null && Attribute(key, "METHOD").Equals("AES-128", StringComparison.OrdinalIgnoreCase) && Attribute(key, "IV").Length == 0;
                var keyUri = key is null ? string.Empty : Attribute(key, "URI");
                var keyIdentity = key is null ? string.Empty : Attribute(key, "METHOD") + "|" + (keyUri.Length == 0 ? string.Empty : new Uri(new Uri(sourceUrl), keyUri).AbsoluteUri)
                    + "|" + (implicitIv ? sequence.ToString("x32", CultureInfo.InvariantCulture) : Attribute(key, "IV").ToLowerInvariant()) + "|" + Attribute(key, "KEYFORMAT");
                result.Add(new Segment(i, uri + "\n" + effectiveRange + "\n" + keyIdentity + "\n" + map, sequence++, discontinuity, implicitIv ? key : null));
                previousUri = uri;
                range = null;
            }
        }

        return result;
    }

    private static int FindOverlap(Segment[] previous, List<Segment> next)
    {
        for (var count = Math.Min(previous.Length, next.Count); count > 0; count--)
        {
            var matches = true;
            var discontinuityDelta = previous[previous.Length - count].Discontinuity - next[0].Discontinuity;
            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(previous[previous.Length - count + i].Identity, next[i].Identity, StringComparison.Ordinal)
                    || previous[previous.Length - count + i].Discontinuity - next[i].Discontinuity != discontinuityDelta)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return count;
            }
        }

        return 0;
    }

    private static bool TryCounter(string line, out long value)
        => long.TryParse(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;

    private static string Attribute(string line, string name)
    {
        var match = Regex.Match(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..], "(?:^|,)\\s*" + name + "=(?:\"([^\"]*)\"|([^,]*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim() : string.Empty;
    }

    private sealed record Segment(int Line, string Identity, long Sequence, long Discontinuity, string? ImplicitIvKey);
}
