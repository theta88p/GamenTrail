using System.Diagnostics;

namespace GamenTrail.Platform.Windows.Video;

public sealed unsafe partial class VcmVideoEncoder
{
    private const uint IcmGetState = 0x5000;
    private const uint IcmSetState = 0x5001;
    private const uint IcmConfigure = 0x500A;
    private const int MaximumStateSize = 16 * 1024 * 1024;

    public static bool CanConfigure(string codecFourCc)
    {
        var codec = IcOpen(ToFourCc("vidc"), ToFourCc(codecFourCc), IcModeCompress);
        if (codec == 0) return false;
        try
        {
            return IcSendMessage(codec, IcmConfigure, -1, 1) == 0;
        }
        finally
        {
            CloseConfigurationCodec(codec);
        }
    }

    /// <summary>Shows the driver's modal configuration dialog on the calling UI thread.</summary>
    public static byte[] Configure(string codecFourCc, nint ownerWindow, ReadOnlyMemory<byte> state = default)
    {
        if (ownerWindow == 0) throw new ArgumentException("A parent window is required.", nameof(ownerWindow));
        var codec = OpenConfigurationCodec(codecFourCc);
        try
        {
            if (IcSendMessage(codec, IcmConfigure, -1, 1) != 0)
                throw new NotSupportedException("このコーデックには設定画面がありません。");
            RestoreCodecState(codec, state);
            var result = IcSendMessage(codec, IcmConfigure, ownerWindow, 0);
            if (result == -10) return state.ToArray(); // ICERR_ABORT: driver cancelled.
            ThrowForCodecError(result, "コーデックの設定画面を開けませんでした。");
            return ReadCodecState(codec);
        }
        finally
        {
            CloseConfigurationCodec(codec);
        }
    }

    internal static byte[] GetConfigurationState(string codecFourCc, ReadOnlyMemory<byte> state = default)
    {
        var codec = OpenConfigurationCodec(codecFourCc);
        try
        {
            RestoreCodecState(codec, state);
            return ReadCodecState(codec);
        }
        finally
        {
            CloseConfigurationCodec(codec);
        }
    }

    private static nint OpenConfigurationCodec(string codecFourCc)
    {
        var codec = IcOpen(ToFourCc("vidc"), ToFourCc(codecFourCc), IcModeCompress);
        return codec != 0 ? codec : throw new VcmCodecException($"コーデック '{codecFourCc}' を開けませんでした。");
    }

    private static byte[] ReadCodecState(nint codec)
    {
        var size = IcSendMessage(codec, IcmGetState, 0, 0);
        // Some codecs keep configuration in their own registry and expose no state.
        if (size is 0 or -1) return [];
        if (size < 0 || size > MaximumStateSize)
            throw new VcmCodecException("コーデックが返した設定データのサイズが不正です。");
        var state = new byte[(int)size];
        fixed (byte* data = state)
        {
            var result = IcSendMessage(codec, IcmGetState, (nint)data, state.Length);
            // Drivers return either ICERR_OK or the number of bytes copied.
            if (result != 0 && result != state.Length)
                throw new VcmCodecException($"コーデックの設定を取得できませんでした。VCM error: {result}.");
        }

        return state;
    }

    private static void RestoreCodecState(nint codec, ReadOnlyMemory<byte> state)
    {
        if (state.IsEmpty) return;
        if (state.Length > MaximumStateSize)
            throw new VcmCodecException("コーデックの設定データが大きすぎます。");
        using var pin = state.Pin();
        var result = IcSendMessage(codec, IcmSetState, (nint)pin.Pointer, state.Length);
        // Both ICERR_OK and a byte count are used by installed VCM drivers.
        if (result != 0 && result != state.Length)
            throw new VcmCodecException($"保存したコーデック設定を復元できませんでした。VCM error: {result}.");
    }

    private static void CloseConfigurationCodec(nint codec)
    {
        var result = IcClose(codec);
        Debug.Assert(result == 0, "VCM codec close failed.");
    }
}