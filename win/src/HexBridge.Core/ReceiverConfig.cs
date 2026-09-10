using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using HexBridge.Localization;

namespace HexBridge;

/// <summary>
/// On-disk settings, shared by the console receiver and the desktop app. The JSON shape is
/// part of the deployed product — a config written by an older build must keep working, so
/// property names and defaults here are frozen.
/// </summary>
public sealed class ReceiverConfig
{
    /// <summary>
    /// Which end of the link this machine is. Absent from every config written before roles
    /// existed, and <see cref="BridgeRole.Receiver"/> is the first enum value, so those
    /// configs keep doing exactly what they were written to do.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeRole Role { get; set; } = BridgeRole.Receiver;

    public string Listen { get; set; } = "0.0.0.0:47702";

    /// <summary>
    /// <c>host:port</c> of the machine that receives, read only in
    /// <see cref="BridgeRole.Sender"/>. In the receiving role the peer is whoever turns up
    /// on <see cref="Listen"/>, so this stays empty.
    /// </summary>
    public string Target { get; set; } = "";

    public string Psk { get; set; } = "";
    public string? Device { get; set; }
    public string? Relay { get; set; }
    public string Output { get; set; } = "wasapi";

    /// <summary>
    /// Where the sending role reads audio from: <c>wasapi</c>, <c>null</c>, <c>tone</c> or
    /// <c>wav:путь</c>. The last three exist for the same reason <see cref="Output"/> has
    /// them — proving the whole path without a sound card anywhere in it.
    /// </summary>
    public string Input { get; set; } = "wasapi";

    /// <summary>Part of a capture endpoint's name, or its id. Null means the system default.</summary>
    public string? InputDevice { get; set; }

    /// <summary>Opus bitrate for the voice stream. 32 kbit/s is what the Mac sends.</summary>
    public int Bitrate { get; set; } = 32000;

    /// <summary>Multiplier applied to captured audio before it is encoded.</summary>
    public float InputGain { get; set; } = 1.0f;

    /// <summary>What the encoder is told to expect, which is what inband FEC is sized for.</summary>
    public int ExpectedLossPercent { get; set; } = 10;

    /// <summary>
    /// Come up with the microphone muted. The flag rides on every AUDIO and HELLO packet, so
    /// the far end says «заглушен» instead of wondering where the sound went.
    /// </summary>
    public bool StartMuted { get; set; }

    public int JitterMs { get; set; } = 60;
    public int MaxJitterMs { get; set; } = 240;
    public float Gain { get; set; } = 1.0f;
    public int LatencyMs { get; set; } = 50;

    /// <summary>
    /// Accept HID devices from the Mac and present them as virtual USB devices. The JSON
    /// name is frozen: a config written by an older build must keep working.
    /// </summary>
    public bool Gamepad { get; set; } = true;

    /// <summary>
    /// Share the clipboard with the Mac in both directions. Off in a fresh config and it
    /// stays off until somebody asks: the clipboard holds passwords, and this sends whatever
    /// is on it to another machine.
    /// </summary>
    public bool Clipboard { get; set; }

    /// <summary>
    /// HD haptics: serve the controller's audio function alongside its HID interface, so the
    /// PCM a game writes to the voice-coil actuators reaches the Mac.
    ///
    /// Off in a fresh config, and separate from <see cref="Gamepad"/> on purpose. Adaptive
    /// triggers, rumble and lighting travel as HID output reports and need none of this;
    /// haptics need an isochronous endpoint, which means presenting a composite device, which
    /// means Windows loading usbaudio.sys on top of usbip-win2's vhci. That driver has an open
    /// bug in the lifetime of a request on exactly that path — issue #181, a bugcheck when an
    /// audio pin is closed. Anyone who hits it has to be able to keep the controller and drop
    /// the haptics, and with this off not one byte of the audio path is reachable.
    /// </summary>
    public bool Haptics { get; set; }

    /// <summary>
    /// Where the USB/IP server listens. usbip-win2 dials 3240 by default and the vhci
    /// driver runs on this machine, so there is no reason to open this beyond loopback.
    /// </summary>
    public string UsbIpListen { get; set; } = "127.0.0.1:3240";

    /// <summary>Run <c>usbip.exe attach</c> once the virtual device is assembled.</summary>
    public bool UsbIpAutoAttach { get; set; } = true;

    /// <summary>Full path to usbip.exe, when it is not in the usual place or on PATH.</summary>
    public string? UsbIpPath { get; set; }

    /// <summary>Config lives next to the executable so a portable copy carries its own settings.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static ReceiverConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new ReceiverConfig();
        return JsonSerializer.Deserialize<ReceiverConfig>(File.ReadAllText(path)) ?? new ReceiverConfig();
    }

    /// <summary>
    /// Writes through a temp file: a half-written config.json would leave the receiver
    /// unable to start at the next logon.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(this, SaveOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ReceiverConfig Clone() => (ReceiverConfig)MemberwiseClone();

    /// <summary>Decodes the shared key, or explains why it cannot be used.</summary>
    public bool TryGetKey(out byte[] key, out string? error)
    {
        try
        {
            key = Convert.FromBase64String(Psk);
            if (key.Length != 32) throw new FormatException();
            error = null;
            return true;
        }
        catch (FormatException)
        {
            key = [];
            error = Strings.Err_Psk;
            return false;
        }
    }

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Where the sending role dials. A relay wins over a direct address when both are set —
    /// that is the whole point of configuring one — and otherwise it is <see cref="Target"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing to dial, worded for the user rather than for a log.
    /// </exception>
    public IPEndPoint ResolvePeer()
    {
        var address = string.IsNullOrWhiteSpace(Relay) ? Target : Relay;
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException(
                Strings.Err_NoTarget);
        }

        try
        {
            return ParseEndpoint(address, PairingPayload.DefaultPort);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Loc.F(Strings.Err_BadAddress, address, ex.Message), ex);
        }
    }

    /// <summary>
    /// Accepts "host:port", ":port", "*:port" and bare hosts, resolving names to IPv4 —
    /// the socket is bound as InterNetwork.
    /// </summary>
    public static IPEndPoint ParseEndpoint(string value, int defaultPort)
    {
        if (IPEndPoint.TryParse(value, out var parsed))
        {
            return parsed.Port != 0 ? parsed : new IPEndPoint(parsed.Address, defaultPort);
        }

        var parts = value.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort;

        if (host is "" or "*") return new IPEndPoint(IPAddress.Any, port);

        var resolved = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(resolved, port);
    }
}
