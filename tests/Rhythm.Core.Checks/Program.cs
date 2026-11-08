using System.IO.Compression;
using VK_UI3.Rhythm;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
string Map(string objects, string timing = "0,500,4,2,0,100,1,0") => $"[General]\nAudioFilename: audio.mp3\nMode:1\n[Metadata]\nTitle: Test\nVersion: Easy\n[Difficulty]\nOverallDifficulty:5\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n{timing}\n[HitObjects]\n{objects}";
var map = Beatmap.Parse(Map("256,192,1000,1,0\n256,192,2000,1,2"));
var game = new TaikoSession(map);
game.Strike(1000, false, 0); game.Strike(2000, true, 1);
Check(game.Score == 1000000 && game.Rank == "SS", "perfect normal notes reproduce lazer million-point base");
game.Strike(2000, true, 1); Check(game.Great == 2, "duplicate input cannot score twice");
game = new(map); game.Strike(1000, true, 0); game.Advance(3000);
Check(game.Misses == 2 && game.Score == 0 && game.Rank == "D", "wrong colour and late notes miss without failing song");
game = new(map); game.Strike(1034.5, false, 0); Check(game.Great == 1, "great window inclusive boundary OD5");
game = new(map); game.Strike(1034.6, false, 0); Check(game.Ok == 1, "outside great window is OK");
var judgements = new List<TaikoJudgement>();
game = new(map); game.Judgement += judgements.Add; game.Strike(1020, false, 0); game.Advance(3000); game.Advance(4000);
Check(judgements.Count == 2 && judgements[0].Error == 20 && judgements[0].Value == 300 && judgements[1].Value == 0, "visual judgements carry signed timing and emit misses once");
var practiceGame = new TaikoSession(map, 1500);
practiceGame.Advance(1500); practiceGame.Strike(2000, true, 1);
Check(practiceGame.Misses == 0 && practiceGame.Great == 1 && practiceGame.Score == 1000000, "forward seek excludes skipped notes and scores a fresh practice attempt");
var restartedGame = new TaikoSession(map, 0);
restartedGame.Strike(1000, false, 0); restartedGame.Strike(2000, true, 1);
Check(restartedGame.Great == 2 && restartedGame.MaxCombo == 2 && restartedGame.Score == 1000000, "restart restores all notes without retaining prior attempt state");
var clock = new PlaybackClock();
double previous = clock.Read(0, 0, true); bool smooth = true;
for (int t = 10; t <= 2000; t += 10)
{
    var current = clock.Read(t / 100 * 100, t, true);
    smooth &= current > previous && current - previous <= 12;
    previous = current;
}
Check(smooth && Math.Abs(previous - 2000) < 20, "100ms audio reports interpolate continuously without backwards jumps");
Check(clock.Read(2000, 2010, false) == 2000 && clock.Read(2000, 3000, false) == 2000 && clock.Read(2000, 3010, true) == 2000 && clock.Read(2000, 3020, true) == 2010, "clock freezes on pause and resumes without accumulated elapsed time");
clock.Reset(); Check(clock.Read(0, 10000, true) == 0, "new song resets audio clock");
clock.Read(0, 11000, true); Check(clock.Read(0, 12000, true) <= 250, "stalled audio cannot drift indefinitely");
var kiai = Beatmap.Parse(Map("256,192,1000,1,0", "0,500,4,2,0,100,1,0\n500,-50,4,2,0,100,0,1\n1000,400,4,2,0,100,1,0"));
Check(kiai.Timing[1].Kiai && kiai.Timing[1].BeatLength == 500 && !kiai.Timing[2].Kiai && kiai.Timing[2].BeatLength == 400, "beat pulse follows BPM and inherited Kiai transitions");
var roll = Beatmap.Parse(Map("256,192,1000,2,0,B|300:192,2,140", "0,500,4,2,0,100,1,0\n500,-50,4,2,0,100,0,0"));
Check(roll.Notes[0].End == 1500, "inherited velocity and repeat count define roll duration");
bool rejected = false;
try { Beatmap.Parse(Map("256,192,1000,1,0").Replace("Mode:1", "Mode:0")); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "standard map rejected explicitly");
var run = Guid.NewGuid().ToString("N");
var scratch = Path.Combine(Environment.CurrentDirectory, "work", "music-mmm-checks-" + run); Directory.CreateDirectory(scratch);
AppContext.SetData("MusicMMM.RhythmRoot", Path.Combine(scratch, "library"));
var exported = Path.Combine(scratch, "audio.mp3"); await File.WriteAllTextAsync(exported, "test audio bytes");
RhythmStorage.SaveExport(new(run, await RhythmStorage.Hash(exported), exported, "Test"));
string Archive(string name, bool badAudio = false, bool traversal = false)
{
    var path = Path.Combine(scratch, name + ".osz");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(zip.CreateEntry("level.osu").Open())) writer.Write(Map("256,192,1000,1,0"));
    using (var writer = new StreamWriter(zip.CreateEntry(traversal ? "../escape.mp3" : "audio.mp3").Open())) writer.Write(badAudio ? "different audio" : "test audio bytes");
    return path;
}
var stored = await RhythmStorage.Import(Archive("valid"), run);
Check(stored.Count == 1 && File.Exists(stored[0].AudioPath), "archive imported and persisted");
RhythmStorage.RememberMap(stored[0]);
Check(RhythmStorage.PreferredMap(run) == stored[0].Id && RhythmStorage.PreferredMap("other-track") == null, "last played map preference persists and is isolated by track");
rejected = false; try { await RhythmStorage.Import(Archive("wrong", true), run); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "mismatched audio rejected");
rejected = false; try { await RhythmStorage.Import(Archive("traversal", traversal: true), run); } catch (InvalidDataException) { rejected = true; }
Check(rejected && !File.Exists(Path.Combine(RhythmStorage.Root, "staging", "escape.mp3")), "archive traversal rejected");
await Task.WhenAll(Enumerable.Range(0, 12).Select(i => RhythmAnalytics.Write("test_event", new { index = i })));
var events = File.ReadAllLines(Directory.GetFiles(Path.Combine(RhythmStorage.Root, "analytics"), "*.jsonl").Single());
Check(events.Length == 12 && events.All(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("schema_version").GetInt32() == 1), "concurrent analytics writes preserve complete JSONL events");
var requests = new List<string>();
using (var http = new HttpClient(new FixtureHandler(request =>
{
    requests.Add(request.RequestUri.AbsolutePath);
    return FixtureHandler.Text(request.RequestUri.AbsolutePath switch
    {
        "/master" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nrelative/media.m3u8",
        "/relative/media.m3u8" => "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n#EXTINF:1,\nsegment.ts\n#EXT-X-ENDLIST",
        "/relative/key" => "0123456789abcdef",
        "/relative/segment.ts" => "fixture segment bytes",
        _ => throw new Exception("Unexpected HLS URL")
    });
})))
{
    var stage = Path.Combine(scratch, "hls");
    var local = await new AudioExportService(http).Download(new Uri("https://audio.test/master"), stage);
    var playlist = File.ReadAllText(local);
    Check(requests.SequenceEqual(new[] { "/master", "/relative/media.m3u8", "/relative/key", "/relative/segment.ts" }) && !playlist.Contains("https://") && playlist.Contains("source-"), "HLS variant, relative segments and AES key materialised locally");
}
using (var http = new HttpClient(new FixtureHandler(_ => new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://audio.test/plain") } })))
{
    rejected = false; try { await new AudioExportService(http).Download(new Uri("https://audio.test/redirect"), Path.Combine(scratch, "redirect")); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "HTTPS downgrade rejected");
}
var ffmpegArg = Array.IndexOf(args, "--ffmpeg");
if (ffmpegArg >= 0)
{
    var executable = Path.GetFullPath(args[ffmpegArg + 1]);
    var tone = Path.Combine(scratch, "tone.wav");
    var generate = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", tone }) generate.ArgumentList.Add(argument);
    using (var p = System.Diagnostics.Process.Start(generate)) { await p.WaitForExitAsync(); Check(p.ExitCode == 0, "audio fixture generated"); }
    using var http = new HttpClient(new FixtureHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(tone)) }));
    foreach (var extension in new[] { ".mp3", ".ogg" })
    {
        var output = Path.Combine(scratch, "export" + extension);
        await new AudioExportService(http).Export(new Uri("https://audio.test/file"), output, executable);
        Check(new FileInfo(output).Length > 1000, "download and local transcode " + extension);
    }
    var hlsDir = Path.Combine(scratch, "encrypted-hls"); Directory.CreateDirectory(hlsDir);
    var keyPath = Path.Combine(hlsDir, "key"); await File.WriteAllBytesAsync(keyPath, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
    var keyInfo = Path.Combine(hlsDir, "key-info.txt"); await File.WriteAllTextAsync(keyInfo, "key\n" + keyPath.Replace('\\', '/') + "\n");
    var hls = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { "-loglevel", "error", "-i", tone, "-c:a", "aac", "-f", "hls", "-hls_time", "0.5", "-hls_list_size", "0", "-hls_key_info_file", keyInfo, Path.Combine(hlsDir, "track.m3u8") }) hls.ArgumentList.Add(argument);
    using (var p = System.Diagnostics.Process.Start(hls)) { await p.WaitForExitAsync(); Check(p.ExitCode == 0, "encrypted HLS audio fixture generated"); }
    using var hlsHttp = new HttpClient(new FixtureHandler(r => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(hlsDir, Path.GetFileName(r.RequestUri.AbsolutePath)))) }));
    var fromHls = Path.Combine(scratch, "from-hls.mp3");
    await new AudioExportService(hlsHttp).Export(new Uri("https://audio.test/track.m3u8"), fromHls, executable);
    Check(new FileInfo(fromHls).Length > 1000, "AES-128 HLS downloaded and transcoded without FFmpeg network access");
    using var failedHttp = new HttpClient(new FixtureHandler(_ => new(System.Net.HttpStatusCode.Forbidden)));
    var existingHash = await RhythmStorage.Hash(fromHls);
    rejected = false; try { await new AudioExportService(failedHttp).Export(new Uri("https://audio.test/failure"), fromHls, executable); } catch (IOException) { rejected = true; }
    Check(rejected && await RhythmStorage.Hash(fromHls) == existingHash, "download failure preserves an existing destination");
}
Console.WriteLine($"{checks} checks passed.");
// Only remove the artifacts produced by this test run.
foreach (var item in stored) if (Directory.Exists(item.Directory)) Directory.Delete(item.Directory, true);
var exports = Path.Combine(RhythmStorage.Root, "exports");
foreach (var file in Directory.GetFiles(exports, "*.json")) if (File.ReadAllText(file).Contains(run)) File.Delete(file);
Directory.Delete(scratch, true);
