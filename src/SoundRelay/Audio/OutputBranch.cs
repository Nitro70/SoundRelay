using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRelay.Audio;

/// <summary>
/// One playback destination fed by the relay: it mixes the captured application
/// audio with (optionally) the user's microphone, applies per-source levels,
/// resamples to the device, and plays it out. Several branches let the same mix
/// go to more than one device at once (the virtual-mic output plus a monitor).
/// </summary>
internal sealed class OutputBranch : IDisposable
{
    private readonly BufferedWaveProvider _appBuffer;
    private readonly VolumeSampleProvider _appVolume;
    private readonly BufferedWaveProvider? _micBuffer;
    private readonly VolumeSampleProvider? _micVolume;
    private readonly MediaFoundationResampler? _resampler;
    private readonly WasapiOut _output;

    /// <summary>Peak output level (0..1+), raised only when this branch meters.</summary>
    public event EventHandler<float>? Level;

    /// <summary>Raised if this branch's playback stops on its own.</summary>
    public event EventHandler<Exception?>? Stopped;

    public OutputBranch(WaveFormat captureFormat, MMDevice device, float appLevel, float micLevel, bool hasMic, bool withMeter)
    {
        var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        // Generous buffers so playback is always continuous (no overflow drops)
        // while we measure where latency actually comes from.
        _appBuffer = new BufferedWaveProvider(captureFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        _appVolume = new VolumeSampleProvider(_appBuffer.ToSampleProvider()) { Volume = appLevel };

        // ReadFully keeps the mix producing a continuous stream even when one
        // source is momentarily empty, so the output never underruns.
        var mixer = new MixingSampleProvider(mixFormat) { ReadFully = true };
        mixer.AddMixerInput((ISampleProvider)_appVolume);

        if (hasMic)
        {
            _micBuffer = new BufferedWaveProvider(mixFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _micVolume = new VolumeSampleProvider(_micBuffer.ToSampleProvider()) { Volume = micLevel };
            mixer.AddMixerInput((ISampleProvider)_micVolume);
        }

        ISampleProvider tail = mixer;
        if (withMeter)
        {
            var metering = new MeteringSampleProvider(mixer);
            metering.StreamVolume += (_, e) =>
            {
                float peak = 0f;
                foreach (float channelPeak in e.MaxSampleValues)
                    peak = Math.Max(peak, channelPeak);
                Level?.Invoke(this, peak);
            };
            tail = metering;
        }

        WaveFormat deviceMix;
        using (var probeClient = device.AudioClient)
            deviceMix = probeClient.MixFormat;

        var waveProvider = new SampleToWaveProvider(tail); // 48 kHz float stereo

        // MediaFoundation resampling buffers ~1 second internally, and it is
        // pointless when the device already runs 48 kHz stereo (every endpoint
        // here does). Feed WASAPI directly then, and only resample when a device
        // genuinely differs.
        IWaveProvider outputProvider = waveProvider;
        if (deviceMix.SampleRate != 48000 || deviceMix.Channels != 2)
        {
            _resampler = new MediaFoundationResampler(waveProvider, deviceMix) { ResamplerQuality = 60 };
            outputProvider = _resampler;
        }

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(outputProvider);
    }

    public float AppVolume
    {
        set => _appVolume.Volume = value;
    }

    public float MicVolume
    {
        set
        {
            if (_micVolume != null)
                _micVolume.Volume = value;
        }
    }

    /// <summary>How much audio is currently queued in this branch's buffers, in ms.</summary>
    public double BufferedMs =>
        Math.Max(_appBuffer.BufferedDuration.TotalMilliseconds, _micBuffer?.BufferedDuration.TotalMilliseconds ?? 0);

    public void AddAppSamples(byte[] buffer, int count) => _appBuffer.AddSamples(buffer, 0, count);

    public void AddMicSamples(byte[] buffer, int count) => _micBuffer?.AddSamples(buffer, 0, count);

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
        try { _resampler?.Dispose(); } catch { }
    }
}
