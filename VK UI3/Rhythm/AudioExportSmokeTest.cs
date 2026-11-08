using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VK_UI3.Services;

namespace VK_UI3.Rhythm;

// Opt-in integration harness, enabled only by a local runtimeconfig property.
// It uses a normally selected VK track and never writes its signed URL to disk.
internal static class AudioExportSmokeTest
{
    private static int started;
    public static void Initialize()
    {
        if (AppContext.GetData("MusicMMM.ExportSmokeTestDirectory") is not string directory || !Path.IsPathFullyQualified(directory)) return;
        MediaPlayerService.AudioPlayedChangeEvent += (_, _) =>
        {
            var track = MediaPlayerService.PlayingTrack?.audio;
            if (track?.Url == null || Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                Directory.CreateDirectory(directory);
                try
                {
                    using var client = AudioExportService.CreateClient();
                    var service = new AudioExportService(client);
                    var ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
                    var mp3 = Path.Combine(directory, "vk-export.mp3"); var ogg = Path.Combine(directory, "vk-export.ogg");
                    await service.Export(track.Url, mp3, ffmpeg);
                    await service.Export(track.Url, ogg, ffmpeg);
                    RhythmStorage.SaveExport(new($"{track.OwnerId}_{track.Id}", await RhythmStorage.Hash(mp3), mp3, track.Title));
                    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { ok = true, mp3_bytes = new FileInfo(mp3).Length, ogg_bytes = new FileInfo(ogg).Length, title = track.Title }));
                }
                catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { ok = false, error = AudioExportService.Redact(e.ToString()) })); }
            });
        };
    }
}
