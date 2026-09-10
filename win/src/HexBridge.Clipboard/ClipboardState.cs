namespace HexBridge.Clipboard;

/// <summary>What the clipboard feature publishes on every host tick.</summary>
public sealed record ClipboardState : FeatureState
{
    /// <summary>The clipboard could be opened at least once. False on a machine that has none.</summary>
    public bool Ready { get; init; }

    public long Sent { get; init; }
    public long Received { get; init; }

    /// <summary>Shape and size of the last object that crossed, never its content.</summary>
    public string? LastDescription { get; init; }
    public BulkDirection? LastDirection { get; init; }
    public DateTime? LastAt { get; init; }

    /// <summary>Non-null while something is on the wire.</summary>
    public string? TransferDescription { get; init; }
    public BulkDirection? TransferDirection { get; init; }

    /// <summary>0…1. Only meaningful while <see cref="TransferDescription"/> is set.</summary>
    public double Progress { get; init; }
}
