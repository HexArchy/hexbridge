using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HexBridge.Localization;

namespace HexBridge.Tests;

/// <summary>
/// The string table, checked the way a translation actually goes wrong.
///
/// <para>
/// Not by reading it — a table of six hundred lines is not read, it is skimmed — but by the
/// three failures that ship: a key added to one language and forgotten in the other, a
/// placeholder that survived in one sentence and not in the other, and a line that was
/// copied across and never translated. All three build green and all three show up as an
/// empty label or a crash in front of somebody.
/// </para>
///
/// <para>
/// Nothing here reads or moves the process-wide language: every lookup names its culture.
/// That is what lets these run beside the rest of the suite, which is pinned to Russian.
/// </para>
/// </summary>
public class LocalizationTests
{
    /// <summary>English is the neutral table, so it lives under the invariant culture.</summary>
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    private static string[] Keys(CultureInfo culture) => [.. Localizer.Keys(culture).Order(StringComparer.Ordinal)];

    // MARK: - Both tables carry the same keys

    [Fact]
    public void Both_languages_define_exactly_the_same_keys()
    {
        var english = Keys(English);
        var russian = Keys(Russian);

        Assert.Empty(english.Except(russian, StringComparer.Ordinal));
        Assert.Empty(russian.Except(english, StringComparer.Ordinal));
    }

    [Fact]
    public void The_table_is_not_empty()
    {
        // A guard on the guards: every check below passes trivially over nothing at all.
        Assert.True(Keys(English).Length > 400, $"only {Keys(English).Length} keys");
    }

    [Fact]
    public void Every_key_has_a_string_in_both_languages()
    {
        foreach (var key in Keys(English))
        {
            Assert.False(string.IsNullOrWhiteSpace(Localizer.Get(key, English)), $"{key} is empty in English");
            Assert.False(string.IsNullOrWhiteSpace(Localizer.Get(key, Russian)), $"{key} is empty in Russian");
        }
    }

    /// <summary>
    /// The generated accessor is built from the English table, so a property that comes back
    /// as its own name means the Russian one lost the key — the exact failure that shows an
    /// English label in a Russian window.
    /// </summary>
    [Fact]
    public void Every_generated_property_resolves_in_both_languages()
    {
        var properties = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

        Assert.True(properties.Count > 400, $"only {properties.Count} generated properties");

        foreach (var property in properties)
        {
            Assert.NotEqual(property.Name, Localizer.Get(property.Name, English));
            Assert.NotEqual(property.Name, Localizer.Get(property.Name, Russian));
        }
    }

    // MARK: - Substitutions

    [Fact]
    public void Placeholders_match_one_for_one_between_the_two_languages()
    {
        foreach (var key in Keys(English))
        {
            var english = Placeholders(Localizer.Get(key, English));
            var russian = Placeholders(Localizer.Get(key, Russian));
            Assert.True(english.SequenceEqual(russian),
                $"{key}: English takes [{string.Join(", ", english)}], Russian takes [{string.Join(", ", russian)}]");
        }
    }

    /// <summary>
    /// A template that takes {0} and {2} but not {1} formats fine and then throws the day
    /// somebody passes two arguments. The indices have to be a run from zero.
    /// </summary>
    [Fact]
    public void Placeholder_indices_run_from_zero_with_no_gaps()
    {
        foreach (var key in Keys(English))
        {
            var used = Placeholders(Localizer.Get(key, English));
            Assert.True(used.SequenceEqual(Enumerable.Range(0, used.Count)), $"{key}: [{string.Join(", ", used)}]");
        }
    }

    /// <summary>Every template survives being filled in, in both languages.</summary>
    [Fact]
    public void Every_template_formats_without_throwing()
    {
        // Long enough for the widest template plus room to grow: running out of these
        // reads as a broken string rather than as a short fixture, which cost a minute.
        object?[] arguments = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"];
        foreach (var key in Keys(English))
        {
            foreach (var culture in new[] { English, Russian })
            {
                var template = Localizer.Get(key, culture);
                var count = Placeholders(template).Count;
                _ = string.Format(CultureInfo.InvariantCulture, template, arguments[..count]);
            }
        }
    }

    private static List<int> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().Order()];

    // MARK: - Nothing left untranslated

    [Fact]
    public void The_english_table_carries_no_russian()
    {
        // One line is Russian on purpose: a language names itself in itself, so that somebody
        // who cannot read the current interface can still find their own language in the menu.
        foreach (var key in Keys(English).Where(k => k != "Settings_Language_Russian"))
        {
            Assert.DoesNotMatch("[Ѐ-ӿ]", Localizer.Get(key, English));
        }
    }

    [Fact]
    public void Plural_families_are_complete_in_both_languages()
    {
        var families = Keys(English)
            .Where(k => k.EndsWith("_one", StringComparison.Ordinal))
            .Select(k => k[..^"_one".Length])
            .ToList();

        Assert.NotEmpty(families);

        foreach (var family in families)
        {
            foreach (var form in new[] { "_one", "_few", "_many", "_other" })
            {
                Assert.Contains(family + form, Keys(English), StringComparer.Ordinal);
                Assert.Contains(family + form, Keys(Russian), StringComparer.Ordinal);
            }
        }
    }

    // MARK: - The glossary's own rules

    /// <summary>
    /// docs/GLOSSARY.md: exclamation marks are not used anywhere, on either platform.
    /// </summary>
    [Fact]
    public void No_string_shouts()
    {
        foreach (var key in Keys(English))
        {
            Assert.DoesNotContain('!', Localizer.Get(key, English));
            Assert.DoesNotContain('!', Localizer.Get(key, Russian));
        }
    }

    /// <summary>
    /// docs/GLOSSARY.md: «receiver» and «sender» are protocol words and never appear in text
    /// meant for a person. The console's own help is the one exemption, and only because the
    /// two words are the literal values <c>--role</c> takes.
    /// </summary>
    [Fact]
    public void No_string_calls_a_machine_a_receiver_or_a_sender()
    {
        foreach (var key in Keys(English).Where(k => !k.StartsWith("Cli_", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(@"(?i)\b(receiver|sender)s?\b", Localizer.Get(key, English));
        }

        foreach (var key in Keys(Russian).Where(k => !k.StartsWith("Cli_", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch("(?i)приёмник|отправител", Localizer.Get(key, Russian));
        }
    }

    /// <summary>
    /// The seven feature states from the glossary, spelled the way the table spells them.
    /// A Windows build that says «Off» where the Mac says «off» is two programs.
    /// </summary>
    [Theory]
    [InlineData("Feature_Disabled", "Turned off in the settings", "Выключено в настройках")]
    [InlineData("Feature_Unavailable", "Not available here", "Здесь недоступно")]
    [InlineData("Role_Title_Sender", "Shares its microphone", "Отдаёт свой микрофон")]
    [InlineData("Role_Title_Receiver", "Receives a microphone", "Принимает чужой микрофон")]
    public void The_glossary_wording_is_what_the_table_says(string key, string english, string russian)
    {
        Assert.Equal(english, Localizer.Get(key, English));
        Assert.Equal(russian, Localizer.Get(key, Russian));
    }
}
