# HLS relay regression

Run from the repository root:

```powershell
dotnet run --project tests/JellyTV.HlsRegression -c Release -p:RunAnalyzers=false
```

The October 22 server logs show FFmpeg requesting a truncated 4032-character
subtitle URL without the relay key, followed by `invalid:truncated` for the
video variant. Base64-encoding signed FAST provider URLs makes the relay URLs too long
for FFmpeg's HLS parser. Dropping subtitles alone leaves the video failure.

This check exercises the production rewriter and decoder with signed URLs over
5000 characters, including master variants, separate audio renditions and removal of subtitle declarations/references,
media segments, initialization maps and encryption keys. It checks exact query
preservation, legacy Base64 compatibility, stable references across refreshes,
session isolation, missing references and eviction from the bounded cache.

This does not replace a playback check on the Jellyfin server after deployment.

The relay omits optional HLS subtitle renditions and their `SUBTITLES` attributes.
Only external audio keeps a reduced master; subtitles alone do not prevent
collapsing to the video variant. WebVTT suffix handling remains correct for
already published or explicitly requested subtitle playlists.

To run the local FFmpeg integration check, pass installed executables:

```powershell
dotnet run --project tests/JellyTV.HlsRegression -c Release -p:RunAnalyzers=false -- D:\Jellyfin\V12\ffmpeg.exe D:\Jellyfin\V12\ffprobe.exe
```

The integration check starts a loopback-only HTTP server and generates a small
H264/AAC fixture. It reproduces the old WebVTT/.ts failure, then uses the production
rewriter and verifies that FFmpeg probes and decodes video/audio without requesting
any subtitle playlists or VTT segments. It never contacts the Jellyfin server.

FAST, direct and session-token playlists do not receive a forced `EXT-X-START`.
The player chooses its normal HLS hold-back instead of starting only three
seconds behind live segments that can last five or six seconds. Existing
provider start preferences are preserved without duplicates. The explicit
buffered start for ephemeral tokens remains supported. The relay logs
`StartOffset=client` when it delegates that choice to the player.

## FAST provider sequence restart regression

Five fixtures preserve the structure of provider captures from October 22, 2026,
with all upstream URLs replaced by stable synthetic URLs and session data removed.
The same live rendition reset MEDIA-SEQUENCE 79 -> 0 and DISCONTINUITY-SEQUENCE
1 -> 0 while overlapping media segments were still present. Android TV 0.19.10
uses Media3 1.8.0, whose playlist tracker rejects a new window when its end lies
before the previous window's start (`0 + 5 < 79`).

`HlsTimeline` keeps that FAST rendition continuous per session and source playlist.
The captures publish 79,82,82,83,84, preserving the most recent suffix/prefix overlap
rather than matching the first occurrence of a repeated bumper URL. Discontinuity
identities stay consistent; when there is no overlap, a new boundary is introduced.
Inconsistent provider PROGRAM-DATE-TIME values are omitted after a reset so the
player can derive timing from the preserved media overlap. Segment bytes and keys
are unchanged. When AES-128 uses an implicit IV, the original sequence-derived IV
is made explicit for each segment before the published sequence is changed.

The tests also cover delayed windows, missing discontinuity headers, independent
renditions, byte ranges, different keys, repeated restarts and preservation of
other delivery types. Small counter regressions still overlapping numerically are
treated as delayed responses; the observed FAST provider reset is below that entire window.

References:
- https://github.com/jellyfin/jellyfin-androidtv/blob/v0.19.10/gradle/libs.versions.toml
- https://github.com/androidx/media/blob/1.8.0/libraries/exoplayer_hls/src/main/java/androidx/media3/exoplayer/hls/playlist/DefaultHlsPlaylistTracker.java
