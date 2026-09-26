using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRelay.Audio;

/// <summary>
/// One playback destination fed by the relay: its own buffer, volume, optional
/// level meter, resampler, and WASAPI output. Several branches let the same
/// captured audio go to more than one device at once (for example the mic
/// bridge plus a monitor the user can hear).
/// </summary>
internal sealed class OutputBranch : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private readonly MediaFoundationResampler _resampler;
    private readonly WasapiOut _output;

    /// <summary>Peak output level (0..1+), raised only when this branch meters.</summary>
    public event EventHandler<float>? Level;

    /// <summary>Raised if this branch's playback stops on its own.</summary>
    public event EventHandler<Exception?>? Stopped;

    public OutputBranch(WaveFormat captureFormat, MMDevice device, float volume, bool withMeter)
    {
        _buffer = new BufferedWaveProvider(captureFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };

        ISampleProvider tail = _buffer.ToSampleProvider();
        _volume = new VolumeSampleProvider(tail) { Volume = volume };
        tail = _volume;

        if (withMeter)
        {
            var metering = new MeteringSampleProvider(_volume);
            metering.StreamVolume += (_, e) =>
            {
                float peak = 0f;
                foreach (float channelPeak in e.MaxSampleValues)
                    peak = Math.Max(peak, channelPeak);
                Level?.Invoke(this, peak);
            };
            tail = metering;
        }

        // MMDevice.AudioClient activates a fresh client per access; copy the mix
        // format out and dispose it rather than leaking one per branch.
        WaveFormat targetFormat;
        using (var probeClient = device.AudioClient)
            targetFormat = probeClient.MixFormat;

        var waveProvider = new SampleToWaveProvider(tail);
        _resampler = new MediaFoundationResampler(waveProvider, targetFormat) { ResamplerQuality = 60 };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_resampler);
    }

    public float Volume
    {
        set => _volume.Volume = value;
    }

    public void AddSamples(byte[] buffer, int count) => _buffer.AddSamples(buffer, 0, count);

    public void Play() => _output.Play();

    public void Stop()
    {
        try { _output.Stop(); } catch { /* teardown must not throw upward */ }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        => Stopped?.Invoke(this, e.Exception);

    public void Dispose()
    {
        _output.PlaybackStopped -= OnPlaybackStopped;
        try { _output.Dispose(); } catch { }
        try { _resampler.Dispose(); } catch { }
    }
}
