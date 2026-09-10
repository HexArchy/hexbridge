namespace HexBridge.App.Features;

/// <summary>
/// One tab. <see cref="Content"/> is a view model; the view comes from the data templates in
/// App.axaml, so the shell never names a view either.
/// </summary>
public sealed record FeaturePage(string Title, object Content);

/// <summary>
/// The UI half of a feature. The shell drives every module the same way — build pages, apply
/// a snapshot, sample once a second, reset on stop — and never asks which feature it holds.
/// </summary>
public interface IFeatureUiModule
{
    /// <summary>Matches <see cref="IFeature.Id"/>.</summary>
    string FeatureId { get; }

    IEnumerable<FeaturePage> CreatePages();

    /// <summary>Called on the UI tick, ten times a second.</summary>
    void Apply(ReceiverSnapshot snapshot);

    /// <summary>Called once a second, for history windows.</summary>
    void Sample(ReceiverSnapshot snapshot) { }

    /// <summary>The receiver stopped; drop anything accumulated.</summary>
    void Reset() { }
}
