using System.Collections.ObjectModel;
using System.Windows.Threading;
using SoundRelay.Audio;
using SoundRelay.Config;
using SoundRelay.Models;
using SoundRelay.Windows;

namespace SoundRelay.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private AudioRouter? _router;

    private AudioTargetWindow? _selectedSource;
    private AudioDeviceInfo? _selectedOutput;
    private double _volumePercent = 100;
    private bool _includeProcessTree = true;
    private bool _isRunning;
    private double _meterLevel;
    private string _statusText = "Idle. Pick an app and an output, then press Relay.";

    public ObservableCollection<AudioTargetWindow> Sources { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> Microphones { get; } = new();

    public RelayCommand RefreshSourcesCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ToggleRelayCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _config = AppConfig.Load();

        _volumePercent = Math.Clamp(_config.Volume * 100.0, 0, 150);
        _includeProcessTree = _config.IncludeProcessTree;

        RefreshSourcesCommand = new RelayCommand(RefreshSources);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        ToggleRelayCommand = new RelayCommand(ToggleRelay, () => CanToggle);

        RefreshDevices();
        RefreshSources();
    }

    public AudioTargetWindow? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
                ToggleRelayCommand.RaiseCanExecuteChanged();
        }
    }

    public AudioDeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (SetProperty(ref _selectedOutput, value))
            {
                _config.OutputDeviceId = value?.Id;
                ToggleRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (SetProperty(ref _volumePercent, value))
            {
                OnPropertyChanged(nameof(VolumeLabel));
                _config.Volume = (float)(value / 100.0);
                if (_router != null)
                    _router.Volume = _config.Volume;
            }
        }
    }

    public string VolumeLabel => $"{_volumePercent:0} %";

    public bool IncludeProcessTree
    {
        get => _includeProcessTree;
        set
        {
            if (SetProperty(ref _includeProcessTree, value))
                _config.IncludeProcessTree = value;
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ToggleLabel));
                ToggleRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !_isRunning;

    public string ToggleLabel => _isRunning ? "Stop relay" : "Relay audio";

    public double MeterLevel
    {
        get => _meterLevel;
        private set => SetProperty(ref _meterLevel, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private bool CanToggle =>
        _isRunning || (_selectedSource != null && _selectedOutput?.Device != null);

    public void RefreshSources()
    {
        var previousPid = _selectedSource?.ProcessId;
        Sources.Clear();

        foreach (var window in WindowEnumerator.GetVisibleWindows())
            Sources.Add(window);

        // Reselect the same process if it is still open, otherwise fall back to
        // the process name remembered from last session.
        SelectedSource =
            Sources.FirstOrDefault(w => w.ProcessId == previousPid)
            ?? Sources.FirstOrDefault(w =>
                string.Equals(w.ProcessName, _config.LastSourceProcessName, StringComparison.OrdinalIgnoreCase))
            ?? Sources.FirstOrDefault();

        StatusText = Sources.Count == 0
            ? "No visible apps found. Open the app you want to relay, then Refresh."
            : $"Found {Sources.Count} app(s). Pick your source.";
    }

    public void RefreshDevices()
    {
        var previousId = _selectedOutput?.Id ?? _config.OutputDeviceId;
        OutputDevices.Clear();
        foreach (var device in DeviceManager.GetRenderDevices())
            OutputDevices.Add(device);

        SelectedOutput =
            OutputDevices.FirstOrDefault(d => d.Id == previousId)
            ?? OutputDevices.FirstOrDefault(d => d.IsDefault)
            ?? OutputDevices.FirstOrDefault();

        Microphones.Clear();
        foreach (var mic in DeviceManager.GetCaptureDevices())
            Microphones.Add(mic);
    }

    private void ToggleRelay()
    {
        if (_isRunning)
            StopRelay();
        else
            StartRelay();
    }

    private void StartRelay()
    {
        var source = _selectedSource;
        var output = _selectedOutput;
        if (source == null || output?.Device == null)
            return;

        try
        {
            _router = new AudioRouter(output.Device, source.ProcessId, _includeProcessTree)
            {
                Volume = (float)(_volumePercent / 100.0),
            };
            _router.OutputLevel += OnOutputLevel;
            _router.Stopped += OnRouterStopped;
            _router.Start();

            _config.LastSourceProcessName = source.ProcessName;
            _config.Save();

            IsRunning = true;
            StatusText = $"Relaying {source.ProcessName} into {output.FriendlyName}.";
        }
        catch (Exception ex)
        {
            TeardownRouter();
            IsRunning = false;
            StatusText = "Could not start relay: " + Flatten(ex);
        }
    }

    private void StopRelay()
    {
        TeardownRouter();
        IsRunning = false;
        MeterLevel = 0;
        StatusText = "Relay stopped.";
        _config.Save();
    }

    private void OnOutputLevel(object? sender, float peak)
    {
        double clamped = Math.Clamp(peak, 0, 1);
        _dispatcher.BeginInvoke(() => MeterLevel = clamped);
    }

    private void OnRouterStopped(object? sender, Exception? error)
    {
        _dispatcher.BeginInvoke(() =>
        {
            TeardownRouter();
            IsRunning = false;
            MeterLevel = 0;
            StatusText = error == null
                ? "Relay stopped."
                : "Relay stopped unexpectedly: " + Flatten(error);
        });
    }

    private void TeardownRouter()
    {
        if (_router == null)
            return;
        _router.OutputLevel -= OnOutputLevel;
        _router.Stopped -= OnRouterStopped;
        _router.Dispose();
        _router = null;
    }

    private static string Flatten(Exception ex)
    {
        var message = ex.Message;
        var inner = ex.InnerException;
        while (inner != null)
        {
            message += " (" + inner.Message + ")";
            inner = inner.InnerException;
        }
        return message;
    }

    public void OnClosing()
    {
        TeardownRouter();
        _config.Save();
    }
}
