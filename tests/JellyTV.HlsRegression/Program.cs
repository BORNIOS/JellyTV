using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTV.Services;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length >= 2)
{
    await FfmpegHlsCheck.RunAsync(args[0], args[1]);
    if (args.Contains("--ffmpeg-only")) return;
}

TimelineRegression.Run();
AutoGuideRegression.Run();
DvrWindowRegression.Run();
await GuideIdentityRegression.RunAsync();
await ProviderRegression.RunAsync();
I18nRegression.Run();
if (args.Contains("--live")) { await AutoGuideLiveCheck.RunAsync(); await ProviderRegression.LiveCheckAsync(); }

var relay = new HlsRewriterService(null!, null!, null!, null!, NullLogger<HlsRewriterService>.Instance);
const string session = "regression-session";
const string source = "https://provider.example/live/master.m3u8";
const string proxy = "https://relay.example/JellyTV";
var token = new string('x', 5000);
var video = $"https://provider.example/live/video.m3u8?jwt={token}&signature=video";
var audio = $"https://provider.example/live/audio.m3u8?jwt={token}&signature=audio";
var subtitles = $"https://provider.example/live/subtitle.m3u8?jwt={token}&signature=subtitle";
var master = $"#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",LANGUAGE=\"es\",NAME=\"Spanish\",URI=\"{audio}\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",LANGUAGE=\"es\",NAME=\"Spanish\",URI=\"{subtitles}\"\n#EXT-X-STREAM-INF:BANDWIDTH=2294080,AUDIO=\"a\",SUBTITLES=\"s\"\n{video}\n";
var rewritten = relay.RewritePlaylistContent(master, source, session, proxy);
Check(!rewritten.Contains("#EXT-X-START"), "master must remain a master");
Check(rewritten.Contains("TYPE=AUDIO") && !rewritten.Contains("SUBTITLES"), "keep audio and remove subtitle declarations and references");
var urls = Regex.Matches(rewritten, "https://relay[^\\\"\\s]+").Select(m => m.Value).ToArray();
Check(urls.Length == 2, "all master URLs rewritten");
for (var i = 0; i < urls.Length; i++)
{
    Check(urls[i].Length < 1800, "short HLS line");
    var encoded = System.Web.HttpUtility.ParseQueryString(new Uri(urls[i]).Query)["url"]!;
    Check(relay.DecodeRelayUrl(session, encoded) == new[] { audio, video }[i], "exact signed URL round trip");
    Reject(() => relay.DecodeRelayUrl("other-session", encoded), "reference belongs to its session");
}

var segment = $"https://provider.example/live/segment.ts?jwt={token}";
var media = $"#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=AES-128,URI=\"{segment}&key=1\"\n#EXT-X-MAP:URI=\"{segment}&init=1\"\n#EXTINF:6,\n{segment}\n";
var rewrittenMedia = relay.RewritePlaylistContent(media, source, session, proxy);
foreach (Match match in Regex.Matches(rewrittenMedia, "https://relay[^\\\"\\s]+"))
{
    var encoded = System.Web.HttpUtility.ParseQueryString(new Uri(match.Value).Query)["url"]!;
    Check(relay.DecodeRelayUrl(session, encoded).StartsWith(segment, StringComparison.Ordinal), "media, map and key round trip");
    Check(match.Value.Length < 1800, "media, map and key are compact");
}

Check(!rewrittenMedia.Contains("#EXT-X-START"), "relay must not force a near-edge live start");
var providerStart = "#EXTM3U\n#EXT-X-START:TIME-OFFSET=-18,PRECISE=NO\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nsegment.ts\n";
var preservedStart = relay.RewritePlaylistContent(providerStart, source, session, proxy);
Check(preservedStart.Contains("#EXT-X-START:TIME-OFFSET=-18,PRECISE=NO"), "provider live-start preference preserved");
Check(preservedStart.Split("#EXT-X-START:", StringSplitOptions.None).Length == 2, "do not duplicate live-start preferences");

var legacy = relay.RewritePlaylistContent("#EXTM3U\n#EXTINF:6,\nsegment.ts\n", source, session, proxy);
var legacyUrl = Regex.Match(legacy, "https://relay[^\\\"\\s]+").Value;
var legacyValue = System.Web.HttpUtility.ParseQueryString(new Uri(legacyUrl).Query)["url"]!;
Check(!legacyValue.StartsWith("ref.", StringComparison.Ordinal), "short legacy URLs unchanged");
Check(relay.DecodeRelayUrl(session, legacyValue) == "https://provider.example/live/segment.ts", "legacy Base64 still works");
Reject(() => relay.DecodeRelayUrl(session, "ref.unknown"), "missing references fail closed");
Check(relay.RewritePlaylistContent(master, source, session, proxy) == rewritten, "refreshes reuse references");

