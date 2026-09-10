using System.Runtime.CompilerServices;
using HexBridge.Localization;

namespace HexBridge.Tests;

/// <summary>
/// Pins the whole test assembly to Russian, once, before the first test runs.
///
/// <para>
/// Every assertion in this suite that names a sentence was written against the interface as
/// it shipped, which was Russian; English is the new default, and letting the default decide
/// here would have turned a hundred true assertions into a hundred false ones overnight for
/// no reason connected to what they check. Pinning also removes the machine's own locale from
/// the picture: the same test now passes on a Russian laptop and on an English CI runner.
/// </para>
///
/// <para>
/// The tests that are <em>about</em> language do not rely on this. They ask the localizer for
/// a named culture and compare the answer, so they neither read nor move the process-wide
/// setting and cannot race the tests running beside them.
/// </para>
/// </summary>
internal static class LocalizationFixture
{
    [ModuleInitializer]
    internal static void PinRussian() => Language.Apply(AppLanguage.Russian);
}
