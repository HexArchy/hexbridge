using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using HexBridge.Localization;

namespace HexBridge;

/// <summary>
/// Everything the Mac needs to talk to this PC, in one transferable string.
///
/// The key is generated <b>here</b>, on Windows, and not on the Mac. Windows is the side
/// that listens: it is the only machine that knows its own address and port, and the
/// address has to travel together with the key or the user is back to editing two config
/// files by hand. See DESIGN.md §9.1.
///
/// The payload is deliberately ASCII-only. Apple's QR generator is documented against
/// ISO-Latin-1 in one place and ASCII in another and never mentions UTF-8, so the machine
/// name is transliterated rather than encoded — a mangled name is a cosmetic problem, a
/// mangled key is a support call.
/// </summary>
public sealed record PairingPayload
{
    public const string Scheme = "hexbridge";
    public const string Action = "pair";
    public const int Version = 1;

    /// <summary>The UDP port the receiver listens on when nothing says otherwise.</summary>
    public const int DefaultPort = 47702;

    /// <summary>
    /// Where the short-code exchange answers: one above the audio port. The convention is
    /// fixed by the Mac (<c>Pairing.exchangePort(forDataPort:)</c>), which adds one with
    /// <c>&amp;+</c> — an unchecked wrap — so the last port wraps to zero rather than
    /// throwing. Matched here for the same reason: an out-of-range port must fail as a
    /// refused connection, not as a crash inside the wizard.
    /// </summary>
    public static int ExchangePort(int dataPort) => (dataPort + 1) & 0xFFFF;

    /// <summary>
    /// Where a file crosses when it does not have to be cut into datagrams: two above the
    /// data port, 47704 beside the usual 47702. Wrapped rather than checked for the same
    /// reason <see cref="ExchangePort"/> is — a data port at the very top of the range has
    /// to come out as a connection nobody answers, not as a crash on the way to making one.
    /// </summary>
    public static int FastPathPort(int dataPort) => (dataPort + 2) & 0xFFFF;

    /// <summary>Address the Mac should send to. Never a wildcard — the Mac cannot dial 0.0.0.0.</summary>
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>The 32-byte shared key, raw.</summary>
    public required byte[] Key { get; init; }

    /// <summary>Machine name, shown to the user on the Mac so they can tell two PCs apart.</summary>
    public string Name { get; init; } = "";

    /// <summary>The key the way config.json stores it.</summary>
    public string Psk => Convert.ToBase64String(Key);

    /// <summary>
    /// Four groups of four hex characters, for comparing two machines by eye without
    /// revealing the key itself: <c>A1F2 · 9C40 · 77BE · D103</c>.
    /// </summary>
    public string Fingerprint => FingerprintOf(Key);

    public string ToUri()
    {
        var builder = new StringBuilder(160);
        builder.Append(Scheme).Append("://").Append(Action)
            .Append("?v=").Append(Version)
            .Append("&h=").Append(Uri.EscapeDataString(Host))
            .Append("&p=").Append(Port)
            .Append("&k=").Append(ToBase64Url(Key));

        var name = Ascii(Name);
        if (name.Length > 0) builder.Append("&n=").Append(Uri.EscapeDataString(name));
        return builder.ToString();
    }

