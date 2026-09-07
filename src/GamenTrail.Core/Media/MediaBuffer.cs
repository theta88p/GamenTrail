using System.Buffers;

namespace GamenTrail.Core.Media;

public sealed class MediaBuffer : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    public MediaBuffer(IMemoryOwner<byte> owner, int length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, owner.Memory.Length);

        _owner = owner;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory =>
        (_owner ?? throw new ObjectDisposedException(nameof(MediaBuffer))).Memory[..Length];

    public static MediaBuffer CopyFrom(ReadOnlySpan<byte> source)
    {
        var owner = MemoryPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(owner.Memory.Span);
        return new MediaBuffer(owner, source.Length);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
