// Scoring and hit windows adapted from ppy/osu, MIT; see THIRD-PARTY-NOTICES.md.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VK_UI3.Rhythm;

public sealed class NoteState
{
    public TaikoNote Note { get; init; }
    public bool Judged { get; set; }
    public int Hits { get; set; }
    public bool? LastRim { get; set; }
    public HashSet<int> Ticks { get; } = new();
    public double HitTime { get; set; } = double.NegativeInfinity;
    public int HitHand { get; set; }
    public bool StrongAwarded { get; set; }
}
public sealed record TaikoJudgement(double Time, NoteKind Kind, int Value, double? Error, bool Strong);
public sealed class TaikoSession
{
    public event Action<TaikoJudgement> Judgement;
    private int firstPending;
    public List<NoteState> Objects { get; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public int Misses { get; private set; }
    public int Great { get; private set; }
    public int Ok { get; private set; }
    public double Accuracy => Great + Ok + Misses == 0 ? 1 : (Great + Ok * .5) / (Great + Ok + Misses);
    public string Feedback { get; private set; } = "";
    private double comboPortion, bonus;
    private readonly double maximumComboPortion;
    private readonly int totalNotes;
    private readonly double greatWindow, okWindow, missWindow;
    public long Score => (long)Math.Round((totalNotes == 0 ? 0 : 250000 * comboPortion / maximumComboPortion + 750000 * Math.Pow(Accuracy, 3.6) * (Great + Ok + Misses) / totalNotes) + bonus);
    public string Rank => Accuracy == 1 && Misses == 0 ? "SS" : Accuracy >= .95 && Misses == 0 ? "S" : Accuracy >= .9 ? "A" : Accuracy >= .8 ? "B" : Accuracy >= .7 ? "C" : "D";
    public TaikoSession(Beatmap map, double startTime = 0)
    {
        double Range(double min, double mid, double max) => Math.Floor(map.OD <= 5 ? min + (mid - min) * map.OD / 5 : mid + (max - mid) * (map.OD - 5) / 5) - .5;
        greatWindow = Range(50, 35, 20); okWindow = Range(120, 80, 50); missWindow = Range(135, 95, 70);
        // A seek starts a new practice attempt. Earlier objects aren't misses and
        // cannot award points again through stale roll/strong-hit state.
        Objects = map.Notes.Select(n => new NoteState { Note = n, Judged = n.End < startTime - missWindow }).ToList();
        totalNotes = Objects.Count(s => !s.Judged && s.Note.Kind is NoteKind.Centre or NoteKind.Rim);
        maximumComboPortion = Enumerable.Range(1, totalNotes).Sum(i => 300 * Weight(i));
    }
    private static double Weight(int combo) => Math.Min(Math.Max(.5, Math.Log(Math.Max(1, combo), 4)), Math.Log(400, 4));
    public void Advance(double time)
    {
        for (var i = firstPending; i < Objects.Count; i++)
        {
            var s = Objects[i];
            if (s.Note.Time > time) break;
            if (s.Judged) continue;
            if (s.Note.Kind is NoteKind.Centre or NoteKind.Rim)
            { if (time > s.Note.Time + missWindow) Judge(s, 0, s.Note.Time + missWindow); }
            else if (time > s.Note.End) s.Judged = true;
        }
        while (firstPending < Objects.Count && Objects[firstPending].Judged) firstPending++;
    }
    public void Strike(double time, bool rim, int hand)
    {
        Advance(time);
        var strong = Objects.FirstOrDefault(s => s.Judged && s.Note.Strong && !s.StrongAwarded && s.HitHand != hand && time >= s.HitTime && time - s.HitTime <= 30 && (s.Note.Kind == NoteKind.Rim) == rim);
        if (strong != null) { strong.StrongAwarded = true; bonus += 350; Feedback = "Сильный удар"; Judgement?.Invoke(new(time, strong.Note.Kind, 350, time - strong.Note.Time, true)); return; }
        var hit = Objects.FirstOrDefault(s => !s.Judged && s.Note.Kind is NoteKind.Centre or NoteKind.Rim && Math.Abs(time - s.Note.Time) <= missWindow);
        if (hit != null)
        {
            var delta = Math.Abs(time - hit.Note.Time);
            var value = (hit.Note.Kind == NoteKind.Rim) != rim ? 0 : delta <= greatWindow ? 300 : delta <= okWindow ? 150 : 0;
            Judge(hit, value, time);
            if (value > 0) { hit.HitTime = time; hit.HitHand = hand; }
            return;
        }
        foreach (var s in Objects.Where(s => !s.Judged && s.Note.Time <= time && s.Note.End >= time))
        {
            if (s.Note.Kind == NoteKind.Roll)
            {
                var tick = (int)Math.Round((time - s.Note.Time) / s.Note.TickSpacing);
                if (s.Ticks.Add(tick)) { bonus += s.Note.Strong ? 160 : 10; s.Hits++; Feedback = "Дробь"; Judgement?.Invoke(new(time, s.Note.Kind, 10, null, s.Note.Strong)); }
            }
            else if (s.Note.Kind == NoteKind.Swell && s.LastRim != rim)
            {
                s.LastRim = rim; s.Hits++;
                Feedback = $"Чередование {s.Hits}/{s.Note.RequiredHits}";
                Judgement?.Invoke(new(time, s.Note.Kind, 10, null, s.Note.Strong));
                if (s.Hits >= s.Note.RequiredHits) { bonus += 50; s.Judged = true; }
            }
        }
    }
    private void Judge(NoteState s, int value, double time)
    {
        if (s.Judged) return;
        s.Judged = true;
        if (value == 0) { Misses++; Combo = 0; Feedback = "Промах"; }
        else { Combo++; MaxCombo = Math.Max(MaxCombo, Combo); if (value == 300) Great++; else Ok++; comboPortion += value * Weight(Combo); Feedback = value == 300 ? "Отлично" : "Хорошо"; }
        Judgement?.Invoke(new(time, s.Note.Kind, value, value > 0 ? time - s.Note.Time : null, s.Note.Strong));
    }
}
