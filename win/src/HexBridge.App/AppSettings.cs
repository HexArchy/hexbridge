using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexBridge.App;

public enum ThemePreference { System, Light, Dark }

/// <summary>
/// Window-only preferences. Kept out of config.json on purpose: that file is read by the
/// console receiver and by the deployed Windows install, and its shape must not drift.
/// </summary>
public sealed class AppSettings
{
    // Written as a name rather than an ordinal so the file stays readable — which means
    // reading it back needs the converter as well.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Begin receiving as soon as the app launches, the way the console build does.</summary>
    public bool StartOnLaunch { get; set; } = true;

    /// <summary>Launch straight to the tray, for the autostart-at-logon case.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>Reopens on the page the user was last looking at.</summary>
    public int LastTab { get; set; }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "ui.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(DefaultPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            // Preferences are not worth failing a launch over.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Read-only install directory; the app still works, it just forgets.
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
