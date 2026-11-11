using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace S8Cam;

public sealed record AudioOutputDeviceInfo(int DeviceId, string Name) {
    public override string ToString() => Name;
}

/// <summary>
/// Приёмник микрофона смартфона (H3HProtocol.Audio = 17, 48 кГц 16-bit PCM Mono),
/// обработка усиления/лимитера, линия задержки Lip-Sync, расчёт VU-метра и вывод через WinMM waveOut.
/// </summary>
public sealed class PhoneAudioEngine : IAsyncDisposable {
    // WinMM waveOut P/Invoke — не требует внешних NuGet-пакетов, минимальная задержка и выбор устройства вывода
    private const int WAVE_MAPPER = -1;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const int CALLBACK_NULL = 0;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPSW {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCapsW(IntPtr uDeviceID, out WAVEOUTCAPSW pwoc, uint cbwoc);

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    public static IReadOnlyList<AudioOutputDeviceInfo> GetOutputDevices() {
        var list = new List<AudioOutputDeviceInfo> {
            new(WAVE_MAPPER, "Системное аудиоустройство по умолчанию")
        };
        try {
            uint count = waveOutGetNumDevs();
            uint sz = (uint)Marshal.SizeOf<WAVEOUTCAPSW>();
            for (int i = 0; i < count; i++) {
                if (waveOutGetDevCapsW((IntPtr)i, out var caps, sz) == 0 && !string.IsNullOrWhiteSpace(caps.szPname)) {
                    list.Add(new AudioOutputDeviceInfo(i, caps.szPname.Trim()));
                }
            }
        } catch { }
        return list;
    }

    public static int ResolveDeviceId(string? preferredDeviceName) {
        if (string.IsNullOrWhiteSpace(preferredDeviceName) || preferredDeviceName == "default") return WAVE_MAPPER;
        var devices = GetOutputDevices();
        var exact = devices.FirstOrDefault(d => d.DeviceId >= 0 && d.Name.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.DeviceId;
        var partial = devices.FirstOrDefault(d => d.DeviceId >= 0 && d.Name.Contains(preferredDeviceName, StringComparison.OrdinalIgnoreCase));
        return partial?.DeviceId ?? WAVE_MAPPER;
    }

    private sealed class NativeWaveBuffer : IDisposable {
        public readonly IntPtr HeaderPtr;
        public readonly IntPtr DataPtr;
        public readonly int Capacity;
        public bool Prepared;

        public NativeWaveBuffer(int capacity) {
            Capacity = capacity;
            DataPtr = Marshal.AllocHGlobal(capacity);
            HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            var hdr = new WAVEHDR {
                lpData = DataPtr,
                dwBufferLength = (uint)capacity,
                dwFlags = WHDR_DONE
            };
            Marshal.StructureToPtr(hdr, HeaderPtr, false);
        }

        public uint Flags => Marshal.PtrToStructure<WAVEHDR>(HeaderPtr).dwFlags;

        public void SetLength(int length) {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(HeaderPtr);
            hdr.dwBufferLength = (uint)length;
            Marshal.StructureToPtr(hdr, HeaderPtr, false);
        }

        public void Dispose() {
            Marshal.FreeHGlobal(HeaderPtr);
            Marshal.FreeHGlobal(DataPtr);
        }
    }

    private readonly Action<string> log;
    private CancellationTokenSource? cts;
    private UdpClient? udpListener;
    private TcpListener? tcpListener;
    private Task? netTask;
    private Task? playTask;

    private readonly ConcurrentQueue<(byte[] Pcm, int SampleRate, int Channels, long EnqueuedMs)> delayQueue = new();
    private volatile float gainLinear = 1.0f;
    private volatile bool muted;
    private volatile bool monitorEnabled = true;
    private volatile int delayMs;
    private volatile string outputDeviceName = "default";
    private double peakDb = -90.0;
    private double rmsDb = -90.0;
    private long receivedPackets;
    private long lastPacketTick;

    public double PeakDb => Volatile.Read(ref peakDb);
    public double RmsDb => Volatile.Read(ref rmsDb);
    public long ReceivedPackets => Interlocked.Read(ref receivedPackets);
    public bool Active => Environment.TickCount64 - Interlocked.Read(ref lastPacketTick) < 2500;

    /// <summary>Вызывается для каждого обработанного PCM-блока (48 кГц 16-bit Mono), например для записи в MP4.</summary>
    public event Action<ReadOnlyMemory<byte>, int>? PcmReady;

    public PhoneAudioEngine(Action<string> log) {
        this.log = log;
    }

    public void UpdateSettings(Settings s) {
        double clampedDb = Math.Clamp(s.AudioGainDb, -24.0, 24.0);
        gainLinear = (float)Math.Pow(10.0, clampedDb / 20.0);
        muted = s.AudioMuted || s.PrivacyMute || !s.AudioEnabled;
        monitorEnabled = s.AudioMonitorEnabled;
        delayMs = Math.Clamp(s.AudioDelayMs, 0, 500);
        outputDeviceName = string.IsNullOrWhiteSpace(s.AudioOutputDevice) ? "default" : s.AudioOutputDevice;
    }

    public void Start(string transport, int audioPort, Settings initialSettings, CancellationToken externalToken) {
        StopSync();
        UpdateSettings(initialSettings);
        cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ct = cts.Token;

        if (transport == "wifi") {
            try {
                udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, audioPort));
                udpListener.Client.ReceiveBufferSize = 512 * 1024;
                netTask = Task.Run(() => ReceiveUdpLoop(udpListener, ct), ct);
            } catch (Exception ex) {
                log("Аудиомост Wi-Fi UDP порт " + audioPort + ": " + ex.Message);
            }
        } else if (transport == "usb") {
            try {
                tcpListener = new TcpListener(IPAddress.Loopback, audioPort);
                tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                tcpListener.Start(2);
                netTask = Task.Run(() => ReceiveTcpLoop(tcpListener, ct), ct);
            } catch (Exception ex) {
                log("Аудиомост USB TCP порт " + audioPort + ": " + ex.Message);
            }
        }

        playTask = Task.Run(() => PlaybackLoop(ct), ct);
    }

