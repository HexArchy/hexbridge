using System.Globalization;

namespace HexBridge.Localization;

/// <summary>
/// The two cultures a piece of text is written in: which table the words come from, and
/// which conventions the numbers follow. They are usually the app's own and are almost never
/// passed explicitly — <see cref="Loc.Ambient"/> is the default everywhere — but a test that
/// asks «what does this look like in English» must be able to ask without moving the running
/// app's language out from under whatever is running beside it.
/// </summary>
public readonly record struct LanguageScope(CultureInfo Ui, CultureInfo Format)
{
    public static LanguageScope For(AppLanguage language) =>
        new(Language.UiCultureFor(language), Language.FormatCultureFor(language));
}

/// <summary>
/// The formatting half of localisation: everything that has to change with the language and
/// is not simply a string.
///
/// <para>
/// Numbers are written with the language's own conventions and never with the invariant
/// ones — 12.5 and 12,5 are the same number and only one of them looks right to the person
/// reading it. Units are strings like everything else, because «ms» and «мс» are words, and
/// they are joined to their number by a space that cannot break (§10.1 rule 8).
/// </para>
/// </summary>
public static class Loc
{
    /// <summary>The app's own language: the localizer's table, the thread's number formats.</summary>
    public static LanguageScope Ambient => new(Localizer.Instance.Culture, CultureInfo.CurrentCulture);

    /// <summary>A string by key, for the handful of places where the key is computed.</summary>
    public static string Of(string key, LanguageScope? language = null) =>
        Localizer.Get(key, (language ?? Ambient).Ui);

    /// <summary>Fills a template in the current language's number conventions.</summary>
    public static string F(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>
    /// The right one of a plural family, by CLDR rules. English has two forms and Russian
    /// three, so the table carries four keys per family — <c>_one</c>, <c>_few</c>,
    /// <c>_many</c>, <c>_other</c> — and each language fills in all four. Filling in all four
    /// is not redundancy: it is what keeps the two tables key for key identical, which is the
    /// only thing a test can check.
    /// </summary>
    public static string Plural(string baseKey, long count, LanguageScope? language = null)
    {
        var scope = language ?? Ambient;
        return Fill(scope, Of(baseKey + "_" + Category(count, scope.Ui), scope), count);
    }

    private static string Category(long count, CultureInfo ui)
    {
        if (!IsRussian(ui)) return count == 1 ? "one" : "other";

        var n = Math.Abs(count);
        var ten = n % 10;
        var hundred = n % 100;
        if (ten == 1 && hundred != 11) return "one";
        if (ten is >= 2 and <= 4 && hundred is < 12 or > 14) return "few";
        return "many";
    }

    private static bool IsRussian(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    private static string Fill(LanguageScope scope, string template, params object?[] args) =>
        string.Format(scope.Format, template, args);

    private static string Unit(string key, string value, LanguageScope? language)
    {
        var scope = language ?? Ambient;
        return Fill(scope, Of(key, scope), value);
    }

    private static string Number(double value, string format, LanguageScope? language) =>
        value.ToString(format, (language ?? Ambient).Format);

    // MARK: - Units

    /// <summary>«60 ms» / «60 мс».</summary>
    public static string Ms(double value, LanguageScope? language = null) =>
        Unit("Unit_Ms", Number(value, "F0", language), language);

    public static string Ms(int value, LanguageScope? language = null) =>
        Unit("Unit_Ms", Number(value, "F0", language), language);

    public static string Seconds(double value, LanguageScope? language = null) =>
        Unit("Unit_Seconds", Number(value, "F0", language), language);

    public static string Dbfs(double value, LanguageScope? language = null) =>
        Unit("Unit_Dbfs", Number(value, "F0", language), language);

    public static string Dbfs1(double value, LanguageScope? language = null) =>
        Unit("Unit_Dbfs", Number(value, "F1", language), language);

    public static string Db(double value, LanguageScope? language = null) =>
        Unit("Unit_Db", Number(value, "+0.0;-0.0;0.0", language), language);

    public static string Kbits(int bitsPerSecond, LanguageScope? language = null) =>
        Unit("Unit_Kbits", Number(bitsPerSecond / 1000, "F0", language), language);

    public static string Percent(double value, LanguageScope? language = null) =>
        Unit("Unit_Percent", Number(value, "F2", language), language);

    public static string Rate(double perSecond, LanguageScope? language = null) =>
        Unit("Unit_FramesPerSecond", Number(perSecond, "F0", language), language);

    public static string Updates(double perSecond, LanguageScope? language = null) =>
        Unit("Unit_UpdatesPerSecond", Number(perSecond, "F0", language), language);

    public static string Frames(long count, LanguageScope? language = null) => Plural("Unit_Frames", count, language);

    public static string Bytes(long count, LanguageScope? language = null) => Plural("Unit_Bytes", count, language);

    public static string Devices(long count, LanguageScope? language = null) => Plural("Unit_Devices", count, language);

    /// <summary>A round count with the thousands separator the language uses.</summary>
    public static string Count(long value, LanguageScope? language = null) =>
        value.ToString("N0", (language ?? Ambient).Format);

    /// <summary>«2 h 05 m», «3 m 12 s», «41 s» — never a bare count of seconds past a minute.</summary>
    public static string Duration(TimeSpan span, LanguageScope? language = null)
    {
        var scope = language ?? Ambient;
        return span.TotalHours >= 1
            ? Fill(scope, Of("Unit_Duration_Hours", scope), (int)span.TotalHours, Number(span.Minutes, "00", scope))
            : span.TotalMinutes >= 1
                ? Fill(scope, Of("Unit_Duration_Minutes", scope), span.Minutes, Number(span.Seconds, "00", scope))
                : Fill(scope, Of("Unit_Duration_Seconds", scope), span.Seconds);
    }

    /// <summary>
    /// «370 MB/s» — how fast something actually moved.
    ///
    /// <para>
    /// Rounded to whole megabytes a second and floored at a millisecond, because the one
    /// question it answers is «почему это вдруг так быстро» and neither a decimal place nor a
    /// division by zero helps with that.
    /// </para>
    /// </summary>
    public static string Throughput(long bytes, TimeSpan over, LanguageScope? language = null) =>
        Unit(
            "Unit_MegabytesPerSecond",
            Number(bytes / (1024.0 * 1024.0) / Math.Max(over.TotalSeconds, 0.001), "F0", language),
            language);

    /// <summary>A size in whichever unit keeps it to one or two significant digits.</summary>
    public static string Size(long bytes, LanguageScope? language = null) => bytes >= 1024 * 1024
        ? Unit("Unit_Megabytes", Number(bytes / (1024.0 * 1024.0), "F1", language), language)
        : bytes >= 1024
            ? Unit("Unit_Kilobytes", Number(bytes / 1024.0, "F1", language), language)
            : Unit("Unit_Bytes", Number(bytes, "F0", language), language);
}
