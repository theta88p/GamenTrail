namespace GamenTrail.Platform.Windows.Audio;

internal sealed class AudioCaptureTimeline(int sampleRate)
{
    private long _nextSample;
    private bool _started;

    internal TimeSpan GetTimestamp(uint frames, uint flags, long qpcTicks, long fallbackTicks)
    {
        var valid = (flags & 4) == 0;
        var discontinuity = (flags & 1) != 0;
        // Device positions can use the endpoint's rate rather than the converted PCM rate.
        // QPC anchors the start and actual discontinuities; ordinary packets stay contiguous.
        var start = !_started
            ? ToSamples(valid ? qpcTicks : fallbackTicks)
            : discontinuity
                ? Math.Max(_nextSample, ToSamples(valid ? qpcTicks : fallbackTicks))
                : _nextSample;
        _started = true;
        _nextSample = checked(start + frames);
        return TimeSpan.FromTicks(checked((long)(start * (decimal)TimeSpan.TicksPerSecond / sampleRate)));
    }

    private long ToSamples(long ticks) => Math.Max(0,
        checked((long)Math.Round(ticks * (decimal)sampleRate / TimeSpan.TicksPerSecond)));
}
