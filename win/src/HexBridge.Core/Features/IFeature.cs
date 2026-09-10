namespace HexBridge;

/// <summary>
/// What a feature reports about itself, in terms the shell can render without knowing which
/// feature it is looking at. Features add their own typed fields by deriving from this.
/// </summary>
public record FeatureState
{
    /// <summary>Filled in by the host from <see cref="IFeature.Id"/>; features leave it alone.</summary>
    public string Id { get; init; } = "";

    /// <summary>Filled in by the host from <see cref="IFeature.Title"/>.</summary>
    public string Title { get; init; } = "";

    public FeatureStatus Status { get; init; } = FeatureStatus.Stopped;

    /// <summary>One line, e.g. "Контроллер подключён".</summary>
    public string Headline { get; init; } = "";

    /// <summary>The sentence under the headline, or the reason for a failure.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Non-null while the feature is broken. A non-optional feature's fault also fails the
    /// whole receiver; an optional one only colours its own page.
    /// </summary>
    public string? Fault { get; init; }
}

public enum FeatureStatus
{
    /// <summary>Turned off in the config; not started and not shown as a problem.</summary>
    Disabled,

    /// <summary>Not started, or stopped on request.</summary>
    Stopped,

    /// <summary>Started and healthy, but nothing is flowing yet.</summary>
    Waiting,

    /// <summary>Doing its job.</summary>
    Live,

    /// <summary>Working, with something the user should know about.</summary>
    Warning,

    /// <summary>Broken; <see cref="FeatureState.Fault"/> says why.</summary>
    Failed,
}

/// <summary>
/// Delivery counters a feature contributes to the PONG packet the sender uses to show
/// its own RTT and loss. A feature that does not track loss leaves this at zero.
/// </summary>
public readonly record struct DeliveryStats(long Received, long Lost)
{
    public static DeliveryStats operator +(DeliveryStats a, DeliveryStats b) =>
        new(a.Received + b.Received, a.Lost + b.Lost);
}

/// <summary>
/// Everything a feature is allowed to reach for. Handed in at <see cref="IFeature.Start"/>
/// so a feature never has to know about sockets, keys or the shell.
/// </summary>
public sealed class FeatureContext(
    ReceiverConfig config,
    Action<LogLevel, string> log,
    Action<PacketType, ReadOnlyMemory<byte>> send)
{
    public ReceiverConfig Config { get; } = config;

    /// <summary>Safe to call from any thread.</summary>
    public void Log(LogLevel level, string message) => log(level, message);

    /// <summary>
    /// Seals a payload and sends it to the current peer with <c>direction = 1</c>. Silently
    /// does nothing while no sender is known — a feature must not care.
    /// </summary>
    public void Send(PacketType type, ReadOnlyMemory<byte> payload) => send(type, payload);
}

/// <summary>
/// One capability bolted onto the shared transport: audio, a gamepad, whatever comes next.
///
/// The host knows nothing about any particular feature. It starts the enabled ones, hands
/// each the packet types it asked for, polls <see cref="CaptureState"/> for the UI, and
/// stops them. Adding a third feature is a new class and one registration, with no edits
/// to the first two.
/// </summary>
public interface IFeature : IAsyncDisposable
{
    /// <summary>Stable machine name, e.g. <c>microphone</c>. Used to pair a feature with its UI.</summary>
    string Id { get; }

    /// <summary>Shown to the user, e.g. "Микрофон".</summary>
    string Title { get; }

    /// <summary>
    /// A feature the user can live without. A start failure or a fault in an optional
    /// feature is reported on its own page instead of failing the receiver.
    /// </summary>
    bool IsOptional { get; }

    /// <summary>Packet types this feature wants. Two features must not claim the same type.</summary>
    IReadOnlyList<PacketType> HandledTypes { get; }

    /// <summary>Whether the config asks for this feature at all.</summary>
    bool IsEnabled(ReceiverConfig config);

    /// <summary>
    /// Claims whatever the feature needs. Throwing here is how a feature reports that it
    /// cannot run; the host stops the ones already started and, for a non-optional feature,
    /// fails the whole start.
    /// </summary>
    void Start(FeatureContext context);

    Task StopAsync();

    /// <summary>
    /// One decrypted packet of a type from <see cref="HandledTypes"/>. Called on the socket
    /// thread — anything slow belongs on a thread of the feature's own.
    /// </summary>
    void OnPacket(in Header header, ReadOnlySpan<byte> payload);

    /// <summary>
    /// The sender restarted and everything buffered belongs to a session that is gone.
    /// Called on the socket thread before the first packet of the new session.
    /// </summary>
    void OnSessionReset() { }

    /// <summary>Read on the host's tick, ten times a second. Must not block.</summary>
    FeatureState CaptureState();

    /// <summary>Contribution to the PONG counters. Zero unless the feature tracks loss.</summary>
    DeliveryStats Delivery => default;
}
