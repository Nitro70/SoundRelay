using NAudio.CoreAudioApi;

namespace SoundRelay.Models;

/// <summary>A render or capture endpoint, ready to show in a picker.</summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string FriendlyName { get; init; }
    public required DataFlow Flow { get; init; }
    public bool IsDefault { get; init; }
    public MMDevice? Device { get; init; }

    public string Display => IsDefault ? $"{FriendlyName}  (default)" : FriendlyName;

    public override string ToString() => Display;
}
