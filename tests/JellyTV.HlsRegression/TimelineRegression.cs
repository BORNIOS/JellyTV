using System.Globalization;
using Jellyfin.Plugin.JellyTV.Services;

internal static class TimelineRegression
{
    private const string Source = "https://provider.example/live/channel.m3u8";

    internal static void Run()
    {
        var timeline = new HlsTimeline();
        var fixtures = Enumerable.Range(31, 5)
            .Select(number => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"fast-reset-{number}.m3u8")))
            .ToArray();
        var normalized = fixtures.Select(playlist => timeline.Normalize(playlist, Source)).ToArray();
        var expectedMedia = new long[] { 79, 82, 82, 83, 84 };
        var expectedDiscontinuities = new long[] { 1, 2, 2, 2, 2 };
        for (var i = 0; i < normalized.Length; i++)
        {
            Check(Counter(normalized[i], "MEDIA-SEQUENCE") == expectedMedia[i], "real FAST provider media sequence " + i);
            Check(Counter(normalized[i], "DISCONTINUITY-SEQUENCE") == expectedDiscontinuities[i], "real FAST provider discontinuity sequence " + i);
            Check(Lines(normalized[i]).Where(line => line.StartsWith("#EXT-X-KEY:")).SequenceEqual(Lines(fixtures[i]).Where(line => line.StartsWith("#EXT-X-KEY:"))), "explicit AES keys and IVs remain exact");
            Check(MediaUris(normalized[i]).SequenceEqual(MediaUris(fixtures[i])), "segment bytes stay behind their original URLs");
            if (i > 0)
            {
                Check(!normalized[i].Contains("#EXT-X-PROGRAM-DATE-TIME:"), "inconsistent provider dates omitted after restart");
            }
        }

        Check(normalized[1] == normalized[2], "same raw window with a changed date does not create another epoch");
        Check(Lines(normalized[4]).Count(line => line == "#EXT-X-DISCONTINUITY") == 1, "later source discontinuity retained");

        // The first URI also occurred earlier in the previous window: join its suffix, not its first match.
        timeline = new HlsTimeline();
        timeline.Normalize(Playlist(100, 3, ["a", "b", "c", "a", "b"], boundaryAt: 3), Source);
        var repeated = timeline.Normalize(Playlist(0, 0, ["a", "b", "d", "e", "f"]), Source);
        Check(Counter(repeated, "MEDIA-SEQUENCE") == 103 && Counter(repeated, "DISCONTINUITY-SEQUENCE") == 4, "repeated asset URLs align with the most recent suffix");
        var progressed = timeline.Normalize(Playlist(10, 0, ["g", "h", "i", "j", "k"]), Source);
        Check(Counter(progressed, "MEDIA-SEQUENCE") == 113, "epoch offset stays stable through normal refreshes");
        var secondReset = timeline.Normalize(Playlist(0, 0, ["l", "m", "n", "o", "p"]), Source);
        Check(Counter(secondReset, "MEDIA-SEQUENCE") == 118, "second restart appends after the published window");
        Check(FirstDiscontinuity(secondReset) == 5 && Lines(secondReset).Contains("#EXT-X-DISCONTINUITY"), "restart without overlap adds a timestamp boundary");
        var following = timeline.Normalize(Playlist(1, 0, ["m", "n", "o", "p", "q"]), Source);
        Check(Counter(following, "MEDIA-SEQUENCE") == 119 && FirstDiscontinuity(following) == 5, "next refresh retains the newly introduced discontinuity");

        timeline = new HlsTimeline();
        var current = timeline.Normalize(Playlist(100, 0, ["a", "b", "c", "d", "e"]), Source);
        var stale = timeline.Normalize(Playlist(99, 0, ["previous", "a", "b", "c", "d"]), Source);
        Check(stale == current, "out-of-order overlapping response cannot roll the published window back");
        Check(timeline.Normalize(Playlist(100, 0, ["a", "b", "c"]), Source) == current, "temporarily shortened response cannot roll the tail back");
        timeline = new HlsTimeline();
        current = timeline.Normalize(Playlist(100, 1, ["a", "b", "c", "d", "e"]), Source);
        Check(timeline.Normalize(Playlist(99, 0, ["previous", "a", "b", "c", "d"]), Source) == current, "delayed response with an old discontinuity counter remains stale");

        timeline = new HlsTimeline();
        timeline.Normalize(Playlist(80, 0, ["old-a", "old-b", "old-c"], implicitIv: true), Source);
        var implicitIvs = timeline.Normalize(Playlist(0, 0, ["new-a", "new-b"], implicitIv: true), Source);
        Check(Counter(implicitIvs, "MEDIA-SEQUENCE") == 83, "implicit-IV restart changes only the published counter");
        Check(SegmentIvs(implicitIvs).SequenceEqual(new[] { new string('0', 32), new string('0', 31) + "1" }), "each implicit AES IV uses its original source segment sequence");
        var implicitFollowing = timeline.Normalize(Playlist(1, 0, ["new-b", "new-c"], implicitIv: true), Source);
        Check(SegmentIvs(implicitFollowing).SequenceEqual(new[] { new string('0', 31) + "1", new string('0', 31) + "2" }), "implicit IVs remain correct on following polls");

        timeline = new HlsTimeline();
        timeline.Normalize(Ranges(50, "100@0", "100@100"), Source);
        var differentRanges = timeline.Normalize(Ranges(0, "100@200", "100@300"), Source);
        Check(Counter(differentRanges, "MEDIA-SEQUENCE") == 52, "same file with different byte ranges is not an overlap");

        timeline = new HlsTimeline();
        timeline.Normalize(Playlist(50, 0, ["shared", "tail"], key: "first.key"), Source);
        var differentKey = timeline.Normalize(Playlist(0, 0, ["shared", "tail"], key: "second.key"), Source);
        Check(Counter(differentKey, "MEDIA-SEQUENCE") == 52, "same media URI under a different encryption key is not an overlap");

        timeline = new HlsTimeline();
        timeline.Normalize(Playlist(50, 0, ["a", "b"]), Source);
        var leadingBoundary = timeline.Normalize(Playlist(0, 0, ["b", "c"], boundaryAt: 0), Source);
        Check(Counter(leadingBoundary, "DISCONTINUITY-SEQUENCE") >= 0 && Counter(leadingBoundary, "MEDIA-SEQUENCE") == 52, "leading boundary cannot produce a negative discontinuity header when matching repeated bytes");
        timeline = new HlsTimeline();
        timeline.Normalize(Playlist(100, 0, ["a", "b", "c"]), Source);
        var changedBoundaries = timeline.Normalize(Playlist(0, 0, ["b", "c", "d"], boundaryAt: 1), Source);
        Check(Counter(changedBoundaries, "MEDIA-SEQUENCE") == 103, "overlap cannot change the timestamp identity of an already published segment");

        timeline = new HlsTimeline();
        string WithoutDiscontinuityHeader(string playlist) => string.Join("\n", Lines(playlist).Where(line => !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal)));
        timeline.Normalize(WithoutDiscontinuityHeader(Playlist(100, 0, ["a", "b", "c"], boundaryAt: 1)), Source);
        var implicitDiscontinuity = timeline.Normalize(WithoutDiscontinuityHeader(Playlist(101, 0, ["b", "c", "d"])), Source);
        Check(FirstDiscontinuity(implicitDiscontinuity) == 1, "missing source discontinuity headers preserve common segment identity as boundaries leave the window");
        var implicitDiscontinuityNext = timeline.Normalize(WithoutDiscontinuityHeader(Playlist(102, 0, ["c", "d", "e"])), Source);
        Check(FirstDiscontinuity(implicitDiscontinuityNext) == 1, "implicit discontinuity remains stable on the following window");

        var vod = Playlist(0, 0, ["vod"]) + "#EXT-X-ENDLIST\n";
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nvideo.m3u8\n";
        Check(timeline.Normalize(vod, Source) == vod && timeline.Normalize(master, Source) == master, "VOD and master playlists pass through unchanged");
        Console.WriteLine("PASS: live timeline restart, real FAST provider overlap, repeated assets, multiple epochs, stale polls, PDT and AES IV preservation.");
    }

    private static string Playlist(long sequence, long discontinuity, string[] names, int boundaryAt = -1, bool implicitIv = false, string? key = null)
    {
        var output = $"#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n#EXT-X-DISCONTINUITY-SEQUENCE:{discontinuity}\n#EXT-X-PROGRAM-DATE-TIME:2026-10-22T09:36:00Z\n";
        if (implicitIv || key is not null)
        {
            output += $"#EXT-X-KEY:METHOD=AES-128,URI=\"{key ?? "program.key"}\"" + (implicitIv ? "\n" : ",IV=0x00000000000000000000000000000001\n");
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (i == boundaryAt)
            {
                output += "#EXT-X-DISCONTINUITY\n";
            }

            output += $"#EXTINF:5,\n{names[i]}.ts\n";
        }

        return output;
    }

    private static string Ranges(long sequence, params string[] ranges)
        => $"#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n" + string.Concat(ranges.Select(range => $"#EXTINF:5,\n#EXT-X-BYTERANGE:{range}\nshared.ts\n"));

    private static string[] Lines(string playlist) => playlist.Replace("\r", "", StringComparison.Ordinal).Split('\n');

    private static IEnumerable<string> MediaUris(string playlist) => Lines(playlist).Where(line => line.Length > 0 && !line.StartsWith('#'));

    private static long Counter(string playlist, string name)
        => long.Parse(Lines(playlist).Single(line => line.StartsWith("#EXT-X-" + name + ":", StringComparison.Ordinal)).Split(':', 2)[1], CultureInfo.InvariantCulture);

    private static long FirstDiscontinuity(string playlist)
    {
        var result = Counter(playlist, "DISCONTINUITY-SEQUENCE");
        foreach (var line in Lines(playlist))
        {
            if (line == "#EXT-X-DISCONTINUITY") result++;
            if (line.Length > 0 && !line.StartsWith('#')) return result;
        }

        throw new InvalidOperationException("Playlist had no segment");
    }

    private static IEnumerable<string> SegmentIvs(string playlist)
    {
        var iv = string.Empty;
        foreach (var line in Lines(playlist))
        {
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                iv = line.Contains(",IV=0x", StringComparison.Ordinal) ? line.Split(",IV=0x", 2)[1] : string.Empty;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                yield return iv;
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
