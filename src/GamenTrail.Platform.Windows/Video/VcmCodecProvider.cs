using System.Runtime.InteropServices;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Video;

/// <summary>Enumerates installed VCM encoders available to this process.</summary>
public sealed unsafe partial class VcmCodecProvider : IVideoCodecProvider
{
    private const uint VideoType = 0x63646976; // vidc

    public IReadOnlyList<VideoCodecDescriptor> GetCodecs(VideoEncodingPixelFormat pixelFormat = VideoEncodingPixelFormat.Bgra32,
        IReadOnlyDictionary<string, byte[]>? codecStates = null)
    {
        var codecs = new List<VideoCodecDescriptor>();
        var handlers = new HashSet<uint>();
        for (uint index = 0; ; index++)
        {
            var info = new CodecInfo { Size = (uint)sizeof(CodecInfo) };
            if (IcInfo(VideoType, index, &info) == 0)
            {
                break;
            }

            if (!handlers.Add(info.Handler))
            {
                continue;
            }

            var fourCc = new string(
            [
                (char)(info.Handler & 0xff),
                (char)((info.Handler >> 8) & 0xff),
                (char)((info.Handler >> 16) & 0xff),
                (char)((info.Handler >> 24) & 0xff),
            ]);
            var state = codecStates is not null && codecStates.TryGetValue(fourCc, out var savedState)
                ? savedState : Array.Empty<byte>();
            if (fourCc.Any(static character => character is < ' ' or > '~') ||
                !VcmVideoEncoder.IsCodecAvailable(fourCc, pixelFormat, state))
            {
                continue;
            }

            var codecHandle = IcOpen(VideoType, info.Handler, 1);
            if (codecHandle != 0)
            {
                try
                {
                    var details = new CodecInfo { Size = (uint)sizeof(CodecInfo) };
                    if (IcGetInfo(codecHandle, &details, (uint)sizeof(CodecInfo)) > 0)
                    {
                        info = details;
                    }
                }
                finally
                {
                    var closeResult = IcClose(codecHandle);
                    System.Diagnostics.Debug.Assert(closeResult == 0, "VCM codec close failed.");
                }
            }
            var name = new string(info.Description, 0, 128).TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = new string(info.Name, 0, 16).TrimEnd('\0').Trim();
            }

            codecs.Add(new VideoCodecDescriptor(
                fourCc,
                string.IsNullOrWhiteSpace(name) ? fourCc : $"{name} ({fourCc})",
                IsAvailable: true));
        }

        return codecs.OrderBy(static codec => codec.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    [LibraryImport("msvfw32.dll", EntryPoint = "ICInfo")]
    private static partial int IcInfo(uint type, uint index, CodecInfo* info);

    [LibraryImport("msvfw32.dll", EntryPoint = "ICOpen")]
    private static partial nint IcOpen(uint type, uint handler, uint mode);

    [LibraryImport("msvfw32.dll", EntryPoint = "ICClose")]
    private static partial uint IcClose(nint codec);

    [LibraryImport("msvfw32.dll", EntryPoint = "ICGetInfo")]
    private static partial nint IcGetInfo(nint codec, CodecInfo* info, uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct CodecInfo
    {
        public uint Size;
        public uint Type;
        public uint Handler;
        public uint Flags;
        public uint Version;
        public uint IcmVersion;
        public fixed char Name[16];
        public fixed char Description[128];
        public fixed char Driver[128];
    }
}