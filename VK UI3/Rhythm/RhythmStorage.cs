using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VK_UI3.Rhythm;

public sealed record ExportedTrack(string TrackId, string Hash, string Path, string Title);
public sealed record StoredMap(string Id, string TrackId, string Directory, string MapPath, string AudioPath, string Title);
public static class RhythmStorage
{
    public static string Root { get; } = AppContext.GetData("MusicMMM.RhythmRoot") as string ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Music-MMM", "Rhythm");
    public static async Task<string> Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
    public static void SaveExport(ExportedTrack track)
    {
        var dir = System.IO.Path.Combine(Root, "exports"); Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(track.TrackId))) + ".json"), JsonSerializer.Serialize(track));
    }
    public static ExportedTrack FindExport(string trackId)
    {
        var dir = System.IO.Path.Combine(Root, "exports");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(Read<ExportedTrack>).FirstOrDefault(t => t?.TrackId == trackId) : null;
    }
    public static IEnumerable<StoredMap> Library()
    {
        var dir = System.IO.Path.Combine(Root, "maps");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "manifest.json", SearchOption.AllDirectories).Select(Read<StoredMap>).Where(m => m != null) : Array.Empty<StoredMap>();
    }
    private static string PreferencePath(string trackId) => System.IO.Path.Combine(Root, "preferences", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trackId))) + ".json");
    public static string PreferredMap(string trackId) => Read<string>(PreferencePath(trackId));
    public static void RememberMap(StoredMap map)
    {
        var path = PreferencePath(map.TrackId); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(map.Id)); File.Move(path + ".tmp", path, true);
    }
    private static T Read<T>(string path) where T : class { try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); } catch { return null; } }
    public static async Task<List<StoredMap>> Import(string archive, string trackId)
    {
        var expected = FindExport(trackId) ?? throw new InvalidDataException("Сначала скачайте этот трек кнопкой «Скачать для генерации» и создайте карту с неизменённым файлом.");
        var id = await Hash(archive);
        var destination = System.IO.Path.Combine(Root, "maps", id);
        var staging = System.IO.Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new InvalidDataException("Архив слишком большой.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    var path = Inside(staging, entry.FullName);
                    if (!seen.Add(path)) throw new InvalidDataException("Повторяющиеся имена файлов в архиве.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
                    var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    if (!new[] { ".osu", ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".png", ".jpg", ".jpeg" }.Contains(extension)) continue;
                    if (extension == ".osu" && entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Файл карты слишком большой.");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)); entry.ExtractToFile(path);
                }
            }
            var maps = new List<StoredMap>();
            string failure = "В архиве нет taiko-карт.";
            foreach (var file in Directory.GetFiles(staging, "*.osu", SearchOption.AllDirectories))
            {
                Beatmap map;
                try { map = Beatmap.Parse(await File.ReadAllTextAsync(file)); }
                catch (InvalidDataException e) { failure = e.Message; continue; }
                var audio = Inside(System.IO.Path.GetDirectoryName(file), map.AudioFilename);
                if (!File.Exists(audio) || await Hash(audio) != expected.Hash) throw new InvalidDataException("Не удалось подтвердить соответствие аудио. Используйте неизменённый экспортированный файл.");
                var mapId = await Hash(file);
                maps.Add(new(mapId, trackId, destination, System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(staging, file)), System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(staging, audio)), $"{map.Title} — {map.Difficulty}"));
            }
            if (maps.Count == 0) throw new InvalidDataException(failure);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
            if (!Directory.Exists(destination)) Directory.Move(staging, destination);
            foreach (var map in maps)
            {
                var manifestDir = System.IO.Path.Combine(destination, "catalog", map.Id); Directory.CreateDirectory(manifestDir);
                await File.WriteAllTextAsync(System.IO.Path.Combine(manifestDir, "manifest.json"), JsonSerializer.Serialize(map));
            }
            return maps;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static string Inside(string root, string relative)
    {
        var fullRoot = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || relative.Contains(':')) throw new InvalidDataException("Недопустимый путь в архиве.");
        return full;
    }
}

public static class RhythmAnalytics
{
    private static readonly string session = Guid.NewGuid().ToString("N");
    private static readonly SemaphoreSlim gate = new(1);
    private static string installId;
    public static async Task Write(string name, object data)
    {
        try
        {
            await gate.WaitAsync();
            try
            {
                var dir = System.IO.Path.Combine(RhythmStorage.Root, "analytics"); Directory.CreateDirectory(dir);
                if (installId == null)
                {
                    var identity = System.IO.Path.Combine(dir, "installation-id.txt");
                    installId = File.Exists(identity) ? await File.ReadAllTextAsync(identity) : Guid.NewGuid().ToString("N");
                    if (!File.Exists(identity)) await File.WriteAllTextAsync(identity, installId);
                }
                var line = JsonSerializer.Serialize(new { schema_version = 1, event_id = Guid.NewGuid(), timestamp_utc = DateTimeOffset.UtcNow, anonymous_install_id = installId, app_session_id = session, event_name = name, app_version = typeof(RhythmAnalytics).Assembly.GetName().Version?.ToString(), data });
                await File.AppendAllTextAsync(System.IO.Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"), line + "\n");
            }
            finally { gate.Release(); }
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("Rhythm analytics: " + e.Message); }
    }
}
