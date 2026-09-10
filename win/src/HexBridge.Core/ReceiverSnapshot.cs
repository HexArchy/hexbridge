namespace HexBridge;

public enum ReceiverStatus
{
    /// <summary>Not started, or stopped on request.</summary>
    Stopped,

    /// <summary>
    /// The socket is open and the features are running, but the other machine has not been
    /// heard from yet. In <see cref="BridgeRole.Sender"/> that is «no PONG came back» —
    /// the same fact seen from the other end of the wire.
    /// </summary>
    WaitingForSender,

    /// <summary>Packets are arriving.</summary>
    Live,

    /// <summary>
    /// The microphone is muted: as reported by the far end in
    /// <see cref="BridgeRole.Receiver"/>, and our own switch in <see cref="BridgeRole.Sender"/>.
    /// </summary>
    Muted,

    /// <summary>The other machine was here and went quiet — packets stopped arriving.</summary>
    SenderLost,

    /// <summary>Startup or a required feature failed; <see cref="ReceiverSnapshot.Detail"/> says why.</summary>
    Failed,
}

/// <summary>
/// An immutable read of the transport, taken on a timer rather than per packet so a
/// 300 packet/s stream cannot drive 300 redraws a second.
///
/// Everything feature-specific lives in <see cref="Features"/>: this record knows about
/// sockets and senders, never about audio or gamepads.
/// </summary>
public sealed record ReceiverSnapshot
{
    public ReceiverStatus Status { get; init; } = ReceiverStatus.Stopped;

    /// <summary>Human-readable reason for <see cref="ReceiverStatus.Failed"/>, else null.</summary>
    public string? Detail { get; init; }

    /// <summary>Which end of the link this process is running as.</summary>
    public BridgeRole Role { get; init; } = BridgeRole.Receiver;

    /// <summary>
    /// The bound local endpoint. In <see cref="BridgeRole.Sender"/> that is an ephemeral
    /// port the OS chose, which is what lets both roles run on one machine at once.
    /// </summary>
    public string Listen { get; init; } = "";
    public string? Relay { get; init; }

    public string? PeerAddress { get; init; }

    /// <summary>Name the peer put in its HELLO. Named for the sending side for history.</summary>
    public string SenderName { get; init; } = "";
    public uint Session { get; init; }
    public bool Muted { get; init; }
    public DateTime? LastPacketAt { get; init; }

    /// <summary>Time since the receiver was started, not since the sender appeared.</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>Accepted packets of every type, per second.</summary>
    public double PacketsPerSecond { get; init; }

    public long Received { get; init; }
    public long Rejected { get; init; }

    /// <summary>
    /// Estimated one-way network delay, from the wall-clock stamp in the sender's HELLO.
    /// Null when the two clocks disagree enough to make the number meaningless.
    /// </summary>
    public double? OneWayDelayMs { get; init; }

    /// <summary>
    /// Round trip actually measured, from our own HELLO stamp echoed back in a PONG. Only
    /// the sending role ever gets one, because PONG only travels one way.
    /// </summary>
    public double? MeasuredRttMs { get; init; }

    /// <summary>
    /// The measurement when there is one, otherwise the estimate implied by
    /// <see cref="OneWayDelayMs"/>.
    /// </summary>
    public double? RttMs => MeasuredRttMs ?? OneWayDelayMs * 2;

    /// <summary>AUDIO packets the far end says it received. Zero until a PONG arrives.</summary>
    public long RemoteReceived { get; init; }

    /// <summary>AUDIO packets the far end says it lost or had to conceal.</summary>
    public long RemoteLost { get; init; }

    /// <summary>Per-feature state, keyed by <see cref="IFeature.Id"/>.</summary>
    public IReadOnlyDictionary<string, FeatureState> Features { get; init; } =
        new Dictionary<string, FeatureState>();

    /// <summary>The state a feature published on this tick, typed, or null if it is not running.</summary>
    public T? Feature<T>(string id) where T : FeatureState =>
        Features.TryGetValue(id, out var state) ? state as T : null;

    public bool IsRunning => Status is not (ReceiverStatus.Stopped or ReceiverStatus.Failed);
}
