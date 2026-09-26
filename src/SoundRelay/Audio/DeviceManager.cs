using NAudio.CoreAudioApi;
using SoundRelay.Models;

namespace SoundRelay.Audio;

/// <summary>Enumerates the machine's active audio endpoints.</summary>
public static class DeviceManager
{
    /// <summary>Active render (playback) endpoints: where a relay can send audio.</summary>
    public static List<AudioDeviceInfo> GetRenderDevices() => GetDevices(DataFlow.Render);

    /// <summary>
    /// Active capture endpoints (microphones and line inputs). Shown so the user
    /// can see which mics are enabled; the app never writes to a capture endpoint.
    /// </summary>
    public static List<AudioDeviceInfo> GetCaptureDevices() => GetDevices(DataFlow.Capture);

    private static List<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string defaultId = string.Empty;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch
        {
            // No default endpoint of this kind; leave defaultId empty.
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            list.Add(new AudioDeviceInfo
            {
                Id = device.ID,
                FriendlyName = device.FriendlyName,
                Flow = flow,
                IsDefault = device.ID == defaultId,
                IsVirtualCable = flow == DataFlow.Render && IsLikelyVirtualCable(device.FriendlyName),
                Device = device,
            });
        }

        return list
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Names of the common virtual audio cables whose recording side can act as a
    // microphone. Matched case-insensitively as substrings, so it is a hint, not
    // a guarantee: an unusually named cable may not be recognised.
    private static readonly string[] CableNameMarkers =
    {
        "cable",           // VB-CABLE ("CABLE Input"), many Virtual Audio Cable lines
        "vb-audio",        // VB-Audio family
        "voicemeeter",     // VoiceMeeter virtual inputs
        "voicemod",        // Voicemod Virtual Audio Device
        "virtual audio",   // generic virtual audio devices
        "virtual cable",   // generic virtual cables
    };

    public static bool IsLikelyVirtualCable(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
            return false;

        foreach (string marker in CableNameMarkers)
        {
            if (friendlyName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
