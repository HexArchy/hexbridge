using System.Security.Cryptography;
using System.Text;

using HexBridge;

namespace HexBridge.Tests;

/// <summary>
/// The encryption around the short-code exchange, pinned to the same vector as
/// <c>mac/Tests/HexBridgePairingTests/PairingSealTests.swift</c>.
///
/// <para>
/// These are about interoperability rather than arithmetic. The PC seals and the Mac
/// opens, so a quiet change to the salt length, the iteration count, the associated data
/// or the order of the fields would not fail here — it would fail on somebody's desk,
/// as two machines that can no longer be paired. Both suites hold the same bytes, so a
/// change on either side breaks the other side's build.
/// </para>
/// </summary>
public class PairingSealTests
{
    /// <summary>Salt 01…10, nonce A0…AB, so the whole answer is reproducible.</summary>
    private static byte[] Salt => Enumerable.Range(1, PairingSeal.SaltBytes).Select(i => (byte)i).ToArray();
    private static byte[] Nonce => Enumerable.Range(0, PairingSeal.NonceBytes).Select(i => (byte)(0xA0 + i)).ToArray();

    private const string Code = "TUJJC8XU3LJ4";
    private const string Uri =
        "hexbridge://pair?v=1&h=10.0.0.7&p=47702&k=3q2-796tvu_erb7v3q2-796tvu_erb7v3q2-796tvu8&n=PC";

    private const string Blob =
        "AQECAwQFBgcICQoLDA0ODxCgoaKjpKWmp6ipqquYsSxi+zUzZvJq1//po186Z/Mjzmj52LsWiAN6XTnFUHQm" +
        "oJ2ReIRAhxGw77vd7slbDBE7r5lxvBMh0XPAfjdwIa8S86pAsZ3skt5HC/MCC6Rw0CT9aQd5D0b1ZyCUuovF" +
        "ba2HYAgIqgXf";

    [Fact]
    public void TheKeyIsTheOneTheMacDerives() =>
        Assert.Equal(
            "3b5d88c7628acaec690b06bce40aca7174eed292d27a8ded9d3b801d3beb513c",
            Convert.ToHexString(PairingSeal.DeriveKey(Code, Salt)).ToLowerInvariant());

    [Fact]
    public void TheSameSaltAndNonceProduceTheSameBytes() =>
        Assert.Equal(Blob, PairingSeal.Seal(Uri, PairingSeal.DeriveKey(Code, Salt), Salt, Nonce));

    [Fact]
    public void WhatWasSealedComesBack() => Assert.Equal(Uri, PairingSeal.Open(Blob, Code));

    [Fact]
    public void TheCodeMayBeTypedAnyWayAtAll()
    {
        Assert.Equal(Uri, PairingSeal.Open(Blob, "tujj-c8xu-3lj4"));
        Assert.Equal(Uri, PairingSeal.Open(Blob, "  TUJJ C8XU 3LJ4 "));
    }

    [Fact]
    public void AWrongCodeOpensNothing() => Assert.Null(PairingSeal.Open(Blob, "TUJJC8XU3LJ5"));

    /// <summary>The tag covers the version byte, so a blob relabelled as a future format
    /// cannot be replayed as this one.</summary>
    [Fact]
    public void AChangedVersionIsRefused()
    {
        var raw = Convert.FromBase64String(Blob);
        raw[0] = 2;

        Assert.Null(PairingSeal.Open(Convert.ToBase64String(raw), Code));
    }

    [Fact]
    public void ATamperedBodyIsRefused()
    {
        var raw = Convert.FromBase64String(Blob);
        raw[1 + PairingSeal.SaltBytes + PairingSeal.NonceBytes] ^= 0x01;

        Assert.Null(PairingSeal.Open(Convert.ToBase64String(raw), Code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AQID")]
    public void RubbishIsJustNull(string blob) => Assert.Null(PairingSeal.Open(blob, Code));

    [Fact]
    public void AFreshSealIsNeverTheSameTwice()
    {
        var first = PairingSeal.Seal(Uri, Code);
        var second = PairingSeal.Seal(Uri, Code);

        Assert.NotEqual(first, second);
        Assert.Equal(Uri, PairingSeal.Open(first, Code));
        Assert.Equal(Uri, PairingSeal.Open(second, Code));
    }

    // MARK: - The answer the socket actually sends

    [Fact]
    public void AskingForEncryptionGetsAnEncryptedAnswer()
    {
        var answer = PairingExchangeProtocol.Answer($"GET /pair?code={Code}&enc=1 HTTP/1.1", Code, Uri);

        Assert.True(answer.IsGranted);
        Assert.NotEqual(Uri, answer.Body);
        Assert.Equal(Uri, PairingSeal.Open(answer.Body, Code));
    }

    /// <summary>
    /// A Mac from before the encryption existed does not send <c>enc=1</c>, and gets what
    /// it has always got. Dropping that would turn "your Mac is out of date" into a
    /// pairing that fails with no explanation on either screen.
    /// </summary>
    [Fact]
    public void AnOlderMacStillGetsThePlainAnswer()
    {
        var answer = PairingExchangeProtocol.Answer($"GET /pair?code={Code} HTTP/1.1", Code, Uri);

        Assert.True(answer.IsGranted);
        Assert.Equal(Uri, answer.Body);
    }

    [Fact]
    public void AWrongCodeIsRefusedBeforeAnythingIsSealed()
    {
        var answer = PairingExchangeProtocol.Answer("GET /pair?code=AAAABBBBCCCC&enc=1 HTTP/1.1", Code, Uri);

        Assert.False(answer.IsGranted);
        Assert.Equal(403, answer.Status);
        Assert.Equal("", answer.Body);
    }
}
