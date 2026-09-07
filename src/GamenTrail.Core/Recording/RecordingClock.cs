using System.Diagnostics;

namespace GamenTrail.Core.Recording;

public sealed class RecordingClock
{
    private long _origin;
    private long _stoppedElapsedTicks;
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Start()
    {
        _origin = Stopwatch.GetTimestamp();
        Volatile.Write(ref _stoppedElapsedTicks, 0);
        Volatile.Write(ref _running, 1);
    }

    public TimeSpan GetElapsedTime()
    {
        if (IsRunning)
        {
            return Stopwatch.GetElapsedTime(_origin);
        }

        return TimeSpan.FromTicks(Volatile.Read(ref _stoppedElapsedTicks));
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) != 0)
        {
            Volatile.Write(ref _stoppedElapsedTicks, Stopwatch.GetElapsedTime(_origin).Ticks);
        }
    }
}
