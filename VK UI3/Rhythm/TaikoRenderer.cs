using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace VK_UI3.Rhythm;

// All state is owned by RhythmView's game gate. No XAML access or per-frame
// resource creation; depth is drawn with a small, bounded set of translucent layers.
internal sealed class TaikoRenderer : IDisposable
{
    private readonly CanvasTextFormat small = new() { FontFamily = "Segoe UI", FontSize = 14 };
    private readonly CanvasTextFormat large = new() { FontFamily = "Segoe UI", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 38 };
    private readonly CanvasTextFormat centred = new() { FontFamily = "Segoe UI", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 18, HorizontalAlignment = CanvasHorizontalAlignment.Center };
    private readonly CanvasTextFormat right = new() { FontFamily = "Segoe UI", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 30, HorizontalAlignment = CanvasHorizontalAlignment.Right };
    private readonly List<(TaikoJudgement Hit, double Born)> effects = new();
    private readonly List<double> errors = new();
    private Beatmap map;
    private double[] prefixEnd = Array.Empty<double>();
    private double feedbackAt = -10000, pressedAt = -10000, displayedScore;
    private bool pressedRim;
    private TaikoJudgement feedback;
    public Color Accent { get; set; } = Color.FromArgb(255, 105, 126, 255);
    public bool Dark { get; set; } = true;
    private Color Ink => Dark ? Color.FromArgb(255, 243, 244, 250) : Color.FromArgb(255, 28, 30, 42);
    private Color Panel => Dark ? Color.FromArgb(255, 35, 36, 44) : Color.FromArgb(255, 248, 248, 252);
    private Color Rim => Mix(Accent, Ink, .65f);
    private static Color Alpha(Color c, float opacity) => Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), c.R, c.G, c.B);
    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public void Reset(Beatmap beatmap)
    {
        map = beatmap; effects.Clear(); errors.Clear(); displayedScore = 0; feedback = null;
        prefixEnd = new double[map.Notes.Count];
        double end = 0;
        for (int i = 0; i < prefixEnd.Length; i++) prefixEnd[i] = end = Math.Max(end, map.Notes[i].End);
    }
    public void Press(bool rim, double now) { pressedAt = now; pressedRim = rim; }
    public void Judge(TaikoJudgement hit, double now)
    {
        if (effects.Count == 32) effects.RemoveAt(0);
        effects.Add((hit, now)); feedback = hit; feedbackAt = now;
        if (hit.Error.HasValue && hit.Value is 150 or 300)
        { if (errors.Count == 24) errors.RemoveAt(0); errors.Add(hit.Error.Value); }
    }
    public void Draw(CanvasDrawingSession d, float w, float h, TaikoSession game, double time, double now, bool active, bool paused, double duration)
    {
        if (w < 2 || h < 2) return;
        bool compact = h < 300;
        float x = Math.Clamp(w * .19f, 82, 150), y = h * (compact ? .53f : .48f);
        float r = Math.Clamp(h * .065f, 19, 31), lane = r + (compact ? 17 : 27);
        float beat = 0;
        bool kiai = false;
        if (active && map != null)
        {
            // Binary search includes inherited timing points carrying Kiai changes.
            int lo = 0, hi = map.Timing.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (map.Timing[mid].Time <= time) lo = mid + 1; else hi = mid; }
            if (lo > 0)
            {
                var timing = map.Timing[lo - 1];
                var phase = (time - timing.Time) / timing.BeatLength;
                beat = (float)Math.Pow(1 - (phase - Math.Floor(phase)), 3); kiai = timing.Kiai;
            }
        }
        // Lift the whole lane, then give each object its own contact shadow.
        for (int i = 5; i >= 1; i--)
            d.FillRoundedRectangle(10 - i, y - lane + 6 - i, w - 20 + i * 2, lane * 2 + i * 2, 22 + i, 22 + i, Color.FromArgb((byte)(Dark ? 8 : 3), 0, 0, 0));
        d.FillRoundedRectangle(10, y - lane, w - 20, lane * 2, 22, 22, Panel);
        d.FillRoundedRectangle(10, y - lane, w - 20, lane * 2, 22, 22, Alpha(Accent, .025f + beat * (kiai ? .11f : .035f)));
        d.DrawRoundedRectangle(10, y - lane, w - 20, lane * 2, 22, 22, Alpha(Ink, .10f), 1);
        d.DrawLine(x, y, w - 24, y, Alpha(Ink, .10f), 1);
        d.DrawLine(x, y - lane + 1, w - 40, y - lane + 1, Alpha(Ink, .12f), 1);
        // Musical pulse stays behind the lane and is deliberately restrained.
        d.DrawCircle(x, y, r + 17 + beat * 8, Alpha(Accent, beat * .13f), 4);

        var press = (float)Math.Max(0, 1 - (now - pressedAt) / 150);
        var hitColour = pressedRim ? Rim : Accent;
        d.FillCircle(x, y + 4, r + 12, Color.FromArgb(65, 0, 0, 0));
        d.FillCircle(x, y, r + 11, Dark ? Color.FromArgb(255, 22, 23, 30) : Color.FromArgb(255, 232, 233, 242));
        d.FillCircle(x, y, r + 9, Alpha(hitColour, press * .45f));
        d.DrawCircle(x, y, r + 9, Alpha(Ink, .50f + press * .5f), 2);
        d.DrawCircle(x, y, r - 2, Alpha(Ink, .13f + press * .4f), 1);
        d.DrawLine(x - 5, y, x + 5, y, Alpha(Ink, .45f), 1);
        d.DrawLine(x, y - 5, x, y + 5, Alpha(Ink, .45f), 1);

        if (active && game != null)
        {
            int lo = 0, hi = prefixEnd.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (prefixEnd[mid] < time - 160) lo = mid + 1; else hi = mid; }
            TaikoNote next = null;
            for (int i = lo; i < game.Objects.Count; i++)
            {
                var state = game.Objects[i]; var n = state.Note;
                if (n.Time > time + 2250) break;
                if (state.Judged || n.End < time - 160) continue;
                if (next == null && n.Kind is NoteKind.Centre or NoteKind.Rim && n.Time >= time) next = n;
                var nx = x + (float)((n.Time - time) / 2000) * (w - x);
                var nr = n.Strong ? r * 1.23f : r;
                var colour = n.Kind == NoteKind.Rim ? Rim : Accent;
                if (n.Kind is NoteKind.Roll or NoteKind.Swell)
                {
                    var end = Math.Min(w + nr, x + (float)((n.End - time) / 2000) * (w - x));
                    var begin = Math.Max(x, nx);
                    d.DrawLine(begin, y + 5, end, y + 5, Color.FromArgb(65, 0, 0, 0), nr * 1.7f);
                    d.DrawLine(begin, y, end, y, Mix(Accent, Panel, .35f), nr * 1.7f);
                    d.DrawLine(begin, y - nr * .45f, end, y - nr * .45f, Alpha(Ink, .25f), 2);
                    Note(d, begin, y, nr, Accent, true);
                    if (n.Kind == NoteKind.Swell)
                        d.DrawText($"{state.Hits}/{n.RequiredHits}", new Rect(begin - 50, y + nr + 20, 100, 25), Ink, centred);
                }
                else Note(d, nx, y, nr, colour, n.Kind == NoteKind.Rim);
            }
            // One ring for the nearest note avoids a stack of competing cues.
            if (next != null && next.Time - time <= 850)
            {
                float progress = (float)Math.Clamp(1 - (next.Time - time) / 850, 0, 1);
                var colour = next.Kind == NoteKind.Rim ? Rim : Accent;
                d.DrawCircle(x, y, r + 9 + (1 - progress) * 48, Alpha(colour, .25f + progress * .65f), 2.5f);
            }
            DrawEffects(d, x, y, r, now);
            displayedScore += (game.Score - displayedScore) * .22;
            if (Math.Abs(game.Score - displayedScore) < 1) displayedScore = game.Score;
            d.DrawText(((long)displayedScore).ToString("N0"), new Rect(w - 230, 18, 212, 45), Ink, right);
            d.DrawText($"{game.Accuracy:P1}  ·  ТОЧНОСТЬ", new Vector2(w - 204, compact ? 54 : 64), Alpha(Ink, .75f), small);
            d.DrawText($"×{game.Combo}", new Vector2(20, 27), Accent, large);
            if (!compact) d.DrawText("КОМБО", new Vector2(22, 73), Alpha(Ink, .5f), small);
            var lead = game.Objects[0].Note.Time - time;
            if (paused || lead is > 0 and <= 3000)
                d.DrawText(paused ? "Пауза · Esc — продолжить" : $"{Math.Ceiling(lead / 1000)}", new Rect(0, y - lane - 82, w, 45), Ink, centred);
            if (duration > 0)
            {
                d.FillRoundedRectangle(18, h - 6, w - 36, 3, 1.5f, 1.5f, Alpha(Ink, .08f));
                d.FillRoundedRectangle(18, h - 6, (w - 36) * (float)Math.Clamp(time / duration, 0, 1), 3, 1.5f, 1.5f, Accent);
            }
        }
        if (y + lane + 60 < h) DrawControls(d, x, y + lane + 32, r, now, w);
        DrawTiming(d, w, h);
    }
    private void Note(CanvasDrawingSession d, float x, float y, float r, Color colour, bool rim)
    {
        d.FillCircle(x, y + 6, r + 2, Color.FromArgb(35, 0, 0, 0));
        d.FillCircle(x, y + 3, r, Mix(colour, Color.FromArgb(255, 0, 0, 0), .45f));
        d.FillCircle(x, y - 1, r, colour);
        d.FillCircle(x - r * .16f, y - r * .23f, r * .72f, Alpha(Color.FromArgb(255, 255, 255, 255), .12f));
        d.DrawCircle(x, y - 1, r - .7f, Alpha(Ink, .38f), 1.5f);
        if (rim) { d.FillCircle(x, y - 1, r * .52f, Mix(Accent, Panel, .3f)); d.DrawCircle(x, y - 1, r * .52f, Alpha(Ink, .25f), 1); }
        else { d.FillCircle(x - r * .20f, y - r * .08f, 2, Alpha(Ink, .85f)); d.FillCircle(x + r * .20f, y - r * .08f, 2, Alpha(Ink, .85f)); }
    }
    private void DrawEffects(CanvasDrawingSession d, float x, float y, float r, double now)
    {
        effects.RemoveAll(e => now - e.Born > 650);
        foreach (var e in effects)
        {
            float t = (float)((now - e.Born) / 650), fade = 1 - t;
            var colour = e.Hit.Value == 0 ? Color.FromArgb(255, 232, 153, 116) : e.Hit.Kind == NoteKind.Rim ? Rim : Accent;
            d.DrawCircle(x, y, r + 9 + t * (e.Hit.Strong ? 62 : 42), Alpha(colour, fade * .65f), 2 * fade + .5f);
            if (e.Hit.Value == 0) continue;
            for (int i = 0; i < 7; i++)
            {
                float angle = -.25f - i * .43f;
                float distance = r + t * (44 + i % 3 * 18);
                var p = new Vector2(x + MathF.Cos(angle) * distance, y + MathF.Sin(angle) * distance + t * t * 30);
                d.FillCircle(p, (e.Hit.Strong ? 4 : 3) * fade, Alpha(colour, fade * .9f));
            }
        }
        if (feedback != null && now - feedbackAt < 700)
        {
            float fade = (float)Math.Clamp(1 - (now - feedbackAt - 220) / 480, 0, 1);
            string text = feedback.Value switch { 0 => "МИМО", 150 => "ХОРОШО", 300 => "ОТЛИЧНО", 350 => "СИЛЬНЫЙ!", _ => "ДРОБЬ!" };
            d.DrawText(text, new Rect(x - 100, y - r - 66 - (float)(now - feedbackAt) * .018f, 200, 28), Alpha(feedback.Value == 0 ? Ink : Accent, fade), centred);
            if (feedback.Error.HasValue)
                d.DrawText($"{feedback.Error.Value:+0;−0;0} мс", new Rect(x - 100, y - r - 40, 200, 24), Alpha(Ink, fade * .55f), centred);
        }
    }
    private void DrawControls(CanvasDrawingSession d, float x, float y, float r, double now, float w)
    {
        var press = (float)Math.Max(0, 1 - (now - pressedAt) / 150);
        float drum = Math.Clamp(r * .75f, 16, 23);
        d.FillCircle(x, y + 5, drum + 5, Color.FromArgb(35, 0, 0, 0));
        d.FillCircle(x, y, drum + 4, Mix(Rim, Panel, 1 - (pressedRim ? press * .7f : .2f)));
        d.FillCircle(x, y, drum - 3, Mix(Accent, Panel, 1 - (!pressedRim ? press * .7f : .4f)));
        d.DrawCircle(x, y, drum + 4, Alpha(Ink, .4f), 1.5f);
    }
    private void DrawTiming(CanvasDrawingSession d, float w, float h)
    {
        if (h < 270) return;
        float x = w / 2, y = h - 32;
        d.DrawLine(x - 90, y, x + 90, y, Alpha(Ink, .12f), 4);
        d.DrawLine(x - 34, y, x + 34, y, Alpha(Accent, .30f), 4);
        d.DrawLine(x, y - 6, x, y + 6, Alpha(Ink, .55f), 1);
        for (int i = 0; i < errors.Count; i++)
        {
            float marker = x + (float)Math.Clamp(errors[i], -90, 90);
            d.DrawLine(marker, y - 4, marker, y + 4, Alpha(Accent, .15f + .7f * (i + 1) / errors.Count), 2);
        }
    }
    public void Dispose() { small.Dispose(); large.Dispose(); centred.Dispose(); right.Dispose(); }
}
