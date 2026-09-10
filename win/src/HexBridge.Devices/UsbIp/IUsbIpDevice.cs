namespace HexBridge.Devices;

/// <summary>The outcome of a control transfer: a Linux URB status and whatever data came back.</summary>
public readonly record struct UsbControlResult(int Status, byte[] Data)
{
    public static UsbControlResult Ok(byte[]? data = null) => new(UsbIpProtocol.StatusSuccess, data ?? []);

    /// <summary>What a real device does with a request it does not implement.</summary>
    public static UsbControlResult Stall() => new(UsbIpProtocol.StatusStall, []);
}

/// <summary>
/// What the USB/IP server needs from whatever it is exporting. Keeping this an interface is
/// what lets the whole server be tested on a machine that has never seen a DualSense.
/// </summary>
public interface IUsbIpDevice
{
    UsbIpDeviceInfo Info { get; }

    /// <summary>Endpoint addresses with the direction bit, e.g. 0x84 and 0x03.</summary>
    int InterruptInEndpoint { get; }
    int InterruptOutEndpoint { get; }

    /// <summary>
    /// A setup packet on EP0. <paramref name="data"/> is the OUT stage, empty for an IN
    /// request; <paramref name="requestedLength"/> is the host's transfer_buffer_length,
    /// which the answer must never exceed.
    /// </summary>
    UsbControlResult Control(ReadOnlySpan<byte> setup, ReadOnlySpan<byte> data, int requestedLength);

    /// <summary>
    /// The next HID input report. Completes only when there is one — an interrupt IN URB
    /// with nothing to say is supposed to sit there, not to return empty.
    /// </summary>
    ValueTask<byte[]> ReadInterruptAsync(CancellationToken token);

    /// <summary>A HID output report written by Windows.</summary>
    void WriteInterrupt(ReadOnlySpan<byte> data);
}
