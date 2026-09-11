using System.Security.Cryptography;
using System.Text;

namespace HexBridge;

/// <summary>
/// Wraps the pairing URI so the short-code exchange can cross a network nobody trusts.
///
/// <para>
/// The exchange used to answer with the URI as plain text over plain HTTP. On a home
/// network that was a considered trade; across the internet it hands the 32-byte key to
/// anyone on the path, and macOS refused the request outright rather than let it happen.
/// The two screens already share a secret — the twelve characters the person is copying
/// across — so that is what the answer is encrypted with, and no exception to anybody's
/// transport policy is needed.
/// </para>
///
/// <para>
/// <b>Why a slow derivation.</b> The code is twelve characters from a 32-symbol alphabet:
/// exactly 60 bits. Against someone who captured the blob and can guess offline, 60 bits
/// of HKDF would be worth roughly a weekend of rented GPUs. PBKDF2-SHA256 at
/// <see cref="Iterations"/> multiplies that by about 2^19, which puts it out of reach for
/// the value of one microphone link — and costs each side one derivation, once, during a
/// step a person is already waiting on. The PC derives when it *shows* the code rather
/// than per request, so an attacker cannot spend its CPU by asking repeatedly; and the
/// code has to match before there is anything to decrypt at all.
/// </para>
///
/// <para>
/// The Mac's half is <c>PairingSeal</c> in <c>mac/Sources/HexBridge/Core/Pairing.swift</c>.
/// Both are pinned to the same vector in <c>docs/PROTOCOL.md</c> — change one and the
/// tests on the other platform fail, which is the point.
/// </para>
/// </summary>
public static class PairingSeal
{
    /// <summary>Leading byte of the blob, and the only associated data the tag covers.</summary>
    public const byte Version = 1;

    public const int SaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    /// <summary>PBKDF2-SHA256 rounds. See the note on the class about why this is not HKDF.</summary>
    public const int Iterations = 600_000;

    /// <summary>
    /// The only associated data the tag covers: the version byte. Held as a field because
    /// a collection expression at the call site leaves the AesGcm overload ambiguous.
    /// </summary>
    private static readonly byte[] AssociatedData = [Version];

    /// <summary>
    /// The key both sides arrive at from the same code and salt.
    ///
    /// <paramref name="code"/> is normalised first, so it does not matter whether it came
    /// from a screen with hyphens or from a text field mid-edit.
    /// </summary>
    public static byte[] DeriveKey(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.ASCII.GetBytes(ShortCode.Normalise(code)),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            32);

    private static readonly byte[] RendezvousPrefix = "hexbridge-pair-rendezvous-v1"u8.ToArray();

    /// <summary>
    /// The name two machines that know the same code agree on without either of them
    /// saying it, so a relay can introduce them without being told the code.
    ///
    /// <para>
    /// It has to be a one-way function of the code and nothing else. The relay holds the
    /// sealed answer, and the key to that answer is derived from the code — so a relay
    /// that learned the code could read what it is carrying, which is the one thing this
    /// relay is built not to do. Guessing the id means guessing the code: 60 bits.
    /// </para>
    ///
    /// <para>
    /// Matched by <c>RendezvousID</c> in <c>relay/rendezvous.go</c> and
    /// <c>PairingSeal.rendezvousID</c> on the Mac, against one vector in docs/PROTOCOL.md.
    /// </para>
    /// </summary>
    public static string RendezvousId(string code)
    {
        var material = Encoding.ASCII.GetBytes(ShortCode.Normalise(code));
        var input = new byte[RendezvousPrefix.Length + material.Length];
        RendezvousPrefix.CopyTo(input, 0);
        material.CopyTo(input, RendezvousPrefix.Length);

        // Base64url without padding, truncated to 16 bytes: 22 characters, and the same
        // shape as the discovery tag so one reader recognises both.
        return Convert.ToBase64String(SHA256.HashData(input).AsSpan(0, 16))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Seals <paramref name="plaintext"/> under a fresh salt and nonce.</summary>
    public static string Seal(string plaintext, string code)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return Seal(plaintext, DeriveKey(code, salt), salt, nonce);
    }

    /// <summary>
    /// Seals with a caller's salt and nonce, and with the key already derived.
    ///
    /// Used by the wizard, which derives once when it starts showing a code, and by the
    /// tests, which need the answer to be reproducible.
    /// </summary>
    public static string Seal(string plaintext, byte[] key, byte[] salt, byte[] nonce)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(salt.Length, SaltBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceBytes);

        var body = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[body.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, body, cipher, tag, AssociatedData);

        var blob = new byte[1 + SaltBytes + NonceBytes + cipher.Length + TagBytes];
        blob[0] = Version;
        salt.CopyTo(blob, 1);
        nonce.CopyTo(blob, 1 + SaltBytes);
        cipher.CopyTo(blob, 1 + SaltBytes + NonceBytes);
        tag.CopyTo(blob, 1 + SaltBytes + NonceBytes + cipher.Length);

        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Undoes <see cref="Seal(string, string)"/>, or null if the blob is malformed, the
    /// version is one this build does not know, or the code is wrong.
    ///
    /// Windows never needs this in production — it is the side that seals — but the two
    /// halves of a format are only really pinned when one implementation can check itself.
    /// </summary>
    public static string? Open(string blob, string code)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(blob.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length < 1 + SaltBytes + NonceBytes + TagBytes) return null;
        if (raw[0] != Version) return null;

        var salt = raw[1..(1 + SaltBytes)];
        var nonce = raw[(1 + SaltBytes)..(1 + SaltBytes + NonceBytes)];
        var cipher = raw[(1 + SaltBytes + NonceBytes)..^TagBytes];
        var tag = raw[^TagBytes..];

        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(DeriveKey(code, salt), TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain, AssociatedData);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plain);
    }
}
