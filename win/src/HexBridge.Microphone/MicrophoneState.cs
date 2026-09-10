namespace HexBridge.Microphone;

/// <summary>What the microphone feature publishes on every host tick.</summary>
public sealed record MicrophoneState : FeatureState
{
    public string OutputDescription { get; init; } = "";

    /// <summary>Render endpoint we play into, and the capture endpoint to pick in games.</summary>
    public string? DeviceName { get; init; }
    public string? PairedCaptureName { get; init; }

    /// <summary>Audio packets per second, which is not the same as packets on the socket.</summary>
    public double PacketsPerSecond { get; init; }

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
