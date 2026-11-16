using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DecryptsClipShot.Audio;

internal static class Program
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int CaptureBufferMilliseconds = 40;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("--list", StringComparison.OrdinalIgnoreCase))
            return ListDevices();

        string mode = Argument(args, "--mode") ?? "";
        string deviceName = Argument(args, "--device") ?? "default";
        string pipeName = Argument(args, "--pipe") ?? "";
        if (!mode.Equals("loopback", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine(
                "Usage: ClipShotAudio.exe --mode loopback --device default|name --pipe name");
            return 2;
        }

        try { return CaptureLoopback(deviceName, pipeName); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Game-audio capture failed: {exception.Message}");
            return 3;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        for (int index = 0; index + 1 < args.Length; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private static int ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice? defaultOutput = TryDefault(enumerator, DataFlow.Render);
        using MMDevice? defaultMicrophone = TryDefault(enumerator, DataFlow.Capture);
        string[] outputs = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
        string[] microphones = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            defaultOutput = defaultOutput?.FriendlyName ?? "",
            defaultMicrophone = defaultMicrophone?.FriendlyName ?? "",
            outputs,
            microphones
        }));
        return 0;
    }

    private static MMDevice? TryDefault(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    private static int CaptureLoopback(string requestedDevice, string pipeName)
    {
        int pipeBufferBytes = OutputSampleRate * OutputChannels * sizeof(short);
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            pipeBufferBytes,
            pipeBufferBytes);
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = ResolveDevice(enumerator, requestedDevice);
        var requestedFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            OutputSampleRate,
            OutputChannels);
        using var capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithPollingSync()
            .WithBufferLength(CaptureBufferMilliseconds)
            .WithFormat(requestedFormat)
            .Build();
        using var writer = new DirectPcmPipeWriter(pipe, capture.WaveFormat);

        capture.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
            writer.Write(
                data,
                (flags & AudioClientBufferFlags.Silent) != 0,
                (flags & AudioClientBufferFlags.DataDiscontinuity) != 0,
                devicePosition,
                qpcPosition);

        Console.Error.WriteLine(
            $"Waiting for FFmpeg on audio pipe '{pipeName}' (loopback: {device.FriendlyName}).");
        pipe.WaitForConnection();

        // WASAPI loopback delivers no packets at all while nothing is playing
        // on the device. That left holes in the audio timeline and, during long
        // quiet periods, no audio_*.mka files at all. Playing an inaudible
        // silent stream on the same device keeps loopback packets flowing (the
        // same technique OBS uses). If it cannot start, the writer's stall
        // filler below still keeps the stream continuous.
        IDisposable? keepAlive = SilentKeepAlive.TryStart(device);

        capture.StartRecording();
        writer.Start();
        Console.Error.WriteLine(
            $"Capturing direct Windows game audio from '{device.FriendlyName}': " +
            $"{capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} channel(s), " +
            $"{capture.WaveFormat.BitsPerSample}-bit {capture.WaveFormat.Encoding}. " +
            "No application-level audio queue is active.");

        try
        {
            while (pipe.IsConnected && !writer.HasFailed)
            {
                writer.FillStall();
                Thread.Sleep(20);
            }
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
            try { keepAlive?.Dispose(); } catch { }
        }

        if (writer.HasFailed && !string.IsNullOrWhiteSpace(writer.FailureMessage))
            Console.Error.WriteLine($"Game-audio pipe closed: {writer.FailureMessage}");
        return 0;
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            requested.StartsWith("Default Windows", StringComparison.OrdinalIgnoreCase))
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        MMDevice[] devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToArray();
        MMDevice? selected = devices.FirstOrDefault(
                device => device.ID.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.FriendlyName.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.FriendlyName.Contains(requested, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;

        MMDevice fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.Error.WriteLine(
            $"Windows could not find audio device '{requested}'. Using current default '{fallback.FriendlyName}'.");
        return fallback;
    }
}

internal static class SilentKeepAlive
{
    public static IDisposable? TryStart(MMDevice device)
    {
        try
        {
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(new SilenceProvider(device.AudioClient.MixFormat));
            output.Play();
            Console.Error.WriteLine("Silent keep-alive stream active on the loopback device.");
            return output;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Silent keep-alive stream unavailable ({exception.Message}); silence gaps will be filled from the clock instead.");
            return null;
        }
    }
}

internal sealed class DirectPcmPipeWriter : IDisposable
{
    private const long QpcUnitsPerSecond = 10_000_000;
    private const int FramesPerSecond = 48000;
    private const int BytesPerFrame = 4; // s16 stereo
    // A real gap between packets larger than this is filled with silence; a
    // packet that overlaps audio already written is trimmed. Normal packet
    // timing jitter is far below this.
    private const long GapThreshold = 200_000;        // 20 ms
    // If no packet arrives for this long, write silence up to (now - margin)
    // so the stream never stops advancing with real time.
    private const long StallThreshold = 1_500_000;    // 150 ms
    private const long StallMargin = 500_000;         // 50 ms
    private const long MaxFill = 30 * QpcUnitsPerSecond;
    private static readonly byte[] SilenceBlock = new byte[FramesPerSecond / 10 * BytesPerFrame];

    private long coveredUntilQpc = -1;
    private long filledFrames;
    private long trimmedFrames;

    private readonly NamedPipeServerStream pipe;
    private readonly WaveFormat sourceFormat;
    private readonly object writeLock = new();
    private long firstDevicePosition = -1;
    private long firstQpcPosition = -1;
    private long expectedDevicePosition = -1;
    private long nextDiagnosticQpc = -1;
    private long discontinuityCount;
    private volatile bool failed;
    private string? failureMessage;