    private async Task ReceiveUdpLoop(UdpClient socket, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var datagram = await socket.ReceiveAsync(ct);
                if (datagram.Buffer.Length < H3HProtocol.HeaderSize) continue;
                using var ms = new MemoryStream(datagram.Buffer, 0, datagram.Buffer.Length, writable: false);
                var headerBuf = ArrayPool<byte>.Shared.Rent(H3HProtocol.HeaderSize);
                try {
                    var msg = await H3HProtocol.ReadAsync(ms, headerBuf, ct);
                    if (msg is { Type: H3HMessageType.Audio }) {
                        SubmitMessage(msg.Value);
                    }
                } finally {
                    ArrayPool<byte>.Shared.Return(headerBuf);
                }
            } catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task ReceiveTcpLoop(TcpListener server, CancellationToken ct) {
        var headerBuf = new byte[H3HProtocol.HeaderSize];
        while (!ct.IsCancellationRequested) {
            try {
                using var client = await server.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                var stream = client.GetStream();
                while (!ct.IsCancellationRequested) {
                    var msg = await H3HProtocol.ReadAsync(stream, headerBuf, ct);
                    if (msg == null) break;
                    if (msg.Value.Type == H3HMessageType.Audio) {
                        SubmitMessage(msg.Value);
                    }
                }
            } catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    /// <summary>Принимает аудиопакет H3H (включая мультиплексированный поток USB Direct AOA).</summary>
    public void SubmitMessage(H3HMessage message) {
        if (message.Type != H3HMessageType.Audio || message.Payload == null || message.Payload.Length < 4) return;

        int channels = (int)(message.Flags & 0xFF);
        if (channels is < 1 or > 2) channels = 1;
        int rateCode = (int)((message.Flags >> 8) & 0xFFFF);
        int sampleRate = rateCode > 0 ? rateCode * 100 : 48000;
        if (sampleRate is < 8000 or > 96000) sampleRate = 48000;

        var processed = ProcessSamples(message.Payload, gainLinear, muted, out double pDb, out double rDb);
        Volatile.Write(ref peakDb, pDb);
        Volatile.Write(ref rmsDb, rDb);
        Interlocked.Increment(ref receivedPackets);
        Interlocked.Exchange(ref lastPacketTick, Environment.TickCount64);

        // Ограничиваем очередь максимум ~650 мс (~65 фреймов по 10 мс), чтобы исключить накопление дрейфа
        while (delayQueue.Count > 68) {
            delayQueue.TryDequeue(out _);
        }
        delayQueue.Enqueue((processed, sampleRate, channels, Environment.TickCount64));
    }

    public static byte[] ProcessSamples(ReadOnlySpan<byte> input, float gain, bool isMuted, out double peakDbOut, out double rmsDbOut) {
        int sampleCount = input.Length / 2;
        byte[] output = new byte[sampleCount * 2];
        if (isMuted || sampleCount == 0) {
            peakDbOut = -90.0;
            rmsDbOut = -90.0;
            return output;
        }

        double maxAbs = 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < sampleCount; i++) {
            short raw = (short)(input[i * 2] | (input[i * 2 + 1] << 8));
            float amplified = raw * gain;
            // Мягкое ограничение пиков около ±30000..32767 без резкого цифрового треска
            if (amplified > 30000f) {
                float excess = (amplified - 30000f) / 8000f;
                amplified = 30000f + 2767f * (excess / (1f + excess));
            } else if (amplified < -30000f) {
                float excess = (-amplified - 30000f) / 8000f;
                amplified = -30000f - 2767f * (excess / (1f + excess));
            }
            short clamped = (short)Math.Clamp((int)MathF.Round(amplified), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(clamped & 0xFF);
            output[i * 2 + 1] = (byte)((clamped >> 8) & 0xFF);

            double norm = Math.Abs((double)clamped) / 32768.0;
            if (norm > maxAbs) maxAbs = norm;
            sumSq += norm * norm;
        }

        double rms = Math.Sqrt(sumSq / sampleCount);
        peakDbOut = maxAbs > 1e-5 ? 20.0 * Math.Log10(maxAbs) : -90.0;
        rmsDbOut = rms > 1e-5 ? 20.0 * Math.Log10(rms) : -90.0;
        return output;
    }

    private async Task PlaybackLoop(CancellationToken ct) {
        IntPtr hWaveOut = IntPtr.Zero;
        int openedSampleRate = 0;
        int openedChannels = 0;
        string openedDevice = "";
        const int ringSize = 16;
        const int maxBufBytes = 8192;
        var buffers = new NativeWaveBuffer[ringSize];
        for (int i = 0; i < ringSize; i++) buffers[i] = new NativeWaveBuffer(maxBufBytes);
        int head = 0;
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();

        void CloseWaveOut() {
            if (hWaveOut == IntPtr.Zero) return;
            try { waveOutReset(hWaveOut); } catch { }
            foreach (var b in buffers) {
                if (b.Prepared) {
                    try { waveOutUnprepareHeader(hWaveOut, b.HeaderPtr, hdrSize); } catch { }
                    b.Prepared = false;
                }
            }
            try { waveOutClose(hWaveOut); } catch { }
            hWaveOut = IntPtr.Zero;
        }

        try {
            while (!ct.IsCancellationRequested) {
                if (!delayQueue.TryPeek(out var nextItem)) {
                    if (Environment.TickCount64 - Interlocked.Read(ref lastPacketTick) > 300) {
                        Volatile.Write(ref peakDb, -90.0);
                        Volatile.Write(ref rmsDb, -90.0);
                    }
                    await Task.Delay(5, ct);
                    continue;
                }

                long waited = Environment.TickCount64 - nextItem.EnqueuedMs;
                int targetDelay = delayMs;
                if (waited < targetDelay) {
                    await Task.Delay((int)Math.Min(10, targetDelay - waited), ct);
                    continue;
                }

                if (!delayQueue.TryDequeue(out var item)) continue;

                // Отправляем подписчикам (например, StreamRecorder) синхронизированный PCM-кадр
                PcmReady?.Invoke(item.Pcm, item.SampleRate);

                if (!monitorEnabled) {
                    if (hWaveOut != IntPtr.Zero) CloseWaveOut();
                    continue;
                }

                string desiredDev = outputDeviceName;
                if (hWaveOut == IntPtr.Zero || openedSampleRate != item.SampleRate || openedChannels != item.Channels || openedDevice != desiredDev) {
                    CloseWaveOut();
                    var wfx = new WAVEFORMATEX {
                        wFormatTag = WAVE_FORMAT_PCM,
                        nChannels = (ushort)item.Channels,
                        nSamplesPerSec = (uint)item.SampleRate,
                        wBitsPerSample = 16,
                        nBlockAlign = (ushort)(item.Channels * 2),
                        nAvgBytesPerSec = (uint)(item.SampleRate * item.Channels * 2),
                        cbSize = 0
                    };
                    int devId = ResolveDeviceId(desiredDev);
                    int mmRes = waveOutOpen(out hWaveOut, devId, ref wfx, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                    if (mmRes != 0 && devId != WAVE_MAPPER) {
                        mmRes = waveOutOpen(out hWaveOut, WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                    }
                    if (mmRes != 0) {
                        hWaveOut = IntPtr.Zero;
                        await Task.Delay(50, ct);
                        continue;
                    }
                    openedSampleRate = item.SampleRate;
                    openedChannels = item.Channels;
                    openedDevice = desiredDev;
                }

                // Находим свободный буфер кольца
                var slot = buffers[head];
                head = (head + 1) % ringSize;
                if (slot.Prepared && (slot.Flags & WHDR_DONE) == 0) {
                    // Звуковая карта перегружена — пропускаем один 10-мс блок вместо накопления задержки
                    continue;
                }
                if (slot.Prepared) {
                    waveOutUnprepareHeader(hWaveOut, slot.HeaderPtr, hdrSize);
                    slot.Prepared = false;
                }

                int copyLen = Math.Min(item.Pcm.Length, slot.Capacity);
                Marshal.Copy(item.Pcm, 0, slot.DataPtr, copyLen);
                slot.SetLength(copyLen);
                if (waveOutPrepareHeader(hWaveOut, slot.HeaderPtr, hdrSize) == 0) {
                    slot.Prepared = true;
                    waveOutWrite(hWaveOut, slot.HeaderPtr, hdrSize);
                }
            }
        } catch (OperationCanceledException) {
        } finally {
            CloseWaveOut();
            foreach (var b in buffers) b.Dispose();
        }
    }

    private void StopSync() {
        var oldCts = cts;
        cts = null;
        oldCts?.Cancel();
        try { udpListener?.Dispose(); } catch { }
        udpListener = null;
        try { tcpListener?.Stop(); } catch { }
        tcpListener = null;
        while (delayQueue.TryDequeue(out _)) { }
        Volatile.Write(ref peakDb, -90.0);
        Volatile.Write(ref rmsDb, -90.0);
        oldCts?.Dispose();
    }

    public async ValueTask DisposeAsync() {
        var pTask = playTask;
        var nTask = netTask;
        StopSync();
        try {
            if (pTask != null) await pTask.WaitAsync(TimeSpan.FromSeconds(1));
            if (nTask != null) await nTask.WaitAsync(TimeSpan.FromSeconds(1));
        } catch { }
    }
}
