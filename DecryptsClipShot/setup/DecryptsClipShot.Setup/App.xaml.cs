using System.Threading;
using System.Windows;

namespace DecryptsClipShot.Setup;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\DecryptsClipShot.Setup", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Decrypt's ClipShot Setup is already running.", "Decrypt's ClipShot Setup",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
