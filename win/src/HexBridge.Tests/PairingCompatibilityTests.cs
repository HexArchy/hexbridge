using System.Security.Cryptography;
using System.Text;

namespace HexBridge.Tests;

/// <summary>
/// A transcription of the Mac's <c>Pairing.parse</c>, from
/// <c>mac/Sources/HexBridge/Core/Pairing.swift</c>, into C#.
///
/// <para>
/// This is the point of the whole file. The pairing payload is generated on Windows and
/// consumed on the Mac, the two are written in different languages by different agents,
/// and nothing else in either build would notice if the two halves drifted — a Windows
/// unit test that parses the payload with Windows' own <c>PairingPayload.TryParse</c>
/// proves only that the Windows code agrees with itself.
/// </para>
///
/// <para>
/// So the algorithm is re-implemented here from the Swift source rather than reused, and
/// the differences are deliberate:
/// </para>
/// <list type="bullet">
///   <item>the URI is taken apart by hand the way Foundation's <c>URLComponents</c> does —
///         scheme, authority, query, then <c>&amp;</c> and the first <c>=</c>, with both
///         halves percent-decoded — rather than through <see cref="Uri"/>, whose treatment
///         of an unregistered scheme is a .NET detail and not part of the contract;</item>
///   <item>the key is base64url-decoded with the exact padding loop Swift uses
///         (<c>while count % 4 != 0 { += "=" }</c>), which is what makes a length that can
///         never be valid base64 come back as <c>badKey</c> instead of throwing;</item>
///   <item>the fingerprint is hashed with the domain prefix spelled out again, so removing
///         it from the product code fails here rather than silently on a user's desk.</item>
/// </list>
/// </summary>
internal static class SwiftPairing
{
    internal const string Scheme = "hexbridge";
    internal const string Host = "pair";
    internal const int Version = 1;

    /// <summary>Swift's <c>PairingError</c>, in the same order and with the same meaning.</summary>
    internal enum Error
    {
        None,
        NotAPairingLink,
        Malformed,
        WrongVersion,
        BadKey,
    }

    internal sealed record Payload(string Host, int Port, string Psk, string MachineName)
    {
        public string Target => $"{Host}:{Port}";
    }

    internal static Error Parse(string text, out Payload? payload)
    {
        payload = null;

        // `parse(text:)`: trim, then `URL(string:)`.
        var trimmed = text.Trim(' ', '\t', '\r', '\n', '\u000B', '\u000C');
        if (trimmed.Length == 0) return Error.Malformed;

        // `URL(string:)` requires a scheme; a bare word is not a URL.
        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return Error.Malformed;

        var scheme = trimmed[..colon];
        if (!scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return Error.NotAPairingLink;

        var rest = trimmed[(colon + 1)..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) return Error.NotAPairingLink;
        rest = rest[2..];

        // Authority runs to the first '/', '?' or '#'.
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var tail = end < 0 ? "" : rest[end..];

        // Foundation's `url.host` strips userinfo and port; neither ever appears here, but
        // an authority that is not exactly "pair" must not be waved through.
        if (!authority.Equals(Host, StringComparison.OrdinalIgnoreCase)) return Error.NotAPairingLink;

        var query = "";
        if (tail.StartsWith('?'))
        {
            var hash = tail.IndexOf('#');
            query = hash < 0 ? tail[1..] : tail[1..hash];
        }
        else if (tail.Length > 0 && !tail.StartsWith('#'))
        {
            // A path before the query. `queryItems` would then be empty, and the required
            // fields go missing.
            var mark = tail.IndexOf('?');
            if (mark >= 0)
            {
                var hash = tail.IndexOf('#');
                query = hash < 0 ? tail[(mark + 1)..] : tail[(mark + 1)..hash];
            }
        }

        var values = QueryItems(query);

        if (values.TryGetValue("v", out var rawVersion))
        {
            // Swift: `if let raw = values["v"], Int(raw) != version`. A `v` that is not a
            // number parses as nil, which is also != 1.
            if (!int.TryParse(rawVersion, out var parsedVersion) || parsedVersion != Version)
            {
                return Error.WrongVersion;
            }
        }

        if (!values.TryGetValue("h", out var host) || host.Length == 0) return Error.Malformed;

        if (!values.TryGetValue("p", out var portText)
            || !ushort.TryParse(portText, out var port))
        {
            return Error.Malformed;
        }

        if (!values.TryGetValue("k", out var keyText)) return Error.BadKey;

        var psk = Base64FromUrlSafe(keyText);
        if (psk is null) return Error.BadKey;
        if (!TryDecodeBase64(psk, out var raw) || raw.Length != 32) return Error.BadKey;

        payload = new Payload(host, port, psk, values.GetValueOrDefault("n", "ПК"));
        return Error.None;
    }

    /// <summary>
    /// <c>URLComponents.queryItems</c>: split on <c>&amp;</c>, then on the first <c>=</c>,
    /// percent-decoding both halves. A repeated name keeps the last value, which is what
    /// building a dictionary out of the item list does in the Swift source.
    /// </summary>
    private static Dictionary<string, string> QueryItems(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            var name = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            result[PercentDecode(name)] = PercentDecode(value);
        }
        return result;
    }

