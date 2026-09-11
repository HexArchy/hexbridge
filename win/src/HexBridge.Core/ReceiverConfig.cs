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
    /// Send and take files, in both directions. **On**, unlike the clipboard.
    ///
    /// <para>
    /// The clipboard is off because it sends your own data out continuously and unasked,
    /// and what sits on a clipboard is often a password. Files go the other way: one
    /// leaves only because somebody dropped it, and one that arrives is written to
    /// Downloads and never opened. That is a smaller footprint than the microphone, which
    /// is on, and the two machines are already holding one key between them.
    /// </para>
    ///
    /// <para>
    /// It is also the difference between the feature working and appearing broken. Off on
    /// one of the two machines means a file dropped on the other goes nowhere, and the
    /// person is left looking for a switch on a screen that is not in front of them.
    /// </para>
    /// </summary>
    public bool Files { get; set; } = true;

    /// <summary>
    /// How fast files and clipboard objects may be sent, in chunks a second — a chunk being
    /// the 1024 bytes docs/PROTOCOL.md fixes, so the number is also kibibytes a second.
    /// Zero — which is what a config that was never asked says — means no ceiling of ours.
    ///
    /// <para>
    /// No ceiling does not mean as fast as a loop can spin. HexBridge speeds up while the
    /// other machine is getting everything and slows down as soon as it starts asking for
    /// chunks a second time, so an unset limit settles on what the link actually carries.
    /// The setting is for the link this program cannot see into: a connection somebody pays
    /// for by the gigabyte, or a household that notices when one machine takes it all.
    /// </para>
    ///
    /// <para>
    /// The name is the Mac's, which calls the same setting <c>sendRate</c> and counts it in
    /// the same units. One idea, one word — the two configs are separate files, but the
    /// person changing one of them is the same person.
    /// </para>
    /// </summary>
    public int SendRate { get; set; }

    /// <summary>
    /// What the relay carries from one address before it starts dropping, in packets per
    /// second. Zero means the number in docs/PROTOCOL.md, which is 2000.
    ///
    /// <para>
    /// Read only when a relay is configured, and only to keep transfers underneath it: what
    /// a relay drops comes back as a hole and is sent again, so aiming above its limit makes
    /// a transfer slower rather than faster. A relay started with a higher limit than the
    /// contract's is worth saying so here — this end has no way to ask it.
    /// </para>
    /// </summary>
    public int RelayPacketsPerSecond { get; set; }

    /// <summary>
    /// HD haptics: serve the controller's audio function alongside its HID interface, so the
    /// PCM a game writes to the voice-coil actuators reaches the Mac.
    ///
    /// On, and separate from <see cref="Gamepad"/> on purpose. Adaptive triggers, rumble and
    /// lighting travel as HID output reports and need none of this; haptics need an
    /// isochronous endpoint, which means presenting a composite device, which means Windows
    /// loading usbaudio.sys on top of usbip-win2's vhci.
    ///
    /// <para>
    /// It was off for a long time because that path could bugcheck the machine — usbip-win2
    /// issue #181, a crash when an audio pin closes. The fixes for it are merged and shipped
    /// in 0.9.8.0, which is the version we require. The issue is still open and somebody was
    /// still reproducing it weeks after those fixes landed, so being on by default is backed
    /// by <see cref="HexBridge.Devices.HapticsGuard"/>: a run that goes down while the audio
    /// function is presented costs the next run its haptics, and nothing else.
    /// </para>
    ///
    /// <para>
    /// Turning it off still removes the audio path entirely — not one byte of it is
    /// reachable — so anyone who does hit a crash can keep the controller and drop the
    /// haptics.
    /// </para>
    /// </summary>
    public bool Haptics { get; set; } = true;

    /// <summary>
    /// Where the USB/IP server listens. usbip-win2 dials 3240 by default and the vhci
    /// driver runs on this machine, so there is no reason to open this beyond loopback.
    /// </summary>
    public string UsbIpListen { get; set; } = "127.0.0.1:3240";

    /// <summary>Run <c>usbip.exe attach</c> once the virtual device is assembled.</summary>
    public bool UsbIpAutoAttach { get; set; } = true;

    /// <summary>Full path to usbip.exe, when it is not in the usual place or on PATH.</summary>
    public string? UsbIpPath { get; set; }

    /// <summary>
    /// Where settings live: beside the user's other application data, not beside the
    /// executable.
    ///
    /// <para>
    /// It used to be the executable's own folder, which is tidy for a portable copy and
    /// wrong for an installed one. The Setup installer puts each version in a folder of
    /// its own and swaps them on update, so a config living there went away with the
    /// version that wrote it — and the pairing key went with it. Somebody who updates is
    /// then asked to pair again, which is the one piece of setup nobody wants to repeat.
    /// </para>
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexBridge", "config.json");

    /// <summary>
    /// Places an older version, or an older name, may have left a config.
    ///
    /// Ordered by how likely each is to be the one in use. `MicBridge` is what this was
    /// called before, and an install from those days still holds a working key.
    /// </summary>
    private static IEnumerable<string> LegacyPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(AppContext.BaseDirectory, "config.json");
        yield return Path.Combine(local, "HexBridge", "config.json");
        yield return Path.Combine(local, "MicBridge", "config.json");
        yield return @"C:\HexBridge-Windows\config.json";
        yield return @"C:\MicBridge-Windows\config.json";
    }

    /// <summary>
    /// Brings an older config forward, once, and answers where to read from.
    ///
    /// Copies rather than moves, and never over the top of one that is already there. A
    /// migration that loses the file it was migrating is worse than no migration, and the
    /// old copy costs nothing to leave where it lies.
    /// </summary>
    /// <summary>
    /// The path to actually use, having first brought an older config forward.
    ///
    /// <para>
    /// Everything that needs the config asks for this rather than <see cref="DefaultPath"/>.
    /// Doing the migration inside <see cref="Load"/> was not enough: every caller passes an
    /// explicit path — the console receiver computes it, the app takes it from the command
    /// line — so the migration never ran, and an upgrade started from an empty config with
    /// the key sitting untouched in the old folder. That is the exact failure this was
    /// written to prevent, and it took a run on a real machine to notice.
    /// </para>
    /// </summary>
    public static string ResolvedPath() => Resolve(DefaultPath, LegacyPaths());

    /// <summary>
    /// The same decision against paths a test can hand in, because the real one writes to
    /// the user's own application data and a test must not.
    /// </summary>
    internal static string Resolve(string wanted, IEnumerable<string> legacyPaths)
    {
        if (File.Exists(wanted)) return wanted;

        foreach (var legacy in legacyPaths)
        {
            try
            {
                if (!File.Exists(legacy)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(wanted)!);
                File.Copy(legacy, wanted, overwrite: false);
                return wanted;
            }
            catch (Exception)
            {
                // Unreadable, or a race with another copy of us that got there first.
                // Either way the file it was going to read is still readable in place.
                return legacy;
            }
        }

        return wanted;
    }

    public static ReceiverConfig Load(string? path = null)
    {
        path ??= ResolvedPath();
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
