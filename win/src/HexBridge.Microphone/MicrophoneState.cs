namespace HexBridge.Microphone;

/// <summary>
/// What the microphone publishes on every host tick — from whichever end of the link this
/// machine is.
///
/// <para>
/// One record for both halves, because almost everything on it is the same question asked
/// twice: which endpoint, how loud, how many packets, how much was lost. The handful of
/// fields that belong to one side only say so, and <see cref="IsCapture"/> is how a page
/// knows which set it is looking at. Two records would have meant two of every page.
/// </para>
/// </summary>
public sealed record MicrophoneState : FeatureState
{
    /// <summary>
    /// True when this is the machine holding the microphone rather than the one playing it.
    /// A level meter reads «сколько мы слышим» on one side and «сколько мы говорим» on the
    /// other, and something has to say which.
    /// </summary>
    public bool IsCapture { get; init; }

    /// <summary>
    /// The audio endpoint in words: the render device being played into, or the capture
    /// device being read from, according to <see cref="IsCapture"/>.
    /// </summary>
    public string OutputDescription { get; init; } = "";

    /// <summary>Endpoint name, and — playing side only — the microphone to pick in games.</summary>
    public string? DeviceName { get; init; }
    public string? PairedCaptureName { get; init; }

    /// <summary>Audio packets per second, which is not the same as packets on the socket.</summary>
    public double PacketsPerSecond { get; init; }

    /// <summary>Frames put on the wire. Capture side only.</summary>
    public long Sent { get; init; }

    /// <summary>Our own microphone is muted. Capture side only.</summary>
    public bool IsMuted { get; init; }

    /// <summary>Opus bitrate in use. Capture side only.</summary>
    public int Bitrate { get; init; }

    /// <summary>Size of the last Opus packet produced, which is what the bitrate buys.</summary>
    public int LastPacketBytes { get; init; }

    public long Received { get; init; }
    public long Decoded { get; init; }
    public long Concealed { get; init; }
    public long DroppedLate { get; init; }
    public long Underruns { get; init; }

    /// <summary>Linear peak of the most recent frame, 0..1.</summary>
    public float Peak { get; init; }

    /// <summary>Highest peak since the previous snapshot, so a 10 Hz meter cannot miss a transient.</summary>
    public float PeakHold { get; init; }

    public int Depth { get; init; }
    public int TargetDepth { get; init; }
    public int MaxDepth { get; init; }

    public static float ToDbfs(float linear) => linear > 0 ? 20f * MathF.Log10(linear) : -99f;
}