    public DirectPcmPipeWriter(NamedPipeServerStream pipe, WaveFormat sourceFormat)
    {
        this.pipe = pipe;
        this.sourceFormat = sourceFormat;
        if (sourceFormat.SampleRate != 48000 ||
            sourceFormat.Channels != 2 ||
            sourceFormat.BitsPerSample != 32)
        {
            throw new NotSupportedException(
                $"Windows returned {sourceFormat.SampleRate} Hz, {sourceFormat.Channels} channel(s), " +
                $"{sourceFormat.BitsPerSample}-bit audio instead of the requested 48 kHz float stereo format.");
        }
    }

    public bool HasFailed => failed;
    public string? FailureMessage => failureMessage;

    private static long NowQpc()
    {
        long ticks = Stopwatch.GetTimestamp();
        long frequency = Stopwatch.Frequency;
        return ticks / frequency * QpcUnitsPerSecond +
               ticks % frequency * QpcUnitsPerSecond / frequency;
    }

    private static long FramesFor(long qpcUnits) => qpcUnits * FramesPerSecond / QpcUnitsPerSecond;
    private static long QpcFor(long frames) => frames * QpcUnitsPerSecond / FramesPerSecond;

    public void Start()
    {
        lock (writeLock) coveredUntilQpc = NowQpc();
    }

    // Called every ~20 ms by the capture loop.
    public void FillStall()
    {
        lock (writeLock)
        {
            if (failed || coveredUntilQpc < 0) return;
            long now = NowQpc();
            if (now - coveredUntilQpc < StallThreshold) return;
            long target = now - StallMargin;
            WriteSilence(FramesFor(Math.Min(target - coveredUntilQpc, MaxFill)));
            coveredUntilQpc = target;
        }
    }

    private void WriteSilence(long frames)
    {
        if (frames <= 0) return;
        filledFrames += frames;
        long bytes = frames * BytesPerFrame;
        try
        {
            while (bytes > 0)
            {
                int chunk = (int)Math.Min(bytes, SilenceBlock.Length);
                pipe.Write(SilenceBlock, 0, chunk);
                bytes -= chunk;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            failureMessage = exception.Message;
            failed = true;
        }
    }

    public void Write(
        ReadOnlySpan<byte> data,
        bool silent,
        bool discontinuity,
        long devicePosition,
        long qpcPosition)
    {
        if (failed || data.IsEmpty) return;

        lock (writeLock)
        {
            if (failed) return;
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(data);
            int frames = samples.Length / sourceFormat.Channels;
            int packetFrames = frames;
            if (qpcPosition > 0 && coveredUntilQpc >= 0)
            {
                long gap = qpcPosition - coveredUntilQpc;
                if (gap > GapThreshold)
                {
                    WriteSilence(FramesFor(Math.Min(gap, MaxFill)));
                    if (failed) return;
                }
                else if (gap < -GapThreshold)
                {
                    // Part of this packet's time span was already covered by
                    // stall silence; keep only the part after it.
                    int skip = (int)Math.Min(frames, FramesFor(-gap));
                    trimmedFrames += skip;
                    samples = samples[(skip * sourceFormat.Channels)..];
                    frames -= skip;
                }
            }
            long packetEnd = qpcPosition > 0
                ? qpcPosition + QpcFor(packetFrames)
                : coveredUntilQpc + QpcFor(frames);
            if (frames <= 0)
            {
                coveredUntilQpc = Math.Max(coveredUntilQpc, packetEnd);
                return;
            }
            int sampleCount = frames * 2;
            int byteCount = sampleCount * sizeof(short);
            byte[] pcm = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                if (silent)
                {
                    Array.Clear(pcm, 0, byteCount);
                }
                else
                {
                    for (int index = 0; index < sampleCount; index++)
                    {
                        short value = (short)Math.Round(
                            Math.Clamp(samples[index], -1.0f, 1.0f) * short.MaxValue);
                        pcm[index * 2] = (byte)(value & 0xff);
                        pcm[index * 2 + 1] = (byte)((value >> 8) & 0xff);
                    }
                }

                pipe.Write(pcm, 0, byteCount);
                coveredUntilQpc = Math.Max(coveredUntilQpc, packetEnd);
                TrackClock(packetFrames, discontinuity, devicePosition, qpcPosition);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                failureMessage = exception.Message;
                failed = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pcm);
            }
        }
    }

    private void TrackClock(
        int frames,
        bool discontinuity,
        long devicePosition,
        long qpcPosition)
    {
        if (firstDevicePosition < 0 && devicePosition >= 0 && qpcPosition > 0)
        {
            firstDevicePosition = devicePosition;
            firstQpcPosition = qpcPosition;
            nextDiagnosticQpc = qpcPosition + 60 * QpcUnitsPerSecond;
        }

        if (discontinuity ||
            (expectedDevicePosition >= 0 && devicePosition != expectedDevicePosition))
            discontinuityCount++;
        expectedDevicePosition = devicePosition + frames;

        if (firstQpcPosition < 0 || qpcPosition < nextDiagnosticQpc) return;
        double elapsedSeconds = (qpcPosition - firstQpcPosition) / (double)QpcUnitsPerSecond;
        double measuredRate = elapsedSeconds > 0
            ? (devicePosition - firstDevicePosition) / elapsedSeconds
            : 0;
        Console.Error.WriteLine(
            $"Audio clock: {measuredRate:F2} frames/sec, " +
            $"WASAPI discontinuities {discontinuityCount}, " +
            $"silence filled {filledFrames * 1000 / FramesPerSecond} ms, " +
            $"overlap trimmed {trimmedFrames * 1000 / FramesPerSecond} ms.");
        nextDiagnosticQpc += 60 * QpcUnitsPerSecond;
    }

    public void Dispose()
    {
        // The pipe belongs to the capture routine and is disposed there.
    }
}
