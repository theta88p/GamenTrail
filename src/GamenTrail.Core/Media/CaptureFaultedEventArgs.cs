namespace GamenTrail.Core.Media;

public sealed class CaptureFaultedEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}
