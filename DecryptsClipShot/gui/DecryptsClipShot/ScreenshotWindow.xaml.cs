using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DecryptsClipShot;

// Lets a player pick any frame of a clip and save it as a full-quality screenshot.
// The engine does the work (TrickClip.exe --find-kill / --screenshot), so saved
// screenshots are exact frames from the clip, not a copy of the preview.
public partial class ScreenshotWindow : Window
{
    private const double FrameStep = 1.0 / 60.0;
    private readonly string _clipPath;
    private readonly string _enginePath;
    private readonly string _screenshotsFolder;
    private readonly DispatcherTimer _timer;
    private bool _playing;
    private bool _updatingSlider;
    private bool _busy;

    public string? LastSavedPath { get; private set; }

    public ScreenshotWindow(string clipPath, string enginePath, string screenshotsFolder)
    {
        InitializeComponent();
        _clipPath = clipPath;
        _enginePath = enginePath;
        _screenshotsFolder = screenshotsFolder;
        TitleText.Text = $"Screenshot from {Path.GetFileNameWithoutExtension(clipPath)}";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => SyncSlider();
        Player.Source = new Uri(clipPath);
        // Showing a paused frame makes the player load the video.
        Player.Play();
        Player.Pause();
        StatusText.Text = "Loading clip…";
    }

    private double Duration => Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0.0;
    private double Position => Player.Position.TotalSeconds;

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        PositionSlider.Maximum = Math.Max(0.1, Duration);
        StatusText.Text = "Ready.";
        _timer.Start();
        SyncSlider();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StatusText.Text = "This clip can't be previewed here, but the Find buttons and Save screenshot still work.";
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _playing = false;
        PlayButton.Content = "Play";
    }

    private void SyncSlider()
    {
        _updatingSlider = true;
        PositionSlider.Value = Math.Min(PositionSlider.Maximum, Position);
        _updatingSlider = false;
        TimeSpan time = TimeSpan.FromSeconds(Position);
        TimeText.Text = $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider) return;
        SeekTo(e.NewValue);
    }

    private void SeekTo(double seconds)
    {
        double clamped = Math.Clamp(seconds, 0.0, Math.Max(0.0, Duration));
        Player.Position = TimeSpan.FromSeconds(clamped);
        SyncSlider();
    }

    private void PausePlayback()
    {
        if (!_playing) return;
        Player.Pause();
        _playing = false;
        PlayButton.Content = "Play";
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            PausePlayback();
            return;
        }
        if (Duration > 0 && Position >= Duration - 0.05) SeekTo(0);
        Player.Play();
        _playing = true;
        PlayButton.Content = "Pause";
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e) { PausePlayback(); SeekTo(Position - FrameStep); }
    private void NextFrame_Click(object sender, RoutedEventArgs e) { PausePlayback(); SeekTo(Position + FrameStep); }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) { PreviousFrame_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right) { NextFrame_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { Play_Click(sender, e); e.Handled = true; }
    }

    private void FindKill_Click(object sender, RoutedEventArgs e) =>
        _ = FindMomentAsync("--find-kill", "Looking for the swag lines…",
            "Jumped to where the swag lines are most visible. Use the frame buttons to fine-tune, then Save screenshot.",
            "Couldn't find swag lines here (kills on bots don't have them). Pick the moment yourself.");

    private void FindDamage_Click(object sender, RoutedEventArgs e) =>
        _ = FindMomentAsync("--find-damage", "Looking for the damage number…",
            "Jumped to where the damage number is clearest. Use the frame buttons to fine-tune, then Save screenshot.",
            "Couldn't find a damage number here. Pick the moment yourself.");

    private async Task FindMomentAsync(string command, string busyText, string foundText, string missingText)
    {
        if (_busy) return;
        PausePlayback();
        // Short clips are searched whole; for long recordings, search a minute around the current spot.
        double from = 0.0, to = Duration;
        if (to <= 0.0) to = 600.0;
        if (to > 300.0)
        {
            from = Math.Max(0.0, Position - 30.0);
            to = from + 60.0;
        }
        SetBusy(true, busyText);
        string? output = await RunEngineAsync(command, _clipPath,
            from.ToString("0.###", CultureInfo.InvariantCulture), to.ToString("0.###", CultureInfo.InvariantCulture));
        SetBusy(false, "");
        string? value = output is null ? null : ReadField(output, "KILL|");
        if (value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double moment))
        {
            SeekTo(moment);
            StatusText.Text = foundText;
        }
        else
        {
            StatusText.Text = missingText;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PausePlayback();
        Directory.CreateDirectory(_screenshotsFolder);
        TimeSpan time = TimeSpan.FromSeconds(Position);
        string name = $"{Path.GetFileNameWithoutExtension(_clipPath)}_{(int)time.TotalMinutes}m{time.Seconds:00}s{time.Milliseconds:000}.png";
        string output = Path.Combine(_screenshotsFolder, name);
        SetBusy(true, "Saving screenshot…");
        string? result = await RunEngineAsync("--screenshot", _clipPath,
            Position.ToString("0.###", CultureInfo.InvariantCulture), output);
        SetBusy(false, "");
        if (result is not null && result.Contains("SHOT|saved", StringComparison.OrdinalIgnoreCase) && File.Exists(output))
        {
            LastSavedPath = output;
            StatusText.Text = $"Saved {name}";
        }
        else
        {
            StatusText.Text = "Couldn't save the screenshot. Try a slightly different moment.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_screenshotsFolder);
        if (LastSavedPath is not null && File.Exists(LastSavedPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastSavedPath}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(_screenshotsFolder) { UseShellExecute = true });
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        FindKillButton.IsEnabled = !busy;
        FindDamageButton.IsEnabled = !busy;
        if (busy) StatusText.Text = message;
        Cursor = busy ? Cursors.Wait : null;
    }

    private static string? ReadField(string output, string prefix)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..];
        }
        return null;
    }

    private async Task<string?> RunEngineAsync(params string[] arguments)
    {
        if (!File.Exists(_enginePath)) return null;
        try
        {
            var start = new ProcessStartInfo(_enginePath)
            {
                WorkingDirectory = Path.GetDirectoryName(_enginePath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process? process = Process.Start(start);
            if (process is null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            return await output;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Player.Stop();
        Player.Close();
    }
}
