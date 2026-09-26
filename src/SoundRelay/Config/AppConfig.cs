using System.IO;
using System.Text.Json;

namespace SoundRelay.Config;

/// <summary>
/// User settings, persisted to %AppData%\SoundRelay\config.json. Nothing here
/// ships with the app: every value is chosen by the user and read back on start.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Endpoint id of the render device the relay plays into.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Endpoint id of the capture device the user considers their mic.</summary>
    public string? PreferredMicId { get; set; }

    /// <summary>Also play the relay to a monitor device so the user can hear it.</summary>
    public bool MonitorEnabled { get; set; }

    /// <summary>Endpoint id of the render device used for monitoring.</summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>Process name last used as the source, matched on next launch.</summary>
    public string? LastSourceProcessName { get; set; }

    /// <summary>Relay volume multiplier (1.0 = unity).</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Include audio from the target's child processes as well.</summary>
    public bool IncludeProcessTree { get; set; } = true;

    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundRelay");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                    return config;
            }
        }
        catch
        {
            // A corrupt or unreadable config should never block startup.
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Saving settings is best-effort; failure must not crash the app.
        }
    }
}
