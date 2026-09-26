using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRelay.Audio;

/// <summary>
/// Owns a full relay pipeline: it captures one process's audio and plays it
/// back through a chosen render device, applying a volume multiplier and
/// reporting output levels for the UI meter. Instances are single-use: create
/// a fresh router for each relay session.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private readonly MMDevice _outputDevice;
    private readonly int _targetProcessId;
    private readonly bool _includeProcessTree;
    private readonly object _sync = new();

    private ProcessLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volume;
    private MediaFoundationResampler? _resampler;
    private WasapiOut? _output;
    private bool _shuttingDown;
    private volatile bool _isRunning;

    private float _pendingVolume = 1.0f;

    public bool IsRunning => _isRunning;

    /// <summary>Reports the loudest sample seen since the last notification (0..1+).</summary>
    public event EventHandler<float>? OutputLevel;

    /// <summary>Raised when the relay stops on its own, carrying any error.</summary>
    public event EventHandler<Exception?>? Stopped;

    public AudioRouter(MMDevice outputDevice, int targetProcessId, bool includeProcessTree)
    {
        _outputDevice = outputDevice;
        _targetProcessId = targetProcessId;
        _includeProcessTree = includeProcessTree;
    }

    /// <summary>Volume multiplier applied to the relayed audio. 1.0 is unity.</summary>
    public float Volume
    {
        get => _volume?.Volume ?? _pendingVolume;
        set
        {
            _pendingVolume = value;
            var volume = _volume;
            if (volume != null)
                volume.Volume = value;
        }
    }

    public void Start()
    {
        if (_isRunning || _shuttingDown)
            return;

        _capture = new ProcessLoopbackCapture(_targetProcessId, _includeProcessTree);
        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += OnCaptureStopped;

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            // Two seconds of slack absorbs scheduling jitter; older audio is
            // dropped rather than allowed to build unbounded latency.
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };

        ISampleProvider sampleChain = _buffer.ToSampleProvider();
        _volume = new VolumeSampleProvider(sampleChain) { Volume = _pendingVolume };

        var metering = new MeteringSampleProvider(_volume);
        metering.StreamVolume += (_, e) =>
        {
            float peak = 0f;
            foreach (float channelPeak in e.MaxSampleValues)
                peak = Math.Max(peak, channelPeak);
            OutputLevel?.Invoke(this, peak);
        };

        // The WASAPI shared-mode engine always mixes in 32-bit float, so we
        // resample the captured stream to the device's own mix format and hand
        // WasapiOut a stream it can open without asking for a conversion.
        // MMDevice.AudioClient activates a fresh client on every access, so we
        // dispose this throwaway one instead of leaking it per relay session.
        WaveFormat targetFormat;
        using (var probeClient = _outputDevice.AudioClient)
            targetFormat = probeClient.MixFormat;

        var meteredWaveProvider = new SampleToWaveProvider(metering);
        _resampler = new MediaFoundationResampler(meteredWaveProvider, targetFormat)
        {
            ResamplerQuality = 60,
        };

        _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 100);
        _output.PlaybackStopped += OnOutputStopped;
        _output.Init(_resampler);

        _isRunning = true;
        _capture.Start();
        _output.Play();
    }

    public void Stop() => Shutdown(null, notify: false);

    private void Shutdown(Exception? error, bool notify)
    {
        // Exactly one caller wins the shutdown; the others (a second thread's
        // stop notification, an explicit Stop, Dispose) return immediately. That
        // keeps teardown single-threaded without holding a lock across the
        // blocking capture-thread join, which would otherwise deadlock.
        lock (_sync)
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;
        }

        _isRunning = false;

        try { _capture?.Stop(); } catch { /* teardown must not throw upward */ }
        try { _output?.Stop(); } catch { }

        CleanupPipeline();

        if (notify)
            Stopped?.Invoke(this, error);
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Shutdown(e.Exception, notify: true);
    }

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Shutdown(e.Exception, notify: true);
    }

    private void CleanupPipeline()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnCaptureData;
            capture.RecordingStopped -= OnCaptureStopped;
            capture.Dispose();
        }

        var output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnOutputStopped;
            output.Dispose();
        }

        var resampler = _resampler;
        _resampler = null;
        resampler?.Dispose();

        _volume = null;
        _buffer = null;
    }

    public void Dispose() => Shutdown(null, notify: false);
}
