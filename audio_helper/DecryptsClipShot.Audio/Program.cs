using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DecryptsClipShot.Audio;

/// <summary>
/// ClipShot's audio recorder. Captures game sound (WASAPI loopback) and,
/// optionally, a microphone, mixes them, and streams one continuous
/// 48 kHz stereo 16-bit PCM signal to the engine's FFmpeg through a pipe.
///
/// Mixing here keeps the microphone completely out of the video recorder:
/// earlier versions mixed it live inside the video FFmpeg, which cost FPS.
/// </summary>
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
        string? micName = Argument(args, "--mic");
        float gameVolume = Percent(Argument(args, "--game-volume"));
        float micVolume = Percent(Argument(args, "--mic-volume"));
        if (!mode.Equals("loopback", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine(
                "Usage: ClipShotAudio.exe --mode loopback --device default|name --pipe name " +
                "[--game-volume 0-200] [--mic default|name] [--mic-volume 0-200]");
            return 2;
        }

        try { return Capture(deviceName, pipeName, micName, gameVolume, micVolume); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Audio capture failed: {exception.Message}");
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

    private static float Percent(string? value) =>
        int.TryParse(value, out int percent) ? Math.Clamp(percent, 0, 200) / 100f : 1f;

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

    private static int Capture(string requestedDevice, string pipeName, string? micName,
                               float gameVolume, float micVolume)
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
        using MMDevice device = ResolveDevice(enumerator, DataFlow.Render, requestedDevice);
        var requestedFormat = WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);
        using var capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithPollingSync()
            .WithBufferLength(CaptureBufferMilliseconds)
            .WithFormat(requestedFormat)
            .Build();

        // The microphone is optional. If it can't start (unplugged, blocked by
        // Windows privacy settings), game audio still records.
        MicrophoneSource? microphone = null;
        if (!string.IsNullOrWhiteSpace(micName))
        {
            try
            {
                microphone = new MicrophoneSource(ResolveDevice(enumerator, DataFlow.Capture, micName), micVolume);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Microphone unavailable ({exception.Message}). If Windows blocks it, allow desktop apps " +
                    "to use the microphone in Settings → Privacy & security → Microphone. Game audio continues.");
            }
        }

        using var writer = new DirectPcmPipeWriter(pipe, capture.WaveFormat, gameVolume, microphone);

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
        microphone?.Start();
        writer.Start();
        Console.Error.WriteLine(
            $"Capturing game audio from '{device.FriendlyName}' at {gameVolume:P0}" +
            (microphone is null ? "." : $" and microphone '{microphone.Name}' at {micVolume:P0}."));

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
            try { microphone?.Dispose(); } catch { }
            try { keepAlive?.Dispose(); } catch { }
        }

        if (writer.HasFailed && !string.IsNullOrWhiteSpace(writer.FailureMessage))
            Console.Error.WriteLine($"Audio pipe closed: {writer.FailureMessage}");
        return 0;
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            requested.StartsWith("Default ", StringComparison.OrdinalIgnoreCase))
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);

        MMDevice[] devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToArray();
        MMDevice? selected = devices.FirstOrDefault(
                device => device.ID.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.FriendlyName.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.FriendlyName.Contains(requested, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;

        MMDevice fallback = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
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

/// <summary>
/// Captures a microphone in its native Windows format, converts it to 48 kHz
/// stereo float, and buffers it so the game-audio stream (the master clock)
/// can pull exactly as many frames as it writes. The buffer is kept short so
/// the microphone stays in sync with the game even though the two devices run
/// on slightly different clocks.
/// </summary>
internal sealed class MicrophoneSource : IDisposable
{
    private const int TargetRate = 48000;
    private const int MaxBacklogFrames = TargetRate / 5;      // 200 ms: trim beyond this
    private const int TrimToFrames = TargetRate / 20;         // back down to 50 ms

    private readonly IDisposable _capture;
    private readonly Action _start;
    private readonly Action _stop;
    private readonly WaveFormat _format;
    private readonly float _volume;
    private readonly object _lock = new();
    private float[] _ring = new float[TargetRate * 2 * 2];  // 2 s of stereo frames
    private int _readFrame;
    private int _countFrames;

    // Linear resampler state (only used when the mic isn't already 48 kHz).
    private float _prevLeft, _prevRight;
    private double _phase;

    public string Name { get; }

    public MicrophoneSource(MMDevice device, float volume)
    {
        Name = device.FriendlyName;
        _volume = volume;
        // No WithFormat: microphones are captured in their own format (often
        // mono or 44.1 kHz) and converted here, so any mic works.
        var recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithPollingSync()
            .WithBufferLength(20)
            .Build();
        _format = recorder.WaveFormat;
        if (_format.BitsPerSample is not (32 or 16))
            throw new NotSupportedException($"{_format.BitsPerSample}-bit microphone audio is not supported.");
        recorder.DataAvailable += (data, flags, _, _) =>
            OnData(data, (flags & AudioClientBufferFlags.Silent) != 0);
        _capture = recorder;
        _start = recorder.StartRecording;
        _stop = recorder.StopRecording;
    }

    public void Start() => _start();

    private void OnData(ReadOnlySpan<byte> data, bool silent)
    {
        int channels = Math.Max(1, _format.Channels);
        int bytesPerSample = _format.BitsPerSample / 8;
        int frames = data.Length / (bytesPerSample * channels);
        if (frames == 0) return;

        // Decode to stereo float at the device rate.
        float[] stereo = ArrayPool<float>.Shared.Rent(frames * 2);
        try
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float left = 0, right = 0;
                if (!silent)
                {
                    left = Sample(data, frame * channels, bytesPerSample);
                    right = channels > 1 ? Sample(data, frame * channels + 1, bytesPerSample) : left;
                }
                stereo[frame * 2] = left * _volume;
                stereo[frame * 2 + 1] = right * _volume;
            }
            lock (_lock)
            {
                if (_format.SampleRate == TargetRate)
                    for (int frame = 0; frame < frames; frame++)
                        Push(stereo[frame * 2], stereo[frame * 2 + 1]);
                else
                    Resample(stereo, frames);
                // Keep latency bounded if the mic clock runs fast.
                if (_countFrames > MaxBacklogFrames)
                {
                    int drop = _countFrames - TrimToFrames;
                    _readFrame = (_readFrame + drop) % RingFrames;
                    _countFrames -= drop;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(stereo);
        }
    }

    private float Sample(ReadOnlySpan<byte> data, int index, int bytesPerSample) =>
        bytesPerSample == 4
            ? MemoryMarshal.Read<float>(data[(index * 4)..])
            : MemoryMarshal.Read<short>(data[(index * 2)..]) / 32768f;

    private void Resample(float[] stereo, int frames)
    {
        // Interpolate between the previous chunk's last frame (position 0)
        // and this chunk's frames (positions 1..frames).
        double step = _format.SampleRate / (double)TargetRate;
        double position = _phase;
        while (position < frames)
        {
            int index = (int)position;
            float fraction = (float)(position - index);
            float l0 = index == 0 ? _prevLeft : stereo[(index - 1) * 2];
            float r0 = index == 0 ? _prevRight : stereo[(index - 1) * 2 + 1];
            float l1 = stereo[index * 2], r1 = stereo[index * 2 + 1];
            Push(l0 + (l1 - l0) * fraction, r0 + (r1 - r0) * fraction);
            position += step;
        }
        _phase = position - frames;
        _prevLeft = stereo[(frames - 1) * 2];
        _prevRight = stereo[(frames - 1) * 2 + 1];
    }

    private int RingFrames => _ring.Length / 2;

    private void Push(float left, float right)
    {
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

    /// <summary>Adds up to <paramref name="frames"/> microphone frames into <paramref name="mix"/>.</summary>
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
            // Any shortfall stays silent; the mic simply catches up.
        }
    }

    public void Dispose()
    {
        try { _stop(); } catch { }
        _capture.Dispose();
    }
}

internal sealed class DirectPcmPipeWriter : IDisposable
{
    private const long QpcUnitsPerSecond = 10_000_000;
    private const int FramesPerSecond = 48000;
    // A real gap between packets larger than this is filled with silence; a
    // packet that overlaps audio already written is trimmed. Normal packet
    // timing jitter is far below this.
    private const long GapThreshold = 200_000;        // 20 ms
    // If no packet arrives for this long, write silence up to (now - margin)
    // so the stream never stops advancing with real time.
    private const long StallThreshold = 1_500_000;    // 150 ms
    private const long StallMargin = 500_000;         // 50 ms
    private const long MaxFill = 30 * QpcUnitsPerSecond;
    private const int FillChunkFrames = FramesPerSecond / 10;

    private readonly NamedPipeServerStream pipe;
    private readonly WaveFormat sourceFormat;
    private readonly float gameVolume;
    private readonly MicrophoneSource? microphone;
    private readonly object writeLock = new();
    private long coveredUntilQpc = -1;
    private long filledFrames;
    private long trimmedFrames;
    private long firstDevicePosition = -1;
    private long firstQpcPosition = -1;
    private long expectedDevicePosition = -1;
    private long nextDiagnosticQpc = -1;
    private long discontinuityCount;
    private volatile bool failed;
    private string? failureMessage;

    public DirectPcmPipeWriter(NamedPipeServerStream pipe, WaveFormat sourceFormat,
                               float gameVolume, MicrophoneSource? microphone)
    {
        this.pipe = pipe;
        this.sourceFormat = sourceFormat;
        this.gameVolume = gameVolume;
        this.microphone = microphone;
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

    // Game silence still carries the microphone.
    private void WriteSilence(long frames)
    {
        if (frames <= 0) return;
        filledFrames += frames;
        while (frames > 0 && !failed)
        {
            int chunk = (int)Math.Min(frames, FillChunkFrames);
            Emit(ReadOnlySpan<float>.Empty, chunk);
            frames -= chunk;
        }
    }

    /// <summary>Mixes game samples (or silence when empty) with the microphone and writes 16-bit PCM.</summary>
    private void Emit(ReadOnlySpan<float> game, int frames)
    {
        int samples = frames * 2;
        float[] mix = ArrayPool<float>.Shared.Rent(samples);
        byte[] pcm = ArrayPool<byte>.Shared.Rent(samples * sizeof(short));
        try
        {
            Span<float> mixSpan = mix.AsSpan(0, samples);
            if (game.IsEmpty)
                mixSpan.Clear();
            else
                for (int index = 0; index < samples; index++)
                    mixSpan[index] = game[index] * gameVolume;
            microphone?.MixInto(mixSpan, frames);

            for (int index = 0; index < samples; index++)
            {
                short value = (short)Math.Round(Math.Clamp(mixSpan[index], -1.0f, 1.0f) * short.MaxValue);
                pcm[index * 2] = (byte)(value & 0xff);
                pcm[index * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            pipe.Write(pcm, 0, samples * sizeof(short));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            failureMessage = exception.Message;
            failed = true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mix);
            ArrayPool<byte>.Shared.Return(pcm);
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
            if (frames > 0)
            {
                Emit(silent ? ReadOnlySpan<float>.Empty : samples[..(frames * 2)], frames);
                if (failed) return;
            }
            coveredUntilQpc = Math.Max(coveredUntilQpc, packetEnd);
            TrackClock(packetFrames, discontinuity, devicePosition, qpcPosition);
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
