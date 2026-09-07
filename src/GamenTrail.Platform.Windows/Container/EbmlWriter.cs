using System.Buffers.Binary;
using System.Text;

namespace GamenTrail.Platform.Windows.Container;

internal sealed class EbmlWriter(Stream stream)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public long Position => _stream.Position;

    public async ValueTask WriteMasterAsync(
        uint id,
        Func<EbmlWriter, ValueTask> writeChildren,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeChildren);

        using var content = new MemoryStream();
        var contentWriter = new EbmlWriter(content);
        await writeChildren(contentWriter).ConfigureAwait(false);

        await WriteIdAsync(id, cancellationToken).ConfigureAwait(false);
        await WriteSizeAsync((ulong)content.Length, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(content.GetBuffer().AsMemory(0, checked((int)content.Length)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask WriteUnsignedAsync(
        uint id,
        ulong value,
        CancellationToken cancellationToken)
    {
        var width = GetUnsignedWidth(value);
        var bytes = new byte[width];
        WriteBigEndian(value, bytes);
        await WriteBinaryAsync(id, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteDoubleAsync(
        uint id,
        double value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        await WriteBinaryAsync(id, bytes, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteUtf8Async(uint id, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteBinaryAsync(id, Encoding.UTF8.GetBytes(value), cancellationToken);
    }

    public async ValueTask WriteBinaryAsync(
        uint id,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        await WriteIdAsync(id, cancellationToken).ConfigureAwait(false);
        await WriteSizeAsync((ulong)value.Length, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteIdAsync(uint id, CancellationToken cancellationToken)
    {
        var width = id switch
        {
            <= 0xFF => 1,
            <= 0xFFFF => 2,
            <= 0xFFFFFF => 3,
            _ => 4,
        };

        var bytes = new byte[width];
        WriteBigEndian(id, bytes);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteSizeAsync(
        ulong value,
        CancellationToken cancellationToken,
        int? fixedWidth = null)
    {
        var width = fixedWidth ?? GetVintWidth(value);
        ValidateVint(value, width);

        var bytes = new byte[width];
        var encoded = value | (1UL << (width * 7));
        WriteBigEndian(encoded, bytes);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long> WriteUnknownSizeAsync(
        int width,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 8);

        var position = _stream.Position;
        var bytes = new byte[width];
        Array.Fill(bytes, byte.MaxValue);
        bytes[0] = (byte)(byte.MaxValue >> (width - 1));
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return position;
    }

    public async ValueTask PatchSizeAsync(
        long position,
        ulong value,
        int width,
        CancellationToken cancellationToken)
    {
        if (!_stream.CanSeek)
        {
            throw new NotSupportedException("The EBML output stream must be seekable to patch element sizes.");
        }

        var returnPosition = _stream.Position;
        _stream.Position = position;
        await WriteSizeAsync(value, cancellationToken, width).ConfigureAwait(false);
        _stream.Position = returnPosition;
    }

    public async ValueTask PatchDoubleAsync(
        long position,
        double value,
        CancellationToken cancellationToken)
    {
        if (!_stream.CanSeek)
        {
            throw new NotSupportedException("The EBML output stream must be seekable to patch values.");
        }

        var returnPosition = _stream.Position;
        _stream.Position = position;
        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        _stream.Position = returnPosition;
    }

    public async ValueTask WriteRawAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }

    private static int GetUnsignedWidth(ulong value) => value switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= 0xFFFFFF => 3,
        <= uint.MaxValue => 4,
        <= 0xFFFFFFFFFF => 5,
        <= 0xFFFFFFFFFFFF => 6,
        <= 0xFFFFFFFFFFFFFF => 7,
        _ => 8,
    };

    private static int GetVintWidth(ulong value)
    {
        for (var width = 1; width <= 8; width++)
        {
            if (value < GetUnknownVintValue(width))
            {
                return width;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), "EBML VINT value is too large.");
    }

    private static void ValidateVint(ulong value, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 8);

        if (value >= GetUnknownVintValue(width))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value does not fit in the requested VINT width.");
        }
    }

    private static ulong GetUnknownVintValue(int width) => (1UL << (width * 7)) - 1;

    private static void WriteBigEndian(ulong value, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[destination.Length - index - 1] = (byte)(value >> (index * 8));
        }
    }
}