    /// <summary>
    /// Reads a payload back. Returns false with a sentence the UI can show rather than
    /// throwing: a mistyped code is an expected input, not an exceptional one.
    /// </summary>
    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out PairingPayload? payload,
        [NotNullWhen(false)] out string? error)
    {
        payload = null;

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = Strings.Err_Code_Empty;
            return false;
        }

        var prefix = $"{Scheme}://{Action}?";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = Loc.F(Strings.Err_Code_NotOurs, Scheme, Action);
            return false;
        }

        var query = ParseQuery(trimmed[prefix.Length..]);

        if (!query.TryGetValue("v", out var version) || !int.TryParse(version, out var parsedVersion))
        {
            error = Strings.Err_Code_NoVersion;
            return false;
        }

        if (parsedVersion != Version)
        {
            error = Loc.F(Strings.Err_Code_Version, parsedVersion, Version);
            return false;
        }

        if (!query.TryGetValue("h", out var host) || host.Length == 0)
        {
            error = Strings.Err_Code_NoHost;
            return false;
        }

        if (!query.TryGetValue("p", out var port) || !int.TryParse(port, out var parsedPort)
            || parsedPort is < 1 or > 65535)
        {
            error = Strings.Err_Code_NoPort;
            return false;
        }

        if (!query.TryGetValue("k", out var key) || !TryFromBase64Url(key, out var parsedKey))
        {
            error = Strings.Err_Code_BadKey;
            return false;
        }

        if (parsedKey.Length != 32)
        {
            error = Loc.F(Strings.Err_Code_KeyLength, Loc.Bytes(parsedKey.Length));
            return false;
        }

        payload = new PairingPayload
        {
            Host = host,
            Port = parsedPort,
            Key = parsedKey,
            Name = query.GetValueOrDefault("n", ""),
        };
        error = null;
        return true;
    }

    /// <summary>Fresh key, this machine's address, this machine's name.</summary>
    public static PairingPayload Create(string host, int port, string name) => new()
    {
        Host = host,
        Port = port,
        Key = RandomNumberGenerator.GetBytes(32),
        Name = name,
    };

    /// <summary>
    /// Eight bytes of a domain-separated hash of the key, in four groups of four hex
    /// characters. Shown so two machines can be compared by eye without either of them
    /// revealing the key (DESIGN.md §10.3).
    ///
    /// <para>
    /// The domain string is part of the contract with the Mac — <c>Pairing.fingerprint</c>
    /// in <c>mac/Sources/HexBridge/Core/Pairing.swift</c> prefixes the same bytes. Hash the
    /// bare key here and the two sides print different fingerprints for the same key, which
    /// is exactly the failure the fingerprint exists to rule out.
    /// </para>
    /// </summary>
    public const string FingerprintDomain = "hexbridge-fingerprint-v1";

    public static string FingerprintOf(byte[] key)
    {
        var domain = Encoding.UTF8.GetBytes(FingerprintDomain);
        var buffer = new byte[domain.Length + key.Length];
        domain.CopyTo(buffer, 0);
        key.CopyTo(buffer, domain.Length);

        var digest = SHA256.HashData(buffer);
        var groups = new string[4];
        for (var i = 0; i < 4; i++) groups[i] = Convert.ToHexString(digest, i * 2, 2);
        return string.Join(" · ", groups);
    }

    /// <summary>Fingerprint of a base64 key from config.json, or null when it cannot be read.</summary>
    public static string? FingerprintOfPsk(string? psk)
    {
        if (string.IsNullOrWhiteSpace(psk)) return null;
        try
        {
            var key = Convert.FromBase64String(psk);
            return key.Length == 32 ? FingerprintOf(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            var name = pair[..equals];
            // Last value wins, which is what every URL parser does.
            result[name] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return result;
    }

    internal static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static bool TryFromBase64Url(string value, out byte[] result)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

        try
        {
            result = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            result = [];
            return false;
        }
    }

    /// <summary>
    /// Cyrillic machine names are common and the payload has to stay ASCII, so they are
    /// transliterated rather than dropped: «Никита-ПК» reads better than «-».
    /// </summary>
    internal static string Ascii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is >= ' ' and <= '~')
            {
                builder.Append(ch);
                continue;
            }

            var index = CyrillicUpper.IndexOf(char.ToUpperInvariant(ch));
            if (index < 0) continue;

            var latin = LatinFor[index];
            builder.Append(char.IsUpper(ch) ? latin : latin.ToLowerInvariant());
        }
        return builder.ToString().Trim();
    }

    private const string CyrillicUpper = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";

    private static readonly string[] LatinFor =
    [
        "A", "B", "V", "G", "D", "E", "E", "Zh", "Z", "I", "Y", "K", "L", "M", "N", "O", "P",
        "R", "S", "T", "U", "F", "Kh", "Ts", "Ch", "Sh", "Shch", "", "Y", "", "E", "Yu", "Ya",
    ];
}

/// <summary>
/// The twelve-character fallback for when there is no camera and autodiscovery did not
/// work: <c>ABCD-EFGH-JKLM</c>. Too short to carry a 32-byte key, so it identifies a
/// short-lived exchange rather than being the secret itself (DESIGN.md §9.1).
///
/// The alphabet has no I, O, 0 or 1 — those are the characters people read off a screen
/// and type wrong.
/// </summary>
public static class ShortCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 12;

    /// <summary>How long a code stays valid before the wizard offers a new one.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    public static string Generate()
    {
        var raw = new char[Length];
        for (var i = 0; i < Length; i++) raw[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(new string(raw));
    }

    /// <summary>
    /// Groups of four separated by hyphens, which is how the code is shown and typed.
    ///
    /// <para>
    /// Partial input is formatted too, and anything past the twelfth character is dropped.
    /// That is not a nicety: the Mac reformats the field on every keystroke
    /// (<c>Pairing.formatCode</c>), so a Windows build that only formatted a complete code
    /// would print <c>ABCDEFGH</c> where the Mac prints <c>ABCD-EFGH</c>, and the user
    /// comparing the two screens would be looking at two different strings.
    /// </para>
    /// </summary>
    public static string Format(string code)
    {
        var bare = Normalise(code);
        if (bare.Length > Length) bare = bare[..Length];

        var builder = new StringBuilder(Length + 2);
        for (var index = 0; index < bare.Length; index++)
        {
            if (index > 0 && index % 4 == 0) builder.Append('-');
            builder.Append(bare[index]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Uppercases and keeps only characters that are in the alphabet, so hyphens, spaces
    /// and a stray paste of the surrounding sentence all wash out. Characters outside the
    /// alphabet are dropped rather than guessed at: silently turning a typed O into a Q
    /// would hand the user a code they never saw.
    /// </summary>
    public static string Normalise(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";

        var builder = new StringBuilder(Length);
        foreach (var raw in code)
        {
            var ch = char.ToUpperInvariant(raw);
            if (Alphabet.Contains(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    public static bool IsComplete(string? code) => Normalise(code).Length == Length;
}
