using Avalonia.Data;
using Avalonia.Markup.Xaml;
using HexBridge.Localization;

namespace HexBridge.App;

/// <summary>
/// A literal on screen, written <c>{loc:Tr Some_Key}</c>.
///
/// <para>
/// It produces a binding rather than a string, and that is the whole reason it exists: the
/// language can change while the window is open, and a string handed to a TextBlock at load
/// time would still be there in the old language afterwards. The binding's source is the
/// localizer's <see cref="LocalizedText"/> for that key — one shared object per key, not one
/// per use, because Avalonia does not keep a binding's source alive and a private object
/// would be collected before the first language change ever reached it.
/// </para>
///
/// <para>
/// A key with no string in the table comes out as the key. That is deliberate: an untranslated
/// label has to be visible in a screenshot and catchable in a test, and neither happens if it
/// silently renders as an empty line.
/// </para>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = Localizer.Instance.Text(Key),
        Path = nameof(LocalizedText.Value),
        Mode = BindingMode.OneWay,
    };
}
