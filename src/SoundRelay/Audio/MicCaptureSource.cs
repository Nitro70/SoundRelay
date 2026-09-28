using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRelay.Audio;

/// <summary>
/// Captures the user's own microphone with WASAPI and converts it to the relay's
/// common format (32-bit float, 48 kHz, stereo) so it can be mixed alongside the
/// captured application audio. This is ordinary microphone recording, the same
/// thing any voice or streaming app does; nothing is injected anywhere.
/// </summary>
public sealed class MicCaptureSource : IDisposable
{
    private readonly MMDevice _device;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _srcBuffer;
    private ISampleProvider? _chain;
    private Thread? _pump;
    private volatile bool _running;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    /// <summary>How much captured mic audio is queued waiting for the pump, in ms.</summary>
    public double BufferedMs => _srcBuffer?.BufferedDuration.TotalMilliseconds ?? 0;

    /// <summary>Converted microphone audio (48 kHz float stereo), on the pump thread.</summary>
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    /// <summary>Raised when capture stops on its own, carrying any error.</summary>
    public event EventHandler<Exception?>? Stopped;

    public MicCaptureSource(MMDevice device)
    {
        _device = device;
    }

    public void Start()
    {
        _capture = new WasapiCapture(_device, true, 20);
        _srcBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            // Must be false: with the default (true) an empty buffer read returns
            // a full zero-filled block, so the pump would never see read <= 0, its
            // real-time pacing would be dead code, and it would busy-spin a core
            // while flooding the mix with silence. With false, an empty read
            // returns 0 and the pump throttles to real time.
            ReadFully = false,
        };

        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += OnCaptureStopped;

        // ToSampleProvider converts PCM or float to float samples; then match the
        // relay's sample rate and channel count.
        ISampleProvider sp = _srcBuffer.ToSampleProvider();
        if (sp.WaveFormat.SampleRate != 48000)
            sp = new WdlResamplingSampleProvider(sp, 48000);
        sp = ToStereo(sp);
        _chain = sp;

        _running = true;
        _capture.StartRecording();

        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "SoundRelay.MicPump" };
        _pump.Start();
    }

    private static ISampleProvider ToStereo(ISampleProvider sp)
    {
        int channels = sp.WaveFormat.Channels;
        if (channels == 2)
            return sp;
        if (channels == 1)
            return new MonoToStereoSampleProvider(sp);
        // More than two channels is unusual for a microphone; take the first two.
        return new MultiplexingSampleProvider(new[] { sp }, 2);
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        _srcBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        _running = false;
        Stopped?.Invoke(this, e.Exception);
    }

    private void PumpLoop()
    {
        const int frames = 480; // 10 ms at 48 kHz
        var floatBuf = new float[frames * 2];
        var byteBuf = new byte[frames * 2 * sizeof(float)];
        var chain = _chain;
        if (chain == null)
            return;

        while (_running)
        {
            int read;
            try
            {
                read = chain.Read(floatBuf, 0, floatBuf.Length);
            }
            catch
            {
                break;
            }

            if (read <= 0)
            {
                Thread.Sleep(3);
                continue;
            }

            Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, read * sizeof(float));
            DataAvailable?.Invoke(this, new WaveInEventArgs(byteBuf, read * sizeof(float)));
        }
    }

    public void Stop()
    {
        _running = false;
        try { _capture?.StopRecording(); } catch { /* stopping must not throw upward */ }

        var pump = _pump;
        if (pump != null && pump.IsAlive && pump != Thread.CurrentThread)
            pump.Join(1000);
        _pump = null;
    }

    public void Dispose()
    {
        Stop();
        if (_capture != null)
        {
            _capture.DataAvailable -= OnCaptureData;
            _capture.RecordingStopped -= OnCaptureStopped;
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }
}
