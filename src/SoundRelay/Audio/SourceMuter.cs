using NAudio.CoreAudioApi;

namespace SoundRelay.Audio;

/// <summary>
/// Mutes or unmutes a process's audio in the Windows volume mixer, so the user
/// stops hearing the relayed app on their own speakers. It only toggles that
/// app's normal session mute; SoundRelay's process-loopback capture is a separate
/// tap and keeps receiving the audio, so the relay still works while muted.
/// </summary>
public static class SourceMuter
{
    public static void SetProcessMuted(int processId, bool muted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.GetProcessID == (uint)processId)
                            session.SimpleAudioVolume.Mute = muted;
                    }
                }
                catch
                {
                    // A device that refuses session enumeration is simply skipped.
                }
            }
        }
        catch
        {
            // Muting is best-effort and must never break the relay.
        }
    }
}
