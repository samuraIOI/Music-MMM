using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VK_UI3.Rhythm;

// Network transport uses the platform HTTPS implementation. FFmpeg receives
// only a private, fully materialised local file/playlist and cannot access HTTP.
public sealed class AudioExportService
{
    private readonly HttpClient client;
    private const long MaxBytes = 512L * 1024 * 1024;
    public AudioExportService(HttpClient client) => this.client = client;
    public static HttpClient CreateClient()
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(45) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        return result;
    }
    public async Task Export(Uri source, string destination, string ffmpeg, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); cancellation = timeout.Token;
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Для экспорта не найден ffmpeg.exe.");
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination)), ".music-mmm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var local = await Download(source, directory, cancellation);
            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (extension is not (".mp3" or ".ogg")) throw new InvalidDataException("Выберите формат MP3 или OGG.");
            var encoded = Path.Combine(directory, "encoded" + extension);
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,crypto,data" }) info.ArgumentList.Add(arg);
            if (Path.GetExtension(local) == ".m3u8") foreach (var arg in new[] { "-allowed_extensions", "ALL", "-extension_picky", "0" }) info.ArgumentList.Add(arg);
            foreach (var arg in new[] { "-i", local, "-vn", "-map_metadata", "-1", "-c:a", extension == ".mp3" ? "libmp3lame" : "libvorbis", encoded }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить FFmpeg.");
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellation); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var error = Redact(await stderr);
            if (process.ExitCode != 0) throw new IOException($"Локальное кодирование FFmpeg не выполнено ({process.ExitCode}): {error}");
            if (!File.Exists(encoded) || new FileInfo(encoded).Length == 0) throw new IOException("FFmpeg создал пустой файл.");
            File.Move(encoded, destination, true);
        }
        catch (HttpRequestException e) { throw new IOException("Не удалось загрузить аудио по HTTPS: " + Redact(e.GetBaseException().Message), e); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    internal static string Redact(string value) => Regex.Replace(value, @"https?://[^\s\""<>]+", "[audio URL]");

    public async Task<string> Download(Uri source, string directory, CancellationToken cancellation = default)
    {
        long total = 0;
        int resources = 0;
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<(string Path, Uri Final)> Fetch(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != "https") throw new InvalidDataException("Источник аудио должен использовать HTTPS.");
            if (++resources > 5000) throw new InvalidDataException("Слишком много файлов в аудиопотоке.");
            for (int redirects = 0; redirects < 6; redirects++)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Перенаправление без адреса.");
                    uri = new Uri(uri, location);
                    if (uri.Scheme != "https") throw new InvalidDataException("Перенаправление аудио на незащищённый HTTP запрещено.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var path = Path.Combine(directory, "source-" + Guid.NewGuid().ToString("N") + ".media");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = File.Create(path);
                var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count; if (total > MaxBytes) throw new InvalidDataException("Аудиопоток превышает 512 МБ.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                return (path, response.RequestMessage?.RequestUri ?? uri);
            }
            throw new InvalidDataException("Слишком много перенаправлений аудио.");
        }
        async Task<string> Resource(Uri uri)
        {
            if (cache.TryGetValue(uri.AbsoluteUri, out var cached)) return cached;
            var fetched = await Fetch(uri); cache[uri.AbsoluteUri] = fetched.Path; return fetched.Path;
        }
        async Task<string> Materialise(Uri uri, int depth)
        {
            if (depth > 4) throw new InvalidDataException("Слишком много вложенных HLS-плейлистов.");
            var fetched = await Fetch(uri);
            var prefix = new byte[16];
            using (var stream = File.OpenRead(fetched.Path)) _ = stream.Read(prefix, 0, prefix.Length);
            if (!Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF').StartsWith("#EXTM3U", StringComparison.Ordinal)) return fetched.Path;
            if (new FileInfo(fetched.Path).Length > 4 * 1024 * 1024) throw new InvalidDataException("HLS-плейлист слишком большой.");
            var lines = await File.ReadAllLinesAsync(fetched.Path, cancellation);
            // Prefer a separate audio rendition over a multiplexed video variant.
            foreach (var line in lines)
                if (line.StartsWith("#EXT-X-MEDIA:") && line.Contains("TYPE=AUDIO"))
                {
                    var match = Regex.Match(line, "URI=\"([^\"]+)\"");
                    if (match.Success) return await Materialise(new Uri(fetched.Final, match.Groups[1].Value), depth + 1);
                }
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("#EXT-X-STREAM-INF:"))
                {
                    var next = lines.Skip(i + 1).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith('#'));
                    if (next == null) throw new InvalidDataException("Неполный вариант HLS.");
                    return await Materialise(new Uri(fetched.Final, next.Trim()), depth + 1);
                }
            if (!lines.Any(l => l.Trim() == "#EXT-X-ENDLIST")) throw new InvalidDataException("Экспорт прямых эфиров не поддерживается.");
            var result = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXT-X-BYTERANGE:")) throw new InvalidDataException("Этот вариант HLS использует неподдерживаемые byte ranges.");
                if (line.StartsWith("#EXT-X-KEY:") && !line.Contains("METHOD=AES-128") && !line.Contains("METHOD=NONE")) throw new InvalidDataException("Неподдерживаемый метод шифрования HLS.");
                if (line.StartsWith('#') && line.Contains("URI="))
                {
                    if (!line.StartsWith("#EXT-X-KEY:") && !line.StartsWith("#EXT-X-MAP:")) throw new InvalidDataException("Неподдерживаемая ссылка в HLS-плейлисте.");
                    if (line.Contains("BYTERANGE=")) throw new InvalidDataException("HLS byte ranges пока не поддерживаются.");
                    var match = Regex.Match(line, "URI=\"([^\"]+)\"");
                    if (!match.Success) throw new InvalidDataException("Неверный URI в HLS.");
                    var local = await Resource(new Uri(fetched.Final, match.Groups[1].Value));
                    line = line[..match.Groups[1].Index] + Path.GetFileName(local) + line[(match.Groups[1].Index + match.Groups[1].Length)..];
                }
                else if (line.Length > 0 && !line.StartsWith('#')) line = Path.GetFileName(await Resource(new Uri(fetched.Final, line)));
                result.Add(line);
            }
            // These are private downloaded files; extension restrictions no longer
            // need to encode remote access policy. FFmpeg's protocol whitelist does.
            var playlist = Path.Combine(directory, "playlist-" + depth + ".m3u8");
            await File.WriteAllLinesAsync(playlist, result, new UTF8Encoding(false), cancellation);
            return playlist;
        }
        Directory.CreateDirectory(directory);
        return await Materialise(source, 0);
    }
}
