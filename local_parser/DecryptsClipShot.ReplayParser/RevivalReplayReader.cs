using System.Diagnostics;
using System.Runtime.InteropServices;
using FortniteReplayReader;
using Unreal.Core;
using Unreal.Core.Models.Enums;
using Unreal.Encryption;

namespace DecryptsClipShot.ReplayParser;

/// <summary>
/// The normal replay reader, plus support for replays from older Fortnite
/// builds (revival projects). Those compress replay data with Oodle codecs
/// (such as LZHLW) that the built-in C# decoder can't read. When that happens,
/// the data is handed to the Oodle library that ships with the player's own
/// Fortnite build (oo2core_*_win64.dll next to the game). ClipShot doesn't
/// include that file; it only uses the copy already on the PC.
/// </summary>
internal sealed class RevivalReplayReader : ReplayReader
{
    public RevivalReplayReader(ParseMode parseMode) : base(null, parseMode)
    {
    }

    protected override FArchive Decompress(FArchive archive)
    {
        if (!Replay.Info.IsCompressed)
        {
            return archive;
        }

        int decompressedSize = archive.ReadInt32();
        int compressedSize = archive.ReadInt32();
        byte[] compressed = archive.ReadBytes(compressedSize).ToArray();

        ReadOnlyMemory<byte> output;
        try
        {
            output = Oodle.DecompressReplayData(compressed, decompressedSize);
        }
        catch (Exception builtInError)
        {
            output = GameOodle.Decompress(compressed, decompressedSize, builtInError);
        }

        return new Unreal.Core.BinaryReader(output)
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion
        };
    }
}

/// <summary>Oodle from the player's Fortnite install.</summary>
internal static class GameOodle
{
    // long OodleLZ_Decompress(const void* comp, SINTa compLen, void* raw, SINTa rawLen,
    //     fuzzSafe, checkCRC, verbosity, decBufBase, decBufSize, fpCallback,
    //     callbackUserData, decoderMemory, decoderMemorySize, threadPhase)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleLZDecompress(
        byte[] compressed, long compressedSize, byte[] output, long outputSize,
        int fuzzSafe, int checkCrc, int verbosity, IntPtr decodeBufferBase, long decodeBufferSize,
        IntPtr callback, IntPtr callbackUserData, IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

    private static readonly object Gate = new();
    private static OodleLZDecompress? _decompress;
    private static string? _loadedFrom;

    // Remembered so replays can still be read after the game has closed.
    private static string RememberedPathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Decrypts ClipShot", "fortnite_oodle_path.txt");

    public static ReadOnlyMemory<byte> Decompress(byte[] compressed, int decompressedSize, Exception builtInError)
    {
        OodleLZDecompress decompress = Load()
            ?? throw new InvalidOperationException(
                $"{builtInError.Message}. This replay is from an older Fortnite build. ClipShot reads it using " +
                "the Oodle file that comes with that build (oo2core_*_win64.dll), but couldn't find it: " +
                "keep the game open until ClipShot finishes checking the match.", builtInError);

        byte[] output = new byte[decompressedSize];
        // Fuzz-safe off: Oodle refuses older codecs like LZHLW in fuzz-safe mode.
        // Thread phase 3 = decode everything in one call (what Unreal does).
        long written = decompress(compressed, compressed.Length, output, output.Length,
                                  0, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
        if (written != decompressedSize)
            throw new InvalidOperationException(
                $"{builtInError.Message}. The game's own Oodle ({Path.GetFileName(_loadedFrom)}) couldn't read it either.",
                builtInError);
        return output;
    }

    private static OodleLZDecompress? Load()
    {
        lock (Gate)
        {
            if (_decompress is not null) return _decompress;
            foreach (string path in Candidates())
            {
                try
                {
                    IntPtr library = NativeLibrary.Load(path);
                    if (!NativeLibrary.TryGetExport(library, "OodleLZ_Decompress", out IntPtr function)) continue;
                    _decompress = Marshal.GetDelegateForFunctionPointer<OodleLZDecompress>(function);
                    _loadedFrom = path;
                    Remember(path);
                    return _decompress;
                }
                catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
                {
                }
            }
            return null;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        // 1. Next to a running Fortnite (any build, revival or not).
        foreach (Process process in Process.GetProcesses())
        {
            string? folder = null;
            try
            {
                if (process.ProcessName.StartsWith("FortniteClient-Win", StringComparison.OrdinalIgnoreCase))
                    folder = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
            if (folder is null) continue;
            foreach (string dll in OodleIn(folder)) yield return dll;
        }

        // 2. Where it was found last time.
        string? remembered = null;
        try { if (File.Exists(RememberedPathFile)) remembered = File.ReadAllText(RememberedPathFile).Trim(); }
        catch (IOException) { }
        if (!string.IsNullOrEmpty(remembered) && File.Exists(remembered)) yield return remembered;
    }

    // Newest Oodle version first (oo2core_9 before oo2core_5).
    private static IEnumerable<string> OodleIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "oo2core_*_win64.dll")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Remember(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RememberedPathFile)!);
            File.WriteAllText(RememberedPathFile, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
