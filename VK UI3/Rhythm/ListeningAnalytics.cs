using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using VK_UI3.Services;
using Windows.Media.Playback;

namespace VK_UI3.Rhythm;

// Observe regular playback without changing its queue or transport behaviour.
public static class ListeningAnalytics
{
    private static DispatcherTimer timer;
    private static string track, title, id;
    private static double previous, duration, listened;
    private static readonly List<(double Start, double End)> intervals = new();
    private static bool playing;
    public static void Initialize(Window window)
    {
        if (timer != null) return;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Sample(); timer.Start();
        window.Closed += (_, _) => { Sample(); End("app_closed"); timer.Stop(); };
        MediaPlayerService.AudioPlayedChangeEvent += (_, _) => window.DispatcherQueue.TryEnqueue(Sample);
    }
    private static void Sample()
    {
        var audio = MediaPlayerService.PlayingTrack?.audio;
        var current = audio == null ? null : $"{audio.OwnerId}_{audio.Id}";
        var session = MediaPlayerService.MediaPlayer.PlaybackSession;
        var position = session.Position.TotalMilliseconds;
        if (current != track || (playing && previous > 2000 && position < 500))
        {
            End(current != track ? "track_changed" : "restarted");
            track = current; title = audio?.Title; id = Guid.NewGuid().ToString("N"); duration = (audio?.Duration ?? 0) * 1000;
            previous = position; listened = 0; intervals.Clear();
            if (track != null) _ = RhythmAnalytics.Write("track_started", new { track_id = track, track_title = title, artist = audio.Artist, listening_session_id = id, duration_ms = duration, playback_context = "player" });
        }
        if (playing && !MediaPlayerService.RhythmGameActive && position > previous && position - previous < 1500)
        { listened += position - previous; intervals.Add((previous, position)); }
        previous = position;
        playing = session.PlaybackState == MediaPlaybackState.Playing && !MediaPlayerService.RhythmGameActive;
        if (track != null && duration > 0 && position >= duration - 250 && !playing) End("completed");
    }
    private static void End(string reason)
    {
        if (id == null || track == null) return;
        double coverage = 0, end = 0;
        foreach (var interval in intervals.OrderBy(i => i.Start)) { coverage += Math.Max(0, interval.End - Math.Max(end, interval.Start)); end = Math.Max(end, interval.End); }
        _ = RhythmAnalytics.Write("track_listening_ended", new { track_id = track, track_title = title, listening_session_id = id, reason, position_ms = previous, duration_ms = duration, position_percent = duration > 0 ? Math.Clamp(previous / duration * 100, 0, 100) : (double?)null, listened_ms = listened, coverage_percent = duration > 0 ? Math.Clamp(coverage / duration * 100, 0, 100) : (double?)null, playback_context = "player" });
        id = null;
    }
}
