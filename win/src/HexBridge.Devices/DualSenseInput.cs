using HexBridge.Localization;

namespace HexBridge.Devices;

// Everything in this file is DualSense-specific and nothing else in the project depends on
// it being right. The passthrough forwards bytes; this is the one place that claims to know
// what a byte means, and it is reached only through DeviceProfile, keyed by VID/PID.

/// <summary>The fifteen digital buttons of a DualSense, as one bitmask.</summary>
[Flags]
public enum PadButtons
{
    None = 0,
    Square = 1 << 0,
    Cross = 1 << 1,
    Circle = 1 << 2,
    Triangle = 1 << 3,
    L1 = 1 << 4,
    R1 = 1 << 5,
    L2 = 1 << 6,
    R2 = 1 << 7,
    Create = 1 << 8,
    Options = 1 << 9,
    L3 = 1 << 10,
    R3 = 1 << 11,
    Home = 1 << 12,
    TouchpadClick = 1 << 13,
    Mute = 1 << 14,
}

/// <summary>
/// The eight hat positions plus «centred», in the order the controller reports them:
/// 0 is up and the value walks clockwise.
/// </summary>
public enum PadHat
{
    Up = 0,
    UpRight = 1,
    Right = 2,
    DownRight = 3,
    Down = 4,
    DownLeft = 5,
    Left = 6,
    UpLeft = 7,
    Centre = 8,
}

/// <summary>One finger on the touchpad. The pad reports 1920 × 1080 logical units.</summary>
public readonly record struct PadTouch(bool Active, int X, int Y)
{
    public const int Width = 1920;
    public const int Height = 1080;

    public double NormalisedX => Math.Clamp(X / (double)(Width - 1), 0, 1);
    public double NormalisedY => Math.Clamp(Y / (double)(Height - 1), 0, 1);
}

