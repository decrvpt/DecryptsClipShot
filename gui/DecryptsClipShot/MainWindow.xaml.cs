using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DecryptsClipShot;

public partial class MainWindow : Window
{
    private const string DefaultOutputDevice = "Default Windows output";
    private const string SaveEventName = @"Local\DecryptsClipShot.Save";
    private const string LongEventName = @"Local\DecryptsClipShot.LongRecord";
    private const string ShotEventName = @"Local\DecryptsClipShot.Screenshot";
    private const string StopEventName = @"Local\DecryptsClipShot.Stop";
    private readonly ObservableCollection<ClipItem> _trickshotClips = [];
    private readonly ObservableCollection<ClipItem> _otherClips = [];
    private readonly ObservableCollection<ClipItem> _screenshots = [];
    private readonly ObservableCollection<ClipItem> _recentClips = [];
    private readonly ObservableCollection<string> _outputDevices = [];
    private readonly ObservableCollection<string> _microphones = [];
    private const string DefaultMicrophone = "Default microphone";
    private readonly DispatcherTimer _timer;
    private Process? _engine;
    private DateTime? _captureStarted;
    private DateTime? _longRecordStarted;
    private bool _longRecordSaving;
    private TextBox? _hotkeyTarget;
    private Button? _hotkeyTargetButton;
    private bool _captureActive;
    private bool _loadingSettings;
    private bool _capturingHotkey;
    private bool _stoppingEngine;
    private bool _settingsWriteFailed;
    private bool _changingClipSelection;
    private bool _trayTipShown;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private string BaseDirectory => AppContext.BaseDirectory;
    private string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Decrypts ClipShot");
    private string IniPath => Path.Combine(UserDataDirectory, "trickclip.ini");
    private string EnginePath => Path.Combine(BaseDirectory, "TrickClip.exe");
    private string AudioHelperPath => Path.Combine(BaseDirectory, "ClipShotAudio.exe");
    private string ReplayParserPath => Path.Combine(BaseDirectory, "ClipShotReplayParser.exe");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(string section, string key, string defaultValue,
        StringBuilder returnedString, uint size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

    private string? _pendingStartupMessage;

    public MainWindow()
    {
        InitializeComponent();
        CleanupOrphanedBundledFfmpeg();
        TrickshotClipsList.ItemsSource = _trickshotClips;
        OtherClipsList.ItemsSource = _otherClips;
        ScreenshotsList.ItemsSource = _screenshots;
        RecentClipsList.ItemsSource = _recentClips;
        GameAudioDeviceBox.ItemsSource = _outputDevices;
        MicrophoneDeviceBox.ItemsSource = _microphones;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        EnsureUserConfig();
        MigrateRecommendedDefaults();
        LoadSettings();
        SyncQuickSwitches();
        InitializeTrayIcon();
        // The installer's "Start with Windows" shortcut passes --startup:
        // open quietly in the system tray instead of showing the window.
        if (Environment.GetCommandLineArgs().Any(argument =>
                argument.Equals("--startup", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = WindowState.Minimized;
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(HideToTray), DispatcherPriority.ApplicationIdle);
        }
        Loaded += async (_, _) => await RefreshAudioDevicesAsync(false);
        // Look for a newer release a few seconds after opening, so startup stays fast.
        Loaded += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await CheckForUpdatesAsync(manual: false);
        };
        _ = RefreshClipsAsync();
        _ = RefreshScreenshotsAsync();
        SetStatus("Engine offline", false, false);
        UpdateSourceStatus();
        AddActivity("ClipShot is ready");
        // Recording starts as soon as ClipShot opens (Settings can turn this off).
        Loaded += (_, _) =>
        {
            if (ReadIni("app", "start_recording_on_open", "1") == "1" && _engine is null)
                EngineButton_Click(EngineButton, new RoutedEventArgs());
        };
        if (_pendingStartupMessage is not null) AddActivity(_pendingStartupMessage);
    }

    private string ReadIni(string section, string key, string fallback)
    {
        var result = new StringBuilder(2048);
        GetPrivateProfileString(section, key, fallback, result, (uint)result.Capacity, IniPath);
        return result.ToString();
    }

    private void EnsureUserConfig()
    {
        Directory.CreateDirectory(UserDataDirectory);
        if (File.Exists(IniPath)) return;
        string bundled = Path.Combine(BaseDirectory, "trickclip.ini");
        if (File.Exists(bundled)) File.Copy(bundled, IniPath, overwrite: false);
    }

    private void WriteIni(string section, string key, string value)
    {
        if (!WritePrivateProfileString(section, key, value, IniPath))
            _settingsWriteFailed = true;
    }

    private void MigrateRecommendedDefaults()
    {
        int.TryParse(ReadIni("app", "defaults_version", "0"), out int defaultsVersion);
        if (defaultsVersion < 7) MigrateToDefaults7();
        if (defaultsVersion < 8) MigrateClipsToVideosFolder();
        // Version 9: ClipShot records any game; waiting for Fortnite became optional (off).
        if (defaultsVersion < 9) WriteIni("app", "start_only_with_fortnite", "0");
        WriteIni("app", "defaults_version", "9");
    }

    // Version 8: clips moved from "<install folder>\clips" to Videos\Decrypt's ClipShot,
    // where players expect to find recordings. Existing clips are moved once.
    private void MigrateClipsToVideosFolder()
    {
        string configured = ReadIni("folders", "clips", "clips").Trim();
        if (configured.Length > 0 && !configured.Equals("clips", StringComparison.OrdinalIgnoreCase))
            return; // The player chose their own folder; leave it alone.

        WriteIni("folders", "clips", "");
        string oldFolder = Path.Combine(BaseDirectory, "clips");
        string newFolder = DefaultClipsDirectory;
        try
        {
            Directory.CreateDirectory(newFolder);
            if (!Directory.Exists(oldFolder)) return;
            int moved = 0;
            foreach (string file in Directory.EnumerateFiles(oldFolder, "*", SearchOption.AllDirectories).ToList())
            {
                string destination = Path.Combine(newFolder, Path.GetRelativePath(oldFolder, file));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination)) continue; // never overwrite
                    File.Move(file, destination);
                    moved++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // Remove the old folders only if they ended up empty.
            foreach (string directory in Directory.EnumerateDirectories(oldFolder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length).Append(oldFolder))
            {
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch { }
            }
            if (moved > 0) _pendingStartupMessage = $"Moved {moved} clip file(s) to {newFolder}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leave clips where they are; the player can still open the old folder.
        }
    }

