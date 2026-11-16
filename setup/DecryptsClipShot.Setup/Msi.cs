using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DecryptsClipShot.Setup;

/// <summary>Windows Installer (msi.dll) interop used by the setup wizard.</summary>
internal static class Msi
{
    public const uint Success = 0;
    public const uint UserExit = 1602;
    public const uint InstallFailure = 1603;
    public const uint AnotherInstallRunning = 1618;
    public const uint RebootInitiated = 1641;
    public const uint RebootRequired = 3010;
    public const uint NoMoreItems = 259;

    private const int UiLevelNone = 2;
    private const uint LogVerbose = 0x1000;
    private const uint LogAll = 0x3FFF;
    private const uint LogFlushEachLine = 0x2;
    private const int InstallStateAbsent = 2;
    private const int InstallLevelDefault = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public delegate int UiHandler(IntPtr context, uint messageType,
                                  [MarshalAs(UnmanagedType.LPWStr)] string? message);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiSetInternalUI(int uiLevel, IntPtr window);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSetExternalUIW")]
    private static extern IntPtr MsiSetExternalUI(UiHandler? handler, uint messageFilter, IntPtr context);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnableLogW")]
    private static extern uint MsiEnableLog(uint logMode, string? logFile, uint attributes);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiInstallProductW")]
    private static extern uint MsiInstallProduct(string packagePath, string commandLine);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiConfigureProductExW")]
    private static extern uint MsiConfigureProductEx(string product, int installLevel,
                                                     int installState, string commandLine);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved,
                                                      uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string product, string property,
                                                 StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field,
                                                  StringBuilder value, ref uint length);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);

    /// <summary>Reads one value from a package's Property table.</summary>
    public static string? ReadPackageProperty(string msiPath, string property)
    {
        IntPtr database = IntPtr.Zero, view = IntPtr.Zero, record = IntPtr.Zero;
        try
        {
            // persist = MSIDBOPEN_READONLY (0)
            if (MsiOpenDatabase(msiPath, IntPtr.Zero, out database) != Success) return null;
            if (MsiDatabaseOpenView(database,
                    $"SELECT `Value` FROM `Property` WHERE `Property`='{property}'", out view) != Success)
                return null;
            if (MsiViewExecute(view, IntPtr.Zero) != Success) return null;
            if (MsiViewFetch(view, out record) != Success) return null;
            var value = new StringBuilder(256);
            uint length = (uint)value.Capacity;
            return MsiRecordGetString(record, 1, value, ref length) == Success ? value.ToString() : null;
        }
        finally
        {
            if (record != IntPtr.Zero) MsiCloseHandle(record);
            if (view != IntPtr.Zero) MsiCloseHandle(view);
            if (database != IntPtr.Zero) MsiCloseHandle(database);
        }
    }

    /// <summary>Finds an installed product sharing the package's UpgradeCode.</summary>
    public static (string ProductCode, string Version)? FindInstalled(string upgradeCode)
    {
        var code = new StringBuilder(39);
        if (MsiEnumRelatedProducts(upgradeCode, 0, 0, code) != Success) return null;
        var version = new StringBuilder(64);
        uint length = (uint)version.Capacity;
        string installedVersion = MsiGetProductInfo(code.ToString(), "VersionString", version, ref length) == Success
            ? version.ToString()
            : "";
        return (code.ToString(), installedVersion);
    }

    public static uint Install(string msiPath, string commandLine, ProgressReporter reporter, string logPath) =>
        Run(reporter, logPath, () => MsiInstallProduct(msiPath, commandLine));

    public static uint Uninstall(string productCode, ProgressReporter reporter, string logPath) =>
        Run(reporter, logPath, () => MsiConfigureProductEx(productCode, InstallLevelDefault,
            InstallStateAbsent, "REBOOT=ReallySuppress"));

    private static readonly object RunLock = new();

    private static uint Run(ProgressReporter reporter, string logPath, Func<uint> operation)
    {
        lock (RunLock)
        {
            MsiSetInternalUI(UiLevelNone, IntPtr.Zero);
            MsiEnableLog(LogAll | LogVerbose, logPath, LogFlushEachLine);
            // The delegate must stay alive for as long as msi.dll may call it.
            UiHandler handler = reporter.Handle;
            MsiSetExternalUI(handler, ProgressReporter.MessageFilter, IntPtr.Zero);
            try
            {
                return operation();
            }
            finally
            {
                MsiSetExternalUI(null, 0, IntPtr.Zero);
                MsiEnableLog(0, null, 0);
                GC.KeepAlive(handler);
            }
        }
    }

    public static string Describe(uint result) => result switch
    {
        Success or RebootRequired or RebootInitiated => "Completed.",
        UserExit => "Setup was cancelled. No changes were made.",
        AnotherInstallRunning => "Another installation is already in progress. Finish it, then run this setup again.",
        1638 => "Another version of Decrypt's ClipShot is already installed. Uninstall it from Windows Settings, then run this setup again.",
        1632 or 1631 => "Windows Installer could not write to the temporary folder. Free some disk space and try again.",
        112 => "There is not enough free disk space.",
        _ => $"Windows Installer reported error {result}."
    };
}

