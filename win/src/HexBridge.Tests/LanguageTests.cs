using System.Globalization;
using HexBridge.Localization;

namespace HexBridge.Tests;

/// <summary>
/// The half of localisation that is behaviour rather than text: which language a machine
/// starts in, what a switch actually moves, and whether numbers come out in the conventions
/// the language uses.
///
/// <para>
/// Every test here makes its own <see cref="Localizer"/> or names its culture outright, so
/// none of them touches the process-wide setting the rest of the suite runs under. The one
/// test that does move the process puts it back, and only ever between two Russian
/// cultures, so nothing running beside it can tell.
/// </para>
/// </summary>
public class LanguageTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    // MARK: - Which language a fresh install starts in

    [Theory]
    [InlineData("ru-RU", AppLanguage.Russian)]
    [InlineData("ru", AppLanguage.Russian)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.English)]
    [InlineData("", AppLanguage.English)]
    public void A_fresh_install_follows_the_system(string system, AppLanguage expected) =>
        Assert.Equal(expected, Language.Detect(CultureInfo.GetCultureInfo(system)));

    /// <summary>
    /// «System» is the machine's own culture and not a guess at one, which is the only way a
    /// German install gets German number separators — the interface falls back to English
    /// because there is no German table, and that is a different question.
    /// </summary>
    [Fact]
    public void System_keeps_the_machines_own_cultures()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal(german, Language.UiCultureFor(AppLanguage.System, german));
        Assert.Equal(german, Language.FormatCultureFor(AppLanguage.System, german));
    }

    [Fact]
    public void An_explicit_choice_brings_its_own_number_conventions()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal("en", Language.UiCultureFor(AppLanguage.English, german).TwoLetterISOLanguageName);
        Assert.Equal(".", Language.FormatCultureFor(AppLanguage.English, german).NumberFormat.NumberDecimalSeparator);

        Assert.Equal("ru", Language.UiCultureFor(AppLanguage.Russian, german).TwoLetterISOLanguageName);
        Assert.Equal(",", Language.FormatCultureFor(AppLanguage.Russian, german).NumberFormat.NumberDecimalSeparator);
    }

    // MARK: - Switching

    [Fact]
    public void A_lookup_follows_the_culture_it_is_given()
    {
        Assert.Equal("Settings", Localizer.Get("Tab_Settings", English));
        Assert.Equal("Настройки", Localizer.Get("Tab_Settings", Russian));
    }

    [Fact]
    public void A_missing_key_comes_back_as_itself()
    {
        // Visible in a screenshot, catchable in a test, and never a crash in a drawn window.
        Assert.Equal("No_Such_Key_Anywhere", Localizer.Get("No_Such_Key_Anywhere", English));
    }

    [Fact]
    public void Moving_the_culture_changes_what_the_indexer_answers()
    {
        var localizer = new Localizer { Culture = English };
        Assert.Equal("Settings", localizer["Tab_Settings"]);

        localizer.Culture = Russian;
        Assert.Equal("Настройки", localizer["Tab_Settings"]);
    }

    /// <summary>
    /// The whole of «no restart»: a string already on screen is an object holding a key, and
    /// a language change tells it to read the key again.
    /// </summary>
    [Fact]
    public void A_string_on_screen_follows_the_language()
    {
        var localizer = new Localizer { Culture = English };
        var text = localizer.Text("Tab_Settings");

        var changes = 0;
        text.PropertyChanged += (_, _) => changes++;

        Assert.Equal("Settings", text.Value);

        localizer.Culture = Russian;
        Assert.Equal(1, changes);
        Assert.Equal("Настройки", text.Value);

        // Assigning the same culture is not a change and must not repaint the window.
        localizer.Culture = Russian;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void The_shell_is_told_once_per_change()
    {
        var localizer = new Localizer { Culture = English };
        var events = 0;
        localizer.LanguageChanged += (_, _) => events++;

        localizer.Culture = Russian;
        localizer.Culture = Russian;
        localizer.Culture = English;

        Assert.Equal(2, events);
    }

    /// <summary>
    /// One object per key and not one per use. Avalonia does not keep a binding's source
    /// alive, so a private object handed to each literal would be collected before the first
    /// language change reached it — and a list of every literal ever drawn would grow for as
    /// long as the window is open. Interning by key answers both.
    /// </summary>
    [Fact]
    public void Every_binding_onto_a_key_shares_one_object()
    {
        var localizer = new Localizer { Culture = English };

        Assert.Same(localizer.Text("Tab_Settings"), localizer.Text("Tab_Settings"));
        Assert.NotSame(localizer.Text("Tab_Settings"), localizer.Text("Tab_Log"));

        // Two bindings onto the same key, both told once.
        var text = localizer.Text("Tab_Settings");
        var first = 0;
        var second = 0;
        text.PropertyChanged += (_, _) => first++;
        localizer.Text("Tab_Settings").PropertyChanged += (_, _) => second++;

        localizer.Culture = Russian;
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }


    /// <summary>
    /// <see cref="Language.Apply"/> moves the process: the table, the number formats and the
    /// thread it was called on. Checked between two Russian cultures so the assembly-wide
    /// pin is never actually left.
    /// </summary>
    [Fact]
    public void Apply_moves_the_whole_process()
    {
        var before = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            Language.Apply(AppLanguage.Russian);

            Assert.Equal("ru-RU", CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("ru", CultureInfo.DefaultThreadCurrentUICulture?.TwoLetterISOLanguageName);
            Assert.Equal("ru", Localizer.Instance.Culture.TwoLetterISOLanguageName);
            Assert.Equal("Настройки", Strings.Tab_Settings);
        }
        finally
        {
            Language.Apply(AppLanguage.Russian);
            Assert.Equal(before?.Name, CultureInfo.DefaultThreadCurrentCulture?.Name);
        }
    }

    // MARK: - Numbers and units

    [Fact]
    public void Units_are_words_and_are_translated()
    {
        Assert.Equal("60\u00a0ms", Loc.Ms(60, Scope(AppLanguage.English)));
        Assert.Equal("60\u00a0мс", Loc.Ms(60, Scope(AppLanguage.Russian)));

        Assert.Equal("32\u00a0kbit/s", Loc.Kbits(32000, Scope(AppLanguage.English)));
        Assert.Equal("32\u00a0кбит/с", Loc.Kbits(32000, Scope(AppLanguage.Russian)));
    }

    /// <summary>§10.1 rule 8: a unit follows its number across a space that cannot break.</summary>
    [Fact]
    public void A_unit_never_wraps_away_from_its_number()
    {
        foreach (var key in Localizer.Keys(CultureInfo.InvariantCulture)
                     .Where(k => k.StartsWith("Unit_", StringComparison.Ordinal)))
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, Russian })
            {
                Assert.DoesNotContain("} ", Localizer.Get(key, culture), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_decimal_separator_follows_the_language()
    {
        Assert.Equal("-12.3\u00a0dBFS", Loc.Dbfs1(-12.34, Scope(AppLanguage.English)));
        Assert.Equal("-12,3\u00a0dBFS", Loc.Dbfs1(-12.34, Scope(AppLanguage.Russian)));

        Assert.Equal("1,234", Loc.Count(1234, Scope(AppLanguage.English)));

        // Russian groups with a space of some kind rather than a comma; which one is ICU's
        // business and has changed between releases, so the check is that it is not a comma.
        Assert.DoesNotContain(",", Loc.Count(1234, Scope(AppLanguage.Russian)), StringComparison.Ordinal);
        Assert.Equal("1234", Loc.Count(1234, Scope(AppLanguage.Russian)).Where(char.IsDigit).ToArray());
    }

    [Fact]
    public void Durations_are_written_in_the_language_of_the_moment()
    {
        var en = Scope(AppLanguage.English);
        var ru = Scope(AppLanguage.Russian);

        Assert.Equal("41\u00a0s", Loc.Duration(TimeSpan.FromSeconds(41), en));
        Assert.Equal("41\u00a0с", Loc.Duration(TimeSpan.FromSeconds(41), ru));

        Assert.Equal("3\u00a0m 12\u00a0s", Loc.Duration(TimeSpan.FromSeconds(192), en));
        Assert.Equal("3\u00a0м 12\u00a0с", Loc.Duration(TimeSpan.FromSeconds(192), ru));

        Assert.Equal("2\u00a0h 05\u00a0m", Loc.Duration(TimeSpan.FromMinutes(125), en));
        Assert.Equal("2\u00a0ч 05\u00a0м", Loc.Duration(TimeSpan.FromMinutes(125), ru));
    }

    [Fact]
    public void Sizes_pick_the_unit_that_keeps_the_number_short()
    {
        Assert.Equal("512\u00a0B", Loc.Size(512, Scope(AppLanguage.English)));
        Assert.Equal("1.5\u00a0KB", Loc.Size(1536, Scope(AppLanguage.English)));
        Assert.Equal("1,5\u00a0КБ", Loc.Size(1536, Scope(AppLanguage.Russian)));
        Assert.Equal("2.0\u00a0MB", Loc.Size(2 * 1024 * 1024, Scope(AppLanguage.English)));
    }

    // MARK: - Plurals

    /// <summary>English has two forms.</summary>
    [Theory]
    [InlineData(1, "1 frame")]
    [InlineData(0, "0 frames")]
    [InlineData(2, "2 frames")]
    [InlineData(21, "21 frames")]
    public void English_counts_one_and_the_rest(int count, string expected) =>
        Assert.Equal(expected.Replace(' ', '\u00a0'), Loc.Frames(count, Scope(AppLanguage.English)));

    /// <summary>Russian has three, and the rule is about the last two digits.</summary>
    [Theory]
    [InlineData(1, "1 кадр")]
    [InlineData(2, "2 кадра")]
    [InlineData(4, "4 кадра")]
    [InlineData(5, "5 кадров")]
    [InlineData(11, "11 кадров")]
    [InlineData(12, "12 кадров")]
    [InlineData(14, "14 кадров")]
    [InlineData(21, "21 кадр")]
    [InlineData(22, "22 кадра")]
    [InlineData(25, "25 кадров")]
    [InlineData(111, "111 кадров")]
    [InlineData(0, "0 кадров")]
    public void Russian_counts_one_a_few_and_many(int count, string expected) =>
        Assert.Equal(expected.Replace(' ', '\u00a0'), Loc.Frames(count, Scope(AppLanguage.Russian)));

    // MARK: - Harness

    private static LanguageScope Scope(AppLanguage language) => LanguageScope.For(language);
}
