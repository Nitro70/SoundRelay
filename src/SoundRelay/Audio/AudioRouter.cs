using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundRelay.Audio;

/// <summary>
/// Owns a full relay pipeline: it captures one process's audio, optionally mixes
/// in the user's microphone, and plays the result to a chosen render device
/// (which the user points at a virtual audio device to act as a microphone). It
/// can also mirror the mix to a second "monitor" device. Instances are
/// single-use: create a fresh router for each relay session.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private readonly MMDevice _outputDevice;
    private readonly MMDevice? _monitorDevice;
    private readonly MMDevice? _micDevice;
    private readonly int _targetProcessId;
    private readonly bool _includeProcessTree;
    private readonly object _sync = new();

    private ProcessLoopbackCapture? _capture;
    private MicCaptureSource? _micSource;

    // Copy-on-write: the list is replaced wholesale (never mutated in place)
    // under _sync, and read without a lock. A capture callback that grabbed an
    // earlier reference keeps iterating a valid, immutable list.
    private volatile List<OutputBranch> _branches = new();
    private volatile OutputBranch? _mainBranch;

    private bool _shuttingDown;
    private volatile bool _isRunning;

    private float _pendingVolume = 1.0f;
    private float _pendingMicLevel = 1.0f;

    public bool IsRunning => _isRunning;

    /// <summary>Reports the loudest sample seen since the last notification (0..1+).</summary>
    public event EventHandler<float>? OutputLevel;

    /// <summary>Raised when the relay stops on its own, carrying any error.</summary>
    public event EventHandler<Exception?>? Stopped;

    /// <summary>Live buffered-latency text, for diagnosing where delay accumulates.</summary>
    public event EventHandler<string>? Diagnostics;

    public AudioRouter(
        MMDevice outputDevice,
        MMDevice? monitorDevice,
        MMDevice? micDevice,
        int targetProcessId,
        bool includeProcessTree)
    {
        _outputDevice = outputDevice;
        _monitorDevice = monitorDevice;
        _micDevice = micDevice;
        _targetProcessId = targetProcessId;
        _includeProcessTree = includeProcessTree;
    }

    /// <summary>Level multiplier applied to the captured application audio.</summary>
    public float Volume
    {
        get => _pendingVolume;
        set
        {
            _pendingVolume = value;
            foreach (var branch in _branches)
                branch.AppVolume = value;
        }
    }

    /// <summary>Level multiplier applied to the mixed-in microphone.</summary>
    public float MicLevel
    {
        get => _pendingMicLevel;
        set
        {
            _pendingMicLevel = value;
            foreach (var branch in _branches)
                branch.MicVolume = value;
        }
    }

    public void Start()
    {
        if (_isRunning || _shuttingDown)
            return;

        _capture = new ProcessLoopbackCapture(_targetProcessId, _includeProcessTree);
        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += OnCaptureStopped;

        bool hasMic = _micDevice != null;

        // The main branch carries the meter and is the destination that becomes
        // the microphone (via a virtual audio device). The monitor branch, if
        // present, is a second device the user listens on.
        var main = new OutputBranch(_capture.WaveFormat, _outputDevice, _pendingVolume, _pendingMicLevel, hasMic, withMeter: true);
        main.Level += OnBranchLevel;
        main.Stopped += OnBranchStopped;
        _mainBranch = main;

        var branches = new List<OutputBranch> { main };
        if (_monitorDevice != null)
        {
            var monitor = new OutputBranch(_capture.WaveFormat, _monitorDevice, _pendingVolume, _pendingMicLevel, hasMic, withMeter: false);
            monitor.Stopped += OnBranchStopped;
            branches.Add(monitor);
        }
        _branches = branches;

        if (hasMic)
        {
            _micSource = new MicCaptureSource(_micDevice!);
            _micSource.DataAvailable += OnMicData;
            _micSource.Stopped += OnMicStopped;
        }

        _isRunning = true;
        _capture.Start();
        _micSource?.Start();
        foreach (var branch in branches)
            branch.Play();
    }

    public void Stop() => Shutdown(null, notify: false);

    private void Shutdown(Exception? error, bool notify)
    {
        // Exactly one caller wins the shutdown; the others return immediately.
        // That keeps teardown single-threaded without holding a lock across the
        // blocking capture-thread join, which would otherwise deadlock.
        lock (_sync)
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;
        }

        _isRunning = false;

        try { _capture?.Stop(); } catch { /* teardown must not throw upward */ }
        try { _micSource?.Stop(); } catch { }
        foreach (var branch in _branches)
            branch.Stop();

        CleanupPipeline();

        if (notify)
            Stopped?.Invoke(this, error);
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        var branches = _branches;
        for (int i = 0; i < branches.Count; i++)
            branches[i].AddAppSamples(e.Buffer, e.BytesRecorded);
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        var branches = _branches;
        for (int i = 0; i < branches.Count; i++)
            branches[i].AddMicSamples(e.Buffer, e.BytesRecorded);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Shutdown(e.Exception, notify: true);
    }

    private void OnMicStopped(object? sender, Exception? error)
    {
        // Losing the microphone is not fatal: the application audio keeps
        // relaying, the voice mix just goes quiet until the mic returns.
    }

    private void OnBranchLevel(object? sender, float peak)
    {
        OutputLevel?.Invoke(this, peak);

        var main = _mainBranch;
        if (main != null)
        {
            double outMs = main.BufferedMs;
            double micMs = _micSource?.BufferedMs ?? 0;
            Diagnostics?.Invoke(this, $"buffered: mic-in {micMs:0} ms  |  output {outMs:0} ms");
        }
    }

    private void OnBranchStopped(object? sender, Exception? error)
    {
        if (error == null)
            return;

        // The main output feeds the microphone: losing it ends the relay. A
        // monitor-device fault only drops the monitor; the mix keeps running.
        if (ReferenceEquals(sender, _mainBranch))
        {
            Shutdown(error, notify: true);
            return;
        }

        if (sender is not OutputBranch faulted)
            return;

        lock (_sync)
        {
            if (_shuttingDown || !_branches.Contains(faulted))
                return;
            var updated = new List<OutputBranch>(_branches);
            updated.Remove(faulted);
            _branches = updated; // atomic swap
        }

        faulted.Stopped -= OnBranchStopped;
        faulted.Dispose();
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

        var micSource = _micSource;
        _micSource = null;
        if (micSource != null)
        {
            micSource.DataAvailable -= OnMicData;
            micSource.Stopped -= OnMicStopped;
            micSource.Dispose();
        }

        List<OutputBranch> toDispose;
        lock (_sync)
        {
            toDispose = _branches;
            _branches = new List<OutputBranch>();
        }

        foreach (var branch in toDispose)
        {
            branch.Level -= OnBranchLevel;
            branch.Stopped -= OnBranchStopped;
            branch.Dispose();
        }
        _mainBranch = null;
    }

    public void Dispose() => Shutdown(null, notify: false);
}