// FAST provider names subtitle segments "es.m3u8_0000000063.vtt"; the final extension is what matters.
foreach (var subtitlePath in new[] { "es.m3u8_0000000063.vtt", "captions.VTT", "captions.webvtt" })
{
    foreach (var query in new[] { "?signature=short", $"?jwt={token}" })
    {
        var subtitleUrl = $"https://provider.example/live/{subtitlePath}{query}";
        var playlist = $"#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\n{subtitleUrl}\n";
        var output = relay.RewritePlaylistContent(playlist, source, session, proxy);
        var rewrittenUrl = Regex.Match(output, "https://relay[^\\\"\\s]+").Value;
        Check(new Uri(rewrittenUrl).AbsolutePath.EndsWith("/segment.vtt", StringComparison.Ordinal), "WebVTT keeps a compatible extension");
        var value = System.Web.HttpUtility.ParseQueryString(new Uri(rewrittenUrl).Query)["url"]!;
        Check(relay.DecodeRelayUrl(session, value) == subtitleUrl, "WebVTT signed URL is unchanged");
    }
}

// A subtitles-only master must not force a reduced master when audio is already muxed.
var reduce = typeof(HlsRewriterService).GetMethod("TryBuildReducedMasterForDemuxedTracks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
object?[] reducedArgs = [master, source, video, null];
Check((bool)reduce.Invoke(null, reducedArgs)!, "separate audio keeps a reduced master");
var subtitlesOnly = string.Join("\n", master.Split('\n').Where(l => !l.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal))).Replace(",AUDIO=\"a\"", "", StringComparison.Ordinal);
object?[] subtitlesOnlyArgs = [subtitlesOnly, source, video, null];
Check(!(bool)reduce.Invoke(null, subtitlesOnlyArgs)!, "subtitles alone do not prevent media collapse");
Check(!relay.RewritePlaylistContent(subtitlesOnly, source, session, proxy).Contains("SUBTITLES"), "nested masters also omit subtitles");

// Cleaning ad markers must preserve timeline boundaries and changes in encryption.
var transition = "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-DISCONTINUITY-SEQUENCE:4\n#EXT-X-MEDIA-SEQUENCE:170\n#EXT-X-CUE-OUT:5\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:5,\nad.ts\n#EXT-X-DISCONTINUITY\n#EXT-X-CUE-IN\n#EXT-X-KEY:METHOD=AES-128,URI=\"program.key\",IV=0x0000000000000000000000000000003f\n#EXTINF:5,\nprogram.ts\n";
var cleanedTransition = relay.RewritePlaylistContent(transition, source, session, proxy);
foreach (var directive in new[] { "#EXT-X-DISCONTINUITY-SEQUENCE:4", "#EXT-X-MEDIA-SEQUENCE:170", "#EXT-X-DISCONTINUITY\n", "#EXT-X-KEY:METHOD=NONE", "IV=0x0000000000000000000000000000003f" })
    Check(cleanedTransition.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(directive, StringComparison.Ordinal), "cleaning preserves timeline and encryption: " + directive);
Check(!cleanedTransition.Contains("#EXT-X-CUE-"), "ad markers removed independently of discontinuities");

