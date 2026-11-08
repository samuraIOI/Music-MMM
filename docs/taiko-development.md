# Taiko development

Implemented on `feature/taiko-mvp`. This is a development prototype, not a completed MVP release.

## Layout

- `VK UI3/Rhythm/Beatmap.cs`: mode validation, timing points, taiko objects.
- `TaikoSession.cs`: independent judgement and scoring state, without UI dependencies.
- `RhythmStorage.cs`: bounded archive import, audio SHA-256 check, library and JSONL.
- `RhythmView.cs`: Win2D lane, transport lifecycle, keyboard/mouse and export/import.
- `PlaybackClock.cs`: continuous interpolation of audio position reports.
- `TaikoRenderer.cs`: game-thread drawing, depth, beat pulse and judgement effects.
- `ListeningAnalytics.cs`: observer for regular player listening sessions.
- `AudioExportService.cs`: HTTPS download via the platform HttpClient, materialised VOD HLS resources, and local-only FFmpeg encoding.
- `tests/Rhythm.Core.Checks`: executable offline checks for core logic and archive handling.

## Build and checks

Use .NET 8 SDK, Visual Studio Windows App SDK tooling and Windows SDK.

```powershell
dotnet build 'VK UI3/VK UI3.csproj' -c Unpacked -p:Platform=x64 -p:RunAnalyzers=false
dotnet run --project tests/Rhythm.Core.Checks
dotnet run --project tests/Rhythm.Core.Checks -- --ffmpeg 'VK UI3/Tools/ffmpeg.exe'
```

When using a custom NuGet cache, use the same NUGET_PACKAGES for restore and build.
The test harness writes its fixtures under cwd/work and removes its own artifacts.

### FFmpeg executable for export

The upstream player installs FFmpeg DLLs, not the CLI executable required by this
export implementation. Place a Windows x64 `ffmpeg.exe` in `VK UI3/Tools` before
building; MSBuild copies it to `Tools` beside the application. It is intentionally
excluded from Git. Preserve `FFmpeg-LICENSE.txt` and `FFmpeg-README.txt` for the
specific build being used. The current local test build was supplied by the user
from gyan.dev, version `2026-11-05-git-0893feba65`, GPLv3. Distribution of a release
must include the corresponding notices and satisfy the binary's source provision
requirements. This dependency has not yet been automated for clean checkouts.

FFmpeg must not fetch remote audio directly: its TLS backend is separate from
Windows. AudioExportService downloads HTTPS resources with normal platform
certificate validation and materialises HLS variants, segments, maps and AES-128
keys in a private staging directory. FFmpeg receives a local file/playlist with
only file/crypto/data protocols enabled. HTTPS downgrades and unsupported live or
byte-range HLS are rejected explicitly. Conversion commits the destination only
after success and cleans up staging files on failure/cancellation.

For an opt-in real-VK integration run, set the runtimeconfig config property
`MusicMMM.ExportSmokeTestDirectory` to an absolute scratch output directory.
After the first normally selected VK track becomes available, AudioExportSmokeTest
runs MP3 and OGG export and writes `result.json`. It does not record the signed
audio URL and does not open file pickers. Do not set this property in release
configuration. A normal build has this integration hook disabled.

## Manual path

1. Play a VK track, enable the Taiko switch in the bottom playback bar.
2. Export the track with “Скачать для генерации”. FFmpeg executable with MP3/Vorbis encoders is required.
3. Generate a **taiko** map in Colab (`gamemode=1`), using the exported file without transcoding. Export OSZ.
4. Import the OSZ. The imported map starts automatically. On later visits the last played map for that track is selected and started automatically; “Играть сначала” restarts or applies another selected difficulty.
5. F/J = centre, D/K = rim, left/right mouse = centre/rim. Esc pauses.
6. Complete the level. The normal player manages repeat/next-track behaviour. If the next track has a saved map it starts automatically; otherwise listening continues without a level.

Gameplay follows the existing MediaPlayerService player rather than owning a second
audio player. A verified map replaces that player's source with its local audio;
track identity and queue remain managed by Music M. Pause, volume, previous/next, repeat, system transport and the native
seek slider remain available. Closing the overlay does not restore an earlier
position or pause shared playback. Import/export still pause music explicitly.
Changing track/source invalidates asynchronous map preparation, so it cannot start
a stale map. SeekCompleted clears the score and object state for a fresh practice
attempt at the selected position; skipped objects are excluded, not scored as misses.
JSONL game_seek events record resets and game_finished carries the practice flag.

The transport slider commits pointer seeks on release and keyboard seeks on key-up;
hovering does not seek and playback reports do not overwrite an active gesture.

Rhythm data is stored in LocalApplicationData/Music-MMM/Rhythm. Event logs are UTF-8 JSONL in analytics. A standalone OSZ with audio not previously exported by this installation is intentionally rejected.

## Verification and remaining acceptance work

Core checks cover perfect score for ordinary notes, timing boundaries, wrong colour, repeated hits, roll duration, unsupported modes, archive import, mismatched audio and path traversal.

Not yet established: complete parity with lazer on every taiko judgement, scroll-speed changes and overlapping-object edge cases; measured end-to-end input/audio latency; clean-machine MSIX installation and signing. Local development launches distinguish application paths, allowing test copies beside the installed player. Always target the exact test path when checking the UI.

The packaging project still has upstream identity/signing configuration. Do not distribute or install it over the original Music M. A separate fork identity and certificate must be configured and tested before a release. No installer or release is claimed by this prototype.
