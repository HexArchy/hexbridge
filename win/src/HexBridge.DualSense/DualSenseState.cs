namespace HexBridge.DualSense;

/// <summary>What the DualSense feature publishes on every host tick.</summary>
public sealed record DualSenseState : FeatureState
{
    // Driver.
    public bool DriverInstalled { get; init; }
    public string? DriverPath { get; init; }

    /// <summary>What to tell the user when the driver is missing. Never an error message.</summary>
    public string DriverHint { get; init; } = UsbIpAttacher.InstallHint;

    // USB/IP server.
    public bool ServerRunning { get; init; }
    public string ServerListen { get; init; } = "";

    /// <summary>A usbip client has a socket open — listing the device counts.</summary>
    public bool ClientConnected { get; init; }

    /// <summary>The virtual device is imported and URBs are flowing.</summary>
    public bool Imported { get; init; }

    /// <summary>vhci port the controller is plugged into, when we attached it ourselves.</summary>
    public int? VhciPort { get; init; }

    // Controller.
    public bool Attached { get; init; }
    public string? Product { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string? BusId { get; init; }
    public string? Battery { get; init; }

    /// <summary>Input reports per second off the wire; a wired DualSense sends 250.</summary>
    public double ReportsPerSecond { get; init; }

    public long ReportsReceived { get; init; }
    public long ReportsLost { get; init; }
    public long OutputsSent { get; init; }

    /// <summary>Reports thrown away because Windows was not collecting them fast enough.</summary>
    public long ReportsDropped { get; init; }

    /// <summary>
    /// Live input, for the visualisation on the DualSense screen. A reference rather than
    /// a value: the screen polls it at its own frame rate instead of being limited to the
    /// ten snapshots a second the rest of this record is built from. Null when no
    /// controller is attached.
    /// </summary>
    public DualSenseInputSource? Input { get; init; }

    public string Identity => VendorId == 0 ? "—" : $"{VendorId:X4}:{ProductId:X4}";
}
