using System.Windows.Media;

namespace SoundRelay.Models;

/// <summary>A single alt-tab visible window and the process behind it.</summary>
public sealed class AudioTargetWindow
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string Title { get; init; }
    public IntPtr Handle { get; init; }
    public ImageSource? Icon { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Title)
        ? ProcessName
        : $"{Title}  ({ProcessName})";

    public override string ToString() => Display;
}
