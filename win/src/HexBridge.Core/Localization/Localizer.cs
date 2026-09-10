using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace HexBridge.Localization;

/// <summary>
/// The one place a string is turned into text, and the one place the language lives.
///
/// <para>
/// Lookups go through this object rather than through <see cref="CultureInfo.CurrentUICulture"/>
/// so that the answer never depends on which thread asked. The receiver logs from its socket
/// thread, the features report state from theirs and the window reads all of it on the
/// dispatcher; a language that is a property of the app rather than of a thread is what keeps
/// those three agreeing.
/// </para>
///
/// <para>
/// Switching is live. Everything on screen is either a binding onto a <see cref="Text"/>
/// object or a view-model property recomputed on the shell's tick, so a new language is on
/// screen within a frame and nothing is restarted.
/// </para>
/// </summary>
public sealed class Localizer
{
    private static ResourceManager Resources => Strings.ResourceManager;

    /// <summary>The app's localizer. Tests make their own rather than moving this one.</summary>
    public static Localizer Instance { get; } = new(shared: true);

    /// <summary>
    /// One <see cref="LocalizedText"/> per key, shared by every binding onto that key.
    ///
    /// <para>
    /// Shared, and not one object per use, for a reason that cost an evening: Avalonia does
    /// not keep a binding's source alive, so a fresh object handed to each literal is
    /// collected the moment the markup extension returns and the language change reaches
    /// nobody. Interning by key fixes that and bounds the table at the number of keys —
    /// six hundred small objects, once, rather than one per row of every list.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, LocalizedText> _texts = [];

    /// <summary>
    /// Invariant to begin with, which resolves to the neutral table — English. Nothing is on
    /// screen before the shell has read the preference and pushed it in, and a language the
    /// app never chose is better as the documented default than as whatever thread happened
    /// to ask first.
    /// </summary>
    private CultureInfo _culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Only the shared instance owns the generated accessors' static culture. A localizer a
    /// test makes for itself must not reach out and move the running app's language.
    /// </summary>
    private readonly bool _shared;

    public Localizer() => _shared = false;

    private Localizer(bool shared)
    {
        _shared = shared;
        if (shared) Strings.Culture = _culture;
    }

    /// <summary>
    /// Which language the strings come out in. Assigning the same culture twice is free and
    /// notifies nobody, so the shell may push the preference on every save.
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value)) return;
            _culture = value;

            // The generated accessors read their own static culture; keeping it in step is
            // what makes Strings.Foo and this["Foo"] the same lookup.
            if (_shared) Strings.Culture = value;
            Notify();
        }
    }

    /// <summary>Raised after <see cref="Culture"/> changed. For the shell, which then retranslates.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// The string for a key, in the current language. A key with no string is returned as
    /// itself: a missing translation must look like a missing translation on screen and be
    /// caught by the tests, never crash a window that was drawing fine a moment ago.
    /// </summary>
    public string this[string key] => Get(key, _culture);

    /// <summary>The same lookup against a culture of the caller's choosing. Pure; used by tests.</summary>
    public static string Get(string key, CultureInfo culture) =>
        Resources.GetString(key, culture) ?? key;

    /// <summary>
    /// Every key one language's table defines. Pass <see cref="CultureInfo.InvariantCulture"/>
    /// for English, which is the neutral table and lives in the assembly itself.
    ///
    /// <para>
    /// The set is deliberately not disposed: <see cref="ResourceManager"/> hands out the one
    /// it caches, and closing it here would take every later lookup down with it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Keys(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        if (set is null) return [];

        var keys = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            if (entry.Key is string key) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// The bindable form of one key. Asking twice gives the same object, which is what makes
    /// a language change reach every place that key is drawn.
    /// </summary>
    public LocalizedText Text(string key)
    {
        lock (_texts)
        {
            if (!_texts.TryGetValue(key, out var text)) _texts[key] = text = new LocalizedText(key, this);
            return text;
        }
    }

    private void Notify()
    {
        LocalizedText[] texts;
        lock (_texts) texts = [.. _texts.Values];

        // The literals first, then the shell: by the time a view model is told to retranslate
        // itself, everything bound straight to a key is already reading the new table.
        foreach (var text in texts) text.Raise();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// One string on screen, as an object a binding can watch. The XAML markup extension points
/// every literal at one of these, so a language change is a property change on a few hundred
/// small objects rather than a rebuilt window.
/// </summary>
public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly Localizer _localizer;

    internal LocalizedText(string key, Localizer localizer)
    {
        Key = key;
        _localizer = localizer;
    }

    public string Key { get; }

    public string Value => _localizer[Key];

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}
