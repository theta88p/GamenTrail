namespace GamenTrail.Core.Video;

public sealed record VideoCodecDescriptor(string FourCc, string DisplayName, bool IsAvailable);

public interface IVideoCodecProvider
{
    IReadOnlyList<VideoCodecDescriptor> GetCodecs(VideoEncodingPixelFormat pixelFormat = VideoEncodingPixelFormat.Bgra32,
        IReadOnlyDictionary<string, byte[]>? codecStates = null);
}
