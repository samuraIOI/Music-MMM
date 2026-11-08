using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VK_UI3.Rhythm;

public enum NoteKind { Centre, Rim, Roll, Swell }
public sealed record TaikoNote(double Time, double End, NoteKind Kind, bool Strong, double TickSpacing, int RequiredHits);
public sealed record BeatTiming(double Time, double BeatLength, bool Kiai);
public sealed class Beatmap
{
    public string AudioFilename { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Difficulty { get; private set; } = "";
    public double OD { get; private set; } = 5;
    public double ScrollMultiplier { get; private set; } = 1.4;
    public List<TaikoNote> Notes { get; } = new();
    public List<BeatTiming> Timing { get; } = new();
    public static Beatmap Parse(string text)
    {
        var map = new Beatmap();
        var section = "";
        var mode = -1;
        double tickRate = 1;
        var timing = new List<(double Time, double Length, bool Base, bool Kiai)>();
        var objects = new List<string>();
        double Number(string s) { var n = double.Parse(s, CultureInfo.InvariantCulture); return double.IsFinite(n) ? n : throw new FormatException("Некорректное число в карте."); }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('[')) { section = line; continue; }
            var colon = line.IndexOf(':');
            if (colon >= 0 && section != "[HitObjects]" && section != "[TimingPoints]")
            {
                var key = line[..colon].Trim(); var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "Mode": mode = int.Parse(value); break;
                    case "AudioFilename": map.AudioFilename = value; break;
                    case "Title": map.Title = value; break;
                    case "Version": map.Difficulty = value; break;
                    case "OverallDifficulty": map.OD = Math.Clamp(Number(value), 0, 10); break;
                    case "SliderMultiplier": map.ScrollMultiplier = Number(value); break;
                    case "SliderTickRate": tickRate = Number(value); break;
                }
            }
            else if (section == "[TimingPoints]")
            {
                var p = line.Split(',');
                timing.Add((Number(p[0]), Number(p[1]), p.Length < 7 || p[6] == "1", p.Length > 7 && (int.Parse(p[7]) & 1) != 0));
            }
            else if (section == "[HitObjects]") objects.Add(line);
        }
        if (mode != 1) throw new InvalidDataException("MVP поддерживает только osu!taiko (Mode=1). В генераторе выберите taiko.");
        if (string.IsNullOrWhiteSpace(map.AudioFilename) || map.ScrollMultiplier <= 0 || tickRate <= 0) throw new InvalidDataException("В карте отсутствует аудио или некорректная сложность.");
        timing = timing.OrderBy(p => p.Time).ToList();
        double currentBeat = 500;
        foreach (var point in timing)
        {
            // Every timing point can change Kiai; only uninherited points change BPM.
            if (point.Base && point.Length > 0) currentBeat = point.Length;
            map.Timing.Add(new(point.Time, currentBeat, point.Kiai));
        }
        foreach (var line in objects)
        {
            var p = line.Split(','); var t = Number(p[2]); var type = int.Parse(p[3]); var sound = int.Parse(p[4]);
            double beat = 500, sv = 1;
            foreach (var point in timing.Where(x => x.Time <= t))
                if (point.Base) { if (point.Length <= 0) throw new InvalidDataException("Неверный BPM."); beat = point.Length; sv = 1; }
                else if (point.Length < 0) sv = Math.Clamp(-100 / point.Length, 0.1, 10);
            var strong = (sound & 4) != 0;
            var kind = (sound & (2 | 8)) != 0 ? NoteKind.Rim : NoteKind.Centre;
            double end = t; int required = 0;
            if ((type & 2) != 0)
            {
                kind = NoteKind.Roll;
                var repeats = int.Parse(p[6]); var length = Number(p[7]);
                if (repeats < 1 || repeats > 100 || length < 0) throw new InvalidDataException("Некорректная дробь.");
                end += length * repeats * beat / (100 * map.ScrollMultiplier * sv);
            }
            else if ((type & 8) != 0)
            {
                kind = NoteKind.Swell; end = Number(p[5].Split(':')[0]);
                var rate = map.OD <= 5 ? 3 + map.OD * .4 : 5 + (map.OD - 5) * .5;
                required = (int)Math.Max(1, (end - t) / 1000 * rate * 1.65);
            }
            else if ((type & 1) == 0) throw new InvalidDataException("Неподдерживаемый объект в карте.");
            if (t < 0 || end < t || end > 24 * 3600000) throw new InvalidDataException("Некорректное время объекта.");
            map.Notes.Add(new(t, end, kind, strong, beat / (tickRate == 3 ? 3 : 4), required));
            if (map.Notes.Count > 100000) throw new InvalidDataException("Слишком много объектов.");
        }
        if (map.Notes.Count == 0) throw new InvalidDataException("В карте нет объектов.");
        map.Notes.Sort((a, b) => a.Time.CompareTo(b.Time));
        return map;
    }
}
