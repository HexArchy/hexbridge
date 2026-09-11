using System.ComponentModel;
using HexBridge.Localization;

namespace HexBridge.App.Features;

/// <summary>
/// One tab. <see cref="Content"/> is a view model; the view comes from the data templates in
/// App.axaml, so the shell never names a view either.
///
/// <para>
/// The tab carries the <em>key</em> of its title rather than the title, and raises a change
/// when the language moves. A tab strip is built once, at launch, and a record holding a
/// finished string would still be showing «Настройки» half an hour after somebody switched
/// the app to English.
/// </para>
/// </summary>
public sealed class FeaturePage(string titleKey, object content) : INotifyPropertyChanged
{
    public string TitleKey { get; } = titleKey;

    public object Content { get; } = content;

    public string Title => Loc.Of(TitleKey);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Retranslate() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
}

/// <summary>
/// The UI half of a feature. The shell drives every module the same way — build pages, apply
/// a snapshot, sample once a second, reset on stop — and never asks which feature it holds.
/// </summary>
public interface IFeatureUiModule
{
    /// <summary>Matches <see cref="IFeature.Id"/>.</summary>
    string FeatureId { get; }

    IEnumerable<FeaturePage> CreatePages();

    /// <summary>
    /// The feature itself, handed over once at startup. Only a module with something to ask
    /// of its feature needs it — a page that sends a file has to have something to send it
    /// with, and reaching for the receiver from a view model is how a shell stops being one.
    /// </summary>
    void Attach(IFeature feature) { }

    /// <summary>Called on the UI tick, ten times a second.</summary>
    void Apply(ReceiverSnapshot snapshot);

    /// <summary>Called once a second, for history windows.</summary>
    void Sample(ReceiverSnapshot snapshot) { }

    /// <summary>The receiver stopped; drop anything accumulated.</summary>
    void Reset() { }
}
