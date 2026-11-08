using System;

namespace VK_UI3.Rhythm;

// Smooth coarse media position reports against a monotonic clock. Never integrate
// frame counts: rendering and input use the same audio-synchronised timeline.
public sealed class PlaybackClock
{
    private bool initialised, wasPlaying;
    private double position, lastNow, sample, sampleAt;
    public void Reset() => initialised = false;
    public double Read(double audioMilliseconds, double nowMilliseconds, bool playing)
    {
        if (!initialised || !playing || !wasPlaying)
        {
            position = sample = audioMilliseconds;
            lastNow = sampleAt = nowMilliseconds;
            initialised = true; wasPlaying = playing;
            return position;
        }
        var elapsed = Math.Max(0, nowMilliseconds - lastNow);
        var next = position + elapsed;
        if (audioMilliseconds != sample)
        {
            // Seek/discontinuity resets explicitly; normal corrections are slewed
            // so a delayed report cannot move a travelling note backwards.
            var error = audioMilliseconds - next;
            next = Math.Abs(error) > 250 ? audioMilliseconds : next + Math.Clamp(error, -elapsed * .15, elapsed * .15);
            sample = audioMilliseconds; sampleAt = nowMilliseconds;
        }
        position = Math.Min(next, sample + 250);
        lastNow = nowMilliseconds;
        return position;
    }
}
