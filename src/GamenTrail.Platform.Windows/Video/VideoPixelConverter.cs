using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Video;

/// <summary>Converts bottom-up BGRA capture data to VCM layouts. YUV uses BT.601 limited range.</summary>
internal static class VideoPixelConverter
{
    public static (int Width, int Height) AlignDimensions(int width, int height, VideoEncodingPixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (format is VideoEncodingPixelFormat.Yuy2 or VideoEncodingPixelFormat.Yv12 or VideoEncodingPixelFormat.Nv12)
            width = checked((width + 1) & ~1);
        if (format is VideoEncodingPixelFormat.Yv12 or VideoEncodingPixelFormat.Nv12)
            height = checked((height + 1) & ~1);
        return (width, height);
    }

    public static int GetBufferSize(int width, int height, VideoEncodingPixelFormat format)
    {
        if (AlignDimensions(width, height, format) != (width, height))
            throw new ArgumentException("YUV dimensions must be aligned to the chroma subsampling.");
        return format switch
        {
            VideoEncodingPixelFormat.Bgra32 => checked(width * height * 4),
            VideoEncodingPixelFormat.Bgr24 => checked(((width * 3 + 3) & ~3) * height),
            VideoEncodingPixelFormat.Yuy2 => checked(width * height * 2),
            VideoEncodingPixelFormat.Yv12 or VideoEncodingPixelFormat.Nv12 => checked(width * height * 3 / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static void Convert(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, VideoEncodingPixelFormat format)
    {
        var length = GetBufferSize(width, height, format);
        if (source.Length < checked(width * height * 4) || destination.Length < length)
            throw new ArgumentException("The pixel buffer is too small.");
        destination = destination[..length];
        if (format == VideoEncodingPixelFormat.Bgra32)
        {
            source[..length].CopyTo(destination);
            return;
        }

        if (format == VideoEncodingPixelFormat.Bgr24)
        {
            destination.Clear();
            var stride = (width * 3 + 3) & ~3;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    source.Slice((y * width + x) * 4, 3).CopyTo(destination.Slice(y * stride + x * 3, 3));
            return;
        }

        // YUV DIBs are top-down even with a positive BITMAPINFOHEADER height.
        var blockHeight = format == VideoEncodingPixelFormat.Yuy2 ? 1 : 2;
        var lumaSize = width * height;
        for (var y = 0; y < height; y += blockHeight)
        {
            for (var x = 0; x < width; x += 2)
            {
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var dy = 0; dy < blockHeight; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var offset = ((height - 1 - y - dy) * width + x + dx) * 4;
                        var b = source[offset];
                        var g = source[offset + 1];
                        var r = source[offset + 2];
                        red += r;
                        green += g;
                        blue += b;
                        var luma = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                        var pixel = (y + dy) * width + x + dx;
                        destination[format == VideoEncodingPixelFormat.Yuy2 ? pixel * 2 : pixel] = luma;
                    }
                }

                var count = 2 * blockHeight;
                red = (red + count / 2) / count;
                green = (green + count / 2) / count;
                blue = (blue + count / 2) / count;
                var u = (byte)(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                var v = (byte)(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
                if (format == VideoEncodingPixelFormat.Yuy2)
                {
                    var offset = (y * width + x) * 2;
                    destination[offset + 1] = u;
                    destination[offset + 3] = v;
                }
                else if (format == VideoEncodingPixelFormat.Nv12)
                {
                    var offset = lumaSize + y / 2 * width + x;
                    destination[offset] = u;
                    destination[offset + 1] = v;
                }
                else
                {
                    var offset = y / 2 * (width / 2) + x / 2;
                    destination[lumaSize + offset] = v;
                    destination[lumaSize + lumaSize / 4 + offset] = u;
                }
            }
        }
    }
}