namespace HexBridge.Devices;

/// <summary>One forwarded device, as the UI sees it.</summary>
public sealed record ForwardedDeviceState
{
    /// <summary>Device number on the wire, 0…3. The only thing that identifies it.</summary>
    public byte Number { get; init; }

    /// <summary>Virtual bus id, <c>1-1</c>…<c>1-4</c>, one per device number.</summary>
    public string BusId { get; init; } = "";

    public string Product { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }

    /// <summary>Model name when the device was recognised; null for the ordinary case.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Whether anything beyond a name and a rate can honestly be drawn.</summary>
    public bool CanVisualise { get; init; }

    /// <summary>Null unless the model has a battery byte we know how to find.</summary>
    public string? Battery { get; init; }

    /// <summary>Input reports per second off the wire; a wired DualSense sends 250.</summary>
    public double ReportsPerSecond { get; init; }

    public long ReportsReceived { get; init; }
    public long ReportsLost { get; init; }
    public long OutputsSent { get; init; }

    /// <summary>Reports thrown away because Windows was not collecting them fast enough.</summary>
    public long ReportsDropped { get; init; }

    /// <summary>This device's own busid is imported and URBs are flowing.</summary>
    public bool Imported { get; init; }

    /// <summary>vhci port, when we attached this one ourselves.</summary>
    public int? VhciPort { get; init; }

    /// <summary>
    /// Live input for the visualisation. A reference rather than a value: the screen polls
    /// it at its own frame rate instead of being pinned to the ten snapshots a second the
    /// rest of this record is built from.
    /// </summary>
    public HidInputSource? Input { get; init; }

    public string Identity => $"{VendorId:X4}:{ProductId:X4}";
}

/// <summary>What the device feature publishes on every host tick.</summary>
public sealed record DevicesState : FeatureState
{
    // Driver.
    public bool DriverInstalled { get; init; }
    public string? DriverPath { get; init; }

    /// <summary>What to tell the user when the driver is missing. Never an error message.</summary>
    public string DriverHint { get; init; } = UsbIpAttacher.InstallHint;

    // USB/IP server.
    public bool ServerRunning { get; init; }
    public string ServerListen { get; init; } = "";

    /// <summary>A usbip client has a socket open — listing the devices counts.</summary>
    public bool ClientConnected { get; init; }

    /// <summary>Up to four, in device-number order.</summary>
    public IReadOnlyList<ForwardedDeviceState> Devices { get; init; } = [];

    /// <summary>The protocol's ceiling, so the UI never has to hard-code it.</summary>
    public int MaxDevices { get; init; } = DevicesFeature.MaxDevices;

    public bool Attached => Devices.Count > 0;

    /// <summary>True while every forwarded device is actually plugged into vhci.</summary>
    public bool AllImported => Devices.Count > 0 && Devices.All(d => d.Imported);
}
