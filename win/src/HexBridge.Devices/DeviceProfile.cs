namespace HexBridge.Devices;

/// <summary>
/// Extra knowledge about one model, switched on by VID/PID.
///
/// The device channel carries none of this: DEV_ATTACH ships the real descriptors and
/// DEV_IN/DEV_OUT ship reports as they came, so a device with no profile is forwarded
/// exactly as well as one with. A profile buys three things a generic HID passthrough
/// cannot invent — which feature reports a game refuses to start without, what a byte of
/// an input report means, and a name worth showing in Device Manager.
/// </summary>
public sealed record DeviceProfile
{
    public required string Name { get; init; }

    /// <summary>Whose reports the on-screen visualisation knows how to decode.</summary>
    public bool CanVisualise { get; init; }

    /// <summary>
    /// Feature reports the receiver must be able to answer GET_REPORT with. A missing one
    /// is worth saying out loud: zeros in the DualSense calibration report are a divide by
    /// zero inside games.
    /// </summary>
    public IReadOnlyList<byte> RequiredFeatureReports { get; init; } = [];

    /// <summary>Battery as one input report describes it, or null when it does not.</summary>
    public Func<byte[], string?>? Battery { get; init; }

    /// <summary>Shown when the device's own string descriptors are not available.</summary>
    public string Manufacturer { get; init; } = "HexBridge";

    public static readonly DeviceProfile DualSense = new()
    {
        Name = "Wireless Controller",
        CanVisualise = true,
        RequiredFeatureReports = [0x05, 0x09, 0x20],
        Battery = report => DualSenseInput.DescribeBattery(report),
        Manufacturer = "Sony Interactive Entertainment",
    };

    /// <summary>Sony Interactive Entertainment.</summary>
    public const ushort DualSenseVendorId = 0x054C;

    /// <summary>DualSense (CFI-ZCT1) and DualSense Edge (CFI-ZCP1) share the report layout.</summary>
    public static bool IsDualSenseProduct(ushort productId) => productId is 0x0CE6 or 0x0DF2;

    /// <summary>The profile for a model, or null — and null is the ordinary case.</summary>
    public static DeviceProfile? Of(ushort vendorId, ushort productId) =>
        vendorId == DualSenseVendorId && IsDualSenseProduct(productId) ? DualSense : null;
}
