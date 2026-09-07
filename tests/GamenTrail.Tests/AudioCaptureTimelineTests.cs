using GamenTrail.Platform.Windows.Audio;

internal static class AudioCaptureTimelineTests
{
    internal static void Run()
    {
        foreach (var rate in new[] { 44100, 48000 })
        {
            var clock = new AudioCaptureTimeline(rate);
            var frames = (uint)(rate / 100);
            long Sample(TimeSpan t) => (long)Math.Round(t.Ticks * (decimal)rate / TimeSpan.TicksPerSecond);
            for (uint i = 0; i < 10000; i++)
            {
                var jitter = i == 0 ? 0 : (i % 2 == 0 ? 15000 : -15000);
                var actual = Sample(clock.GetTimestamp(frames, 0, i * 100000L + jitter, 0));
                if (actual != i * frames) throw new InvalidOperationException("QPC jitter must not insert silence or remove PCM samples.");
            }
            var next = 10000UL * frames;
            var afterGap = Sample(clock.GetTimestamp(frames, 1, 1000300000L, 0));
            if (afterGap != (long)next + frames * 3) throw new InvalidOperationException("Real missing device frames must retain their gap.");
            var error = Sample(clock.GetTimestamp(frames, 4, long.MaxValue, 0));
            var recovered = Sample(clock.GetTimestamp(frames, 0, 0, 0));
            if (error != afterGap + frames || recovered != error + frames)
                throw new InvalidOperationException("Timestamp errors and position resets must preserve continuous audio.");
        }
        Console.WriteLine("Audio capture timeline tests passed.");
    }
}
