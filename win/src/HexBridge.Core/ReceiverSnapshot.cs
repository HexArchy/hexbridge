namespace HexBridge;

public enum ReceiverStatus
{
    /// <summary>Not started, or stopped on request.</summary>
    Stopped,

    /// <summary>The socket is open and the features are running, but no sender has been heard yet.</summary>
    WaitingForSender,

    /// <summary>Packets are arriving.</summary>
    Live,

    /// <summary>The sender is connected but has muted its microphone.</summary>
    Muted,

    /// <summary>The sender was here and went quiet — packets stopped arriving.</summary>
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

    public string Listen { get; init; } = "";
    public string? Relay { get; init; }

    public string? PeerAddress { get; init; }
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

    /// <summary>Round trip implied by <see cref="OneWayDelayMs"/>. An estimate, not a measurement.</summary>
    public double? RttMs => OneWayDelayMs * 2;

    /// <summary>Per-feature state, keyed by <see cref="IFeature.Id"/>.</summary>
    public IReadOnlyDictionary<string, FeatureState> Features { get; init; } =
        new Dictionary<string, FeatureState>();

    /// <summary>The state a feature published on this tick, typed, or null if it is not running.</summary>
    public T? Feature<T>(string id) where T : FeatureState =>
        Features.TryGetValue(id, out var state) ? state as T : null;

    public bool IsRunning => Status is not (ReceiverStatus.Stopped or ReceiverStatus.Failed);
}
