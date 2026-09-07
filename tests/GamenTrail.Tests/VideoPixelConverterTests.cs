using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Video;

internal static class VideoPixelConverterTests
{
    public static void Run()
    {
        // Bottom-up: blue/blue on the bottom, red/red on the top.
        byte[] source = [255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255];
        Check(source, VideoEncodingPixelFormat.Bgra32, source);
        Check(source, VideoEncodingPixelFormat.Bgr24,
            [255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0]);
        Check(source, VideoEncodingPixelFormat.Yuy2, [82, 90, 82, 240, 41, 240, 41, 110]);
        Check(source, VideoEncodingPixelFormat.Yv12, [82, 82, 41, 41, 175, 165]);
        Check(source, VideoEncodingPixelFormat.Nv12, [82, 82, 41, 41, 165, 175]);

        byte[] black = new byte[16];
        Check(black, VideoEncodingPixelFormat.Yv12, [16, 16, 16, 16, 128, 128]);
        byte[] white = Enumerable.Repeat((byte)255, 16).ToArray();
        Check(white, VideoEncodingPixelFormat.Nv12, [235, 235, 235, 235, 128, 128]);

        Assert(VideoPixelConverter.AlignDimensions(641, 481, VideoEncodingPixelFormat.Yuy2) == (642, 481),
            "YUY2 must align width only.");
        Assert(VideoPixelConverter.AlignDimensions(641, 481, VideoEncodingPixelFormat.Yv12) == (642, 482),
            "YV12 must align both dimensions.");
        Assert(VideoPixelConverter.AlignDimensions(641, 481, VideoEncodingPixelFormat.Nv12) == (642, 482),
            "NV12 must align both dimensions.");
        Assert(VideoPixelConverter.AlignDimensions(641, 481, VideoEncodingPixelFormat.Bgra32) == (641, 481),
            "RGB must preserve dimensions.");
        try
        {
            VideoPixelConverter.GetBufferSize(3, 2, VideoEncodingPixelFormat.Yuy2);
            throw new InvalidOperationException("Unaligned YUV input must be rejected.");
        }
        catch (ArgumentException)
        {
        }

        Console.WriteLine("Pixel conversion tests passed.");
    }

    private static void Check(byte[] source, VideoEncodingPixelFormat format, byte[] expected)
    {
        var actual = new byte[VideoPixelConverter.GetBufferSize(2, 2, format)];
        VideoPixelConverter.Convert(source, actual, 2, 2, format);
        Assert(actual.AsSpan().SequenceEqual(expected), $"{format} color values, row orientation and plane order must match.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}