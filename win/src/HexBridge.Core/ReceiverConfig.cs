using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexBridge;

/// <summary>
/// On-disk settings, shared by the console receiver and the desktop app. The JSON shape is
/// part of the deployed product — a config written by an older build must keep working, so
/// property names and defaults here are frozen.
/// </summary>
public sealed class ReceiverConfig
{
    public string Listen { get; set; } = "0.0.0.0:47702";
    public string Psk { get; set; } = "";
    public string? Device { get; set; }
    public string? Relay { get; set; }
    public string Output { get; set; } = "wasapi";
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
            error = "psk должен быть 32 байта в base64";
            return false;
        }
    }

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
