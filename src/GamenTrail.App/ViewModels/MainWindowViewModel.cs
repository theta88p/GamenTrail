using System.Buffers.Binary;
using GamenTrail.Platform.Windows.Audio;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamenTrail.App.Services;
using GamenTrail.Core.Audio;
using GamenTrail.Core.Recording;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GamenTrailServices _services;
    private readonly IUserDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _statusTimer;
    private readonly SynchronizationContext _uiContext;
    private IReadOnlyList<CaptureTargetDescriptor> _monitors = [];
    private IReadOnlyList<CaptureTargetDescriptor> _windows = [];
    private IReadOnlyList<AudioDeviceDescriptor> _audioDevices = [];
    private IReadOnlyList<ProcessDescriptor> _processes = [];
    private Recorder? _recorder;
    private string? _currentOutputPath;
    private string? _preferredMonitorId;
    private string? _preferredWindowId;
    private string? _preferredAudioDeviceId;
    private string? _preferredProcessName;
    private bool _audioCatalogReady;
    public string LameExecutablePath { get; private set; } = string.Empty;
    public string FlacExecutablePath { get; private set; } = string.Empty;
    public int Mp3BitRate { get; private set; } = 192;
    public int FlacCompressionLevel { get; private set; } = 5;

    public async Task SaveExternalEncoderPathsAsync(string lamePath, string flacPath)
    {
        if (IsRecording) return;
        LameExecutablePath = lamePath;
        FlacExecutablePath = flacPath;
        RefreshAudioCodecs();
        await _settings.SaveAsync(CreateSettings()).ConfigureAwait(true);
    }

    public async Task SaveExternalAudioQualityAsync(bool mp3, int value)
    {
        if (IsRecording) return;
        if (mp3) Mp3BitRate = value;
        else FlacCompressionLevel = value;
        await _settings.SaveAsync(CreateSettings()).ConfigureAwait(true);
    }
    private readonly Dictionary<string, byte[]> _audioCodecFormats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _codecStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectedCodecHasSettings;

    [ObservableProperty]
    private IReadOnlyList<CaptureTargetDescriptor> targets = [];

    [ObservableProperty]
    private IReadOnlyList<object> audioSources = [];

    [ObservableProperty]
    private IReadOnlyList<VideoCodecDescriptor> codecs = [];

    [ObservableProperty]
    private CaptureModeOption? selectedCaptureMode;

    [ObservableProperty]
    private CaptureTargetDescriptor? selectedTarget;

    [ObservableProperty]
    private AudioModeOption? selectedAudioMode;

    [ObservableProperty]
    private object? selectedAudioSource;

    [ObservableProperty]
    private AudioCodecOption? selectedAudioCodec;

    [ObservableProperty]
    private AudioSampleRateOption? selectedAudioSampleRate;

    [ObservableProperty]
    private VideoCodecDescriptor? selectedCodec;

    [ObservableProperty]
    private FrameRateOption? selectedFrameRate;

    [ObservableProperty]
    private ContainerFormatOption? selectedContainerFormat;

    [ObservableProperty]
    private string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    [ObservableProperty]
    private string regionX = "0";

    [ObservableProperty]
    private string regionY = "0";

    [ObservableProperty]
    private string regionWidth = "1280";

    [ObservableProperty]
    private string regionHeight = "720";

    [ObservableProperty]
    private string regionHint = string.Empty;

    [ObservableProperty]
    private string windowWidth = "1280";

    [ObservableProperty]
    private string windowHeight = "720";

    [ObservableProperty]
    private bool includeCursor = true;

    [ObservableProperty]
    private bool showCaptureBorder;

    [ObservableProperty]
    private bool disableWindowCornerRounding;

    [ObservableProperty]
    private bool captureOverlappingWindows;

    [ObservableProperty]
    private bool isRecording;

    [ObservableProperty]
    private string elapsedText = "00:00:00";

    [ObservableProperty]
    private string fileSizeText = "0 B";

    [ObservableProperty]
    private string freeSpaceText = "—";

    [ObservableProperty]
    private string statusText = "準備中...";

    [ObservableProperty]
    private StatusKind statusKind = StatusKind.Ready;

    public MainWindowViewModel(
        GamenTrailServices services,
        IUserDialogService dialogs,
        ISettingsService settings)
    {
        _services = services;
        _dialogs = dialogs;
        _settings = settings;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _statusTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnStatusTimerTick,
            Dispatcher.CurrentDispatcher);

        SelectedCaptureMode = CaptureModes[0];
        SelectedAudioMode = AudioModes[0];
        SelectedAudioCodec = AudioCodecs[1];
        SelectedAudioSampleRate = AudioSampleRates[1];
        SelectedFrameRate = FrameRates.First(option => option.Value == 30);
        SelectedContainerFormat = ContainerFormats[0];
        UpdateFreeSpace();
        PropertyChanged += PreviewPropertyChanged;
    }

    public IReadOnlyList<CaptureModeOption> CaptureModes { get; } =
    [
        new(CaptureMode.Monitor, "モニター全体"),
        new(CaptureMode.Window, "ウィンドウ"),
        new(CaptureMode.Region, "座標指定"),
    ];

    public IReadOnlyList<AudioModeOption> AudioModes { get; } =
    [
        new(AudioMode.System, "システム全体"),
        new(AudioMode.Process, "特定のアプリ"),
        new(AudioMode.None, "音声なし"),
    ];

    [ObservableProperty]
    private IReadOnlyList<AudioCodecOption> audioCodecs =
    [
        new("Pcm16", AudioSampleFormat.SignedInteger, 16, "PCM 16-bit（非圧縮）"),
        new("Float32", AudioSampleFormat.IeeeFloat, 32, "IEEE Float 32-bit（非圧縮）"),
    ];

    public IReadOnlyList<AudioSampleRateOption> AudioSampleRates { get; } =
    [
        new(44_100, "44.1 kHz"),
        new(48_000, "48 kHz"),
    ];

    public IReadOnlyList<FrameRateOption> FrameRates { get; } =
    [
        new(15, "15 fps"),
        new(23.976, "23.976 fps"),
        new(24, "24 fps"),
        new(25, "25 fps"),
        new(29.97, "29.97 fps"),
        new(30, "30 fps"),
        new(50, "50 fps"),
        new(59.94, "59.94 fps"),
        new(60, "60 fps"),
    ];

    public IReadOnlyList<ContainerFormatOption> ContainerFormats { get; } =
    [
        new("avi", "AVI（OpenDML）"),
        new("mkv", "Matroska（MKV）"),
    ];

    public bool IsRegionMode => SelectedCaptureMode?.Mode is CaptureMode.Region;

    public bool IsWindowMode => SelectedCaptureMode?.Mode is CaptureMode.Window;

    public bool IsVisualPickerEnabled => IsWindowMode || IsRegionMode;

    public bool IsSettingsEnabled => !IsRecording;

    public bool CanConfigureAudioCodec => !IsRecording && IsAudioSettingsEnabled && SelectedAudioCodec is not null;

    public bool CanConfigureCodec => !IsRecording && _selectedCodecHasSettings;

    public bool IsAudioSourceEnabled =>
        SelectedAudioMode?.Mode is not AudioMode.None &&
        (SelectedAudioMode?.Mode is not AudioMode.Process || GamenTrailServices.SupportsProcessLoopback);

    public bool IsAudioSettingsEnabled => SelectedAudioMode?.Mode is not AudioMode.None;

    public string AudioSourceLabel =>
        SelectedAudioMode?.Mode is AudioMode.Process ? "対象アプリ" : "出力デバイス";

    public string OutputFileHint =>
        $"ファイル名は GamenTrail_日時.{SelectedContainerFormat?.Extension ?? "avi"} で自動作成されます。";

    public string RecordButtonText => IsRecording ? "録画を停止" : "録画を開始";

    public async Task InitializeAsync()
    {
        try
        {
            var savedSettings = await _settings.LoadAsync().ConfigureAwait(true);
            ApplySettings(savedSettings);
            SetStatus("録画対象とデバイスを確認しています...", StatusKind.Ready);
            _monitors = _services.CaptureTargets.GetMonitors();
            _windows = _services.CaptureTargets.GetWindows();
            _audioDevices = await _services.AudioDevices.GetRenderDevicesAsync().ConfigureAwait(true);
            _processes = _services.Processes.GetProcesses();
            _audioCatalogReady = true;
            RefreshAudioCodecs(savedSettings.AudioCodec);
            RefreshCodecs();
            RefreshTargetItems();
            RefreshAudioItems();
            RestoreRegionCoordinates(savedSettings);
            SetStatus(
                Codecs.Count > 0 ? "録画できます" : "選択した出力色形式に対応する映像コーデックがありません",
                Codecs.Count > 0 ? StatusKind.Ready : StatusKind.Error);
            ToggleRecordingCommand.NotifyCanExecuteChanged();
        }
        catch (Exception error)
        {
            ShowError("初期化に失敗しました", error);
        }
        _previewReady = true;
        QueuePreview();
    }

    [RelayCommand]
    private void RefreshTargets()
    {
        try
        {
            _monitors = _services.CaptureTargets.GetMonitors();
            _windows = _services.CaptureTargets.GetWindows();
            _processes = _services.Processes.GetProcesses();
            RefreshTargetItems();
            RefreshAudioItems();
            SetStatus("一覧を更新しました", StatusKind.Ready);
            ToggleRecordingCommand.NotifyCanExecuteChanged();
        }
        catch (Exception error)
        {
            ShowError("一覧を更新できませんでした", error);
        }
    }

    public async Task PickWindowAsync(
        Func<CancellationToken, Task<CaptureTargetDescriptor?>> picker,
        CancellationToken cancellationToken = default)
    {
        SetStatus("対象ウィンドウをクリックしてください（Escでキャンセル）", StatusKind.Ready);
        var selected = await picker(cancellationToken).ConfigureAwait(true);
        if (selected is null)
        {
            SetStatus("ウィンドウ選択をキャンセルしました", StatusKind.Ready);
            return;
        }

        _windows = _services.CaptureTargets.GetWindows();
        if (_windows.All(candidate => candidate.Id != selected.Id))
        {
            _windows = _windows.Append(selected).ToArray();
        }

        _preferredWindowId = selected.Id;
        SelectedCaptureMode = CaptureModes.First(option => option.Mode is CaptureMode.Window);
        SelectedTarget = _windows.First(candidate => candidate.Id == selected.Id);

        var audioMatched = TrySelectProcessAudio(selected.ProcessId);
        SetStatus(
            audioMatched
                ? $"「{selected.DisplayName}」を選択し、音声も同じアプリに切り替えました"
                : $"「{selected.DisplayName}」を選択しました",
            StatusKind.Ready);
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    public async Task PickRegionAsync(
        Func<CancellationToken, Task<(
            CaptureTargetDescriptor Monitor,
            int X,
            int Y,
            int Width,
            int Height)?>> picker,
        CancellationToken cancellationToken = default)
    {
        SetStatus("録画する範囲をドラッグしてください（Escでキャンセル）", StatusKind.Ready);
        var selection = await picker(cancellationToken).ConfigureAwait(true);
        if (selection is null)
        {
            SetStatus("範囲選択をキャンセルしました", StatusKind.Ready);
            return;
        }

        var value = selection.Value;
        _monitors = _services.CaptureTargets.GetMonitors();
        if (_monitors.All(candidate => candidate.Id != value.Monitor.Id))
        {
            _monitors = _monitors.Append(value.Monitor).ToArray();
        }

        _preferredMonitorId = value.Monitor.Id;
        SelectedCaptureMode = CaptureModes.First(option => option.Mode is CaptureMode.Region);
        SelectedTarget = _monitors.First(candidate => candidate.Id == value.Monitor.Id);
        RegionX = value.X.ToString(CultureInfo.InvariantCulture);
        RegionY = value.Y.ToString(CultureInfo.InvariantCulture);
        RegionWidth = value.Width.ToString(CultureInfo.InvariantCulture);
        RegionHeight = value.Height.ToString(CultureInfo.InvariantCulture);
        SetStatus(
            $"範囲 X {value.X}, Y {value.Y}, {value.Width} × {value.Height} を選択しました",
            StatusKind.Ready);
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    private bool CanResizeWindow() =>
        !IsRecording && !ToggleRecordingCommand.IsRunning &&
        IsWindowMode && SelectedTarget?.Target is CaptureTarget.Window;

    [RelayCommand(CanExecute = nameof(CanResizeWindow))]
    private void ResizeWindow()
    {
        if (!CanResizeWindow())
        {
            return;
        }

        try
        {
            var width = ParseCoordinate(WindowWidth, "幅");
            var height = ParseCoordinate(WindowHeight, "高さ");
            var window = (CaptureTarget.Window)SelectedTarget!.Target;
            var actual = WindowsWindowResizer.Resize(window.Handle, width, height);
            QueuePreview();
            SetStatus(
                actual.Width == width && actual.Height == height
                    ? $"ウィンドウを {actual.Width} × {actual.Height} px に変更しました"
                    : $"アプリのサイズ制限により、実際のサイズは {actual.Width} × {actual.Height} px です",
                StatusKind.Ready);
        }
        catch (Exception error)
        {
            ShowError("ウィンドウサイズを変更できませんでした", error);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var selectedFolder = _dialogs.SelectFolder(OutputFolder);
        if (selectedFolder is not null)
        {
            OutputFolder = selectedFolder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        await SuspendPreviewAsync().ConfigureAwait(true);
        var wasRecording = IsRecording;
        try
        {
            if (wasRecording)
            {
                await StopRecordingAsync().ConfigureAwait(true);
            }
            else
            {
                await StartRecordingAsync().ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            ShowError(
                wasRecording ? "録画を停止できませんでした" : "録画を開始できませんでした",
                error);
            await DisposeRecorderAsync().ConfigureAwait(true);
            SetRecordingState(false);
        }
        finally
        {
            ResumePreview();
        }
    }

    private bool CanToggleRecording() =>
        IsRecording || (Codecs.Count > 0 && _monitors.Count > 0);

    public async Task ShutdownAsync()
    {
        try
        {
            if (_recorder is null)
            {
                return;
            }

            if (_recorder.State is not RecorderState.Idle)
            {
                await _recorder.StopAsync().ConfigureAwait(true);
            }

            await DisposeRecorderAsync().ConfigureAwait(true);
        }
        finally
        {
            await _settings.SaveAsync(CreateSettings()).ConfigureAwait(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _previewReady = false;
        PropertyChanged -= PreviewPropertyChanged;
        await SuspendPreviewAsync().ConfigureAwait(true);
        _statusTimer.Stop();
        await ShutdownAsync().ConfigureAwait(true);
    }

    partial void OnSelectedCaptureModeChanged(CaptureModeOption? value)
    {
        OnPropertyChanged(nameof(IsRegionMode));
        OnPropertyChanged(nameof(IsWindowMode));
        ResizeWindowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsVisualPickerEnabled));
        RefreshTargetItems();
    }

    partial void OnSelectedTargetChanged(CaptureTargetDescriptor? value)
    {
        ResizeWindowCommand.NotifyCanExecuteChanged();
        if (value is not null)
        {
            if (SelectedCaptureMode?.Mode is CaptureMode.Window)
            {
                _preferredWindowId = value.Id;
            }
            else
            {
                _preferredMonitorId = value.Id;
            }
        }

        if (IsRegionMode && value is not null)
        {
            PopulateRegion(value);
        }
    }

    partial void OnSelectedAudioModeChanged(AudioModeOption? value)
    {
        OnPropertyChanged(nameof(AudioSourceLabel));
        OnPropertyChanged(nameof(IsAudioSourceEnabled));
        OnPropertyChanged(nameof(IsAudioSettingsEnabled));
        OnPropertyChanged(nameof(CanConfigureAudioCodec));
        RefreshAudioItems();
    }

    partial void OnSelectedAudioSourceChanged(object? value)
    {
        if (value is AudioDeviceDescriptor device)
        {
            _preferredAudioDeviceId = device.Id;
        }
        else if (value is ProcessDescriptor process)
        {
            _preferredProcessName = process.ProcessName;
        }
    }

    partial void OnSelectedAudioCodecChanged(AudioCodecOption? value) =>
        OnPropertyChanged(nameof(CanConfigureAudioCodec));

    partial void OnSelectedAudioSampleRateChanged(AudioSampleRateOption? value)
    {
        if (_audioCatalogReady) RefreshAudioCodecs();
    }

    public async Task ConfigureAudioCodecAsync(nint ownerWindow)
    {
        if (!CanConfigureAudioCodec) return;
        try
        {
            var rate = SelectedAudioSampleRate?.Value ?? 48_000;
            var format = AcmAudioCodec.Configure(ownerWindow, rate, SelectedAudioCodec?.WaveFormat ?? []);
            if (format is null) return;
            var id = GetAudioCodecId(format);
            _audioCodecFormats[id] = format;
            RefreshAudioCodecs(id);
            await _settings.SaveAsync(CreateSettings()).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ShowError("音声コーデックの設定に失敗しました", error);
        }
    }

    private static string GetAudioCodecId(byte[] format) =>
        $"Acm:{BinaryPrimitives.ReadUInt16LittleEndian(format):X4}:{BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4))}";

    private void RefreshAudioCodecs(string? preferredId = null)
    {
        preferredId ??= SelectedAudioCodec?.Id;
        var choices = new List<AudioCodecOption>
        {
            new("Pcm16", AudioSampleFormat.SignedInteger, 16, "PCM 16-bit（非圧縮）"),
            new("Float32", AudioSampleFormat.IeeeFloat, 32, "IEEE Float 32-bit（非圧縮）"),
        };
        try
        {
            var rate = SelectedAudioSampleRate?.Value ?? 48_000;
            var formats = AcmAudioCodec.GetFormats(rate);
            foreach (var group in formats.GroupBy(format => GetAudioCodecId(format.WaveFormat)))
            {
                var format = group.OrderByDescending(format =>
                    BinaryPrimitives.ReadUInt32LittleEndian(format.WaveFormat.AsSpan(8))).First().WaveFormat;
                if (_audioCodecFormats.TryGetValue(group.Key, out var saved) &&
                    AcmAudioCodec.CanEncode(AcmAudioCodec.CreatePcm(rate, 2), saved)) format = saved;
                choices.Add(new(group.Key, AudioSampleFormat.SignedInteger, 16,
                    AcmAudioCodec.Describe(format).DisplayName, format));
            }

            // The format chooser can also return custom PCM formats.
            foreach (var (id, format) in _audioCodecFormats)
            {
                if (choices.Any(choice => choice.Id == id) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != rate) continue;
                if (AcmAudioCodec.CanEncode(AcmAudioCodec.CreatePcm(rate, 2), format))
                    choices.Add(new(id, AudioSampleFormat.SignedInteger, 16, AcmAudioCodec.Describe(format).DisplayName, format));
            }
        }
        catch (Exception error) { ShowError("音声コーデックの確認に失敗しました", error); }
        if (File.Exists(LameExecutablePath))
            choices.Add(new("Lame", AudioSampleFormat.SignedInteger, 16, "MP3（LAME）"));
        if (File.Exists(FlacExecutablePath))
            choices.Add(new("Flac", AudioSampleFormat.SignedInteger, 16, "FLAC"));
        AudioCodecs = choices;
        SelectedAudioCodec = choices.FirstOrDefault(choice => choice.Id == preferredId) ??
            (preferredId is { Length: >= 9 } && preferredId.StartsWith("Acm:", StringComparison.Ordinal)
                ? choices.FirstOrDefault(choice => choice.Id.StartsWith(preferredId[..9], StringComparison.Ordinal))
                : null) ?? choices[1];
    }

    partial void OnSelectedCodecChanged(VideoCodecDescriptor? value)
    {
        _selectedCodecHasSettings = value is not null && VcmVideoEncoder.CanConfigure(value.FourCc);
        OnPropertyChanged(nameof(CanConfigureCodec));
    }

    public async Task ConfigureCodecAsync(nint ownerWindow)
    {
        var codec = SelectedCodec;
        if (!CanConfigureCodec || codec is null) return;
        try
        {
            var state = VcmVideoEncoder.Configure(codec.FourCc, ownerWindow, GetCodecState(codec.FourCc));
            if (state.Length > 0) _codecStates[codec.FourCc] = state;
            else _codecStates.Remove(codec.FourCc);
            RefreshCodecs();
            await _settings.SaveAsync(CreateSettings()).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ShowError("コーデックの設定に失敗しました", error);
        }
    }

    private ReadOnlyMemory<byte> GetCodecState(string fourCc) =>
        _codecStates.TryGetValue(fourCc, out var state) ? state : ReadOnlyMemory<byte>.Empty;

    private void RefreshCodecs()
    {
        var previousFourCc = SelectedCodec?.FourCc;
        try
        {
            Codecs = Enum.GetValues<VideoEncodingPixelFormat>()
                .SelectMany(format => _services.VideoCodecs.GetCodecs(format, _codecStates))
                .DistinctBy(codec => codec.FourCc, StringComparer.OrdinalIgnoreCase)
                .OrderBy(codec => codec.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Where(static codec => codec.IsAvailable)
                .ToArray();
            SelectedCodec = Codecs.FirstOrDefault(codec =>
                string.Equals(codec.FourCc, previousFourCc, StringComparison.OrdinalIgnoreCase)) ??
                (Codecs.Count > 0 ? Codecs[0] : null);
            SetStatus(Codecs.Count > 0 ? "録画できます" : "選択した出力色形式に対応する映像コーデックがありません",
                Codecs.Count > 0 ? StatusKind.Ready : StatusKind.Error);
        }
        catch (Exception error)
        {
            Codecs = [];
            SelectedCodec = null;
            ShowError("コーデックの確認に失敗しました", error);
        }

        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedContainerFormatChanged(ContainerFormatOption? value)
    {
        OnPropertyChanged(nameof(OutputFileHint));
        if (_audioCatalogReady) RefreshAudioCodecs();
    }

    partial void OnOutputFolderChanged(string value) => UpdateFreeSpace();

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSettingsEnabled));
        OnPropertyChanged(nameof(CanConfigureCodec));
        OnPropertyChanged(nameof(CanConfigureAudioCodec));
        ResizeWindowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RecordButtonText));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTargetItems()
    {
        var useWindow = SelectedCaptureMode?.Mode is CaptureMode.Window;
        Targets = useWindow ? _windows : _monitors;
        var preferredId = useWindow ? _preferredWindowId : _preferredMonitorId;
        SelectedTarget = Targets.FirstOrDefault(target => target.Id == preferredId) ??
            (Targets.Count > 0 ? Targets[0] : null);
    }

    private void RefreshAudioItems()
    {
        var useProcess = SelectedAudioMode?.Mode is AudioMode.Process;
        var useAudio = SelectedAudioMode?.Mode is not AudioMode.None;
        AudioSources = !useAudio
            ? []
            : useProcess
            ? _processes.Cast<object>().ToArray()
            : _audioDevices.Cast<object>().ToArray();
        SelectedAudioSource = useProcess
            ? AudioSources.OfType<ProcessDescriptor>()
                .FirstOrDefault(process => process.ProcessName == _preferredProcessName)
            : AudioSources.OfType<AudioDeviceDescriptor>()
                .FirstOrDefault(device => device.Id == _preferredAudioDeviceId);
        SelectedAudioSource ??= AudioSources.Count > 0 ? AudioSources[0] : null;
        if (useProcess && !GamenTrailServices.SupportsProcessLoopback)
        {
            SetStatus("このWindowsではアプリ単位の音声録音を利用できません", StatusKind.Error);
        }
    }

    private bool TrySelectProcessAudio(int? processId)
    {
        if (SelectedAudioMode?.Mode is AudioMode.None ||
            processId is null ||
            !GamenTrailServices.SupportsProcessLoopback)
        {
            return false;
        }

        _processes = _services.Processes.GetProcesses();
        var process = _processes.FirstOrDefault(candidate => candidate.ProcessId == processId.Value);
        if (process is null)
        {
            return false;
        }

        _preferredProcessName = process.ProcessName;
        SelectedAudioMode = AudioModes.First(option => option.Mode is AudioMode.Process);
        SelectedAudioSource = AudioSources
            .OfType<ProcessDescriptor>()
            .FirstOrDefault(candidate => candidate.ProcessId == process.ProcessId);
        return SelectedAudioSource is not null;
    }

    private void PopulateRegion(CaptureTargetDescriptor monitor)
    {
        RegionX = monitor.X.ToString(CultureInfo.InvariantCulture);
        RegionY = monitor.Y.ToString(CultureInfo.InvariantCulture);
        RegionWidth = Math.Min(1280, monitor.Width).ToString(CultureInfo.InvariantCulture);
        RegionHeight = Math.Min(720, monitor.Height).ToString(CultureInfo.InvariantCulture);
        RegionHint =
            $"範囲: X {monitor.X}～{monitor.X + monitor.Width - 1}, Y {monitor.Y}～{monitor.Y + monitor.Height - 1}";
    }

    private void ApplySettings(AppSettings settings)
    {
        LameExecutablePath = settings.LameExecutablePath ?? string.Empty;
        FlacExecutablePath = settings.FlacExecutablePath ?? string.Empty;
        Mp3BitRate = new[] { 96, 128, 160, 192, 224, 256, 320 }.Contains(settings.Mp3BitRate) ? settings.Mp3BitRate : 192;
        FlacCompressionLevel = Math.Clamp(settings.FlacCompressionLevel, 0, 8);
        _audioCodecFormats.Clear();
        if (settings.AudioCodecFormats is not null)
        {
            foreach (var (id, format) in settings.AudioCodecFormats)
            {
                if (format is { Length: >= 18 })
                {
                    try { AcmAudioCodec.ValidateFormat(format); _audioCodecFormats[id] = format; }
                    catch (InvalidDataException) { }
                }
            }
        }
        _codecStates.Clear();
        if (settings.CodecStates is not null)
        {
            foreach (var (fourCc, state) in settings.CodecStates)
            {
                if (state is { Length: > 0 }) _codecStates[fourCc] = state;
            }
        }

        _preferredMonitorId = settings.MonitorId;
        _preferredWindowId = settings.WindowId;
        _preferredAudioDeviceId = settings.AudioDeviceId;
        _preferredProcessName = settings.ProcessName;

        if (Enum.TryParse<CaptureMode>(settings.CaptureMode, ignoreCase: true, out var captureMode))
        {
            SelectedCaptureMode = CaptureModes.FirstOrDefault(option => option.Mode == captureMode) ?? CaptureModes[0];
        }

        if (Enum.TryParse<AudioMode>(settings.AudioMode, ignoreCase: true, out var audioMode))
        {
            SelectedAudioMode = AudioModes.FirstOrDefault(option => option.Mode == audioMode) ?? AudioModes[0];
        }

        SelectedCodec = new VideoCodecDescriptor(settings.CodecFourCc, settings.CodecFourCc, IsAvailable: true);
        SelectedAudioCodec = AudioCodecs.FirstOrDefault(option => option.Id == settings.AudioCodec) ?? AudioCodecs[1];
        SelectedAudioSampleRate = AudioSampleRates.FirstOrDefault(
            option => option.Value == settings.AudioSampleRate) ?? AudioSampleRates[1];
        SelectedFrameRate = FrameRates.FirstOrDefault(option => option.Value == settings.FrameRate) ?? FrameRates.First(option => option.Value == 30);
        SelectedContainerFormat = ContainerFormats.FirstOrDefault(
            option => option.Extension == settings.ContainerExtension) ?? ContainerFormats[0];
        IncludeCursor = settings.IncludeCursor;
        ShowCaptureBorder = settings.ShowCaptureBorder;
        DisableWindowCornerRounding = settings.DisableWindowCornerRounding;
        CaptureOverlappingWindows = settings.CaptureOverlappingWindows;
        WindowWidth = settings.WindowWidth;
        WindowHeight = settings.WindowHeight;
        if (!string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            OutputFolder = settings.OutputFolder;
        }

        RegionX = settings.RegionX;
        RegionY = settings.RegionY;
        RegionWidth = settings.RegionWidth;
        RegionHeight = settings.RegionHeight;
    }

    private void RestoreRegionCoordinates(AppSettings settings)
    {
        RegionX = settings.RegionX;
        RegionY = settings.RegionY;
        RegionWidth = settings.RegionWidth;
        RegionHeight = settings.RegionHeight;
    }

    private AppSettings CreateSettings() => new()
    {
        CaptureMode = SelectedCaptureMode?.Mode.ToString() ?? CaptureMode.Monitor.ToString(),
        MonitorId = _preferredMonitorId,
        WindowId = _preferredWindowId,
        CodecFourCc = SelectedCodec?.FourCc ?? "UMRG",
        CodecStates = new Dictionary<string, byte[]>(_codecStates, StringComparer.OrdinalIgnoreCase),
        FrameRate = SelectedFrameRate?.Value ?? 30,
        IncludeCursor = IncludeCursor,
        ShowCaptureBorder = ShowCaptureBorder,
        DisableWindowCornerRounding = DisableWindowCornerRounding,
        CaptureOverlappingWindows = CaptureOverlappingWindows,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        AudioMode = SelectedAudioMode?.Mode.ToString() ?? AudioMode.System.ToString(),
        AudioDeviceId = _preferredAudioDeviceId,
        ProcessName = _preferredProcessName,
        LameExecutablePath = LameExecutablePath,
        FlacExecutablePath = FlacExecutablePath,
        Mp3BitRate = Mp3BitRate,
        FlacCompressionLevel = FlacCompressionLevel,
        AudioCodec = SelectedAudioCodec?.Id ?? "Float32",
        AudioCodecFormats = new Dictionary<string, byte[]>(_audioCodecFormats, StringComparer.Ordinal),
        AudioSampleRate = SelectedAudioSampleRate?.Value ?? 48_000,
        ContainerExtension = SelectedContainerFormat?.Extension ?? "avi",
        OutputFolder = OutputFolder,
        RegionX = RegionX,
        RegionY = RegionY,
        RegionWidth = RegionWidth,
        RegionHeight = RegionHeight,
    };

    private async Task StartRecordingAsync()
    {
        var options = CreateRecordingOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        _currentOutputPath = options.OutputPath;
        FileSizeText = "0 B";
        UpdateFreeSpace();
        _recorder = GamenTrailServices.CreateRecorder();
        _recorder.StateChanged += Recorder_StateChanged;
        SetStatus("録画を開始しています...", StatusKind.Ready);
        await _recorder.StartAsync(options).ConfigureAwait(true);
        SetRecordingState(true);
    }

    private async Task StopRecordingAsync()
    {
        SetStatus("録画を保存しています...", StatusKind.Ready);
        if (_recorder is not null)
        {
            await _recorder.StopAsync().ConfigureAwait(true);
        }

        await DisposeRecorderAsync().ConfigureAwait(true);
        UpdateStorageStatus();
        SetRecordingState(false);
        SetStatus("録画を保存しました", StatusKind.Ready);
    }

    private RecordingOptions CreateRecordingOptions()
    {
        var target = CreateCaptureTarget();
        var codec = SelectedCodec ?? throw new InvalidOperationException("映像コーデックを選択してください。");
        var frameRate = SelectedFrameRate?.Value ??
            throw new InvalidOperationException("フレームレートを選択してください。");
        var folder = OutputFolder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("保存先を選択してください。");
        }

        var extension = SelectedContainerFormat?.Extension ??
            throw new InvalidOperationException("保存形式を選択してください。");
        var outputPath = Path.Combine(folder, $"GamenTrail_{DateTime.Now:yyyy-MM-dd_HHmmss}.{extension}");
        return new RecordingOptions(
            outputPath,
            new VideoCaptureOptions(
                target,
                frameRate,
                IncludeCursor,
                ShowCaptureBorder,
                DisableWindowCornerRounding,
                CaptureOverlappingWindows),
            new VideoEncoderSettings(codec.FourCc, AutoSelectInputFormat: true,
                CodecState: GetCodecState(codec.FourCc)),
            CreateAudioOptions());
    }

    private CaptureTarget CreateCaptureTarget(bool resolveWindow = true)
    {
        var descriptor = SelectedTarget ?? throw new InvalidOperationException("録画対象を選択してください。");
        if (SelectedCaptureMode?.Mode is CaptureMode.Window)
        {
            if (!resolveWindow) return descriptor.Target;
            var currentWindows = _services.CaptureTargets.GetWindows();
            var resolved = currentWindows.FirstOrDefault(candidate => candidate.Id == descriptor.Id) ??
                currentWindows
                    .Where(candidate => candidate.DisplayName == descriptor.DisplayName)
                    .OrderBy(candidate =>
                        Math.Abs((long)candidate.Width - descriptor.Width) +
                        Math.Abs((long)candidate.Height - descriptor.Height))
                    .FirstOrDefault();
            _windows = currentWindows;
            Targets = _windows;
            if (resolved is null)
            {
                SelectedTarget = null;
                throw new InvalidOperationException(
                    $"録画対象「{descriptor.DisplayName}」は既に閉じられています。" +
                    "対象ウィンドウを開いてから再読込してください。");
            }

            SelectedTarget = resolved;
            return resolved.Target;
        }

        if (SelectedCaptureMode?.Mode is not CaptureMode.Region)
        {
            return descriptor.Target;
        }

        var x = ParseCoordinate(RegionX, "X");
        var y = ParseCoordinate(RegionY, "Y");
        var width = ParseCoordinate(RegionWidth, "幅");
        var height = ParseCoordinate(RegionHeight, "高さ");
        if (width <= 0 || height <= 0 ||
            x < descriptor.X || y < descriptor.Y ||
            (long)x + width > (long)descriptor.X + descriptor.Width ||
            (long)y + height > (long)descriptor.Y + descriptor.Height)
        {
            throw new InvalidOperationException("座標指定の範囲を、選択したモニター内に収めてください。");
        }

        var monitor = descriptor.Target as CaptureTarget.Monitor ??
            throw new InvalidOperationException("座標指定の基準モニターが不正です。");
        return new CaptureTarget.Region(
            monitor.Handle,
            x - descriptor.X,
            y - descriptor.Y,
            width,
            height);
    }

    private AudioCaptureOptions? CreateAudioOptions()
    {
        if (SelectedAudioMode?.Mode is AudioMode.None)
        {
            return null;
        }

        var codec = SelectedAudioCodec ?? throw new InvalidOperationException("音声コーデックを選択してください。");
        var external = codec.Id is "Lame" or "Flac"
            ? new ExternalAudioEncoderSettings(codec.Id, codec.Id == "Lame" ? LameExecutablePath : FlacExecutablePath, Mp3BitRate, FlacCompressionLevel)
            : null;
        var sampleRate = SelectedAudioSampleRate?.Value ??
            throw new InvalidOperationException("音声サンプリングレートを選択してください。");
        if (SelectedAudioMode?.Mode is AudioMode.Process)
        {
            if (!GamenTrailServices.SupportsProcessLoopback)
            {
                throw new PlatformNotSupportedException("このWindowsではアプリ単位の音声録音を利用できません。");
            }

            var process = SelectedAudioSource as ProcessDescriptor ??
                throw new InvalidOperationException("録音するアプリを選択してください。");
            return new AudioCaptureOptions(
                ProcessId: process.ProcessId,
                SampleRate: sampleRate,
                SampleFormat: codec.SampleFormat,
                BitsPerSample: codec.BitsPerSample,
                ChannelCount: codec.WaveFormat is null && external is null ? null : 2,
                EncodedWaveFormat: codec.WaveFormat, ExternalEncoder: external);
        }

        var device = SelectedAudioSource as AudioDeviceDescriptor;
        return new AudioCaptureOptions(
            DeviceId: device?.Id,
            SampleRate: sampleRate,
            SampleFormat: codec.SampleFormat,
            BitsPerSample: codec.BitsPerSample,
                ChannelCount: codec.WaveFormat is null && external is null ? null : 2,
                EncodedWaveFormat: codec.WaveFormat, ExternalEncoder: external);
    }

    private static int ParseCoordinate(string text, string fieldName)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{fieldName}には整数を入力してください。");
        }

        return value;
    }

    private void Recorder_StateChanged(object? sender, RecorderStateChangedEventArgs e)
    {
        if (e.Current is RecorderState.Faulted)
        {
            _uiContext.Post(
                _ => _ = HandleRecorderFaultAsync(e.Error),
                null);
        }
    }

    private async Task HandleRecorderFaultAsync(Exception? error)
    {
        SetStatus(error?.Message ?? "録画中にエラーが発生しました", StatusKind.Error);
        SetRecordingState(false);
        await DisposeRecorderAsync().ConfigureAwait(true);
        ResumePreview();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        var statistics = _recorder?.GetStatistics();
        var elapsed = statistics?.Elapsed ?? TimeSpan.Zero;
        ElapsedText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        UpdateStorageStatus(statistics?.MuxedBytes ?? 0);
    }

    private void UpdateStorageStatus(long muxedBytes = 0)
    {
        var fileBytes = muxedBytes;
        if (_currentOutputPath is not null)
        {
            try
            {
                if (File.Exists(_currentOutputPath))
                {
                    fileBytes = Math.Max(fileBytes, new FileInfo(_currentOutputPath).Length);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        FileSizeText = FormatByteSize(fileBytes);
        UpdateFreeSpace();
    }

    private void UpdateFreeSpace()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                FreeSpaceText = "—";
                return;
            }

            var fullPath = Path.GetFullPath(OutputFolder);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                FreeSpaceText = "—";
                return;
            }

            var drive = new DriveInfo(root);
            FreeSpaceText = drive.IsReady ? FormatByteSize(drive.AvailableFreeSpace) : "—";
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            FreeSpaceText = "—";
        }
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private void SetRecordingState(bool value)
    {
        IsRecording = value;
        if (value)
        {
            SetStatus("録画中", StatusKind.Recording);
            _statusTimer.Start();
        }
        else
        {
            _statusTimer.Stop();
            ElapsedText = "00:00:00";
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText = message;
        StatusKind = kind;
    }

    private void ShowError(string title, Exception error)
    {
        SetStatus(error.Message, StatusKind.Error);
        _dialogs.ShowError(title, error.Message);
    }

    private async Task DisposeRecorderAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        _recorder.StateChanged -= Recorder_StateChanged;
        await _recorder.DisposeAsync().ConfigureAwait(true);
        _recorder = null;
    }
}

public enum CaptureMode
{
    Monitor,
    Window,
    Region,
}

public enum AudioMode
{
    System,
    Process,
    None,
}

public enum StatusKind
{
    Ready,
    Recording,
    Error,
}

public sealed record CaptureModeOption(CaptureMode Mode, string DisplayName);

public sealed record AudioModeOption(AudioMode Mode, string DisplayName);

public sealed record AudioCodecOption(
    string Id,
    AudioSampleFormat SampleFormat,
    int BitsPerSample,
    string DisplayName,
    byte[]? WaveFormat = null);

public sealed record AudioSampleRateOption(int Value, string DisplayName);

public sealed record FrameRateOption(double Value, string DisplayName);

public sealed record ContainerFormatOption(string Extension, string DisplayName);

