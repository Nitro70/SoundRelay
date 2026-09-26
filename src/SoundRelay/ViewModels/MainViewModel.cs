using System.Collections.ObjectModel;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
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
    private AudioDeviceInfo? _selectedMonitorDevice;
    private AudioDeviceInfo? _selectedMicDevice;
    private double _volumePercent = 100;
    private double _micLevelPercent = 100;
    private bool _includeProcessTree = true;
    private bool _monitorEnabled;
    private bool _micEnabled;
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
        _micLevelPercent = Math.Clamp(_config.MicLevel * 100.0, 0, 150);
        _includeProcessTree = _config.IncludeProcessTree;
        _monitorEnabled = _config.MonitorEnabled;
        _micEnabled = _config.IncludeMicrophone;

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
                OnPropertyChanged(nameof(OutputHint));
                OnPropertyChanged(nameof(OutputReachesMic));
                ToggleRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the chosen output is a virtual cable, so it can feed a mic.</summary>
    public bool OutputReachesMic => _selectedOutput?.IsVirtualCable == true;

    /// <summary>Plain-language guidance about where the chosen output actually goes.</summary>
    public string OutputHint
    {
        get
        {
            if (_selectedOutput?.IsVirtualCable == true)
                return "This is a virtual cable. In the app you want to feed, set its microphone to this cable's recording side.";
            if (!OutputDevices.Any(d => d.IsVirtualCable))
                return "No virtual cable detected. Audio can only reach a microphone through one. Install a virtual cable (for example VB-CABLE), then pick it here.";
            return "This looks like a speaker or headset, so you will hear it and no microphone will receive it. Pick your virtual cable to feed a mic.";
        }
    }

    public AudioDeviceInfo? SelectedMonitorDevice
    {
        get => _selectedMonitorDevice;
        set
        {
            if (SetProperty(ref _selectedMonitorDevice, value))
                _config.MonitorDeviceId = value?.Id;
        }
    }

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (SetProperty(ref _monitorEnabled, value))
            {
                _config.MonitorEnabled = value;
                OnPropertyChanged(nameof(CanEditMonitorDevice));
            }
        }
    }

    /// <summary>The monitor device picker is usable only when monitoring is on and idle.</summary>
    public bool CanEditMonitorDevice => IsIdle && _monitorEnabled;

    public AudioDeviceInfo? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set
        {
            if (SetProperty(ref _selectedMicDevice, value))
                _config.MicDeviceId = value?.Id;
        }
    }

    /// <summary>Mix the user's real microphone into the output (their voice plus the app).</summary>
    public bool IncludeMicrophone
    {
        get => _micEnabled;
        set
        {
            if (SetProperty(ref _micEnabled, value))
            {
                _config.IncludeMicrophone = value;
                OnPropertyChanged(nameof(CanEditMic));
            }
        }
    }

    /// <summary>The mic device picker and level are usable only when mic mixing is on and idle.</summary>
    public bool CanEditMic => IsIdle && _micEnabled;

    public double MicLevelPercent
    {
        get => _micLevelPercent;
        set
        {
            if (SetProperty(ref _micLevelPercent, value))
            {
                OnPropertyChanged(nameof(MicLevelLabel));
                _config.MicLevel = (float)(value / 100.0);
                if (_router != null)
                    _router.MicLevel = _config.MicLevel;
            }
        }
    }

    public string MicLevelLabel => $"{_micLevelPercent:0} %";

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
                OnPropertyChanged(nameof(CanEditMonitorDevice));
                OnPropertyChanged(nameof(CanEditMic));
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
        // Capture both selections BEFORE clearing. Clearing the shared ItemsSource
        // makes each bound ComboBox write a null SelectedItem back to its source,
        // so reading these ids after the clear would always see null.
        var previousId = _selectedOutput?.Id ?? _config.OutputDeviceId;
        var previousMonitorId = _selectedMonitorDevice?.Id ?? _config.MonitorDeviceId;
        var previousMicId = _selectedMicDevice?.Id ?? _config.MicDeviceId;

        OutputDevices.Clear();
        foreach (var device in DeviceManager.GetRenderDevices())
            OutputDevices.Add(device);

        SelectedOutput =
            OutputDevices.FirstOrDefault(d => d.Id == previousId)
            ?? OutputDevices.FirstOrDefault(d => d.IsDefault)
            ?? OutputDevices.FirstOrDefault();

        // The monitor picker draws from the same render-device list.
        SelectedMonitorDevice =
            OutputDevices.FirstOrDefault(d => d.Id == previousMonitorId)
            ?? OutputDevices.FirstOrDefault(d => d.IsDefault)
            ?? OutputDevices.FirstOrDefault();

        Microphones.Clear();
        foreach (var mic in DeviceManager.GetCaptureDevices())
            Microphones.Add(mic);

        SelectedMicDevice =
            Microphones.FirstOrDefault(d => d.Id == previousMicId)
            ?? Microphones.FirstOrDefault(d => d.IsDefault)
            ?? Microphones.FirstOrDefault();
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

        // Only mirror to a monitor when it is enabled and a genuinely different
        // device is chosen; monitoring to the same device would just echo.
        MMDevice? monitorDevice = null;
        var monitor = _selectedMonitorDevice;
        if (_monitorEnabled && monitor?.Device != null && monitor.Id != output.Id)
            monitorDevice = monitor.Device;

        MMDevice? micDevice = null;
        var mic = _selectedMicDevice;
        if (_micEnabled && mic?.Device != null)
            micDevice = mic.Device;

        try
        {
            _router = new AudioRouter(output.Device, monitorDevice, micDevice, source.ProcessId, _includeProcessTree)
            {
                Volume = (float)(_volumePercent / 100.0),
                MicLevel = (float)(_micLevelPercent / 100.0),
            };
            _router.OutputLevel += OnOutputLevel;
            _router.Stopped += OnRouterStopped;
            _router.Start();

            _config.LastSourceProcessName = source.ProcessName;
            _config.Save();

            IsRunning = true;
            string voice = micDevice != null ? $"{mic!.FriendlyName} + " : string.Empty;
            StatusText = monitorDevice != null
                ? $"Relaying {voice}{source.ProcessName} into {output.FriendlyName}, monitoring on {monitor!.FriendlyName}."
                : $"Relaying {voice}{source.ProcessName} into {output.FriendlyName}.";
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
