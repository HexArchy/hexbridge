using HexBridge.App.ViewModels;
using HexBridge.DualSense;
using HexBridge.Microphone;

namespace HexBridge.App.Features;

/// <summary>
/// The window's composition root: the one file that knows which UI module belongs to which
/// feature. A third feature is a new module and one more line here — nothing above this
/// point, and nothing in the other two modules, has to change.
/// </summary>
public static class FeatureUiCatalog
{
    private static readonly Func<IFeatureUiModule>[] Factories =
    [
        () => new MicrophoneUiModule(),
        () => new DualSenseUiModule(),
    ];

    /// <summary>
    /// One module per feature the receiver was built with, in the receiver's own order, so
    /// the tabs follow registration rather than this list. A feature with no UI is simply
    /// absent from the result.
    /// </summary>
    public static IReadOnlyList<IFeatureUiModule> For(IEnumerable<IFeature> features)
    {
        var available = Factories.Select(factory => factory()).ToDictionary(module => module.FeatureId);
        return [.. features
            .Select(feature => available.GetValueOrDefault(feature.Id))
            .OfType<IFeatureUiModule>()];
    }
}

/// <summary>The microphone's two pages: live status and buffer quality.</summary>
public sealed class MicrophoneUiModule : IFeatureUiModule
{
    public string FeatureId => "microphone";

    public StatusViewModel Status { get; } = new();
    public QualityViewModel Quality { get; } = new();

    public IEnumerable<FeaturePage> CreatePages() =>
        [new FeaturePage("Статус", Status), new FeaturePage("Качество", Quality)];

    public void Apply(ReceiverSnapshot snapshot)
    {
        var state = snapshot.Feature<MicrophoneState>(FeatureId);
        Status.Apply(snapshot, state);
        Quality.Apply(snapshot, state);
    }

    public void Sample(ReceiverSnapshot snapshot) =>
        Quality.Sample(snapshot, snapshot.Feature<MicrophoneState>(FeatureId));

    public void Reset() => Quality.Reset();
}

/// <summary>The gamepad's single page: driver, forwarding and controller.</summary>
public sealed class DualSenseUiModule : IFeatureUiModule
{
    public string FeatureId => "dualsense";

    public DualSenseViewModel Gamepad { get; } = new();

    public IEnumerable<FeaturePage> CreatePages() => [new FeaturePage("DualSense", Gamepad)];

    public void Apply(ReceiverSnapshot snapshot) =>
        Gamepad.Apply(snapshot.Feature<DualSenseState>(FeatureId));
}