    private static string PercentDecode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes([value[i]]));
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>Swift's <c>base64(fromURLSafe:)</c>, padding loop and all.</summary>
    private static string? Base64FromUrlSafe(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        while (value.Length % 4 != 0) value += "=";
        return value;
    }

    /// <summary>
    /// Foundation's <c>Data(base64Encoded:)</c> with default options: strict alphabet,
    /// strict padding, nothing skipped.
    /// </summary>
    private static bool TryDecodeBase64(string value, out byte[] raw)
    {
        raw = [];
        if (value.Length == 0 || value.Length % 4 != 0) return false;

        var body = value.TrimEnd('=');
        var padding = value.Length - body.Length;
        if (padding > 2) return false;
        if (body.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '+' && ch != '/')) return false;

        try
        {
            raw = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Swift's <c>Pairing.fingerprint(ofBase64Key:)</c>.</summary>
    internal static string? Fingerprint(string base64Key)
    {
        if (!TryDecodeBase64(base64Key, out var raw) || raw.Length != 32) return null;

        var domain = Encoding.UTF8.GetBytes("hexbridge-fingerprint-v1");
        var buffer = new byte[domain.Length + raw.Length];
        domain.CopyTo(buffer, 0);
        raw.CopyTo(buffer, domain.Length);

        var hex = Convert.ToHexString(SHA256.HashData(buffer)).AsSpan(0, 16);
        var groups = new List<string>(4);
        for (var i = 0; i < 16; i += 4) groups.Add(hex.Slice(i, 4).ToString());
        return string.Join(" · ", groups);
    }

    // MARK: - Short code, from the same file

    internal const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    internal const int CodeLength = 12;

    /// <summary>Swift's <c>formatCode</c>: keep alphabet characters, group in fours.</summary>
    internal static string FormatCode(string input)
    {
        var kept = new string(input.ToUpperInvariant().Where(CodeAlphabet.Contains).Take(CodeLength).ToArray());
        var output = new StringBuilder(CodeLength + 2);
        for (var index = 0; index < kept.Length; index++)
        {
            if (index > 0 && index % 4 == 0) output.Append('-');
            output.Append(kept[index]);
        }
        return output.ToString();
    }

    internal static string NormalizedCode(string input) =>
        new(input.ToUpperInvariant().Where(CodeAlphabet.Contains).ToArray());

    internal static bool IsCompleteCode(string input) =>
        input.Count(CodeAlphabet.Contains) == CodeLength;

    /// <summary>Swift's <c>exchangePort(forDataPort:)</c>, with its unchecked <c>&amp;+</c>.</summary>
    internal static int ExchangePort(int dataPort) => (ushort)(dataPort + 1);
}