    private void MigrateToDefaults7()
    {

        if (ReadIni("recording", "buffer_seconds", "180") == "180" &&
            ReadIni("recording", "clip_seconds", "60") == "60")
            WriteIni("recording", "clip_seconds", "90");
        if (ReadIni("app", "post_match_seconds", "10") == "10")
            WriteIni("app", "post_match_seconds", "15");
        if (int.TryParse(ReadIni("recording", "segment_seconds", "4"), out int legacySegment) &&
            legacySegment <= 4)
            WriteIni("recording", "segment_seconds", "30");
        if (ReadIni("recording", "video_encoder", "libx264") == "h264_amf")
        {
            string options = ReadIni("recording", "video_options", "")
                .Replace("-pix_fmt nv12", "", StringComparison.OrdinalIgnoreCase)
                .Replace("-pix_fmt d3d11", "", StringComparison.OrdinalIgnoreCase);
            if (!options.Contains("-pix_fmt", StringComparison.OrdinalIgnoreCase))
                options += " -pix_fmt yuv420p";
            WriteIni("recording", "video_options",
                string.Join(' ', options.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        WriteIni("recording", "capture_backend", "auto");
        // Pre-v7 builds could be configured for microphone or Stereo Mix
        // capture; reset audio to the current single game-audio stream.
        WriteIni("recording", "capture_game_audio", "1");
        WriteIni("recording", "game_audio_device", DefaultOutputDevice);
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        string bundledFfmpeg = Path.Combine(BaseDirectory, "ffmpeg.exe");
        string configuredFfmpeg = ReadIni("recording", "ffmpeg_path", "ffmpeg.exe");
        FfmpegPathBox.Text = File.Exists(bundledFfmpeg) ? bundledFfmpeg : configuredFfmpeg;
        EncoderOptionsBox.Text = ReadIni("recording", "video_options", "");
        FpsBox.Text = ReadIni("recording", "framerate", "30");
        BufferBox.Text = ReadIni("recording", "buffer_seconds", "180");
        ClipLengthBox.Text = ReadIni("recording", "clip_seconds", "90");
        PostMatchBox.Text = ReadIni("app", "post_match_seconds", "15");
        VolumeSlider.Value = int.TryParse(ReadIni("recording", "game_audio_volume", "100"), out int volume)
            ? Math.Clamp(volume, 0, 200)
            : 100;
        SegmentBox.Text = ReadIni("recording", "segment_seconds", "30");
        ClipFolderBox.Text = ClipsDirectory;
        string outputHeight = ReadIni("recording", "output_height", "1080");
        ResolutionBox.SelectedIndex = outputHeight switch
        {
            "720" => 2,
            "1080" => 1,
            _ => 0
        };
        PrioritizeGameCheck.IsChecked = ReadIni("recording", "prioritize_game", "1") == "1";
        GameAudioCheck.IsChecked = ReadIni("recording", "capture_game_audio", "1") == "1";
        string gameDevice = ReadIni("recording", "game_audio_device", DefaultOutputDevice);
        if (string.IsNullOrWhiteSpace(gameDevice)) gameDevice = DefaultOutputDevice;
        _outputDevices.Add(gameDevice);
        GameAudioDeviceBox.Text = gameDevice;
        MicrophoneCheck.IsChecked = ReadIni("recording", "capture_microphone", "0") == "1";
        string microphone = ReadIni("recording", "microphone_device", DefaultMicrophone);
        if (string.IsNullOrWhiteSpace(microphone)) microphone = DefaultMicrophone;
        _microphones.Add(microphone);
        MicrophoneDeviceBox.Text = microphone;
        MicVolumeSlider.Value = int.TryParse(ReadIni("recording", "microphone_volume", "100"), out int micVolume)
            ? Math.Clamp(micVolume, 0, 200)
            : 100;
        HotkeyBox.Text = ReadIni("controls", "manual_clip_hotkey", "F8");
        LongHotkeyBox.Text = ReadIni("controls", "long_recording_hotkey", "F9");
        ShotHotkeyBox.Text = ReadIni("controls", "screenshot_hotkey", "F10");
        TrickshotScreenshotCheck.IsChecked = ReadIni("app", "trickshot_screenshot", "1") == "1";
        UpdateHotkeyHint();
        FortniteOnlyCheck.IsChecked = ReadIni("app", "start_only_with_fortnite", "0") == "1";
        StartOnOpenCheck.IsChecked = ReadIni("app", "start_recording_on_open", "1") == "1";
        StartWithWindowsCheck.IsChecked = IsStartWithWindowsEnabled();
        LocalReplayCheck.IsChecked = ReadIni("local_replay", "enabled", "1") == "1";
        KeepRejectedCheck.IsChecked = ReadIni("app", "keep_rejected_clips", "1") == "1";
        MinimizeToTrayCheck.IsChecked = ReadIni("app", "minimize_to_tray", "0") == "1";
        UpdatesCheck.IsChecked = ReadIni("app", "check_for_updates", "1") == "1";
        AppVersionText.Text = $"Version {UpdateChecker.CurrentVersion}";

        string encoder = ReadIni("recording", "video_encoder", "libx264");
        EncoderBox.SelectedIndex = encoder switch
        {
            "h264_nvenc" => 1,
            "h264_mf" => 2,
            "h264_amf" => 3,
            "h264_qsv" => 4,
            _ => 0
        };
        string preset = ReadIni("recording", "encoder_preset", "balanced");
        EncoderPresetBox.SelectedIndex = preset switch
        {
            "quality" => 1,
            "performance" => 2,
            "custom" => 3,
            _ => 0
        };
        if (string.IsNullOrWhiteSpace(EncoderOptionsBox.Text))
            EncoderOptionsBox.Text = GetEncoderOptions(encoder, preset);
        EncoderOptionsBox.IsReadOnly = preset != "custom";
        PerformanceStatusText.Text = encoder == "libx264"
            ? "Recording uses your processor. Click Optimize for my PC to let your graphics card do it instead (smoother games)."
            : "Recording uses your graphics card, so your game stays smooth.";
        _loadingSettings = false;
        RefreshMetrics();
    }

    private static string ComboValue(System.Windows.Controls.ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static string GetEncoderOptions(string encoder, string preset)
    {
        bool quality = preset == "quality";
        bool performance = preset == "performance";
        return encoder switch
        {
            "h264_nvenc" => quality
                ? "-preset p7 -tune hq -rc vbr -cq 18 -b:v 0 -pix_fmt yuv420p"
                : performance
                    ? "-preset p3 -tune ll -rc vbr -cq 23 -b:v 0 -pix_fmt yuv420p"
                    : "-preset p5 -tune hq -rc vbr -cq 20 -b:v 0 -pix_fmt yuv420p",
            "h264_mf" => quality
                ? "-hw_encoding 1 -rate_control quality -quality 90"
                : performance
                    ? "-hw_encoding 1 -rate_control quality -quality 75"
                    : "-hw_encoding 1 -rate_control quality -quality 82",
            "h264_amf" => quality
                ? "-quality quality -rc cqp -qp_i 18 -qp_p 18 -pix_fmt yuv420p"
                : performance
                    ? "-quality speed -rc cqp -qp_i 23 -qp_p 23 -pix_fmt yuv420p"
                    : "-quality balanced -rc cqp -qp_i 20 -qp_p 20 -pix_fmt yuv420p",
            "h264_qsv" => quality
                ? "-preset slow -global_quality 18 -pix_fmt nv12"
                : performance
                    ? "-preset veryfast -global_quality 23 -pix_fmt nv12"
                    : "-preset medium -global_quality 20 -pix_fmt nv12",
            _ => quality
                ? "-preset medium -crf 18 -pix_fmt yuv420p"
                : performance
                    ? "-preset ultrafast -crf 21 -pix_fmt yuv420p"
                    : "-preset veryfast -crf 20 -pix_fmt yuv420p"
        };
    }

    private void EncoderSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || EncoderBox is null || EncoderPresetBox is null || EncoderOptionsBox is null) return;
        string preset = ComboValue(EncoderPresetBox, "balanced");
        EncoderOptionsBox.IsReadOnly = preset != "custom";
        if (preset != "custom")
            EncoderOptionsBox.Text = GetEncoderOptions(ComboValue(EncoderBox, "libx264"), preset);
        RefreshMetrics();
    }

    private void RefreshMetrics()
    {
        FpsMetric.Text = $"{FpsBox.Text} FPS";
        ClipLengthMetric.Text = ClipLengthValueText.Text;
        HotkeyMetric.Text = HotkeyBox.Text;
        string quality = ComboValue(EncoderPresetBox, "balanced");
        QualityMetric.Text = string.IsNullOrWhiteSpace(quality)
            ? "Balanced"
            : char.ToUpperInvariant(quality[0]) + quality[1..].ToLowerInvariant();
    }

    private void AddActivity(string message)
    {
        ActivityList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityList.Items.Count > 100) ActivityList.Items.RemoveAt(ActivityList.Items.Count - 1);
    }

    private void SetStatus(string text, bool engineOnline, bool recording)
    {
        _ = text;
        bool waitsForFortnite = FortniteOnlyCheck.IsChecked == true;
        // Red = recording, green = on and waiting, grey = off.
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            recording ? "#E5484D" : engineOnline ? "#3DD68C" : "#67677A"));
        string shortStatus = recording ? "Recording"
            : engineOnline ? (waitsForFortnite ? "Waiting for Fortnite" : "Starting…")
            : "Off";
        SidebarStatusDot.Fill = brush;
        HeaderStatusDot.Fill = brush;
        RecStatusDot.Fill = brush;
        SidebarStatusText.Text = shortStatus;
        HeaderStatusText.Text = shortStatus;
        RecStatusValue.Text = shortStatus;
        CaptureStateText.Text = recording ? "Recording in the background"
            : engineOnline ? (waitsForFortnite ? "Waiting for Fortnite" : "Getting ready…")
            : "ClipShot is off";
        CaptureSubtext.Text = recording ? $"Press {HotkeyBox.Text} any time to save the last {ClipLengthValueText.Text}"
            : engineOnline ? (waitsForFortnite ? "Recording starts by itself when you open Fortnite" : "Recording starts in a moment")
            : "Click Start ClipShot to begin recording";
        // The button describes the action it will perform, not the current state.
        EngineButton.Content = engineOnline ? "Stop ClipShot" : "Start ClipShot";
        EngineButton.ToolTip = engineOnline
            ? "Stop recording. Clips you already saved are kept."
            : "Start recording. ClipShot keeps the last few minutes ready to save.";
        SaveClipButton.IsEnabled = engineOnline;
        LongRecordButton.IsEnabled = recording && !_longRecordSaving;
        if (!engineOnline) SetLongRecording(false);
        _captureActive = recording;
        if (recording && _captureStarted is null) _captureStarted = DateTime.Now;
        if (!recording) _captureStarted = null;
    }

    // ---------- Dashboard ----------

    // Game sound / microphone rows reflect the saved settings.
    private void UpdateSourceStatus()
    {
        var on = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3DD68C"));
        var off = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#67677A"));
        bool game = ReadIni("recording", "capture_game_audio", "1") == "1";
        bool mic = ReadIni("recording", "capture_microphone", "0") == "1";
        GameAudioStatusDot.Fill = game ? on : off;
        GameAudioStatusValue.Text = game ? "On" : "Off";
        MicStatusDot.Fill = mic ? on : off;
        MicStatusValue.Text = mic ? "On" : "Off";
    }

    private void SetFortniteRunning(bool running)
    {
        FortniteStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#3DD68C" : "#67677A"));
        FortniteStatusValue.Text = running ? "Running · trickshots on" : "Not running";
    }

    private void RecentTab_Click(object sender, RoutedEventArgs e) => ShowDashboardTab(activity: false);
    private void ActivityTab_Click(object sender, RoutedEventArgs e) => ShowDashboardTab(activity: true);

    private void ShowDashboardTab(bool activity)
    {
        RecentClipsPanel.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        RecentSideButton.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        ActivitySideButton.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        RecentTabButton.Foreground = (Brush)FindResource(activity ? "MutedBrush" : "TextBrush");
        ActivityTabButton.Foreground = (Brush)FindResource(activity ? "TextBrush" : "MutedBrush");
    }

    private bool _syncingSwitches;

    private void SyncQuickSwitches()
    {
        _syncingSwitches = true;
        QuickStartOnOpenSwitch.IsChecked = StartOnOpenCheck.IsChecked;
        QuickWindowsSwitch.IsChecked = StartWithWindowsCheck.IsChecked;
        QuickTrickshotSwitch.IsChecked = LocalReplayCheck.IsChecked;
        QuickScreenshotSwitch.IsChecked = TrickshotScreenshotCheck.IsChecked;
        QuickMicSwitch.IsChecked = MicrophoneCheck.IsChecked;
        _syncingSwitches = false;
    }

    // Dashboard switches save immediately and keep the Settings page in step.
    private void QuickSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSwitches || sender is not CheckBox toggle) return;
        bool on = toggle.IsChecked == true;
        string value = on ? "1" : "0";
        bool needsRestart = false;
        if (toggle == QuickStartOnOpenSwitch)
        {
            StartOnOpenCheck.IsChecked = on;
            WriteIni("app", "start_recording_on_open", value);
        }
        else if (toggle == QuickWindowsSwitch)
        {
            StartWithWindowsCheck.IsChecked = on;
            SetStartWithWindows(on);
            QuickWindowsSwitch.IsChecked = IsStartWithWindowsEnabled();
        }
        else if (toggle == QuickTrickshotSwitch)
        {
            LocalReplayCheck.IsChecked = on;
            WriteIni("local_replay", "enabled", value);
            needsRestart = true;
        }
        else if (toggle == QuickScreenshotSwitch)
        {
            TrickshotScreenshotCheck.IsChecked = on;
            WriteIni("app", "trickshot_screenshot", value);
            needsRestart = true;
        }
        else if (toggle == QuickMicSwitch)
        {
            MicrophoneCheck.IsChecked = on;
            WriteIni("recording", "capture_microphone", value);
            UpdateSourceStatus();
            needsRestart = true;
        }
        if (needsRestart && _engine is { HasExited: false })
            AddActivity("Saved. Stop and start ClipShot to use the new setting.");
    }

    // ---------- Open with Windows ----------
    // A "Run" entry opens ClipShot quietly in the tray at sign-in. The installer's
    // optional Startup-folder shortcut counts too, so ClipShot never opens twice.

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Decrypt's ClipShot";

    private static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Decrypt's ClipShot.lnk");

    private static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(RunValueName) is string) return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return File.Exists(StartupShortcutPath);
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                if (!File.Exists(StartupShortcutPath))
                {
                    string exe = Environment.ProcessPath ?? Path.Combine(BaseDirectory, "DecryptsClipShot.exe");
                    key.SetValue(RunValueName, $"\"{exe}\" --startup");
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AddActivity("Couldn't change the Open with Windows setting.");
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        ElapsedText.Text = _captureStarted is null ? "00:00:00" : (DateTime.Now - _captureStarted.Value).ToString(@"hh\:mm\:ss");
        if (_longRecordStarted is not null)
            LongRecordText.Text = "Long recording " + (DateTime.Now - _longRecordStarted.Value).ToString(@"hh\:mm\:ss");
        if (!_stoppingEngine && _engine is { HasExited: true })
        {
            int exitCode = _engine.ExitCode;
            _engine.Dispose();
            _engine = null;
            AddActivity(exitCode == 0
                ? "ClipShot stopped"
                : "ClipShot stopped unexpectedly. Click Start ClipShot to try again.");
            SetStatus("Engine offline", false, false);
        }
    }

    private async void EngineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is { HasExited: false })
        {
            _stoppingEngine = true;
            EngineButton.IsEnabled = false;
            SignalEvent(StopEventName);
            AddActivity("Stopping ClipShot…");
            Process engine = _engine;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await engine.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!engine.HasExited)
                {
                    engine.Kill(entireProcessTree: true);
                    await engine.WaitForExitAsync();
                    AddActivity("ClipShot stopped");
                }
            }
            finally
            {
                if (ReferenceEquals(_engine, engine))
                {
                    engine.Dispose();
                    _engine = null;
                }
                _stoppingEngine = false;
                EngineButton.IsEnabled = true;
                SetStatus("Engine offline", false, false);
            }
            return;
        }

        if (_engine is { HasExited: true } finishedEngine)
        {
            finishedEngine.Dispose();
            _engine = null;
        }

        if (!File.Exists(EnginePath))
        {
            MessageBox.Show("Part of ClipShot is missing. Reinstall ClipShot to fix it.", "ClipShot needs repair", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!SaveSettings(false)) return;

        try
        {
            var start = new ProcessStartInfo(EnginePath)
            {
                WorkingDirectory = BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _engine = new Process { StartInfo = start, EnableRaisingEvents = true };
            _engine.OutputDataReceived += Engine_OutputDataReceived;
            _engine.ErrorDataReceived += Engine_OutputDataReceived;
            _engine.Start();
            _engine.BeginOutputReadLine();
            _engine.BeginErrorReadLine();
            SetStatus("Starting", true, false);
            AddActivity("ClipShot started");
            await Task.Delay(300);
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            SetStatus("Engine offline", false, false);
            MessageBox.Show("ClipShot couldn't start recording. Try restarting your PC, or reinstall ClipShot.\n\nDetails: " + ex.Message, "Couldn't start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Engine_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        Dispatcher.Invoke(() =>
        {
            string line = e.Data.Trim();
            WriteEngineLog(line);
            if (line.StartsWith("RUNTIME|", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRuntimeProfile(line);
                return;
            }
            if (line.StartsWith("LONG|", StringComparison.OrdinalIgnoreCase))
            {
                HandleLongRecordLine(line);
                return;
            }
            if (line.StartsWith("SHOT|", StringComparison.OrdinalIgnoreCase))
            {
                HandleScreenshotLine(line);
                return;
            }

            string? friendly = FriendlyEngineMessage(line);
            if (friendly is not null) AddActivity(friendly);
            if (line.Contains("Capture started", StringComparison.OrdinalIgnoreCase))
                SetStatus("Recording", true, true);
            else if (line.Contains("Capture stopped", StringComparison.OrdinalIgnoreCase))
                SetStatus("Waiting", true, false);
            else if (line.StartsWith("Fortnite detected", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Fortnite was already running", StringComparison.OrdinalIgnoreCase))
                SetFortniteRunning(true);
            else if (line.StartsWith("Fortnite closed", StringComparison.OrdinalIgnoreCase))
                SetFortniteRunning(false);
            else if (line.StartsWith("Saved screenshot", StringComparison.OrdinalIgnoreCase))
                _ = RefreshScreenshotsAsync();
            else if (line.Contains("Saved clip", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Saved recording", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Qualified", StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshClipsAsync();
            }
        });
    }

    private void ApplyRuntimeProfile(string line)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in line.Split('|').Skip(1))
        {
            int equals = field.IndexOf('=');
            if (equals > 0) values[field[..equals]] = field[(equals + 1)..];
        }

        if (values.TryGetValue("fps", out string? fps)) FpsMetric.Text = $"{fps} FPS";
        bool gdi = values.GetValueOrDefault("backend", "").Equals("GDI", StringComparison.OrdinalIgnoreCase);
        string captureName = gdi ? "compatibility capture" : "GPU screen capture";
        PerformanceStatusText.Text =
            $"Recording with {captureName} at {FpsMetric.Text}, {QualityMetric.Text.ToLowerInvariant()} quality.";
        CaptureSubtext.Text = $"Press {HotkeyBox.Text} to save the last {ClipLengthValueText.Text}";
                if (gdi)
            AddActivity("Your PC is using a slower recording method. If clips stutter, try 30 FPS in Settings.");
    }

    private static void SignalEvent(string name)
    {
        try
        {
            using var controlEvent = EventWaitHandle.OpenExisting(name);
            controlEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        SignalEvent(SaveEventName);
        AddActivity("Saving clip…");
    }

    private void LongRecord_Click(object sender, RoutedEventArgs e) => ToggleLongRecording();

    private void ToggleLongRecording()
    {
        if (_engine is not { HasExited: false } || _longRecordSaving) return;
        if (_longRecordStarted is not null)
        {
            _longRecordSaving = true;
            LongRecordButton.Content = "Saving…";
            LongRecordButton.IsEnabled = false;
        }
        SignalEvent(LongEventName);
    }

    private void HandleLongRecordLine(string line)
    {
        string[] parts = line.Split('|', 3);
        string state = parts.Length > 1 ? parts[1] : "";
        if (state.Equals("started", StringComparison.OrdinalIgnoreCase))
        {
            SetLongRecording(true);
            AddActivity($"Long recording started. Press {LongHotkeyBox.Text} or click Stop to save it.");
            ShowTrayTip("Long recording started", $"Press {LongHotkeyBox.Text} again to stop and save.");
        }
        else if (state.Equals("stopped", StringComparison.OrdinalIgnoreCase))
        {
            SetLongRecording(false);
            string path = parts.Length > 2 ? parts[2].Trim() : "";
            if (path.Length > 0)
            {
                AddActivity($"Long recording saved: {Path.GetFileName(path)}");
                ShowTrayTip("Long recording saved", "Find it in My Clips under Recordings.");
                _ = RefreshClipsAsync();
            }
            else AddActivity("The long recording couldn't be saved.");
        }
        else if (state.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity("Long recording starts once ClipShot is recording.");
        }
    }

    private void HandleScreenshotLine(string line)
    {
        string[] parts = line.Split('|', 3);
        string state = parts.Length > 1 ? parts[1] : "";
        if (state.Equals("saved", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity("Screenshot saved");
            ShowTrayTip("Screenshot saved", "Find it on the Screenshots page.");
            _ = RefreshScreenshotsAsync();
        }
        else if (state.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
            AddActivity("Screenshots work once ClipShot is recording.");
        else
            AddActivity("Couldn't take that screenshot. Try again in a second.");
    }

    private void SetLongRecording(bool active)
    {
        bool wasActive = _longRecordStarted is not null;
        _longRecordStarted = active ? (_longRecordStarted ?? DateTime.Now) : null;
        // While stopping, the engine joins the recording; keep the button busy until it reports back.
        _longRecordSaving = false;
        LongRecordBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LongRecordText.Text = "Long recording 00:00:00";
        LongRecordButton.Content = active ? "Stop long recording" : "Start long recording";
        LongRecordButton.IsEnabled = _captureActive || active;
        if (!active && wasActive && _engine is not { HasExited: false })
            AddActivity("Long recording ended because ClipShot stopped.");
    }

    private void ShowTrayTip(string title, string text)
    {
        if (_trayIcon is null || !_trayIcon.Visible) return;
        _trayIcon.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    // Full engine output is kept in a log file for troubleshooting; the Activity list only shows
    // messages a player cares about, in plain words.
    private void WriteEngineLog(string line)
    {
        try
        {
            string folder = ResolveConfiguredPath("folders", "work", "work");
            Directory.CreateDirectory(folder);
            string logPath = Path.Combine(folder, "clipshot_log.txt");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                File.Move(logPath, Path.Combine(folder, "clipshot_log_old.txt"), true);
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? FriendlyEngineMessage(string line)
    {
        static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
        static bool Starts(string text, string part) => text.StartsWith(part, StringComparison.OrdinalIgnoreCase);

        if (Starts(line, "Saved clip:")) return $"Clip saved: {Path.GetFileName(line[11..].Trim())}";
        if (Starts(line, "Saved screenshot:"))
            return line.EndsWith("_damage.png", StringComparison.OrdinalIgnoreCase)
                ? "Trickshot screenshot saved (damage number)"
                : "Trickshot screenshot saved (swag lines)";
        if (Has(line, "final kill happened before this clip starts") || Has(line, "Couldn't find the kill in this clip"))
            return "Couldn't find the kill for a screenshot. Open the clip in My Clips and click Screenshot to pick it yourself.";
        if (Starts(line, "Saved recording:") || Starts(line, "Long recording started")) return null; // shown by the LONG| lines
        if (Has(line, "Qualified trickshot"))
        {
            int open = line.IndexOf('('), close = line.IndexOf(')');
            return open >= 0 && close > open ? $"Trickshot saved! {line[(open + 1)..close].Replace(" final kill, ", ", ")}" : "Trickshot saved!";
        }
        if (Starts(line, "Capture started")) return "Recording started";
        if (Starts(line, "Capture stopped")) return "Recording paused";
        if (Starts(line, "Fortnite detected")) return "Fortnite opened";
        if (Starts(line, "Fortnite closed")) return "Fortnite closed";
        if (Starts(line, "Completed match replay detected")) return "Match finished, checking for a trickshot…";
        if (Has(line, "did not qualify")) return "Not a trickshot this time";
        if (Starts(line, "Replay analysis:") || Has(line, "analysis was inconclusive") ||
            Has(line, "analysis failed") || Has(line, "result could not be read"))
            return Has(line, "failed") || Has(line, "could not") || Has(line, "inconclusive")
                ? "Couldn't check this match. The clip was kept in Pending."
                : null;
        if (Has(line, "replay parser is missing") || Has(line, "Audio helper is missing"))
            return "Part of ClipShot is missing. Reinstall ClipShot to fix it.";
        if (Has(line, "encoder is falling behind"))
            return "Your PC is struggling to record. Try Optimize for my PC or 30 FPS in Settings.";
        if (Has(line, "Desktop Duplication was unavailable"))
            return "Switched to a slower recording method for your PC.";
        if (Has(line, "could not be set up for screenshots"))
            return line.Replace("Warning: ", "").Replace("could not be set up for screenshots. Another app may be using it.",
                "is used by another app, so screenshots need a different key. Change it in Settings.");
        if (Has(line, "could not be registered") || Has(line, "could not be set up for long recording"))
            return line.Replace("Warning: ", "").Replace("could not be registered; GUI controls remain available.",
                "is used by another app. Pick a different shortcut in Settings.");
        if (Has(line, "Audio capture could not restart") || Has(line, "recorder stopped unexpectedly") ||
            Has(line, "audio recorder could not start"))
            return "Sound recording hiccuped. ClipShot is fixing it.";
        if (Has(line, "Audio capture restarted")) return "Sound recording is back.";
        if (Has(line, "Audio capture: disabled or no configured audio source"))
            return "No sound is being recorded. Check Sound settings.";
        if (Has(line, "saving video only") || Has(line, "saving this clip as video only") || Has(line, "saving it without audio"))
            return "This clip was saved without sound.";
        if (Has(line, "FFmpeg could not") || Has(line, "capture could not start"))
            return "Something went wrong while saving. Try again, or restart ClipShot.";
        if (Has(line, "No rolling video segment") || Has(line, "No completed segment"))
            return "Nothing recorded yet. Wait a few seconds and try again.";
        if (Starts(line, "Saving long recording")) return "Saving long recording… long sessions can take a minute.";
        if (Has(line, "long recording was too short")) return "The long recording was too short to save.";
        if (Has(line, "long recording couldn't be saved")) return null; // reported by the LONG| line
        if (Has(line, "Another ClipShot engine is already running"))
            return "ClipShot is already running.";
        // Everything else (timing checks, file steps, start-up details) only goes to the log file.
        return null;
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        ActivityList.Items.Clear();
    }

    // ===== Friendly settings controls =====
    // The sliders and the frame-rate list are front ends for the original number
    // boxes (now under Advanced), which the save/optimize code still uses.

    private bool _syncingFriendlyControls;

    private void FriendlySource_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingFriendlyControls || ClipLengthSlider is null || PostMatchSlider is null || FpsChoiceBox is null) return;
        _syncingFriendlyControls = true;
        try
        {
            if (int.TryParse(ClipLengthBox?.Text, out int clip))
                ClipLengthSlider.Value = Math.Clamp(clip, (int)ClipLengthSlider.Minimum, (int)ClipLengthSlider.Maximum);
            if (int.TryParse(PostMatchBox?.Text, out int post))
                PostMatchSlider.Value = Math.Clamp(post, 0, 60);
            FpsChoiceBox.SelectedIndex = FpsBox?.Text.Trim() switch
            {
                "30" => 0,
                "60" => 1,
                _ => -1 // a custom value set under Advanced
            };
            UpdateSliderLabels();
        }
        finally { _syncingFriendlyControls = false; }
    }

    private void ClipLengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ClipLengthBox is null || BufferBox is null) return;
        UpdateSliderLabels();
        if (_syncingFriendlyControls) return;
        _syncingFriendlyControls = true;
        try
        {
            int clip = (int)ClipLengthSlider.Value;
            ClipLengthBox.Text = clip.ToString(CultureInfo.InvariantCulture);
            // The rolling buffer must hold the clip plus room for automatic clips.
            if (!int.TryParse(BufferBox.Text, out int buffer) || buffer < clip + 60)
                BufferBox.Text = Math.Max(180, clip + 60).ToString(CultureInfo.InvariantCulture);
        }
        finally { _syncingFriendlyControls = false; }
    }

    private void PostMatchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PostMatchBox is null) return;
        UpdateSliderLabels();
        if (_syncingFriendlyControls) return;
        _syncingFriendlyControls = true;
        try { PostMatchBox.Text = ((int)PostMatchSlider.Value).ToString(CultureInfo.InvariantCulture); }
        finally { _syncingFriendlyControls = false; }
    }

    private void FpsChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFriendlyControls || FpsBox is null || FpsChoiceBox.SelectedItem is not ComboBoxItem item) return;
        _syncingFriendlyControls = true;
        try { FpsBox.Text = item.Tag?.ToString() ?? "30"; }
        finally { _syncingFriendlyControls = false; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();

    private void UpdateSliderLabels()
    {
        if (ClipLengthValueText is not null && ClipLengthSlider is not null)
        {
            int clip = (int)ClipLengthSlider.Value;
            ClipLengthValueText.Text = clip >= 60 && clip % 60 == 0 ? $"{clip / 60} min" :
                clip > 60 ? $"{clip / 60} min {clip % 60} sec" : $"{clip} seconds";
            if (ClipLengthMetric is not null) ClipLengthMetric.Text = ClipLengthValueText.Text;
        }
        if (PostMatchValueText is not null && PostMatchSlider is not null)
            PostMatchValueText.Text = PostMatchSlider.Value == 0 ? "Off" : $"{(int)PostMatchSlider.Value} seconds";
        if (VolumeValueText is not null && VolumeSlider is not null)
            VolumeValueText.Text = VolumeSlider.Value == 0 ? "Muted" : $"{(int)VolumeSlider.Value}%";
        if (MicVolumeValueText is not null && MicVolumeSlider is not null)
            MicVolumeValueText.Text = MicVolumeSlider.Value == 0 ? "Muted" : $"{(int)MicVolumeSlider.Value}%";
    }

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = AdvancedPanel.Visibility != Visibility.Visible;
        AdvancedPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggle.Content = show ? "Hide advanced settings ▴" : "Show advanced settings ▾";
    }

    /// <summary>Videos\Decrypt's ClipShot: where clips go unless the player picks another folder.</summary>
    private static string DefaultClipsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Decrypt's ClipShot");

    // Empty, or the old "clips" default inside the install folder, means the Videos folder.
    // The engine applies the same rule.
    private string ClipsDirectory
    {
        get
        {
            string value = ReadIni("folders", "clips", "").Trim();
            if (value.Length == 0 || value.Equals("clips", StringComparison.OrdinalIgnoreCase))
                return DefaultClipsDirectory;
            return Path.IsPathRooted(value) ? value : Path.Combine(BaseDirectory, value);
        }
    }

    private string ResolveConfiguredPath(string section, string key, string fallback)
    {
        string value = ReadIni(section, key, fallback);
        return Path.IsPathRooted(value) ? value : Path.Combine(BaseDirectory, value);
    }

    private async Task RefreshClipsAsync()
    {
        string clipsDirectory = ClipsDirectory;
        string thumbnailDirectory = Path.Combine(ResolveConfiguredPath("folders", "work", "work"), "thumbnails");
        Directory.CreateDirectory(clipsDirectory);
        Directory.CreateDirectory(thumbnailDirectory);

        var files = await Task.Run(() => Directory.EnumerateFiles(clipsDirectory, "*.mkv", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(clipsDirectory, "*.mp4", SearchOption.AllDirectories))
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTime).ToList());

        _trickshotClips.Clear();
        _otherClips.Clear();
        _recentClips.Clear();
        foreach (FileInfo file in files)
        {
            string category = file.Directory?.Name ?? "Clip";
            string thumb = await EnsureThumbnailAsync(file.FullName, thumbnailDirectory);
            var item = new ClipItem
            {
                Title = BuildClipTitle(file, category),
                Category = category,
                Details = $"{file.LastWriteTime:g}  •  {file.Length / 1024d / 1024d:F1} MB",
                FullPath = file.FullName,
                ThumbnailPath = File.Exists(thumb) ? thumb : null
            };
            if (string.Equals(category, "Trickshots", StringComparison.OrdinalIgnoreCase))
                _trickshotClips.Add(item);
            else
                _otherClips.Add(item);
            if (_recentClips.Count < 5) _recentClips.Add(item);
        }
    }

    private static string BuildClipTitle(FileInfo file, string category)
    {
        string baseName = Path.GetFileNameWithoutExtension(file.Name);
        if (string.Equals(category, "Trickshots", StringComparison.OrdinalIgnoreCase))
        {
            string[] tokens = baseName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3)
            {
                string token = tokens[0];
                if (token.Length > 1 && token.EndsWith('m') &&
                    double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double distance))
                {
                    string shot = tokens[1].ToLowerInvariant() switch
                    {
                        "no-scope" => "  •  No-scope",
                        "scoped" => "  •  Scoped",
                        "aimed" => "  •  Iron sights",
                        _ => ""
                    };
                    return $"{distance:0.#} m{shot}";
                }
            }
            return baseName.Replace('_', ' ');
        }
        return baseName.Replace('_', ' ');
    }

    private async Task<string> EnsureThumbnailAsync(string clipPath, string thumbnailDirectory)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clipPath)))[..16];
        string destination = Path.Combine(thumbnailDirectory, hash + ".jpg");
        if (File.Exists(destination)) return destination;

        string ffmpeg = ResolveFfmpegExecutable();
        try
        {
            var process = Process.Start(new ProcessStartInfo(ffmpeg,
                $"-hide_banner -loglevel error -y -ss 1 -i \"{clipPath}\" -frames:v 1 -vf scale=480:-2 -q:v 3 \"{destination}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null) await process.WaitForExitAsync();
        }
        catch
        {
        }
        return destination;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings(true);

    private bool SaveSettings(bool showConfirmation)
    {
        _settingsWriteFailed = false;
        if (!int.TryParse(FpsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) || fps is < 15 or > 240 ||
            !int.TryParse(BufferBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buffer) || buffer is < 10 or > 900 ||
            !int.TryParse(ClipLengthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int clipLength) || clipLength is < 10 or > 300 || clipLength > buffer ||
            !int.TryParse(PostMatchBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int postMatch) || postMatch is < 0 or > 60 ||
            !int.TryParse(SegmentBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int segment) || segment is < 1 or > 60)
        {
            MessageBox.Show("Use 15–240 FPS, a 10–900 second buffer, a 10–300 second saved clip no longer than the buffer, 0–60 seconds of post-match footage, and 1–60 second segments.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string[] shortcuts = [HotkeyBox.Text.Trim(), LongHotkeyBox.Text.Trim(), ShotHotkeyBox.Text.Trim()];
        if (shortcuts.Any(string.IsNullOrWhiteSpace))
        {
            MessageBox.Show("Choose a shortcut for saving clips, long recording and screenshots.", "Shortcut needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
        {
            MessageBox.Show("Each shortcut (clip, long recording, screenshot) needs its own key.", "Same shortcut twice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (GameAudioCheck.IsChecked == true && string.IsNullOrWhiteSpace(GameAudioDeviceBox.Text))
        {
            MessageBox.Show("Choose a game/system loopback device, or disable game audio.", "Game audio device required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        string clipsFolder;
        try
        {
            clipsFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ClipFolderBox.Text.Trim()));
            Directory.CreateDirectory(clipsFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show($"Choose a writable clips folder.\n\n{exception.Message}", "Invalid clips folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        WriteIni("recording", "ffmpeg_path", ResolveFfmpegExecutable());
        WriteIni("recording", "video_encoder", ComboValue(EncoderBox, "libx264"));
        WriteIni("recording", "encoder_preset", ComboValue(EncoderPresetBox, "balanced"));
        WriteIni("recording", "video_options", EncoderOptionsBox.Text.Trim());
        WriteIni("recording", "framerate", fps.ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "buffer_seconds", buffer.ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "clip_seconds", clipLength.ToString(CultureInfo.InvariantCulture));
        WriteIni("app", "post_match_seconds", postMatch.ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "segment_seconds", segment.ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "capture_backend", "auto");
        WriteIni("recording", "output_height", ComboValue(ResolutionBox, "1080"));
        WriteIni("recording", "prioritize_game", PrioritizeGameCheck.IsChecked == true ? "1" : "0");
        WriteIni("recording", "capture_game_audio", GameAudioCheck.IsChecked == true ? "1" : "0");
        WriteIni("recording", "game_audio_device", GameAudioDeviceBox.Text.Trim());
        WriteIni("recording", "game_audio_volume", ((int)VolumeSlider.Value).ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "capture_microphone", MicrophoneCheck.IsChecked == true ? "1" : "0");
        WriteIni("recording", "microphone_device",
            string.IsNullOrWhiteSpace(MicrophoneDeviceBox.Text) ? DefaultMicrophone : MicrophoneDeviceBox.Text.Trim());
        WriteIni("recording", "microphone_volume", ((int)MicVolumeSlider.Value).ToString(CultureInfo.InvariantCulture));
        WriteIni("recording", "audio_helper_path", "ClipShotAudio.exe");
        WriteIni("folders", "clips", clipsFolder);
        WriteIni("controls", "manual_clip_hotkey", HotkeyBox.Text.Trim());
        WriteIni("controls", "long_recording_hotkey", LongHotkeyBox.Text.Trim());
        WriteIni("controls", "screenshot_hotkey", ShotHotkeyBox.Text.Trim());
        WriteIni("app", "trickshot_screenshot", TrickshotScreenshotCheck.IsChecked == true ? "1" : "0");
        UpdateHotkeyHint();
        WriteIni("app", "start_only_with_fortnite", FortniteOnlyCheck.IsChecked == true ? "1" : "0");
        WriteIni("app", "start_recording_on_open", StartOnOpenCheck.IsChecked == true ? "1" : "0");
        SetStartWithWindows(StartWithWindowsCheck.IsChecked == true);
        SyncQuickSwitches();
        UpdateSourceStatus();
        WriteIni("local_replay", "enabled", LocalReplayCheck.IsChecked == true ? "1" : "0");
        WriteIni("app", "keep_rejected_clips", KeepRejectedCheck.IsChecked == true ? "1" : "0");
        WriteIni("app", "minimize_to_tray", MinimizeToTrayCheck.IsChecked == true ? "1" : "0");
        WriteIni("app", "check_for_updates", UpdatesCheck.IsChecked == true ? "1" : "0");
        if (_settingsWriteFailed)
        {
            MessageBox.Show($"ClipShot could not save settings to:\n{IniPath}", "Settings were not saved", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        RefreshMetrics();
        _ = RefreshClipsAsync();
        if (showConfirmation) AddActivity("Settings saved");
        if (showConfirmation)
            MessageBox.Show("Settings saved.\n\nIf ClipShot is running, stop and start it again to use the new recording settings.", "Decrypt's ClipShot", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "FFmpeg executable|ffmpeg.exe|Executables|*.exe", Title = "Choose ffmpeg.exe" };
        if (dialog.ShowDialog() == true) FfmpegPathBox.Text = dialog.FileName;
    }

    private void BrowseClipFolder_Click(object sender, RoutedEventArgs e)
    {
        string current = ClipFolderBox.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where Decrypt's ClipShot saves clips",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog() == true) ClipFolderBox.Text = dialog.FolderName;
    }

    private string ResolveFfmpegExecutable()
    {
        string bundled = Path.Combine(BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            FfmpegPathBox.Text = bundled;
            return bundled;
        }

        string configured = FfmpegPathBox.Text.Trim();
        if (!Path.IsPathRooted(configured) && configured.Contains(Path.DirectorySeparatorChar))
            configured = Path.Combine(BaseDirectory, configured);
        return string.IsNullOrWhiteSpace(configured) ? "ffmpeg.exe" : configured;
    }

    private async void AutoConfigureFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        string ffmpeg = ResolveFfmpegExecutable();
        FfmpegPathBox.Text = ffmpeg;
        PerformanceStatusText.Text = "Checking your PC…";
        string? encoder = await DetectWorkingHardwareEncoderAsync(ffmpeg);
        EncoderBox.SelectedIndex = encoder switch
        {
            "h264_nvenc" => 1,
            "h264_mf" => 2,
            "h264_amf" => 3,
            "h264_qsv" => 4,
            _ => 0
        };
        string preset = ComboValue(EncoderPresetBox, "balanced");
        if (preset == "custom")
        {
            EncoderPresetBox.SelectedIndex = 0;
            preset = "balanced";
        }
        EncoderOptionsBox.Text = GetEncoderOptions(ComboValue(EncoderBox, "libx264"), preset);
        PerformanceStatusText.Text = encoder is null
            ? "Recording will use your processor."
            : "Recording will use your graphics card, so your game stays smooth.";
    }

    private async void OptimizeForGameplay_Click(object sender, RoutedEventArgs e)
    {
        OptimizePerformanceButton.IsEnabled = false;
        PerformanceStatusText.Text = "Checking your PC. This takes a few seconds…";
        try
        {
            string ffmpeg = ResolveFfmpegExecutable();
            string? encoder = await DetectWorkingHardwareEncoderAsync(ffmpeg);
            EncoderBox.SelectedIndex = encoder switch
            {
                "h264_nvenc" => 1,
                "h264_mf" => 2,
                "h264_amf" => 3,
                "h264_qsv" => 4,
                _ => 0
            };
            EncoderPresetBox.SelectedIndex = 2;
            PrioritizeGameCheck.IsChecked = true;
            SegmentBox.Text = "30";
            ClipLengthBox.Text = "90";

            if (encoder is null)
            {
                FpsBox.Text = "30";
                ResolutionBox.SelectedIndex = 2;
                PerformanceStatusText.Text = "Your graphics card couldn't record, so ClipShot picked lighter settings (720p, 30 FPS). Click Save, then restart ClipShot.";
            }
            else
            {
                FpsBox.Text = "60";
                ResolutionBox.SelectedIndex = 1;
                PerformanceStatusText.Text = $"Your graphics card ({encoder}) will do the recording, so your game stays smooth. Click Save, then restart ClipShot.";
            }

            string selectedEncoder = ComboValue(EncoderBox, "libx264");
            EncoderOptionsBox.Text = GetEncoderOptions(selectedEncoder, "performance");
            RefreshMetrics();
            AddActivity("Gameplay performance settings prepared");
        }
        finally
        {
            OptimizePerformanceButton.IsEnabled = true;
        }
    }

    private static async Task<string?> DetectWorkingHardwareEncoderAsync(string ffmpeg)
    {
        // Match the encoder selection order from the last proven smooth-video build.
        string[] candidates = ["h264_nvenc", "h264_amf", "h264_qsv"];
        foreach (string encoder in candidates)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            Process? process = null;
            try
            {
                string encoderTestOptions = encoder == "h264_mf"
                    ? "-vf format=nv12 -hw_encoding 1 -rate_control quality -quality 75"
                    : "";
                var start = new ProcessStartInfo(ffmpeg,
                    $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=128x128:r=30:d=0.2 -frames:v 2 -c:v {encoder} {encoderTestOptions} -f null -")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                process = Process.Start(start);
                if (process is null) continue;
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                await Task.WhenAll(errorTask, outputTask, process.WaitForExitAsync(cancellation.Token));
                if (process.ExitCode == 0) return encoder;
            }
            catch (OperationCanceledException)
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                // Try the next encoder if this hardware test stalls.
            }
            catch
            {
                // Unsupported encoders are expected on systems without that GPU.
            }
            finally
            {
                process?.Dispose();
            }
        }
        return null;
    }

    private async Task RefreshAudioDevicesAsync(bool showResult)
    {
        string gameSelection = GameAudioDeviceBox.Text;
        string micSelection = MicrophoneDeviceBox.Text;
        AudioDeviceStatusText.Text = "Looking for your speakers and headsets…";
        AudioDeviceScan detected;

        try
        {
            detected = await DetectAudioDevicesAsync();
        }
        catch (Exception ex)
        {
            AudioDeviceStatusText.Text = "Part of ClipShot is missing, so sound can't be set up. Reinstall ClipShot to fix it.";
            if (showResult)
                MessageBox.Show(ex.Message, "Could not scan audio devices", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _outputDevices.Clear();
        _outputDevices.Add(DefaultOutputDevice);
        foreach (string device in detected.Outputs)
            if (!_outputDevices.Contains(device, StringComparer.OrdinalIgnoreCase))
                _outputDevices.Add(device);

        if (!string.IsNullOrWhiteSpace(gameSelection) &&
            _outputDevices.Contains(gameSelection, StringComparer.OrdinalIgnoreCase))
            GameAudioDeviceBox.Text = gameSelection;
        else
            GameAudioDeviceBox.Text = DefaultOutputDevice;

        _microphones.Clear();
        _microphones.Add(DefaultMicrophone);
        foreach (string device in detected.Microphones)
            if (!_microphones.Contains(device, StringComparer.OrdinalIgnoreCase))
                _microphones.Add(device);
        MicrophoneDeviceBox.Text =
            !string.IsNullOrWhiteSpace(micSelection) && _microphones.Contains(micSelection, StringComparer.OrdinalIgnoreCase)
                ? micSelection
                : DefaultMicrophone;

        string outputName = string.IsNullOrWhiteSpace(detected.DefaultOutput)
            ? "the Windows default output"
            : detected.DefaultOutput;
        AudioDeviceStatusText.Text =
            $"Game sound follows: {outputName}." +
            (detected.Microphones.Count == 0 ? " No microphone was found." : "");

        if (showResult)
            MessageBox.Show(AudioDeviceStatusText.Text,
                "Audio devices", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed record AudioDeviceScan(string DefaultOutput, List<string> Outputs, List<string> Microphones);

    private async Task<AudioDeviceScan> DetectAudioDevicesAsync()
    {
        if (!File.Exists(AudioHelperPath))
            throw new FileNotFoundException("ClipShotAudio.exe was not found beside the application.", AudioHelperPath);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(AudioHelperPath, "--list")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows audio helper.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellation.Token));
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorTask.Result)
                ? "The Windows audio helper could not enumerate devices."
                : errorTask.Result.Trim());

        using JsonDocument json = JsonDocument.Parse(outputTask.Result);
        JsonElement root = json.RootElement;
        static List<string> Names(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values)
                ? values.EnumerateArray().Select(value => value.GetString() ?? "")
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                : [];
        return new AudioDeviceScan(
            root.TryGetProperty("defaultOutput", out JsonElement output) ? output.GetString() ?? "" : "",
            Names(root, "outputs"),
            Names(root, "microphones"));
    }

    private async void RefreshAudioDevices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAudioDevicesAsync(true);
        }
        catch (OperationCanceledException)
        {
            AudioDeviceStatusText.Text = "Couldn't list your sound devices. Default will still work.";
        }
    }

    private void CaptureHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture(HotkeyBox, HotkeyCaptureButton);
    private void CaptureLongHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture(LongHotkeyBox, LongHotkeyCaptureButton);
    private void CaptureShotHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture(ShotHotkeyBox, ShotHotkeyCaptureButton);

    private void UpdateHotkeyHint()
    {
        HotkeyHintText.Text = $"{HotkeyBox.Text.Trim()} clip  •  {LongHotkeyBox.Text.Trim()} long recording  •  {ShotHotkeyBox.Text.Trim()} screenshot";
        HeaderHotkeysText.Text = $"{HotkeyBox.Text.Trim()} clip  ·  {LongHotkeyBox.Text.Trim()} long  ·  {ShotHotkeyBox.Text.Trim()} screenshot";
        HotkeyMetric.Text = HotkeyBox.Text.Trim();
        LongHotkeyMetric.Text = LongHotkeyBox.Text.Trim();
        ShotHotkeyMetric.Text = ShotHotkeyBox.Text.Trim();
    }

    private void BeginHotkeyCapture(TextBox target, Button button)
    {
        if (_hotkeyTargetButton is not null) _hotkeyTargetButton.Content = "Set shortcut";
        _capturingHotkey = true;
        _hotkeyTarget = target;
        _hotkeyTargetButton = button;
        button.Content = "Press a key...";
        Keyboard.Focus(button);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        string? keyName = FormatHotkeyKey(key);
        if (keyName is null)
        {
            MessageBox.Show("Use F1–F24, A–Z, 0–9, Space, Insert, Delete, Home, End, Page Up, or Page Down.",
                "Unsupported shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
            _capturingHotkey = false;
            if (_hotkeyTargetButton is not null) _hotkeyTargetButton.Content = "Set shortcut";
            e.Handled = true;
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyName);
        (_hotkeyTarget ?? HotkeyBox).Text = string.Join("+", parts);
        UpdateHotkeyHint();
        _capturingHotkey = false;
        if (_hotkeyTargetButton is not null) _hotkeyTargetButton.Content = "Set shortcut";
        e.Handled = true;
    }

    private static string? FormatHotkeyKey(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return key.ToString()[1..];
        return key switch
        {
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => null
        };
    }

    private void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is ClipItem clip)
            Process.Start(new ProcessStartInfo(clip.FullPath) { UseShellExecute = true });
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingClipSelection || (sender as ListBox)?.SelectedItem is null) return;
        _changingClipSelection = true;
        try
        {
            if (ReferenceEquals(sender, TrickshotClipsList))
                OtherClipsList.UnselectAll();
            else
                TrickshotClipsList.UnselectAll();
        }
        finally
        {
            _changingClipSelection = false;
        }
    }

    private ClipItem? SelectedClip() =>
        TrickshotClipsList.SelectedItem as ClipItem ?? OtherClipsList.SelectedItem as ClipItem;

    private async void RenameClip_Click(object sender, RoutedEventArgs e)
    {
        ClipItem? clip = SelectedClip();
        if (clip is null)
        {
            MessageBox.Show("Select a clip first, then choose Rename selected.", "Rename clip",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.Equals(clip.Category, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("This clip is still waiting for replay analysis. Rename it after ClipShot finishes classifying it.",
                "Clip is still pending", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string currentName = Path.GetFileNameWithoutExtension(clip.FullPath);
        string? requestedName = ShowRenameDialog(currentName);
        if (requestedName is null) return;

        string name = requestedName.Trim();
        string extension = Path.GetExtension(clip.FullPath);
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length].TrimEnd();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("Enter a normal file name without slashes or Windows file-name symbols.",
                "Invalid clip name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string directory = Path.GetDirectoryName(clip.FullPath) ?? "";
        string destination = Path.Combine(directory, name + extension);
        if (string.Equals(destination, clip.FullPath, StringComparison.Ordinal)) return;
        if (File.Exists(destination) &&
            !string.Equals(destination, clip.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("A clip with that name already exists in this section.",
                "Name already used", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (string.Equals(destination, clip.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                string temporary = Path.Combine(directory, $".clipshot-rename-{Guid.NewGuid():N}{extension}");
                File.Move(clip.FullPath, temporary);
                try { File.Move(temporary, destination); }
                catch { File.Move(temporary, clip.FullPath); throw; }
            }
            else
            {
                File.Move(clip.FullPath, destination);
            }
            AddActivity($"Renamed clip to {Path.GetFileName(destination)}");
            await RefreshClipsAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"ClipShot could not rename the clip.\n\n{exception.Message}",
                "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? ShowRenameDialog(string currentName)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Rename clip",
            Width = 470,
            Height = 205,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#09090B"))
        };
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141417")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34343A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22)
        };
        var layout = new StackPanel();
        var heading = new TextBlock { Text = "Rename clip", FontSize = 19, FontWeight = FontWeights.SemiBold };
        var hint = new TextBlock
        {
            Text = "The video format stays the same.",
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 12)
        };
        var input = new TextBox
        {
            Text = currentName,
            Style = (Style)FindResource("InputStyle"),
            MinWidth = 400
        };
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 9, 0)
        };
        var rename = new Button { Content = "Rename", Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        rename.Click += (_, _) => dialog.DialogResult = true;
        input.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter) dialog.DialogResult = true;
            else if (key.Key == Key.Escape) dialog.DialogResult = false;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(rename);
        layout.Children.Add(heading);
        layout.Children.Add(hint);
        layout.Children.Add(input);
        layout.Children.Add(actions);
        border.Child = layout;
        dialog.Content = border;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private static string DefaultScreenshotsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Decrypt's ClipShot");

    // Screenshots live apart from clips (Pictures, not Videos). The engine applies the same rule.
    private string ScreenshotsDirectory
    {
        get
        {
            string value = ReadIni("folders", "screenshots", "").Trim();
            return value.Length == 0 ? DefaultScreenshotsDirectory
                : Path.IsPathRooted(value) ? value : Path.Combine(BaseDirectory, value);
        }
    }

    private bool _refreshingScreenshots;

    private async Task RefreshScreenshotsAsync()
    {
        if (_refreshingScreenshots) return;
        _refreshingScreenshots = true;
        try
        {
            string folder = ScreenshotsDirectory;
            string thumbnails = Path.Combine(ResolveConfiguredPath("folders", "work", "work"), "thumbnails");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(thumbnails);
            var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(folder, "*.jpg", SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTime).ToList());
            _screenshots.Clear();
            foreach (FileInfo file in files)
            {
                string thumb = await EnsureImageThumbnailAsync(file.FullName, thumbnails);
                _screenshots.Add(new ClipItem
                {
                    Title = Path.GetFileNameWithoutExtension(file.Name).Replace('_', ' '),
                    Category = "Screenshot",
                    Details = $"{file.LastWriteTime:g}",
                    FullPath = file.FullName,
                    ThumbnailPath = File.Exists(thumb) ? thumb : null
                });
            }
            ScreenshotsEmptyText.Visibility = _screenshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScreenshotCountText.Text = _screenshots.Count == 0 ? "None yet · saved in your Pictures folder"
                : _screenshots.Count == 1 ? "1 screenshot saved" : $"{_screenshots.Count} screenshots saved";
            ScreenshotsEmptyText.Text = $"No screenshots yet. Trickshots get one automatically, press {ShotHotkeyBox.Text} in game, or open a clip in My Clips and click Screenshot.";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            _refreshingScreenshots = false;
        }
    }

    // Small copies for the Screenshots page, so full-size pictures aren't loaded (or locked) by the list.
    private async Task<string> EnsureImageThumbnailAsync(string imagePath, string thumbnailDirectory)
    {
        var info = new FileInfo(imagePath);
        string key = $"{imagePath}|{info.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        string destination = Path.Combine(thumbnailDirectory, "shot_" + hash + ".jpg");
        if (File.Exists(destination)) return destination;
        try
        {
            var process = Process.Start(new ProcessStartInfo(ResolveFfmpegExecutable(),
                $"-hide_banner -loglevel error -y -i \"{imagePath}\" -frames:v 1 -vf scale=480:-2 -q:v 3 \"{destination}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null) await process.WaitForExitAsync();
        }
        catch
        {
        }
        return destination;
    }

    private void RefreshScreenshots_Click(object sender, RoutedEventArgs e) => _ = RefreshScreenshotsAsync();

    private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = ScreenshotsDirectory;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void ScreenshotsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScreenshotsList.SelectedItem is ClipItem shot && File.Exists(shot.FullPath))
            Process.Start(new ProcessStartInfo(shot.FullPath) { UseShellExecute = true });
    }

    private void ClipScreenshot_Click(object sender, RoutedEventArgs e)
    {
        ClipItem? clip = SelectedClip();
        if (clip is null || !File.Exists(clip.FullPath))
        {
            MessageBox.Show("Click a clip first, then click Screenshot.", "Pick a clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new ScreenshotWindow(clip.FullPath, EnginePath, ScreenshotsDirectory) { Owner = this };
        window.ShowDialog();
        if (window.LastSavedPath is not null)
        {
            AddActivity("Screenshot saved");
            _ = RefreshScreenshotsAsync();
        }
    }

    private void OpenClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = ClipsDirectory;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private async void RefreshClips_Click(object sender, RoutedEventArgs e) => await RefreshClipsAsync();

    private void BrowseReplay_Click(object sender, RoutedEventArgs e)
    {
        string demos = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FortniteGame", "Saved", "Demos");
        var dialog = new OpenFileDialog
        {
            Title = "Choose an older Fortnite replay",
            Filter = "Fortnite replay (*.replay)|*.replay|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(demos) ? demos : null,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        ReplayPathBox.Text = dialog.FileName;
        ReplayAnalysisStatusText.Text = "Ready.";
        ReplayResultCard.Visibility = Visibility.Collapsed;
    }

    private async void AnalyzeReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_captureActive || _engine is { HasExited: false })
        {
            ReplayAnalysisStatusText.Text = "Turn ClipShot off before analyzing a replay so parsing cannot affect game performance.";
            MessageBox.Show("Turn ClipShot off before analyzing an older replay.",
                "Meter Checker", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string replayPath = ReplayPathBox.Text.Trim();
        if (!File.Exists(replayPath))
        {
            ReplayAnalysisStatusText.Text = "Choose a replay first.";
            return;
        }
        if (!File.Exists(ReplayParserPath))
        {
            ReplayAnalysisStatusText.Text = "Part of ClipShot is missing. Reinstall ClipShot to fix it.";
            return;
        }

        AnalyzeReplayButton.IsEnabled = false;
        ReplayResultCard.Visibility = Visibility.Collapsed;
        ReplayAnalysisStatusText.Text = "Checking the replay… long matches can take a minute.";
        string analysisFolder = Path.Combine(UserDataDirectory, "replay-analysis");
        Directory.CreateDirectory(analysisFolder);
        string resultPath = Path.Combine(analysisFolder, $"result-{Guid.NewGuid():N}.json");
        Process? parser = null;
        try
        {
            var start = new ProcessStartInfo(ReplayParserPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(replayPath);
            start.ArgumentList.Add(resultPath);
            parser = Process.Start(start);
            if (parser is null) throw new InvalidOperationException("The local parser could not be started.");

            Task<string> outputTask = parser.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = parser.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await parser.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string error = await errorTask;
            if (parser.ExitCode != 0 || !File.Exists(resultPath))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? string.IsNullOrWhiteSpace(output) ? "The replay could not be parsed." : output.Trim()
                    : error.Trim());

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            ShowReplayResult(document.RootElement);
            AddActivity("Replay checked");
        }
        catch (OperationCanceledException)
        {
            if (parser is { HasExited: false }) parser.Kill(entireProcessTree: true);
            ReplayAnalysisStatusText.Text = "This replay took too long to read. It may be unfinished or from an older Fortnite season.";
        }
        catch (Exception exception)
        {
            _ = exception;
            ReplayAnalysisStatusText.Text = "ClipShot couldn't read this replay. It may be unfinished or from an older Fortnite season.";
            AddActivity("Couldn't read that replay");
        }
        finally
        {
            parser?.Dispose();
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
            AnalyzeReplayButton.IsEnabled = true;
        }
    }

    private void ShowReplayResult(JsonElement root)
    {
        bool conclusive = JsonBoolean(root, "conclusive");
        double distanceUU = JsonNumber(root, "distanceUU");
        string killer = JsonText(root, "killer", "");

        // The manual analyzer may inspect a friend's replay kill. Automatic
        // clipping remains protected by the engine's ownerFinalKill check.
        bool haveFinal = conclusive && !string.IsNullOrWhiteSpace(killer);
        FinalKillCard.Visibility = haveFinal ? Visibility.Visible : Visibility.Collapsed;
        if (haveFinal)
            FillKillCard(root, FinalKillShotText, FinalKillNameWeaponButton, FinalKillDistanceText, FinalKillKillerText, FinalKillVictimText,
                FinalKillWeaponText, FinalKillRarityText);

        bool haveFurthest = root.TryGetProperty("furthest", out JsonElement furthest) &&
                            furthest.ValueKind == JsonValueKind.Object &&
                            JsonNumber(furthest, "distanceUU") > 0;
        FurthestKillCard.Visibility = haveFurthest ? Visibility.Visible : Visibility.Collapsed;
        if (haveFurthest)
            FillKillCard(furthest, FurthestKillShotText, FurthestKillNameWeaponButton, FurthestKillDistanceText, FurthestKillKillerText,
                FurthestKillVictimText, FurthestKillWeaponText, FurthestKillRarityText);

        // Keep one card full width when the other has nothing to show.
        Grid.SetColumnSpan(FinalKillCard, haveFurthest ? 1 : 2);
        Grid.SetColumn(FurthestKillCard, haveFinal ? 1 : 0);
        Grid.SetColumnSpan(FurthestKillCard, haveFinal ? 1 : 2);
        FurthestKillCard.Margin = haveFinal ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        FinalKillCard.Margin = haveFurthest ? new Thickness(0, 0, 8, 0) : new Thickness(0);

        ReplayAnalysisStatusText.Text = haveFinal && distanceUU > 0
            ? "Done."
            : "This replay doesn't have an elimination ClipShot could measure.";
        ReplayResultCard.Visibility = haveFinal || haveFurthest ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Naming unknown weapons =====
    // Fortnite names weapons by codename in replays. When one isn't in
    // ClipShot's list, the player can name it once; the parser checks
    // %LOCALAPPDATA%\Decrypts ClipShot\weapons.json before its built-in list.

    private static string UserWeaponsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Decrypts ClipShot", "weapons.json");

    private void NameWeapon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string> info }) return;
        (string key, string shownAs) = info;
        var dialog = new NameWeaponWindow(key, shownAs) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(UserWeaponsPath) &&
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(UserWeaponsPath)) is System.Text.Json.Nodes.JsonObject existing)
                root = existing;
            else
                root = new System.Text.Json.Nodes.JsonObject();
            if (root["weapons"] is not System.Text.Json.Nodes.JsonArray weapons)
            {
                weapons = new System.Text.Json.Nodes.JsonArray();
                root["weapons"] = weapons;
            }
            // Replace an earlier name for the same codename.
            for (int index = weapons.Count - 1; index >= 0; index--)
            {
                if (weapons[index]?["match"] is System.Text.Json.Nodes.JsonArray match &&
                    match.Any(token => string.Equals(token?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase)))
                    weapons.RemoveAt(index);
            }
            weapons.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = dialog.WeaponName,
                ["match"] = new System.Text.Json.Nodes.JsonArray(key),
                ["class"] = dialog.IsSniper ? "Sniper Rifle" : shownAs,
                ["ammo"] = dialog.UsesHeavyAmmo ? "Heavy" : "",
                ["sniper"] = dialog.IsSniper,
                ["scope"] = dialog.HasScope,
                ["source"] = $"Named in Meter Checker on {DateTime.Now:yyyy-MM-dd}"
            });
            Directory.CreateDirectory(Path.GetDirectoryName(UserWeaponsPath)!);
            File.WriteAllText(UserWeaponsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AddActivity($"Saved weapon name \"{dialog.WeaponName}\" for {key}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, $"The weapon name couldn't be saved: {exception.Message}", "Name this weapon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Show the new name straight away.
        AnalyzeReplay_Click(this, new RoutedEventArgs());
    }

    private static void FillKillCard(JsonElement kill, TextBlock shot, Button nameWeapon,
                                     TextBlock distance, TextBlock killer,
                                     TextBlock victim, TextBlock weapon, TextBlock rarity)
    {
        string shotLabel = JsonText(kill, "shotLabel", "");
        shot.Text = Present(shotLabel);
        shot.FontWeight = shotLabel == "No-scope" ? FontWeights.SemiBold : FontWeights.Normal;
        shot.Foreground = shotLabel == "No-scope"
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x55, 0x55))
            : new SolidColorBrush(Color.FromRgb(0xF9, 0xF4, 0xF4));
        double distanceUU = JsonNumber(kill, "distanceUU");
        distance.Text = distanceUU > 0
            ? (distanceUU / 100.0).ToString("F2", CultureInfo.CurrentCulture) + " m"
            : "—";
        killer.Text = WithPlatform(JsonText(kill, "killer", ""), JsonText(kill, "killerPlatform", ""));
        victim.Text = WithPlatform(JsonText(kill, "victim", ""), JsonText(kill, "victimPlatform", ""));
        killer.ToolTip = killer.Text;
        victim.ToolTip = victim.Text;
        weapon.Text = Present(JsonText(kill, "weapon", ""));
        // Hovering the weapon shows the raw Fortnite tags it was read from,
        // which helps when a new season renames weapons.
        string[] tags = kill.TryGetProperty("weaponTags", out JsonElement tagList) && tagList.ValueKind == JsonValueKind.Array
            ? tagList.EnumerateArray().Select(tag => tag.GetString() ?? "").Where(tag => tag.Length > 0).ToArray()
            : [];
        bool known = kill.TryGetProperty("weaponKnown", out JsonElement knownValue) &&
                     knownValue.ValueKind == JsonValueKind.True;
        _ = tags;
        weapon.ToolTip = known ? null
            : "ClipShot doesn't recognize this weapon yet. Use \"Name this weapon\" to teach it.";
        string key = JsonText(kill, "weaponKey", "");
        nameWeapon.Visibility = !known && key.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        nameWeapon.Tag = (key, weapon.Text);
        string rarityName = Present(JsonText(kill, "rarity", ""));
        rarity.Text = rarityName;
        rarity.Foreground = new SolidColorBrush(RarityColor(rarityName));
    }

    private static string Present(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? "—"
            : value;

    private static string WithPlatform(string name, string platform)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return "—";
        return string.IsNullOrWhiteSpace(platform) ? name : $"{name} ({platform})";
    }

    // Standard Fortnite rarity colors.
    private static Color RarityColor(string rarity) => rarity.ToLowerInvariant() switch
    {
        "common" => Color.FromRgb(0xB1, 0xB1, 0xB1),
        "uncommon" => Color.FromRgb(0x60, 0xAA, 0x3A),
        "rare" => Color.FromRgb(0x49, 0xAC, 0xF2),
        "epic" => Color.FromRgb(0xB1, 0x5B, 0xE2),
        "legendary" => Color.FromRgb(0xF0, 0x96, 0x2E),
        "mythic" => Color.FromRgb(0xF4, 0xD0, 0x3E),
        "exotic" => Color.FromRgb(0x76, 0xD6, 0xE3),
        _ => Color.FromRgb(0xF9, 0xF4, 0xF4)
    };

    private static string JsonText(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool JsonBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed);

    private static double JsonNumber(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private void ShowPanel(UIElement panel)
    {
        DashboardPanel.Visibility = panel == DashboardPanel ? Visibility.Visible : Visibility.Collapsed;
        ClipsPanel.Visibility = panel == ClipsPanel ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsPanel.Visibility = panel == ScreenshotsPanel ? Visibility.Visible : Visibility.Collapsed;
        ReplayAnalyzerPanel.Visibility = panel == ReplayAnalyzerPanel ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == SettingsPanel ? Visibility.Visible : Visibility.Collapsed;
        // Highlight the current page in the sidebar.
        DashboardNav.Tag = panel == DashboardPanel ? "Active" : null;
        ClipsNav.Tag = panel == ClipsPanel ? "Active" : null;
        ScreenshotsNav.Tag = panel == ScreenshotsPanel ? "Active" : null;
        ReplayAnalyzerNav.Tag = panel == ReplayAnalyzerPanel ? "Active" : null;
        SettingsNav.Tag = panel == SettingsPanel ? "Active" : null;
    }

    private void DashboardNav_Click(object sender, RoutedEventArgs e) => ShowPanel(DashboardPanel);
    private void ClipsNav_Click(object sender, RoutedEventArgs e) { ShowPanel(ClipsPanel); _ = RefreshClipsAsync(); }
    private void ScreenshotsNav_Click(object sender, RoutedEventArgs e) { ShowPanel(ScreenshotsPanel); _ = RefreshScreenshotsAsync(); }
    private void ReplayAnalyzerNav_Click(object sender, RoutedEventArgs e) => ShowPanel(ReplayAnalyzerPanel);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButton();
        if (WindowState != WindowState.Minimized || MinimizeToTrayCheck?.IsChecked != true) return;
        HideToTray();
    }

    private void InitializeTrayIcon()
    {
        System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
            ?? System.Drawing.SystemIcons.Application;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open ClipShot", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Save Clip", null, (_, _) => Dispatcher.Invoke(() =>
        {
            SignalEvent(SaveEventName);
            AddActivity("Saving clip…");
        }));
        menu.Items.Add("Start / stop long recording", null, (_, _) => Dispatcher.Invoke(ToggleLongRecording));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Decrypt's ClipShot",
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(async () =>
        {
            if (_pendingUpdate is null) return;
            RestoreFromTray();
            await ShowUpdatePopupAsync(_pendingUpdate);
        });
    }

    private void HideToTray()
    {
        if (_trayIcon is null) return;
        ShowInTaskbar = false;
        Hide();
        _trayIcon.Visible = true;
        if (_trayTipShown) return;
        _trayIcon.ShowBalloonTip(2500, "Decrypt's ClipShot",
            "ClipShot is still running. Double-click this icon to restore it.",
            System.Windows.Forms.ToolTipIcon.Info);
        _trayTipShown = true;
    }

    private void RestoreFromTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ===== Updates =====

    private bool _checkingForUpdates;
    private UpdateInfo? _pendingUpdate;

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates) return;
        if (!manual && ReadIni("app", "check_for_updates", "1") == "0") return;
        _checkingForUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            UpdateInfo? update;
            try
            {
                update = await UpdateChecker.CheckAsync();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (manual)
                    MessageBox.Show(this, "ClipShot couldn't check for updates. Check your internet connection and try again.",
                        "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (update is null)
            {
                if (manual)
                    MessageBox.Show(this, $"You're up to date (version {UpdateChecker.CurrentVersion}).",
                        "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Respect "Skip this version" for automatic checks only.
            if (!manual && ReadIni("app", "skipped_version", "") == update.Version.ToString()) return;

            AddActivity($"Update available: ClipShot {update.Version}");
            if (!IsVisible && _trayIcon is { Visible: true })
            {
                // ClipShot is in the tray: don't pop a window over the game.
                _pendingUpdate = update;
                _trayIcon.ShowBalloonTip(6000, "ClipShot update available",
                    $"Version {update.Version} is ready. Click here to update.", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
            await ShowUpdatePopupAsync(update);
        }
        finally
        {
            _checkingForUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async Task ShowUpdatePopupAsync(UpdateInfo update)
    {
        _pendingUpdate = null;
        var popup = new UpdateWindow(update, _captureActive) { Owner = this };
        bool? accepted = popup.ShowDialog();
        if (popup.SkipThisVersion) WriteIni("app", "skipped_version", update.Version.ToString());
        if (accepted != true || popup.DownloadedSetup is null) return;

        // Stop recording cleanly, then hand over to the new setup, which
        // updates in place and offers to reopen ClipShot when it's done.
        if (_engine is { HasExited: false } engine)
        {
            _stoppingEngine = true;
            SignalEvent(StopEventName);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await engine.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { engine.Kill(entireProcessTree: true); } catch { }
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo(popup.DownloadedSetup, "--update") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"The update couldn't start: {exception.Message}", "Update",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void CleanupOrphanedBundledFfmpeg()
    {
        string bundledFfmpeg = Path.GetFullPath(Path.Combine(BaseDirectory, "ffmpeg.exe"));
        bool engineStillRunning = Process.GetProcessesByName("TrickClip").Any(process =>
        {
            try
            {
                return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""),
                    Path.GetFullPath(EnginePath), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                process.Dispose();
            }
        });

        if (engineStillRunning) return;

        foreach (Process process in Process.GetProcessesByName("ffmpeg"))
        {
            try
            {
                string processPath = Path.GetFullPath(process.MainModule?.FileName ?? "");
                if (string.Equals(processPath, bundledFfmpeg, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Ignore protected or already-finished processes.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        if (_engine is { HasExited: false } engine)
        {
            SignalEvent(StopEventName);
            if (!engine.WaitForExit(8000))
            {
                engine.Kill(entireProcessTree: true);
                engine.WaitForExit(3000);
            }
        }
        _engine?.Dispose();
        _engine = null;
        _timer.Stop();
        base.OnClosed(e);
    }
}
