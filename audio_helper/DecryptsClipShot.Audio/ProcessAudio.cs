using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DecryptsClipShot.Audio;

// Records the sound of one app (and its child processes) instead of
// everything playing on the PC, using Windows process loopback capture.
// Needs Windows 11 or Windows 10 build 20348+; IsSupported() checks.

internal delegate void ProcessAudioHandler(ReadOnlySpan<float> stereo, int frames, bool silent,
                                           bool discontinuity, long devicePosition, long qpcPosition);

internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int ChannelCount = 2;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint BufferFlagsDiscontinuity = 0x1;
    private const uint BufferFlagsSilent = 0x2;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly Thread _thread;
    private readonly ProcessAudioHandler _handler;
    private volatile bool _stopping;

    public int ProcessId { get; }

    public ProcessLoopbackCapture(int processId, ProcessAudioHandler handler)
    {
        ProcessId = processId;
        _handler = handler;
        _client = Activate(processId);
        var format = new WaveFormatEx
        {
            FormatTag = 1, // PCM
            Channels = ChannelCount,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * ChannelCount * 2,
            BlockAlign = ChannelCount * 2,
            BitsPerSample = 16,
            Size = 0
        };
        _client.Initialize(0,
            StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
            1_000_000, 0, ref format, IntPtr.Zero);
        _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
        Guid captureId = IidAudioCaptureClient;
        _client.GetService(ref captureId, out object service);
        _capture = (IAudioCaptureClient)service;
        _thread = new Thread(Run) { IsBackground = true, Name = $"App audio {processId}" };
    }

    /// <summary>Process loopback exists on this Windows version.</summary>
    public static bool IsSupported(out string reason)
    {
        try
        {
            IAudioClient client = Activate(Environment.ProcessId);
            Marshal.ReleaseComObject(client);
            reason = "";
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    private void Run()
    {
        float[] stereo = new float[SampleRate * ChannelCount]; // 1 s, grown if needed
        short[] pcm = new short[SampleRate * ChannelCount];
        while (!_stopping)
        {
            _packetReady.WaitOne(100);
            try
            {
                _capture.GetNextPacketSize(out uint packet);
                while (packet > 0 && !_stopping)
                {
                    _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                       out ulong devicePosition, out ulong qpcPosition);
                    try
                    {
                        int count = (int)frames;
                        if (count > 0)
                        {
                            int samples = count * ChannelCount;
                            if (stereo.Length < samples)
                            {
                                stereo = new float[samples];
                                pcm = new short[samples];
                            }
                            bool silent = (flags & BufferFlagsSilent) != 0;
                            if (!silent)
                            {
                                Marshal.Copy(data, pcm, 0, samples);
                                for (int i = 0; i < samples; i++)
                                    stereo[i] = pcm[i] / 32768f;
                            }
                            // Some Windows builds leave the timestamp empty for
                            // this kind of capture; then "it just arrived" is the
                            // closest guess.
                            long qpc = qpcPosition > 0
                                ? (long)qpcPosition
                                : NowQpc() - (long)count * 10_000_000 / SampleRate;
                            _handler(stereo.AsSpan(0, count * ChannelCount), count, silent,
                                     (flags & BufferFlagsDiscontinuity) != 0, (long)devicePosition, qpc);
                        }
                    }
                    finally
                    {
                        _capture.ReleaseBuffer(frames);
                    }
                    _capture.GetNextPacketSize(out packet);
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidComObjectException)
            {
                // The app closed or its audio went away. The tracker notices
                // the process is gone and stops this capture.
                Thread.Sleep(100);
            }
        }
    }

    internal static long NowQpc()
    {
        long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
        return ticks / frequency * 10_000_000 + ticks % frequency * 10_000_000 / frequency;
    }

    public void Dispose()
    {
        _stopping = true;
        try { _client.Stop(); } catch { }
        if (_thread.IsAlive) _thread.Join(1000);
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _packetReady.Dispose();
    }

    private static IAudioClient Activate(int processId)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0 // include this process and its children
        };
        int size = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr blob = Marshal.AllocHGlobal(size);
        IntPtr variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            // PROPVARIANT { VT_BLOB, BLOB { cbSize, pBlobData } }
            Marshal.WriteInt16(variant, 0, 65);
            Marshal.WriteInt32(variant, 8, size);
            Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, blob);

            var completion = new ActivationCompletion();
            Guid iid = IidAudioClient;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, completion,
                                        out IActivateAudioInterfaceAsyncOperation operation);
            try
            {
                if (!completion.Done.Wait(5000))
                    throw new TimeoutException("Windows didn't answer the app-audio request.");
                operation.GetActivateResult(out int result, out object activated);
                Marshal.ThrowExceptionForHR(result);
                return (IAudioClient)activated;
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                        ref WaveFormatEx format, IntPtr audioSessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        void IsFormatSupported(int shareMode, ref WaveFormatEx format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}

/// <summary>A snapshot of running processes: id, parent and exe name.</summary>
internal sealed class ProcessTable
{
    public readonly record struct Entry(int Id, int ParentId, string Exe);
    public readonly List<Entry> Entries = new();

    public static ProcessTable Take()
    {
        var table = new ProcessTable();
        IntPtr snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return table;
            do table.Entries.Add(new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return table;
    }

    public bool IsAlive(int id) => Entries.Any(entry => entry.Id == id);

    public string ExeOf(int id) => Entries.FirstOrDefault(entry => entry.Id == id).Exe ?? "";

    /// <summary>
    /// The top process of an app (for example the main Discord.exe, whose
    /// children include the one playing voice chat), so capturing its whole
    /// process tree gets everything the app plays.
    /// </summary>
    public int? FindRoot(string exe)
    {
        var matches = Entries.Where(entry => entry.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        foreach (var entry in matches)
            if (!matches.Any(other => other.Id == entry.ParentId))
                return entry.Id;
        return matches[0].Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Follows the game: Fortnite whenever it's running, otherwise the app you
/// have open full screen (browsers, video players, Discord and ClipShot
/// itself are skipped). It keeps the last game while it's still running, so
/// alt-tabbing out doesn't cut the game sound.
/// </summary>
internal sealed class GameAudioTracker : IDisposable
{
    // Fortnite and revival projects (older builds): FortniteClient-Win64-Shipping.exe,
    // its _EAC/_BE/_EAC_EOS variants and 32-bit builds all start this way.
    private static bool IsFortnite(string exe) =>
        exe.StartsWith("FortniteClient-Win", StringComparison.OrdinalIgnoreCase) &&
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "LockApp.exe", "TextInputHost.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "Spotify.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe",
        "obs64.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe"
    };

    private readonly ProcessAudioHandler _handler;
    private readonly HashSet<string> _ownExes;
    private ProcessLoopbackCapture? _capture;

    public GameAudioTracker(ProcessAudioHandler handler)
    {
        _handler = handler;
        _ownExes = new HashSet<string>(
            Directory.EnumerateFiles(AppContext.BaseDirectory, "*.exe").Select(path => Path.GetFileName(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    public void Poll(ProcessTable table)
    {
        int? current = _capture?.ProcessId;
        if (current is int alive && !table.IsAlive(alive))
        {
            Console.Error.WriteLine($"Game sound: {table.ExeOf(alive)} closed.");
            Stop();
            current = null;
        }

        int? candidate = FindFortnite(table) ?? FullScreenApp(table);
        if (candidate is null || candidate == current) return;
        // Fortnite stays the target while it runs.
        if (current is int running && IsFortnite(table.ExeOf(running)))
            return;

        Stop();
        string exe = table.ExeOf(candidate.Value);
        try
        {
            _capture = new ProcessLoopbackCapture(candidate.Value, _handler);
            _capture.Start();
            Console.Error.WriteLine($"Game sound: recording {exe}.");
        }
        catch (Exception exception)
        {
            _capture = null;
            Console.Error.WriteLine($"Game sound: couldn't record {exe} ({exception.Message}).");
        }
    }

    // The game process itself, not a launcher stub: prefer the Shipping build
    // that isn't the _EAC/_BE wrapper when both are running.
    private static int? FindFortnite(ProcessTable table)
    {
        var matches = table.Entries.Where(entry => IsFortnite(entry.Exe)).ToList();
        if (matches.Count == 0) return null;
        var game = matches.FirstOrDefault(entry =>
            !entry.Exe.Contains("_EAC", StringComparison.OrdinalIgnoreCase) &&
            !entry.Exe.Contains("_BE", StringComparison.OrdinalIgnoreCase));
        return (game.Exe is null ? matches[0] : game).Id;
    }

    private int? FullScreenApp(ProcessTable table)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;
        string exe = table.ExeOf((int)processId);
        if (exe.Length == 0 || NotGames.Contains(exe) || _ownExes.Contains(exe)) return null;
        if (!GetWindowRect(window, out Rect rect)) return null;
        IntPtr monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return null;
        bool coversScreen = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
                            rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        return coversScreen ? (int)processId : null;
    }

    private void Stop()
    {
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}

internal interface IMixSource
{
    /// <summary>Adds up to <paramref name="frames"/> 48 kHz stereo frames into <paramref name="mix"/>.</summary>
    void MixInto(Span<float> mix, int frames);
}

/// <summary>
/// An extra app to record alongside the game (Discord, Spotify, ...). Mixed in
/// the same way as the microphone: buffered briefly and kept within 200 ms.
/// Follows the app if it restarts.
/// </summary>
internal sealed class AppAudioSource : IMixSource, IDisposable
{
    private const int Rate = 48000;
    private const int MaxBacklogFrames = Rate / 5;
    private const int TrimToFrames = Rate / 20;

    private readonly string _exe;
    private readonly float _volume;
    private readonly object _lock = new();
    private readonly float[] _ring = new float[Rate * 2 * 2];
    private int _readFrame, _countFrames;
    private ProcessLoopbackCapture? _capture;

    public AppAudioSource(string exe, float volume)
    {
        _exe = exe;
        _volume = volume;
    }

    private int RingFrames => _ring.Length / 2;

    public void Poll(ProcessTable table)
    {
        int? root = table.FindRoot(_exe);
        if (root == _capture?.ProcessId) return;
        _capture?.Dispose();
        _capture = null;
        if (root is null)
        {
            Console.Error.WriteLine($"Extra audio: {_exe} isn't running.");
            return;
        }
        try
        {
            _capture = new ProcessLoopbackCapture(root.Value, OnData);
            _capture.Start();
            Console.Error.WriteLine($"Extra audio: recording {_exe}.");
        }
        catch (Exception exception)
        {
            _capture = null;
            Console.Error.WriteLine($"Extra audio: couldn't record {_exe} ({exception.Message}).");
        }
    }

    private void OnData(ReadOnlySpan<float> stereo, int frames, bool silent, bool discontinuity,
                        long devicePosition, long qpcPosition)
    {
        lock (_lock)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float left = silent ? 0 : stereo[frame * 2] * _volume;
                float right = silent ? 0 : stereo[frame * 2 + 1] * _volume;
                if (_countFrames == RingFrames)
                {
                    _readFrame = (_readFrame + 1) % RingFrames;
                    _countFrames--;
                }
                int write = (_readFrame + _countFrames) % RingFrames;
                _ring[write * 2] = left;
                _ring[write * 2 + 1] = right;
                _countFrames++;
            }
            if (_countFrames > MaxBacklogFrames)
            {
                int drop = _countFrames - TrimToFrames;
                _readFrame = (_readFrame + drop) % RingFrames;
                _countFrames -= drop;
            }
        }
    }

    public void MixInto(Span<float> mix, int frames)
    {
        lock (_lock)
        {
            int available = Math.Min(frames, _countFrames);
            for (int frame = 0; frame < available; frame++)
            {
                int read = (_readFrame + frame) % RingFrames;
                mix[frame * 2] += _ring[read * 2];
                mix[frame * 2 + 1] += _ring[read * 2 + 1];
            }
            _readFrame = (_readFrame + available) % RingFrames;
            _countFrames -= available;
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}
