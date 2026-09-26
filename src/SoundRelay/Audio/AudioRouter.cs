using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundRelay.Audio;

/// <summary>
/// Owns a full relay pipeline: it captures one process's audio and plays it
/// back through a chosen render device, optionally mirroring it to a second
/// "monitor" device the user can hear. Applies a volume multiplier and reports
/// output levels for the UI meter. Instances are single-use: create a fresh
/// router for each relay session.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private readonly MMDevice _outputDevice;
    private readonly MMDevice? _monitorDevice;
    private readonly int _targetProcessId;
    private readonly bool _includeProcessTree;
    private readonly object _sync = new();

    private ProcessLoopbackCapture? _capture;

    // Copy-on-write: the list is replaced wholesale (never mutated in place)
    // under _sync, and read without a lock. A capture callback that grabbed an
    // earlier reference keeps iterating a valid, immutable list, and a slider
    // drag never sees a half-cleared collection.
    private volatile List<OutputBranch> _branches = new();
    private volatile OutputBranch? _mainBranch;

    private bool _shuttingDown;
    private volatile bool _isRunning;

    private float _pendingVolume = 1.0f;

    public bool IsRunning => _isRunning;

    /// <summary>Reports the loudest sample seen since the last notification (0..1+).</summary>
    public event EventHandler<float>? OutputLevel;

    /// <summary>Raised when the relay stops on its own, carrying any error.</summary>
    public event EventHandler<Exception?>? Stopped;

    public AudioRouter(MMDevice outputDevice, MMDevice? monitorDevice, int targetProcessId, bool includeProcessTree)
    {
        _outputDevice = outputDevice;
        _monitorDevice = monitorDevice;
        _targetProcessId = targetProcessId;
        _includeProcessTree = includeProcessTree;
    }

    /// <summary>Volume multiplier applied to the relayed audio. 1.0 is unity.</summary>
    public float Volume
    {
        get => _pendingVolume;
        set
        {
            _pendingVolume = value;
            foreach (var branch in _branches) // volatile read of an immutable list
                branch.Volume = value;
        }
    }

    public void Start()
    {
        if (_isRunning || _shuttingDown)
            return;

        _capture = new ProcessLoopbackCapture(_targetProcessId, _includeProcessTree);
        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += OnCaptureStopped;

        // The main branch carries the meter and is the destination that becomes
        // the microphone (via a virtual cable). The monitor branch, if present,
        // is a second device the user listens on and is not essential.
        var main = new OutputBranch(_capture.WaveFormat, _outputDevice, _pendingVolume, withMeter: true);
        main.Level += OnBranchLevel;
        main.Stopped += OnBranchStopped;
        _mainBranch = main;

        var branches = new List<OutputBranch> { main };
        if (_monitorDevice != null)
        {
            var monitor = new OutputBranch(_capture.WaveFormat, _monitorDevice, _pendingVolume, withMeter: false);
            monitor.Stopped += OnBranchStopped;
            branches.Add(monitor);
        }
        _branches = branches;

        _isRunning = true;
        _capture.Start();
        foreach (var branch in branches)
            branch.Play();
    }

    public void Stop() => Shutdown(null, notify: false);

    private void Shutdown(Exception? error, bool notify)
    {
        // Exactly one caller wins the shutdown; the others (a branch's stop
        // notification, an explicit Stop, Dispose) return immediately. That keeps
        // teardown single-threaded without holding a lock across the blocking
        // capture-thread join, which would otherwise deadlock.
        lock (_sync)
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;
        }

        _isRunning = false;

        try { _capture?.Stop(); } catch { /* teardown must not throw upward */ }
        foreach (var branch in _branches)
            branch.Stop();

        CleanupPipeline();

        if (notify)
            Stopped?.Invoke(this, error);
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        // Feed every destination the same captured bytes. The reference is read
        // once; the list it points at is never mutated in place.
        var branches = _branches;
        for (int i = 0; i < branches.Count; i++)
            branches[i].AddSamples(e.Buffer, e.BytesRecorded);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Shutdown(e.Exception, notify: true);
    }

    private void OnBranchLevel(object? sender, float peak) => OutputLevel?.Invoke(this, peak);

    private void OnBranchStopped(object? sender, Exception? error)
    {
        if (error == null)
            return;

        // The main output feeds the microphone: losing it ends the relay. A
        // monitor-device fault (headphones unplugged, device removed) should only
        // drop the monitor and leave the mic bridge running.
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
            _branches = updated; // atomic swap; any in-flight capture callback keeps its old list
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
