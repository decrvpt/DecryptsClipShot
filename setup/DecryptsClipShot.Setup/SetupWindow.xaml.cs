using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DecryptsClipShot.Setup;

/// <summary>
/// One-screen setup: most people just click Install. Folder and shortcut
/// choices are tucked behind "Install options". Running setup again on a
/// PC that already has ClipShot offers Update, Fix, or Remove.
/// </summary>
public partial class SetupWindow : Window
{
    private enum Mode { Install, Update, Maintenance, NewerInstalled }
    private enum Page { Home, Maintenance, License, Progress, Done }
    private enum Operation { Install, Update, Repair, Uninstall }

    private const string ProductName = "Decrypt's ClipShot";
    private const string RegistryPath = @"Software\Decrypt\ClipShot";
    private const string AppFolderName = "Decrypts ClipShot";
    private const string MainExe = "DecryptsClipShot.exe";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string DefaultFolder = Path.Combine(LocalAppData, "Programs", AppFolderName);
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "DecryptsClipShot-Setup.log");

    private string? _msiPath;
    private string? _tempFolder;
    private string _packageVersion = "";
    private string _packageProductCode = "";
    private (string ProductCode, string Version)? _installed;
    private string _previousFolder = "";

    private Mode _mode = Mode.Install;
    private Page _page = Page.Home;
    private Page _pageBeforeLicense = Page.Home;
    private bool _prepared;
    private bool _busy;
    private bool _succeeded;
    private bool _cancelled;
    private Operation _operation;
    private ProgressReporter? _reporter;

    public SetupWindow()
    {
        InitializeComponent();
    }

    // ===================== Startup =====================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadLicense();
        InstallPathBox.Text = DefaultFolder;
        ShowPage(Page.Home);

        string? error = await Task.Run(PreparePackage);
        if (error is not null)
        {
            PreparingText.Text = error;
            PreparingText.Foreground = (Brush)FindResource("AccentTextBrush");
            return;
        }

        _prepared = true;
        PreparingText.Visibility = Visibility.Collapsed;
        ConfigureMode();
    }

    /// <summary>Unpacks the MSI carried inside this setup and checks for an existing install.</summary>
    private string? PreparePackage()
    {
        try
        {
            using Stream? embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipShot.msi");
            if (embedded is not null)
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), "DecryptsClipShot-Setup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempFolder);
                _msiPath = Path.Combine(_tempFolder, "DecryptsClipShot.msi");
                using FileStream output = File.Create(_msiPath);
                embedded.CopyTo(output);
            }
            else
            {
                string beside = Path.Combine(AppContext.BaseDirectory, "DecryptsClipShot-Setup.msi");
                if (!File.Exists(beside))
                    return "This download looks incomplete. Please download the setup again.";
                _msiPath = beside;
            }

            _packageVersion = Msi.ReadPackageProperty(_msiPath, "ProductVersion") ?? "";
            _packageProductCode = Msi.ReadPackageProperty(_msiPath, "ProductCode") ?? "";
            string? upgradeCode = Msi.ReadPackageProperty(_msiPath, "UpgradeCode");
            if (string.IsNullOrEmpty(_packageVersion) || string.IsNullOrEmpty(upgradeCode))
                return "This download looks damaged. Please download the setup again.";

            _installed = Msi.FindInstalled(upgradeCode);
            return null;
        }
        catch (Exception exception)
        {
            return $"Setup couldn't start ({exception.Message}). Try restarting your PC and running it again.";
        }
    }

    private void ConfigureMode()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        _previousFolder = (key?.GetValue("InstallFolder") as string ?? "").TrimEnd('\\');

        if (_installed is not null)
        {
            // Keep what the player chose last time.
            DesktopShortcutCheck.IsChecked = key?.GetValue("DesktopShortcut") is not null;
            StartWithWindowsCheck.IsChecked = key?.GetValue("StartWithWindows") is not null;
            if (_previousFolder.Length > 0) InstallPathBox.Text = _previousFolder;

            int comparison = CompareVersions(_installed.Value.Version, _packageVersion);
            _mode = comparison < 0 ? Mode.Update : comparison == 0 ? Mode.Maintenance : Mode.NewerInstalled;
        }

        switch (_mode)
        {
            case Mode.Install:
                InstallButton.Content = "Install";
                HomeBody.Text = "Save your best Fortnite moments automatically.";
                break;

            case Mode.Update:
                InstallButton.Content = "Update";
                HomeTitle.Text = "Update available";
                HomeBody.Text = $"Version {_packageVersion} is ready to install.\nYour clips and settings stay safe.";
                break;

            case Mode.Maintenance:
                ShowPage(Page.Maintenance);
                break;

            case Mode.NewerInstalled:
                MaintenanceTitle.Text = "You already have a newer version";
                MaintenanceBody.Text = $"ClipShot {_installed!.Value.Version} is installed, which is newer than this setup " +
                                       $"({_packageVersion}). You don't need to do anything.";
                RepairButton.Visibility = Visibility.Collapsed;
                RepairHint.Visibility = Visibility.Collapsed;
                ShowPage(Page.Maintenance);
                break;
        }

        InstallButton.IsEnabled = true;
        InstallButton.Focus();
    }

    private static int CompareVersions(string left, string right) =>
        Version.TryParse(left, out Version? a) && Version.TryParse(right, out Version? b)
            ? a.CompareTo(b)
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

    private void LoadLicense()
    {
        try
        {
            using Stream? rtf = Assembly.GetExecutingAssembly().GetManifestResourceStream("License.rtf");
            if (rtf is null) return;
            var range = new TextRange(LicenseBox.Document.ContentStart, LicenseBox.Document.ContentEnd);
            range.Load(rtf, DataFormats.Rtf);
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE6, 0xDA, 0xDA)));
            range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        }
        catch { }
    }

    // ===================== Pages =====================

    private void ShowPage(Page page)
    {
        _page = page;
        HomePage.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
        MaintenancePage.Visibility = page == Page.Maintenance ? Visibility.Visible : Visibility.Collapsed;
        LicensePage.Visibility = page == Page.License ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == Page.Progress ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = page == Page.Done ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowLicense_Click(object sender, RoutedEventArgs e)
    {
        _pageBeforeLicense = _page;
        ShowPage(Page.License);
    }

    private void CloseLicense_Click(object sender, RoutedEventArgs e) => ShowPage(_pageBeforeLicense);

    private void ToggleOptions_Click(object sender, RoutedEventArgs e)
    {
        bool show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OptionsToggle.Content = show ? "Install options ▴" : "Install options ▾";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            RequestCancel();
            return;
        }
        if (_tempFolder is not null)
            try { Directory.Delete(_tempFolder, true); } catch { }
    }

    // ===================== Buttons =====================

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_prepared) return;
        if (!ValidateLocation(out string? problem))
        {
            // Only possible if the player opened Install options and typed a bad folder.
            OptionsPanel.Visibility = Visibility.Visible;
            OptionsToggle.Content = "Install options ▴";
            LocationError.Text = problem;
            LocationError.Visibility = Visibility.Visible;
            return;
        }
        await RunOperation(_mode == Mode.Update ? Operation.Update : Operation.Install);
    }

    private async void Repair_Click(object sender, RoutedEventArgs e) => await RunOperation(Operation.Repair);

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Ask("Remove Decrypt's ClipShot from this PC?\n\nYour saved clips will not be deleted.",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        await RunOperation(Operation.Uninstall);
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_reporter is null || _reporter.CancelRequested || _operation == Operation.Uninstall) return;
        if (Ask("Stop installing? Nothing will be changed on your PC.", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        _reporter.RequestCancel();
        ProgressStatus.Text = "Stopping…";
        CancelLink.IsEnabled = false;
    }

    private void DonePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_succeeded && _operation != Operation.Uninstall)
        {
            LaunchClipShot();
            Close();
        }
        else if (!_succeeded && !_cancelled)
        {
            // "Try again"
            ShowPage(_mode is Mode.Maintenance or Mode.NewerInstalled ? Page.Maintenance : Page.Home);
        }
        else
        {
            Close();
        }
    }

    private void DoneSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (!_succeeded && !_cancelled)
        {
            // Failure: offer the log for anyone helping the player.
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{LogPath}\"") { UseShellExecute = true }); }
            catch { }
            return;
        }
        Close();
    }

    // ===================== Install folder (Install options) =====================

    private string ChosenFolder()
    {
        string path = Environment.ExpandEnvironmentVariables(InstallPathBox.Text.Trim().Trim('"'));
        try { path = Path.GetFullPath(path); } catch { }
        return path.TrimEnd('\\', '/');
    }

    private bool ValidateLocation(out string? problem)
    {
        problem = null;
        string path = ChosenFolder();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            problem = "That folder doesn't look right. Click Change… to pick one.";
            return false;
        }
        string? root = Path.GetPathRoot(path);
        if (root is null || !Directory.Exists(root))
        {
            problem = "That drive isn't available. Click Change… to pick another folder.";
            return false;
        }
        if (string.Equals(path + "\\", root, StringComparison.OrdinalIgnoreCase))
        {
            problem = "Please pick a folder instead of a whole drive.";
            return false;
        }
        string[] adminOnly =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        if (adminOnly.Any(folder => folder.Length > 0 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
        {
            problem = "Windows protects that folder. Keep the default folder or pick one on another drive.";
            return false;
        }
        return true;
    }

    private void InstallPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        bool valid = ValidateLocation(out string? problem);
        LocationError.Text = problem ?? "";
        LocationError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string current = ChosenFolder();
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to install Decrypt's ClipShot",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Path.GetDirectoryName(current) is { } parent && Directory.Exists(parent) ? parent : LocalAppData
        };
        if (dialog.ShowDialog(this) != true) return;
        string chosen = dialog.FolderName.TrimEnd('\\');
        if (!chosen.EndsWith(AppFolderName, StringComparison.OrdinalIgnoreCase))
            chosen = Path.Combine(chosen, AppFolderName);
        InstallPathBox.Text = chosen;
    }

    // ===================== Doing the work =====================

    private string InstalledFolder() => _previousFolder.Length > 0 ? _previousFolder : DefaultFolder;

    private async Task RunOperation(Operation operation)
    {
        _operation = operation;
        string folder = operation is Operation.Install or Operation.Update ? ChosenFolder() : InstalledFolder();

        if (!await CloseRunningClipShot(folder)) return;

        _busy = true;
        _cancelled = false;
        ProgressTitle.Text = operation switch
        {
            Operation.Update => "Updating ClipShot…",
            Operation.Repair => "Fixing ClipShot…",
            Operation.Uninstall => "Removing ClipShot…",
            _ => "Installing ClipShot…"
        };
        ProgressStatus.Text = "Getting ready…";
        ProgressHint.Text = operation == Operation.Uninstall
            ? "Your saved clips are not touched."
            : "This usually takes less than a minute.";
        InstallProgress.Value = 0;
        CancelLink.Visibility = operation == Operation.Uninstall ? Visibility.Collapsed : Visibility.Visible;
        CancelLink.IsEnabled = true;
        ShowPage(Page.Progress);

        // Windows Installer's own step names are technical; show plain phrases instead.
        _reporter = new ProgressReporter(
            (percent, _) => Dispatcher.InvokeAsync(() =>
            {
                InstallProgress.Value = Math.Max(InstallProgress.Value, percent);
                if (_reporter is { CancelRequested: true }) return;
                ProgressStatus.Text = InstallProgress.Value switch
                {
                    < 10 => "Getting ready…",
                    < 90 => operation == Operation.Uninstall ? "Removing files…" : "Copying ClipShot files…",
                    _ => "Almost done…"
                };
            }),
            (message, flags) => Dispatcher.Invoke(() => NativeMessageBox(message, flags)));

        string options = $"DESKTOPSHORTCUT={(DesktopShortcutCheck.IsChecked == true ? 1 : 0)} " +
                         $"STARTUPSHORTCUT={(StartWithWindowsCheck.IsChecked == true ? 1 : 0)} " +
                         "REBOOT=ReallySuppress";
        string msi = _msiPath!;
        ProgressReporter reporter = _reporter;
        string? installedCode = _installed?.ProductCode;
        bool samePackage = string.Equals(installedCode, _packageProductCode, StringComparison.OrdinalIgnoreCase);

        uint result = await Task.Run(() => operation switch
        {
            Operation.Uninstall => Msi.Uninstall(installedCode!, reporter, LogPath),
            Operation.Repair when samePackage =>
                Msi.Install(msi, $"REINSTALL=ALL REINSTALLMODE=vomus {options}", reporter, LogPath),
            _ => Msi.Install(msi, $"INSTALLFOLDER=\"{folder}\" {options}", reporter, LogPath)
        });

        _busy = false;
        ShowResult(result, folder);
    }

    private void ShowResult(uint result, string folder)
    {
        _succeeded = result is Msi.Success or Msi.RebootRequired or Msi.RebootInitiated;
        _cancelled = result == Msi.UserExit;
        string restart = result is Msi.RebootRequired or Msi.RebootInitiated
            ? "\nRestart your PC when convenient to finish."
            : "";
        TipCard.Visibility = _succeeded && _operation is Operation.Install or Operation.Update
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_succeeded)
        {
            SetBadge("✓", "#123524", "#31D08B");
            switch (_operation)
            {
                case Operation.Uninstall:
                    RemoveLeftovers(folder);
                    DoneTitle.Text = "ClipShot was removed";
                    DoneBody.Text = "Your saved clips are still in your Videos folder,\nunder Decrypt's ClipShot." + restart;
                    DonePrimaryButton.Content = "Close";
                    DoneSecondaryLink.Visibility = Visibility.Collapsed;
                    break;
                case Operation.Repair:
                    DoneTitle.Text = "ClipShot is fixed";
                    DoneBody.Text = "Everything has been put back in place." + restart;
                    ShowOpenButtons();
                    break;
                case Operation.Update:
                    DoneTitle.Text = "You're up to date!";
                    DoneBody.Text = $"ClipShot {_packageVersion} is installed." + restart;
                    ShowOpenButtons();
                    break;
                default:
                    DoneTitle.Text = "All set!";
                    DoneBody.Text = "ClipShot is installed and ready to go." + restart;
                    ShowOpenButtons();
                    break;
            }
        }
        else if (_cancelled)
        {
            SetBadge("–", "#2A1C1C", "#B4A0A0");
            DoneTitle.Text = "Install stopped";
            DoneBody.Text = "Nothing was changed on your PC.";
            DonePrimaryButton.Content = "Close";
            DoneSecondaryLink.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetBadge("!", "#3A1414", "#EF5555");
            DoneTitle.Text = "That didn't work";
            DoneBody.Text = FriendlyError(result) + "\nNothing was left half-installed.";
            DonePrimaryButton.Content = "Try again";
            DoneSecondaryLink.Content = "Show details (for whoever is helping you)";
            DoneSecondaryLink.Visibility = Visibility.Visible;
        }
        ShowPage(Page.Done);
        DonePrimaryButton.Focus();
    }

    private void ShowOpenButtons()
    {
        bool canOpen = File.Exists(Path.Combine(_operation == Operation.Repair ? InstalledFolder() : ChosenFolder(), MainExe));
        DonePrimaryButton.Content = canOpen ? "Open ClipShot" : "Close";
        DoneSecondaryLink.Content = "Close";
        DoneSecondaryLink.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FriendlyError(uint result) => result switch
    {
        Msi.AnotherInstallRunning => "Another program is installing right now. Wait for it to finish, then click Try again.",
        112 => "Your drive is full. Free up some space, then click Try again.",
        1632 or 1631 => "Windows couldn't use its temporary folder. Restart your PC, then try again.",
        _ => "Something stopped the install. Restarting your PC and trying again usually fixes it."
    };

    private void SetBadge(string icon, string background, string foreground)
    {
        DoneIcon.Text = icon;
        DoneBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        DoneIcon.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }

    private static void RemoveLeftovers(string folder)
    {
        foreach (string disposable in new[] { "buffer", "work" })
        {
            try
            {
                string path = Path.Combine(folder, disposable);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }
        try
        {
            // Removed only when empty, so saved clips are never touched.
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch { }
    }

    private void LaunchClipShot()
    {
        string folder = _operation == Operation.Repair ? InstalledFolder() : ChosenFolder();
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(folder, MainExe))
            {
                UseShellExecute = true,
                WorkingDirectory = folder
            });
        }
        catch
        {
            Ask("ClipShot is installed. Open it from the Start menu or your desktop.", MessageBoxButton.OK);
        }
    }

    // ===================== Closing a running ClipShot =====================

    private static readonly string[] ClipShotProcesses =
        { "DecryptsClipShot", "TrickClip", "ClipShotAudio", "ClipShotReplayParser", "ffmpeg" };

    private static List<Process> FindRunningClipShot(string folder)
    {
        var running = new List<Process>();
        foreach (string name in ClipShotProcesses)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                bool inFolder = path is not null &&
                                path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
                // ffmpeg only counts if it runs from the ClipShot folder.
                if (inFolder || (name != "ffmpeg" && path is null) || name is "DecryptsClipShot" or "TrickClip")
                    running.Add(process);
                else
                    process.Dispose();
            }
        }
        return running;
    }

    private async Task<bool> CloseRunningClipShot(string folder)
    {
        List<Process> running = FindRunningClipShot(folder);
        if (running.Count == 0) return true;

        if (Ask("ClipShot is open right now. Setup needs to close it first.\n\nClose ClipShot and continue?",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            running.ForEach(process => process.Dispose());
            return false;
        }

        await Task.Run(() =>
        {
            try
            {
                using var stop = EventWaitHandle.OpenExisting(@"Local\DecryptsClipShot.Stop");
                stop.Set();
            }
            catch { }
            foreach (Process process in running)
                try { process.CloseMainWindow(); } catch { }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (Process process in running)
            {
                try
                {
                    int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (!process.WaitForExit(remaining)) process.Kill(entireProcessTree: true);
                }
                catch { }
                finally { process.Dispose(); }
            }
        });
        return true;
    }

    // ===================== Dialogs =====================

    private MessageBoxResult Ask(string message, MessageBoxButton buttons) =>
        MessageBox.Show(this, message, ProductName, buttons,
            buttons == MessageBoxButton.OK ? MessageBoxImage.Information : MessageBoxImage.Question);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    // Windows Installer passes the MB_* flags it expects an answer for.
    private int NativeMessageBox(string message, uint flags) =>
        MessageBoxW(new WindowInteropHelper(this).Handle, message, ProductName, flags);
}