/// <summary>
/// Windows generates the pairing payload, the Mac consumes it, and these tests are the
/// only place the two halves meet before a user does.
/// </summary>
public class PairingCompatibilityTests
{
    /// <summary>A 32-byte key built by repeating a byte pattern, so a case can name itself.</summary>
    private static byte[] Key(params byte[] pattern)
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = pattern[i % pattern.Length];
        return key;
    }

    private static byte[] KeyFromHex(string pattern) => Key(Convert.FromHexString(pattern));

    // MARK: - The URI round trip

    [Fact]
    public void UriFromWindowsParsesOnTheMac()
    {
        var key = Key(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        var payload = new PairingPayload
        {
            Host = "192.168.1.10",
            Port = 47702,
            Key = key,
            Name = "GAMING-PC",
        };

        var error = SwiftPairing.Parse(payload.ToUri(), out var parsed);

        Assert.Equal(SwiftPairing.Error.None, error);
        Assert.NotNull(parsed);
        Assert.Equal("192.168.1.10", parsed.Host);
        Assert.Equal(47702, parsed.Port);
        Assert.Equal("GAMING-PC", parsed.MachineName);
        // The Mac stores the key as standard base64 — the same string config.json holds.
        Assert.Equal(payload.Psk, parsed.Psk);
        Assert.Equal("192.168.1.10:47702", parsed.Target);
    }

    [Fact]
    public void UriMatchesTheDocumentedShape()
    {
        // DESIGN.md §9.1 prints this URI literally, and the Mac's parser is written
        // against it. Key order is not semantically required, but a change here means the
        // document and the code have parted company.
        var payload = new PairingPayload
        {
            Host = "10.0.0.2",
            Port = 47702,
            Key = Key(0xAB),
            Name = "PC",
        };

        var uri = payload.ToUri();

        Assert.StartsWith("hexbridge://pair?v=1&h=10.0.0.2&p=47702&k=", uri, StringComparison.Ordinal);
        Assert.EndsWith("&n=PC", uri, StringComparison.Ordinal);
    }

    [Theory]
    // Every byte a base64url substitution can touch.
    [InlineData("FBFFBE")]   // base64 that carries both '+' and '/'
    [InlineData("00")]
    [InlineData("FF")]
    [InlineData("7F80")]
    public void EveryKeySurvivesBase64Url(string pattern)
    {
        var key = KeyFromHex(pattern);
        var payload = new PairingPayload { Host = "host.local", Port = 1, Key = key, Name = "" };

        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(payload.ToUri(), out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(Convert.ToBase64String(key), parsed.Psk);
    }

    [Fact]
    public void RandomKeysRoundTrip()
    {
        // Base64url padding depends on the key length, not its contents, but the
        // substitution characters do not — a thousand random keys covers the alphabet.
        for (var i = 0; i < 1000; i++)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var uri = new PairingPayload { Host = "1.2.3.4", Port = 47702, Key = key, Name = "PC" }.ToUri();

            Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(uri, out var parsed));
            Assert.Equal(Convert.ToBase64String(key), parsed!.Psk);
        }
    }

    [Fact]
    public void Base64UrlNeverCarriesPaddingOrUnsafeCharacters()
    {
        // 32 bytes is 43 base64url characters with the '=' dropped. Padding inside a query
        // value would survive Foundation's decoding, but a '+' would not survive a QR
        // reader that re-encodes the string, so neither is allowed out.
        for (var i = 0; i < 200; i++)
        {
            var uri = new PairingPayload
            {
                Host = "1.2.3.4", Port = 47702, Key = RandomNumberGenerator.GetBytes(32), Name = "PC",
            }.ToUri();

            var key = uri[(uri.IndexOf("&k=", StringComparison.Ordinal) + 3)..];
            key = key[..key.IndexOf("&n=", StringComparison.Ordinal)];

            Assert.Equal(43, key.Length);
            Assert.DoesNotContain('=', key);
            Assert.DoesNotContain('+', key);
            Assert.DoesNotContain('/', key);
        }
    }

    [Fact]
    public void CyrillicMachineNameArrivesTransliterated()
    {
        // The payload is ASCII on purpose (§3.5): Apple's QR generator is documented
        // against ISO-Latin-1 in one place and ASCII in another. A mangled name is
        // cosmetic; a mangled key is a support call.
        var payload = new PairingPayload
        {
            Host = "192.168.0.5", Port = 47702, Key = Key(0x11), Name = "Никита-ПК",
        };

        var uri = payload.ToUri();

        Assert.All(uri, ch => Assert.InRange(ch, ' ', '~'));
        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(uri, out var parsed));
        Assert.Equal("Nikita-PK", parsed!.MachineName);
    }

    [Theory]
    [InlineData("PC & Mac")]
    [InlineData("a=b")]
    [InlineData("100% PC")]
    [InlineData("C:\\PC")]
    [InlineData("PC+1")]
    [InlineData("PC?x")]
    [InlineData("PC#1")]
    public void NamesWithQuerySyntaxDoNotBreakTheQuery(string name)
    {
        var payload = new PairingPayload { Host = "1.2.3.4", Port = 47702, Key = Key(0x22), Name = name };

        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(payload.ToUri(), out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(name, parsed.MachineName);
        Assert.Equal("1.2.3.4", parsed.Host);
        Assert.Equal(Convert.ToBase64String(Key(0x22)), parsed.Psk);
    }

    [Fact]
    public void AnEmptyNameIsOmittedAndTheMacFillsInItsOwn()
    {
        var uri = new PairingPayload { Host = "1.2.3.4", Port = 47702, Key = Key(0x33), Name = "" }.ToUri();

        Assert.DoesNotContain("&n=", uri, StringComparison.Ordinal);
        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(uri, out var parsed));
        // Swift: `machineName: values["n"] ?? "ПК"`.
        Assert.Equal("ПК", parsed!.MachineName);
    }

    [Fact]
    public void WindowsAndTheMacAgreeOnItsOwnOutput()
    {
        // Both parsers, same string, same answer — the wizard's «paste a link» field and
        // the Mac's must not disagree about what is valid.
        var uri = new PairingPayload
        {
            Host = "host-1.local", Port = 65535, Key = Key(0xDE, 0xAD, 0xBE, 0xEF), Name = "Gaming PC",
        }.ToUri();

        Assert.True(PairingPayload.TryParse(uri, out var windows, out _));
        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(uri, out var mac));

        Assert.Equal(windows!.Host, mac!.Host);
        Assert.Equal(windows.Port, mac.Port);
        Assert.Equal(windows.Psk, mac.Psk);
        Assert.Equal(windows.Name, mac.MachineName);
    }

    // MARK: - Boundaries

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(47702)]
    [InlineData(65535)]
    public void EveryLegalPortSurvives(int port)
    {
        var uri = new PairingPayload { Host = "1.2.3.4", Port = port, Key = Key(0x44), Name = "PC" }.ToUri();

        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(uri, out var parsed));
        Assert.Equal(port, parsed!.Port);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void APortOutsideUInt16IsRejectedByBothSides(int port)
    {
        // The Mac parses the port as UInt16 and fails on anything else; Windows checks
        // 1…65535 explicitly. Neither may hand a broken payload to the other.
        var uri = new PairingPayload { Host = "1.2.3.4", Port = port, Key = Key(0x55), Name = "PC" }.ToUri();

        Assert.False(PairingPayload.TryParse(uri, out _, out var error));
        Assert.NotNull(error);

        if (port is < 0 or > 65535)
        {
            Assert.Equal(SwiftPairing.Error.Malformed, SwiftPairing.Parse(uri, out _));
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(0)]
    public void AKeyThatIsNot32BytesIsRefused(int length)
    {
        var key = PairingPayload.ToBase64Url(RandomNumberGenerator.GetBytes(length));
        var uri = $"hexbridge://pair?v=1&h=1.2.3.4&p=47702&k={key}&n=PC";

        Assert.Equal(SwiftPairing.Error.BadKey, SwiftPairing.Parse(uri, out _));
        Assert.False(PairingPayload.TryParse(uri, out _, out _));
    }

    [Fact]
    public void AKeyLengthThatCanNeverBeBase64IsRefusedRatherThanCrashing()
    {
        // 41 base64url characters pads to 44 with three '=', which Foundation rejects.
        var uri = $"hexbridge://pair?v=1&h=1.2.3.4&p=47702&k={new string('A', 41)}&n=PC";

        Assert.Equal(SwiftPairing.Error.BadKey, SwiftPairing.Parse(uri, out _));
        Assert.False(PairingPayload.TryParse(uri, out _, out _));
    }

    // MARK: - Rubbish input

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("47702")]
    [InlineData("{\"psk\":\"…\"}")]
    public void NonsenseIsRefusedByBothSides(string text)
    {
        Assert.NotEqual(SwiftPairing.Error.None, SwiftPairing.Parse(text, out _));
        Assert.False(PairingPayload.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://example.com/pair?v=1&h=1.2.3.4&p=1&k=AAAA")]
    [InlineData("hexbridge://connect?v=1&h=1.2.3.4&p=1")]
    [InlineData("hexbridgex://pair?v=1")]
    public void ALinkFromSomewhereElseIsRefused(string text)
    {
        Assert.NotEqual(SwiftPairing.Error.None, SwiftPairing.Parse(text, out _));
        Assert.False(PairingPayload.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData("hexbridge://pair?v=2&h=1.2.3.4&p=47702")]
    [InlineData("hexbridge://pair?v=99&h=1.2.3.4&p=47702")]
    public void AFutureVersionIsRefusedBeforeAnythingElseIsRead(string text)
    {
        Assert.Equal(SwiftPairing.Error.WrongVersion, SwiftPairing.Parse(text, out _));
        Assert.False(PairingPayload.TryParse(text, out _, out var error));
        Assert.Contains("версии", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hexbridge://pair?v=1&p=47702&k=AAAA")]           // no host
    [InlineData("hexbridge://pair?v=1&h=&p=47702&k=AAAA")]        // empty host
    [InlineData("hexbridge://pair?v=1&h=1.2.3.4&k=AAAA")]         // no port
    [InlineData("hexbridge://pair?v=1&h=1.2.3.4&p=abc&k=AAAA")]   // port is not a number
    public void AnIncompleteLinkIsRefused(string text)
    {
        Assert.Equal(SwiftPairing.Error.Malformed, SwiftPairing.Parse(text, out _));
        Assert.False(PairingPayload.TryParse(text, out _, out _));
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        // The user pastes a line out of a chat window; both sides trim before parsing.
        var uri = new PairingPayload { Host = "1.2.3.4", Port = 47702, Key = Key(0x66), Name = "PC" }.ToUri();
        var messy = $"\n  {uri}\t\n";

        Assert.Equal(SwiftPairing.Error.None, SwiftPairing.Parse(messy, out _));
        Assert.True(PairingPayload.TryParse(messy, out _, out _));
    }

    [Fact]
    public void ATruncatedUriIsRefusedRatherThanHalfAccepted()
    {
        var uri = new PairingPayload { Host = "1.2.3.4", Port = 47702, Key = Key(0x77), Name = "PC" }.ToUri();

        // A QR read that drops its last rows is the case this rules out. The cut-off is
        // the last character of the key: the machine name after it is optional, so a
        // prefix that stops there is a complete and legitimate payload, not a truncation.
        var lastKeyCharacter = uri.IndexOf("&k=", StringComparison.Ordinal) + 3 + 43;

        for (var length = 1; length < lastKeyCharacter; length++)
        {
            var prefix = uri[..length];
            Assert.NotEqual(SwiftPairing.Error.None, SwiftPairing.Parse(prefix, out _));
            Assert.False(PairingPayload.TryParse(prefix, out _, out _));
        }
    }

    // MARK: - Fingerprint

    [Theory]
    // Golden values: SHA256("hexbridge-fingerprint-v1" ‖ key), first eight bytes, in four
    // groups of four hex characters. Computed independently of both implementations.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "D60B · 5CE7 · 4C13 · 577C")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", "55E0 · 675D · B8C0 · E996")]
    [InlineData("//////////////////////////////////////////8=", "6FFE · 4273 · 0574 · 0C6C")]
    [InlineData("+/+++/+++/+++/+++/+++/+++/+++/+++/+++/++AQI=", "A4FB · 6690 · 43C0 · 373F")]
    public void FingerprintMatchesTheAgreedHash(string psk, string expected)
    {
        Assert.Equal(expected, PairingPayload.FingerprintOfPsk(psk));
        Assert.Equal(expected, SwiftPairing.Fingerprint(psk));
    }

    [Fact]
    public void FingerprintIsDomainSeparated()
    {
        // Without the domain prefix the two sides would print different fingerprints for
        // the same key, and the wizard's «ключи совпадают» check would be a lie.
        var key = RandomNumberGenerator.GetBytes(32);
        var bare = Convert.ToHexString(SHA256.HashData(key))[..4];

        Assert.NotEqual(bare, PairingPayload.FingerprintOf(key)[..4]);
    }

    [Fact]
    public void FingerprintAgreesForRandomKeys()
    {
        for (var i = 0; i < 200; i++)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var psk = Convert.ToBase64String(key);

            Assert.Equal(SwiftPairing.Fingerprint(psk), PairingPayload.FingerprintOf(key));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void AFingerprintOfSomethingThatIsNotAKeyIsNothing(string psk)
    {
        Assert.Null(PairingPayload.FingerprintOfPsk(psk));
        Assert.Null(SwiftPairing.Fingerprint(psk));
    }

    // MARK: - Short code

    [Fact]
    public void TheAlphabetIsTheSameOnBothSides()
    {
        Assert.Equal(SwiftPairing.CodeAlphabet, ShortCode.Alphabet);
        Assert.Equal(SwiftPairing.CodeLength, ShortCode.Length);
        Assert.Equal(32, ShortCode.Alphabet.Length);
        Assert.Equal(ShortCode.Alphabet.Length, ShortCode.Alphabet.Distinct().Count());
    }

    [Theory]
    [InlineData('I')]
    [InlineData('O')]
    [InlineData('0')]
    [InlineData('1')]
    public void TheFourMistypedCharactersAreAbsent(char ch)
    {
        // §9.3: I, O, 0 and 1 are the four characters people read off a screen and type
        // wrong. Dropping them is what makes the code worth showing at all.
        Assert.DoesNotContain(ch, ShortCode.Alphabet);
        Assert.DoesNotContain(ch, SwiftPairing.CodeAlphabet);
    }

    [Fact]
    public void GeneratedCodesAreFormattedTheWayTheMacFormatsThem()
    {
        for (var i = 0; i < 500; i++)
        {
            var code = ShortCode.Generate();

            Assert.Equal(14, code.Length);
            Assert.Equal('-', code[4]);
            Assert.Equal('-', code[9]);
            Assert.True(ShortCode.IsComplete(code));
            Assert.True(SwiftPairing.IsCompleteCode(code));
            // The Mac reformats whatever the user types; the result must be the code that
            // was on the Windows screen, character for character.
            Assert.Equal(code, SwiftPairing.FormatCode(code));
            Assert.Equal(ShortCode.Normalise(code), SwiftPairing.NormalizedCode(code));
        }
    }

    [Theory]
    [InlineData("abcd-efgh-jklm", "ABCD-EFGH-JKLM")]
    [InlineData("ABCDEFGHJKLM", "ABCD-EFGH-JKLM")]
    [InlineData("  abcd efgh jklm  ", "ABCD-EFGH-JKLM")]
    [InlineData("ABCD-EFGH-JKLM-NPQR", "ABCD-EFGH-JKLM")]
    [InlineData("код: ABCD EFGH JKLM", "ABCD-EFGH-JKLM")]
    public void FormattingAgreesWithTheMac(string typed, string expected)
    {
        Assert.Equal(expected, ShortCode.Format(typed));
        Assert.Equal(expected, SwiftPairing.FormatCode(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("IOIO-IOIO-IOIO")]
    [InlineData("0101-0101-0101")]
    [InlineData("ABC")]
    [InlineData("ABCD-EFGH-JKL")]
    public void AnIncompleteCodeIsNotAccepted(string typed)
    {
        Assert.False(ShortCode.IsComplete(typed));
        Assert.False(SwiftPairing.IsCompleteCode(typed));
    }

    [Fact]
    public void CharactersOutsideTheAlphabetAreDroppedRatherThanGuessedAt()
    {
        // Turning a typed O into a Q silently would hand the user a code they never saw.
        Assert.Equal("ABCD", ShortCode.Normalise("A0B1C!D иi"));
        Assert.Equal("ABCD", SwiftPairing.NormalizedCode("A0B1C!D иi"));
    }

    [Fact]
    public void PartialFormattingIsSafeOnEveryKeystroke()
    {
        // The Mac formats on every keystroke, so every prefix of a code has to survive.
        var code = ShortCode.Generate();
        for (var length = 0; length <= code.Length; length++)
        {
            var typed = code[..length];
            Assert.Equal(SwiftPairing.FormatCode(typed), ShortCode.Format(typed));
        }
    }

    [Fact]
    public void CodesAreDrawnFromTheWholeAlphabet()
    {
        // A generator that quietly fell back to a fixed string would still pass every test
        // above. This one would not.
        var seen = new HashSet<char>();
        for (var i = 0; i < 2000; i++) seen.UnionWith(ShortCode.Normalise(ShortCode.Generate()));

        Assert.Equal(ShortCode.Alphabet.Length, seen.Count);
    }

    // MARK: - Exchange port

    [Theory]
    [InlineData(47702, 47703)]
    [InlineData(1, 2)]
    [InlineData(65534, 65535)]
    [InlineData(65535, 0)]
    public void TheExchangePortIsOneAboveTheDataPort(int data, int expected)
    {
        // Swift uses `&+`, an unchecked add, so the top of the range wraps rather than
        // trapping. Matching that is what keeps the two sides dialling the same number.
        Assert.Equal(expected, PairingPayload.ExchangePort(data));
        Assert.Equal(expected, SwiftPairing.ExchangePort(data));
    }
}
