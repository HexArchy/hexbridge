namespace HexBridge.Files;

/// <summary>One file that arrived and is now on this disk.</summary>
public sealed record ArrivedFile(string Name, string Path, long Bytes, DateTime At);

/// <summary>What the files feature publishes on every host tick.</summary>
public sealed record FilesState : FeatureState
{
    /// <summary>Where arriving files are written. Shown so nobody has to guess.</summary>
    public string Folder { get; init; } = "";

    public long Sent { get; init; }
    public long Received { get; init; }

    /// <summary>The most recent arrivals, newest first, oldest forgotten.</summary>
    public IReadOnlyList<ArrivedFile> Arrived { get; init; } = [];

    /// <summary>Non-null while something is on the wire.</summary>
    public string? TransferDescription { get; init; }
    public BulkDirection? TransferDirection { get; init; }

    /// <summary>0…1. Only meaningful while <see cref="TransferDescription"/> is set.</summary>
    public double Progress { get; init; }
}