// Ephemeral-token callers can still request their buffered start explicitly.
var rewriteMethod = typeof(HlsRewriterService).GetMethod("RewritePlaylist", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
var resolved = new Jellyfin.Plugin.JellyTV.Models.ResolvedStream { BaseUrl = "https://provider.example/live/" };
var ephemeralOutput = (string)rewriteMethod.Invoke(relay, ["#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nsegment.ts\n", resolved, session, proxy, 18d])!;
Check(ephemeralOutput.Contains("#EXT-X-START:TIME-OFFSET=-18.0,PRECISE=YES"), "ephemeral buffered start remains supported");
var explicitWithProvider = (string)rewriteMethod.Invoke(relay, [providerStart, resolved, session, proxy, 24d])!;
Check(explicitWithProvider.Split("#EXT-X-START:", StringSplitOptions.None).Length == 2 && explicitWithProvider.Contains("TIME-OFFSET=-18,"), "provider preference wins over relay preference");

// Replay the anonymized manifests actually captured from FAST provider, through the production relay.
var fastSession = new Jellyfin.Plugin.JellyTV.Models.StreamSession { ChannelType = Jellyfin.Plugin.JellyTV.Models.ChannelEntry.FastType };
long? previousPublishedSequence = null;
var expectedSequences = new long[] { 79, 82, 82, 83, 84 };
for (var capture = 31; capture <= 35; capture++)
{
    var captured = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"fast-reset-{capture}.m3u8"));
    var output = relay.RewritePlaylistContent(captured, source, fastSession.SessionId, proxy, fastSession);
    var sequence = long.Parse(Regex.Match(output, @"(?m)^#EXT-X-MEDIA-SEQUENCE:(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var segmentCount = output.Split('\n').Count(line => line.Trim().Length > 0 && !line.StartsWith('#'));
    Check(sequence == expectedSequences[capture - 31], $"captured reset normalized: {capture} -> {sequence}");
    Check(previousPublishedSequence is null || sequence + segmentCount >= previousPublishedSequence.Value, "Media3 must not see a playlist reset");
    if (capture > 31) Check(!output.Contains("#EXT-X-PROGRAM-DATE-TIME:"), "inconsistent provider dates omitted after reset");
    previousPublishedSequence = sequence;
}
var resetFixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fast-reset-32.m3u8"));
var independentAudio = relay.RewritePlaylistContent(resetFixture, "https://provider.example/audio.m3u8", fastSession.SessionId, proxy, fastSession);
Check(independentAudio.Contains("#EXT-X-MEDIA-SEQUENCE:0"), "renditions keep independent timeline state");
var tokenSession = new Jellyfin.Plugin.JellyTV.Models.StreamSession { ChannelType = Jellyfin.Plugin.JellyTV.Models.ChannelEntry.TokenizedType };
var tokenOutput = relay.RewritePlaylistContent(resetFixture, source, tokenSession.SessionId, proxy, tokenSession);
Check(tokenOutput.Contains("#EXT-X-MEDIA-SEQUENCE:0"), "FAST continuity changes do not alter tokenized delivery");
Console.WriteLine("PASS: real FAST provider reset 79/1 -> 0/0 stays continuous through the FAST relay (79,82,82,83,84), renditions isolated.");

// Fill the bounded cache, keeping an actively used track alive across eviction.
var active = System.Web.HttpUtility.ParseQueryString(new Uri(urls[0]).Query)["url"]!;
for (var i = 0; i < 8200; i++)
{
    relay.DecodeRelayUrl(session, active);
    relay.RewritePlaylistContent($"#EXTM3U\n#EXTINF:6,\n{segment}&n={i}\n", source, session, proxy);
}
Check(relay.DecodeRelayUrl(session, active) == audio, "active track survives cache eviction");
var evicted = System.Web.HttpUtility.ParseQueryString(new Uri(urls[1]).Query)["url"]!;
Reject(() => relay.DecodeRelayUrl(session, evicted), "least used reference is evicted");
// Gateway HTML: the tokenized m3u8 is the stream, whatever player or variable holds it.
const string gateway = "https://gateway.example/vivo/canales.php?stream=canal";
const string cdnToken = "https://cdn.example:443/canal/mono.m3u8?token=abc-1-2";
string? Extract(string html) => RedirectResolverService.TryExtractEmbeddedPlaybackUrl(html, gateway);
Check(Extract("<script>var player = cdnpeer.clappr(Clappr, {\"id\":\"x\",\"src\":\"" + cdnToken + "\",\"parentId\":\"#player\"});</script>") == new Uri(cdnToken).ToString(), "clappr JSON src with token (default port is normalized)");
Check(Extract("{\"file\":\"https:\\/\\/cdn.example\\/a\\/mono.m3u8?token=a\\u0026x=1\"}") == "https://cdn.example/a/mono.m3u8?token=a&x=1", "JSON-escaped URL");
Check(Extract("<a href=\"https://ads.example/promo.m3u8\"></a><script>play('" + cdnToken + "')</script>") == new Uri(cdnToken).ToString(), "tokenized stream wins over a plain one");
Check(Extract("<video src='/live/x.m3u8'></video>") == "https://gateway.example/live/x.m3u8", "relative source without token is the fallback");
Check(Extract("<script>src='https://cdn.example/a/b.m3u8x'</script>") is null, "extension must end at the m3u8 boundary");
Check(Extract("<html>nothing here</html>") is null, "no playlist, no result");

Console.WriteLine("PASS: long master/media URLs, exact signatures, audio retained/subtitles removed, legacy URLs, session isolation, WebVTT extensions, live-start hold-back, timeline/encryption preservation and bounded cache.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action action, string message)
{
    try { action(); }
    catch (FormatException) { return; }
    throw new InvalidOperationException(message);
}
