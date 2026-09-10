using System.Globalization;

namespace HexBridge.Localization;

/// <summary>What the language menu offers. Stored in ui.json by name, so the order is free.</summary>
public enum AppLanguage
{
    /// <summary>Whatever Windows is set to. The default, and the only value a fresh install has.</summary>
    System,

    English,

    Russian,
}

/// <summary>
/// The language preference, and the two cultures it decides.
///
/// <para>
/// Two, not one, and they are not the same question. The <b>UI</b> culture picks which table
/// a string comes out of; the <b>format</b> culture decides whether a number reads 12.5 or
/// 12,5. Under «System» they are both simply the machine's, which is the only way a German
/// install gets German number separators — the interface falls back to English because there
/// is no German table, and that is a different fact from how the machine writes numbers.
/// </para>
/// </summary>
public static class Language
{
    /// <summary>
    /// What the machine was set to before anything here touched it. Captured once, at type
    /// load, because <see cref="Apply"/> overwrites the process defaults and «System» has to
    /// keep meaning the system rather than the last thing chosen.
    /// </summary>
    public static CultureInfo SystemUiCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>The same, for numbers and dates.</summary>
    public static CultureInfo SystemFormatCulture { get; } = CultureInfo.CurrentCulture;

    /// <summary>Which language a fresh install starts in: Russian on a Russian Windows, English elsewhere.</summary>
    public static AppLanguage Detect(CultureInfo? systemUiCulture = null) =>
        IsRussian(systemUiCulture ?? SystemUiCulture) ? AppLanguage.Russian : AppLanguage.English;

    /// <summary>The table to read strings out of.</summary>
    public static CultureInfo UiCultureFor(AppLanguage language, CultureInfo? systemUiCulture = null) => language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.Russian => CultureInfo.GetCultureInfo("ru"),
        _ => systemUiCulture ?? SystemUiCulture,
    };

    /// <summary>
    /// The culture numbers, dates and durations are written in. An explicit choice brings its
    /// own conventions with it — somebody who asked for English asked for 12.5 — while
    /// «System» leaves the machine's alone.
    /// </summary>
    public static CultureInfo FormatCultureFor(AppLanguage language, CultureInfo? systemFormatCulture = null) => language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
        AppLanguage.Russian => CultureInfo.GetCultureInfo("ru-RU"),
        _ => systemFormatCulture ?? SystemFormatCulture,
    };

    /// <summary>
    /// Puts the preference into force across the process: the string table, every thread
    /// started from here on, and the one running this call.
    /// </summary>
    public static void Apply(AppLanguage language, Localizer? localizer = null)
    {
        var format = FormatCultureFor(language);
        var ui = UiCultureFor(language);

        CultureInfo.DefaultThreadCurrentCulture = format;
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentCulture = format;
        CultureInfo.CurrentUICulture = ui;

        (localizer ?? Localizer.Instance).Culture = ui;
    }

    private static bool IsRussian(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
}
