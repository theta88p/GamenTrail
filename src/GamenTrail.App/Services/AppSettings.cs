namespace GamenTrail.App.Services;

public sealed class AppSettings
{
    public string CaptureMode { get; set; } = "Monitor";

    public string? MonitorId { get; set; }

    public string? WindowId { get; set; }

    public string WindowWidth { get; set; } = "1280";

    public string WindowHeight { get; set; } = "720";

    public string CodecFourCc { get; set; } = "UMRG";

    public Dictionary<string, byte[]> CodecStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string VideoPixelFormat { get; set; } = "Bgra32";

    public double FrameRate { get; set; } = 30;

    public bool IncludeCursor { get; set; } = true;

    public bool ShowCaptureBorder { get; set; }

    public bool DisableWindowCornerRounding { get; set; }

    public bool CaptureOverlappingWindows { get; set; }

    public string AudioMode { get; set; } = "System";

    public string? AudioDeviceId { get; set; }

    public string? ProcessName { get; set; }

    public string LameExecutablePath { get; set; } = string.Empty;
    public string FlacExecutablePath { get; set; } = string.Empty;
    public int Mp3BitRate { get; set; } = 192;
    public int FlacCompressionLevel { get; set; } = 5;

    public string AudioCodec { get; set; } = "Float32";

    public Dictionary<string, byte[]> AudioCodecFormats { get; set; } = new(StringComparer.Ordinal);

    public int AudioSampleRate { get; set; } = 48_000;

    public string ContainerExtension { get; set; } = "avi";

    public string OutputFolder { get; set; } = string.Empty;

    public string RegionX { get; set; } = "0";

    public string RegionY { get; set; } = "0";

    public string RegionWidth { get; set; } = "1280";

    public string RegionHeight { get; set; } = "720";
}
