# SoundRelay

Capture the audio of a single running application and relay it, in real time, to
an output device of your choice. Pick a visible app (Chrome, a media player, a
game), pick where its sound should go, set the volume, and press one button.

SoundRelay does not create a virtual microphone and it does not modify the apps
it captures from. It uses the audio Windows already exposes and plays it back
where you tell it to.

![status: early](https://img.shields.io/badge/status-v0.1-22d3ee)

## What it does

- **Per-app capture.** Uses the WASAPI per-process loopback API
  (`ActivateAudioInterfaceAsync` with `PROCESS_LOOPBACK`) to grab exactly one
  process's audio, optionally including its child processes. Windows hands the
  app a read-only copy of what that process is playing; nothing is injected into
  the target.
- **Real source list.** The source picker lists only the windows you would see
  when pressing Alt+Tab, with their icons. Hidden services and background
  processes are filtered out.
- **Route anywhere.** Sends the captured audio to any active playback device,
  with a volume control and a live output level meter.
- **Fully configurable.** Every device, the source, the volume, and the
  include-child-processes toggle are your choice and are saved between runs. No
  device is baked in.

## About routing into a microphone

This is the part most tools are not honest about, so here it is plainly:

**Windows does not let an ordinary user-mode app write audio onto the electrical
input of a physical microphone.** A capture endpoint is fed by hardware. Tools
that make app audio "come out of your mic" do it in one of two ways:

1. They install a **virtual audio device** (a driver), which then shows up as its
   own microphone, or
2. They **hook other applications** and mix audio into what those apps read from
   the mic.

SoundRelay deliberately does neither. It installs no driver and touches no other
process. What it gives you is a clean capture-and-route engine. To make the
relayed audio arrive as microphone input in another app, pair it with a virtual
audio cable that you install separately:

1. Install a virtual audio cable of your choice (for example VB-CABLE). This
   creates a matched pair: a playback device and a recording device.
2. In SoundRelay, set **Send audio to** to the cable's **playback** side.
3. In Discord, OBS, your game, or wherever, set your **microphone** to the
   cable's **recording** side.

Now that app hears whatever SoundRelay is relaying. If you also want to hear it
yourself, most cables let you monitor, or you can run a second copy of the audio
to your speakers.

If you would rather not use a cable at all, SoundRelay is still useful on its own
for piping one app's sound to a specific speaker or headset.

## Requirements

- Windows 10 build 20348 or later, or Windows 11 (per-process loopback capture
  is not available on older builds).
- .NET 10 Desktop Runtime (or the SDK) to build and run.

## Build and run

```bash
cd src/SoundRelay
dotnet build -c Release
dotnet run -c Release
```

The build produces `SoundRelay.exe` under
`src/SoundRelay/bin/Release/net10.0-windows/`.

## Using it

1. Open the app whose audio you want (for example a video in your browser).
2. Launch SoundRelay. Choose it under **Source app** (press **Refresh** if you
   opened it afterwards).
3. Choose where the sound should go under **Send audio to**.
4. Adjust **Volume** if you want, then press **Relay audio**.
5. The **Output level** meter moves when audio is flowing. Press **Stop relay**
   to end.

## Configuration

Settings are stored at:

```
%AppData%\SoundRelay\config.json
```

It remembers your output device, preferred microphone, last source app, volume,
and the include-child-processes toggle. Delete the file to reset to defaults.

## How it fits together

```
Source process ->  WASAPI process loopback (read-only copy)
               ->  BufferedWaveProvider
               ->  volume + level metering
               ->  resample to the output device's mix format
               ->  WasapiOut  ->  chosen playback device
```

- `Audio/ProcessLoopbackCapture.cs` performs the per-process WASAPI capture.
- `Audio/AudioRouter.cs` owns the playback pipeline and metering.
- `Windows/WindowEnumerator.cs` builds the Alt+Tab source list.
- `Audio/DeviceManager.cs` enumerates active render and capture endpoints.
- `ViewModels/MainViewModel.cs` drives the UI.

## Limitations

- Per-process loopback needs a recent Windows build (see Requirements).
- Some apps that render through another host process may show their audio under
  that host. The include-child-processes toggle usually covers the common cases.
- Routing onto a real microphone needs a virtual cable, as described above.

## License

MIT. See [LICENSE](LICENSE).