/// <summary>
/// A decoded HID input report, ready for the visualisation to draw.
///
/// Decoding happens on whichever thread asks for it, not on the network thread: the
/// controller sends 250 reports a second and the screen consumes at most 60, so parsing
/// on arrival would throw four fifths of the work away. <see cref="HidInputSource"/>
/// hands over the raw array; this type turns it into pixels' worth of meaning.
/// </summary>
public readonly record struct DualSenseInput
{
    /// <summary>Sticks, 0…255 with 128 nominally centred, exactly as reported.</summary>
    public byte LeftX { get; init; }
    public byte LeftY { get; init; }
    public byte RightX { get; init; }
    public byte RightY { get; init; }

    /// <summary>Trigger travel, 0…255.</summary>
    public byte L2 { get; init; }
    public byte R2 { get; init; }

    public PadButtons Buttons { get; init; }
    public PadHat Hat { get; init; }

    public PadTouch Touch0 { get; init; }
    public PadTouch Touch1 { get; init; }

    /// <summary>Angular rate, roughly ±32767. Only Z drives the visualisation.</summary>
    public short GyroX { get; init; }
    public short GyroY { get; init; }
    public short GyroZ { get; init; }

    public short AccelX { get; init; }
    public short AccelY { get; init; }
    public short AccelZ { get; init; }

    /// <summary>0…100, or null before a report has been seen.</summary>
    public int? BatteryPercent { get; init; }

    public bool IsCharging { get; init; }

    /// <summary>The controller's own report counter, used to notice a dropped frame.</summary>
    public byte Sequence { get; init; }

    public bool IsPressed(PadButtons button) => (Buttons & button) != 0;

    /// <summary>−1…+1, with the reported centre folded to exactly zero.</summary>
    public static double Axis(byte raw) => Math.Clamp((raw - 127.5) / 127.5, -1, 1);

    /// <summary>0…1 trigger travel.</summary>
    public static double Travel(byte raw) => raw / 255.0;

    /// <summary>
    /// Reads the wired input report. Returns the default value for anything that is not
    /// a 64-byte report 0x01 — Bluetooth reports have a different id and layout, and the
    /// Mac side only ever forwards the wired one.
    /// </summary>
    public static DualSenseInput Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 54 || report[0] != 0x01) return default;

        var buttons0 = report[8];
        var buttons1 = report[9];
        var buttons2 = report[10];

        var buttons = PadButtons.None;
        if ((buttons0 & 0x10) != 0) buttons |= PadButtons.Square;
        if ((buttons0 & 0x20) != 0) buttons |= PadButtons.Cross;
        if ((buttons0 & 0x40) != 0) buttons |= PadButtons.Circle;
        if ((buttons0 & 0x80) != 0) buttons |= PadButtons.Triangle;
        if ((buttons1 & 0x01) != 0) buttons |= PadButtons.L1;
        if ((buttons1 & 0x02) != 0) buttons |= PadButtons.R1;
        if ((buttons1 & 0x04) != 0) buttons |= PadButtons.L2;
        if ((buttons1 & 0x08) != 0) buttons |= PadButtons.R2;
        if ((buttons1 & 0x10) != 0) buttons |= PadButtons.Create;
        if ((buttons1 & 0x20) != 0) buttons |= PadButtons.Options;
        if ((buttons1 & 0x40) != 0) buttons |= PadButtons.L3;
        if ((buttons1 & 0x80) != 0) buttons |= PadButtons.R3;
        if ((buttons2 & 0x01) != 0) buttons |= PadButtons.Home;
        if ((buttons2 & 0x02) != 0) buttons |= PadButtons.TouchpadClick;
        if ((buttons2 & 0x04) != 0) buttons |= PadButtons.Mute;

        var hatRaw = buttons0 & 0x0F;
        var hat = hatRaw <= 7 ? (PadHat)hatRaw : PadHat.Centre;

        var battery = report[53] & 0x0F;
        var chargeState = (report[53] >> 4) & 0x0F;

        return new DualSenseInput
        {
            LeftX = report[1],
            LeftY = report[2],
            RightX = report[3],
            RightY = report[4],
            L2 = report[5],
            R2 = report[6],
            Sequence = report[7],
            Buttons = buttons,
            Hat = hat,
            GyroX = Int16(report, 16),
            GyroY = Int16(report, 18),
            GyroZ = Int16(report, 20),
            AccelX = Int16(report, 22),
            AccelY = Int16(report, 24),
            AccelZ = Int16(report, 26),
            Touch0 = Touch(report, 33),
            Touch1 = Touch(report, 37),
            // The level nibble counts in tenths; the mid-point of the bucket is a
            // fairer thing to show than its floor.
            BatteryPercent = Math.Min(battery * 10 + 5, 100),
            IsCharging = chargeState == 0x1,
        };
    }

    /// <summary>
    /// Battery as one input report describes it: low nibble of byte 53 is the level, high
    /// nibble the charging state.
    /// </summary>
    public static string? DescribeBattery(ReadOnlySpan<byte> report)
    {
        if (report.Length < 54 || report[0] != 0x01) return null;

        var level = report[53] & 0x0F;
        var status = (report[53] >> 4) & 0x0F;
        var percent = Math.Min(level * 10 + 5, 100);

        return status switch
        {
            0x0 => $"{percent}%",
            0x1 => Loc.F(Strings.Devices_Battery_Charging, percent),
            0x2 => Strings.Devices_Battery_Full,
            _ => Loc.F(Strings.Devices_Battery_Status, status.ToString("x", System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private static short Int16(ReadOnlySpan<byte> report, int offset) =>
        offset + 1 < report.Length
            ? (short)(report[offset] | (report[offset + 1] << 8))
            : (short)0;

    /// <summary>
    /// Four bytes per finger: an id whose top bit means «not touching», then two
    /// twelve-bit coordinates packed across the remaining three.
    /// </summary>
    private static PadTouch Touch(ReadOnlySpan<byte> report, int offset)
    {
        if (offset + 3 >= report.Length) return default;

        var active = (report[offset] & 0x80) == 0;
        var x = report[offset + 1] | ((report[offset + 2] & 0x0F) << 8);
        var y = (report[offset + 2] >> 4) | (report[offset + 3] << 4);
        return new PadTouch(active, x, y);
    }
}

/// <summary>
/// The hand-off from the network thread to the screen, and the reason the visualisation
/// can run at 60 Hz without touching the hot path.
///
/// Model-agnostic on purpose: it publishes a raw report and nothing more. Whether that
/// report means anything to anybody is <see cref="DeviceProfile"/>'s problem.
///
/// The HID thread publishes a reference to the report array it already owns — one
/// volatile write, no allocation, no lock, last writer wins. The renderer reads that
/// reference on its own frame and decodes it there. A reader that falls behind loses
/// intermediate reports, which is correct: a gamepad report is absolute state, and the
/// stale ones are worth nothing.
/// </summary>
public sealed class HidInputSource
{
    private byte[]? _input;
    private byte[]? _output;
    private long _version;

    /// <summary>Bumped on every published report, so a renderer can skip an idle frame.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Called from the socket thread at up to 250 Hz.</summary>
    public void Publish(byte[] report)
    {
        Volatile.Write(ref _input, report);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// The last output report Windows sent. Its only use here is the lightbar colour,
    /// which is the one piece of controller state the PC decides rather than reports.
    /// </summary>
    public void PublishOutput(byte[] report) => Volatile.Write(ref _output, report);

    public DualSenseInput Read() => DualSenseInput.Parse(Volatile.Read(ref _input) ?? []);

    /// <summary>
    /// Lightbar colour from output report 0x02, or null when the PC has not set one.
    /// Bytes 0x2C…0x2E carry red, green and blue.
    /// </summary>
    public (byte R, byte G, byte B)? Lightbar
    {
        get
        {
            var report = Volatile.Read(ref _output);
            if (report is null || report.Length < 47 || report[0] != 0x02) return null;

            var (r, g, b) = (report[0x2C], report[0x2D], report[0x2E]);
            return r == 0 && g == 0 && b == 0 ? null : (r, g, b);
        }
    }
}