/// <summary>
/// Turns Windows Installer's external-UI messages into a percentage, a status
/// line and answers for any question the package asks. Follows Microsoft's
/// "Parsing Windows Installer Messages" algorithm.
/// </summary>
internal sealed class ProgressReporter
{
    private const uint KindMask = 0xFF000000;
    private const uint FatalExit = 0x00000000;
    private const uint Error = 0x01000000;
    private const uint Warning = 0x02000000;
    private const uint User = 0x03000000;
    private const uint FilesInUse = 0x05000000;
    private const uint ResolveSource = 0x06000000;
    private const uint OutOfDiskSpace = 0x07000000;
    private const uint ActionStart = 0x08000000;
    private const uint ActionData = 0x09000000;
    private const uint Progress = 0x0A000000;
    private const uint RmFilesInUse = 0x19000000;

    private static uint LogFlag(uint kind) => 1u << (int)(kind >> 24);

    public static readonly uint MessageFilter =
        LogFlag(FatalExit) | LogFlag(Error) | LogFlag(Warning) | LogFlag(User) |
        LogFlag(FilesInUse) | LogFlag(ResolveSource) | LogFlag(OutOfDiskSpace) |
        LogFlag(ActionStart) | LogFlag(ActionData) | LogFlag(Progress) | 0x02000000u /* RMFILESINUSE */;

    private const int IdOk = 1, IdCancel = 2, IdIgnore = 5;

    private readonly Action<double, string?> _update;
    private readonly Func<string, uint, int> _ask;
    private volatile bool _cancelRequested;

    private long _total;
    private long _done;
    private bool _forward = true;
    private bool _executing;
    private bool _actionDataEnabled;
    private long _perActionData;

    /// <param name="update">percent (0-100) and an optional status line</param>
    /// <param name="ask">shows a message box; returns IDOK/IDCANCEL/...</param>
    public ProgressReporter(Action<double, string?> update, Func<string, uint, int> ask)
    {
        _update = update;
        _ask = ask;
    }

    public void RequestCancel() => _cancelRequested = true;
    public bool CancelRequested => _cancelRequested;

    public int Handle(IntPtr context, uint messageType, string? message)
    {
        uint kind = messageType & KindMask;
        switch (kind)
        {
            case Progress:
                ParseProgress(message);
                return _cancelRequested ? IdCancel : IdOk;

            case ActionStart:
                _actionDataEnabled = false;
                _update(Percent(), FriendlyAction(message));
                return _cancelRequested ? IdCancel : IdOk;

            case ActionData:
                if (_actionDataEnabled && _total > 0)
                    _done += _forward ? _perActionData : -_perActionData;
                _update(Percent(), null);
                return _cancelRequested ? IdCancel : IdOk;

            case FatalExit:
            case Error:
            case Warning:
            case User:
            case OutOfDiskSpace:
                if (string.IsNullOrWhiteSpace(message)) return 0;
                // The low bits carry the MB_* buttons/icon the package wants.
                return _ask(message.Trim(), messageType & 0x00FFFFFF);

            case FilesInUse:
                // Setup closes ClipShot before installing; carry on if something
                // still holds a file (Windows finishes the update after restart).
                return IdIgnore;

            case RmFilesInUse:
                // IDOK = let Restart Manager close the applications.
                return IdOk;

            default:
                return 0;
        }
    }

    private void ParseProgress(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        long[] fields = new long[4];
        // Format: "1: f1 2: f2 3: f3 4: f4 "
        string[] parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!parts[i].EndsWith(':')) { i--; continue; }
            if (int.TryParse(parts[i].TrimEnd(':'), out int index) && index is >= 1 and <= 4 &&
                long.TryParse(parts[i + 1], out long value))
                fields[index - 1] = value;
        }

        switch (fields[0])
        {
            case 0: // reset: f2 = total ticks, f3 = direction, f4 = script in progress
                _total = fields[1];
                _forward = fields[2] == 0;
                _done = _forward ? 0 : _total;
                _actionDataEnabled = false;
                _executing = fields[3] == 1;
                break;
            case 1: // ticks to add per ActionData message
                _perActionData = fields[1];
                _actionDataEnabled = fields[2] != 0;
                break;
            case 2: // explicit increment
                if (_total != 0) _done += _forward ? fields[1] : -fields[1];
                break;
            case 3: // add to total
                _total += fields[1];
                break;
        }
        _update(Percent(), null);
    }

    // Windows Installer reports two phases: building the script, then running
    // it. Show them as 0-10% and 10-100% so the bar never jumps backwards.
    private double Percent()
    {
        double phase = _total > 0 ? Math.Clamp((double)_done / _total, 0, 1) : 0;
        return _executing ? 10 + phase * 90 : phase * 10;
    }

    // "Action 12:01:02: InstallFiles. Copying new files" -> "Copying new files"
    private static string? FriendlyAction(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        int dot = message.IndexOf(". ", StringComparison.Ordinal);
        string text = dot >= 0 ? message[(dot + 2)..] : message;
        text = text.Trim().TrimEnd('.');
        return text.Length == 0 ? null : text;
    }
}
