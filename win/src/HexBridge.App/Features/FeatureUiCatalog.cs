using HexBridge.App.ViewModels;
using HexBridge.Clipboard;
using HexBridge.Devices;
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
        () => new DevicesUiModule(),
        () => new ClipboardUiModule(),
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

/// <summary>The passthrough's single page: driver, forwarding and one card per device.</summary>
public sealed class DevicesUiModule : IFeatureUiModule
{
    public string FeatureId => "devices";

    public DevicesViewModel Devices { get; } = new();

    public IEnumerable<FeaturePage> CreatePages() => [new FeaturePage("Устройства", Devices)];

    public void Apply(ReceiverSnapshot snapshot) =>
        Devices.Apply(snapshot.Feature<DevicesState>(FeatureId));
}

/// <summary>The clipboard's single page: what it is, what it costs, and what crossed last.</summary>
public sealed class ClipboardUiModule : IFeatureUiModule
{
    public string FeatureId => "clipboard";

    public ClipboardViewModel Clipboard { get; } = new();

    public IEnumerable<FeaturePage> CreatePages() => [new FeaturePage("Буфер обмена", Clipboard)];

    public void Apply(ReceiverSnapshot snapshot) =>
        Clipboard.Apply(snapshot.Feature<ClipboardState>(FeatureId));
}
