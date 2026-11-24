using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace DecryptsClipShot;

/// <summary>
/// "Update available" popup. Returns DialogResult = true with DownloadedSetup
/// set when the new setup has been downloaded and should be run.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _download;

    public string? DownloadedSetup { get; private set; }
    public bool SkipThisVersion { get; private set; }

    public UpdateWindow(UpdateInfo update, bool recording)
    {
        InitializeComponent();
        _update = update;
        VersionText.Text = $"ClipShot {update.Version} is ready (you have {UpdateChecker.CurrentVersion})";
        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes) ? "Bug fixes and improvements." : update.Notes;
        RecordingWarning.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        SkipButton.Visibility = Visibility.Collapsed;
        LaterButton.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(value =>
        {
            DownloadProgress.Value = value;
            ProgressText.Text = $"Downloading… {value:P0}";
        });
        try
        {
            DownloadedSetup = await UpdateChecker.DownloadAsync(_update, progress, _download.Token);
            ProgressText.Text = "Starting the update…";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
        }
        catch (Exception exception)
        {
            ProgressText.Text = $"Download failed: {exception.Message}. Check your internet connection and try again.";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Try again";
            LaterButton.Content = "Later";
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // While downloading, "Cancel" stops the download; Update_Click then closes the window.
        if (_download is { IsCancellationRequested: false } && !UpdateButton.IsEnabled)
        {
            _download.Cancel();
            return;
        }
        DialogResult = false;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        SkipThisVersion = true;
        DialogResult = false;
    }
}
