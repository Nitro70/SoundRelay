using System.Runtime.InteropServices;
using NAudio.Wave;
using SoundRelay.Audio.Interop;

namespace SoundRelay.Audio;

/// <summary>
/// Captures the audio rendered by a single process (and, optionally, its child
/// processes) using the WASAPI process-loopback activation path introduced in
/// Windows 10 build 20348 / Windows 11. Nothing is injected into the target
/// process; Windows hands us a read-only copy of what that process is playing.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly int _processId;
    private readonly bool _includeProcessTree;
    private readonly object _eventLock = new();
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private IntPtr _sampleReadyEvent = IntPtr.Zero;

    public WaveFormat WaveFormat { get; }

    /// <summary>Raised on the capture thread whenever a packet of audio is ready.</summary>
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    /// <summary>Raised once when capture has stopped, carrying any fatal error.</summary>
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ProcessLoopbackCapture(int processId, bool includeProcessTree = true)
    {
        _processId = processId;
        _includeProcessTree = includeProcessTree;
        // 32-bit IEEE float, stereo, 48 kHz: the shared-mode engine's native shape,
        // so routing stays clean and no lossy conversion happens on capture.
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public void Start()
    {
        if (_captureThread != null)
            throw new InvalidOperationException("Capture is already running.");

        _stopRequested = false;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "SoundRelay.ProcessLoopbackCapture",
        };
        // The async activation callback is delivered on an MTA thread, so the
        // capture thread must live in the MTA.
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;

        // Wake the capture thread's wait. The lock keeps this from racing the
        // finally block that closes and zeroes the same handle.
        lock (_eventLock)
        {
            if (_sampleReadyEvent != IntPtr.Zero)
                NativeEvents.SetEvent(_sampleReadyEvent);
        }

        var thread = _captureThread;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(2000);
        _captureThread = null;
    }

    private void CaptureLoop()
    {
        Exception? error = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr formatPtr = IntPtr.Zero;
        IntPtr activationParamsPtr = IntPtr.Zero;

        try
        {
            IntPtr readyEvent = NativeEvents.CreateEvent(IntPtr.Zero, false, false, null);
            if (readyEvent == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the capture sync event.");
            lock (_eventLock)
                _sampleReadyEvent = readyEvent;

            audioClient = ActivateProcessLoopbackClient(out activationParamsPtr);

            formatPtr = MarshalWaveFormat(WaveFormat);
            int hr = audioClient.Initialize(
                AudioClientShareMode.Shared,
                WasapiNative.AUDCLNT_STREAMFLAGS_LOOPBACK
                    | WasapiNative.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | WasapiNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | WasapiNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                0,
                0,
                formatPtr,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);

            hr = audioClient.SetEventHandle(readyEvent);
            Marshal.ThrowExceptionForHR(hr);

            Guid captureIid = WasapiNative.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref captureIid, out object captureObj);
            Marshal.ThrowExceptionForHR(hr);
            captureClient = (IAudioCaptureClient)captureObj;

            int blockAlign = WaveFormat.BlockAlign;

            hr = audioClient.Start();
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                while (!_stopRequested)
                {
                    // Auto-reset event fires whenever a packet is available; 100 ms
                    // is a safety timeout so we notice a stop request even if idle.
                    NativeEvents.WaitForSingleObject(readyEvent, 100);
                    if (_stopRequested)
                        break;

                    DrainPackets(captureClient, blockAlign);
                }
            }
            finally
            {
                audioClient.Stop();
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (captureClient != null)
                Marshal.ReleaseComObject(captureClient);
            if (audioClient != null)
                Marshal.ReleaseComObject(audioClient);
            if (formatPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(formatPtr);
            if (activationParamsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(activationParamsPtr);

            lock (_eventLock)
            {
                if (_sampleReadyEvent != IntPtr.Zero)
                {
                    NativeEvents.CloseHandle(_sampleReadyEvent);
                    _sampleReadyEvent = IntPtr.Zero;
                }
            }

            // This runs on the background capture thread; a throwing handler here
            // would be unhandled and would take the whole process down.
            try
            {
                RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
            }
            catch
            {
                // Consumers are responsible for their own errors during teardown.
            }
        }
    }

    private void DrainPackets(IAudioCaptureClient captureClient, int blockAlign)
    {
        int hr = captureClient.GetNextPacketSize(out uint packetFrames);
        Marshal.ThrowExceptionForHR(hr);

        while (packetFrames != 0 && !_stopRequested)
        {
            hr = captureClient.GetBuffer(out IntPtr dataPtr, out uint framesRead, out uint flags, out _, out _);
            Marshal.ThrowExceptionForHR(hr);

            int byteCount = (int)framesRead * blockAlign;
            if (byteCount > 0)
            {
                var buffer = new byte[byteCount];
                // On a silent packet the buffer contents are undefined, so we emit
                // zeros instead to keep the routed stream continuous.
                if ((flags & WasapiNative.AUDCLNT_BUFFERFLAGS_SILENT) == 0 && dataPtr != IntPtr.Zero)
                    Marshal.Copy(dataPtr, buffer, 0, byteCount);

                DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, byteCount));
            }

            hr = captureClient.ReleaseBuffer(framesRead);
            Marshal.ThrowExceptionForHR(hr);

            hr = captureClient.GetNextPacketSize(out packetFrames);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private IAudioClient ActivateProcessLoopbackClient(out IntPtr activationParamsPtr)
    {
        var loopbackParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)_processId,
                ProcessLoopbackMode = _includeProcessTree
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        activationParamsPtr = Marshal.AllocHGlobal(paramsSize);
        Marshal.StructureToPtr(loopbackParams, activationParamsPtr, false);

        IntPtr propVariant = BuildBlobPropVariant(activationParamsPtr, paramsSize);
        var handler = new ActivationHandler();
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Guid audioClientIid = WasapiNative.IID_IAudioClient;
            WasapiNative.ActivateAudioInterfaceAsync(
                WasapiNative.VirtualAudioDeviceProcessLoopback,
                ref audioClientIid,
                propVariant,
                handler,
                out operation);

            if (!handler.Completed.WaitOne(5000))
                throw new TimeoutException("Timed out activating the process-loopback audio interface.");

            int activateHr = operation.GetActivateResult(out int resultHr, out object activatedInterface);
            Marshal.ThrowExceptionForHR(activateHr);
            Marshal.ThrowExceptionForHR(resultHr);

            return (IAudioClient)activatedInterface;
        }
        finally
        {
            if (operation != null)
                Marshal.ReleaseComObject(operation);
            handler.Dispose();
            Marshal.FreeHGlobal(propVariant);
        }
    }

    // Wraps a pointer to the activation params in a VT_BLOB PROPVARIANT on the
    // native heap. Layout on x64: vt at 0, cbSize at 8, pBlobData at 16.
    private static IntPtr BuildBlobPropVariant(IntPtr blobData, int blobSize)
    {
        IntPtr propVariant = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++)
            Marshal.WriteByte(propVariant, i, 0);

        Marshal.WriteInt16(propVariant, 0, (short)WasapiNative.VT_BLOB);
        Marshal.WriteInt32(propVariant, 8, blobSize);
        Marshal.WriteIntPtr(propVariant, 16, blobData);
        return propVariant;
    }

    private static IntPtr MarshalWaveFormat(WaveFormat format)
    {
        var ex = new WaveFormatEx
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = (ushort)format.Channels,
            nSamplesPerSec = (uint)format.SampleRate,
            wBitsPerSample = (ushort)format.BitsPerSample,
            nBlockAlign = (ushort)format.BlockAlign,
            nAvgBytesPerSec = (uint)format.AverageBytesPerSecond,
            cbSize = 0,
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(ex, ptr, false);
        return ptr;
    }

    public void Dispose()
    {
        Stop();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        public readonly ManualResetEvent Completed = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Completed.Set();
            return 0; // S_OK
        }

        public void Dispose() => Completed.Dispose();
    }
}

internal static class NativeEvents
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
