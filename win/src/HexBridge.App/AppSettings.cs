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

    /// <summary>
    /// Somebody has answered «что делает этот компьютер» at least once.
    ///
    /// <para>
    /// Here rather than in config.json, and deliberately separate from the role itself: the
    /// role has a default, and an install upgraded from a build that only ever received has
    /// to go on receiving without being asked anything. The question is worth putting on
    /// screen only for an install that has never been set up at all.
    /// </para>
    /// </summary>
    public bool RoleChosen { get; set; }

    /// <summary>
    /// Look for new versions once a day. On by default — a stream utility that silently
    /// rots is worse than one that mentions a release — but it is one switch, and off means
    /// no network request of any kind.
    ///
    /// Nothing is ever installed by this: the check offers, the user decides.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// When the last check finished, UTC. Persisted so restarting the app is not a way to
    /// make it check again — otherwise «раз в сутки» would mean «раз в запуск» for anyone
    /// who starts it with the game.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

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
