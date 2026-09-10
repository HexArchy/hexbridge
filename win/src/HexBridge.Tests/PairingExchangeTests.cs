using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HexBridge.Tests;

/// <summary>
/// The short-code exchange, checked against the request the Mac actually sends.
///
/// <para>
/// The Mac's client is <c>PairingExchange.fetch</c> in <c>Core/Pairing.swift</c>: a
/// <c>URLSession</c> GET of <c>http://host:port/pair?code=…</c>, with the code already
/// normalised, that feeds the whole response body to <c>Pairing.parse(text:)</c> and turns
/// a 403 or a 404 — and only those — into «ПК не принял код».
/// </para>
/// </summary>
public class PairingExchangeTests
{
    private const string Code = "ABCD-EFGH-JKLM";
    private const string Uri = "hexbridge://pair?v=1&h=192.168.1.10&p=47702&k=" +
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&n=PC";

    /// <summary>The line <c>URLSession</c> puts on the wire for the Mac's request.</summary>
    private static string MacRequestLine(string code) =>
        $"GET /pair?code={ShortCode.Normalise(code)} HTTP/1.1";

    [Fact]
    public void TheRightCodeGetsThePayload()
    {
        var answer = PairingExchangeProtocol.Answer(MacRequestLine(Code), Code, Uri);

        Assert.True(answer.IsGranted);
        Assert.Equal(200, answer.Status);
        // The Mac parses the body verbatim, so a stray newline or a JSON wrapper would
        // come back as «ПК ответил, но не ссылкой связывания».
        Assert.Equal(Uri, answer.Body);
        Assert.True(PairingPayload.TryParse(answer.Body, out _, out _));
    }

    [Fact]
    public void TheCodeIsAcceptedHoweverTheUserTypedIt()
    {
        // The Mac normalises before sending, but the wizard's own field and a paste from a
        // chat window both end up here as well.
        foreach (var typed in new[] { "ABCDEFGHJKLM", "abcd-efgh-jklm", "  ABCD EFGH JKLM " })
        {
            var answer = PairingExchangeProtocol.Answer($"GET /pair?code={ShortCode.Normalise(typed)} HTTP/1.1", Code, Uri);
            Assert.True(answer.IsGranted);
        }
    }

    [Theory]
    [InlineData("MMMM-NNNN-PPPP")]
    [InlineData("ABCD-EFGH-JKLN")]
    [InlineData("")]
    [InlineData("ABCD")]
    public void AWrongCodeGetsAStatusTheMacUnderstands(string presented)
    {
        var answer = PairingExchangeProtocol.Answer(MacRequestLine(presented), Code, Uri);

        // 403 and 404 are the two the Mac maps to `.exchangeRefused`; anything else would
        // surface as a confusing «ПК ответил, но не ссылкой связывания».
        Assert.True(answer.Status is 403 or 404);
        Assert.Equal("", answer.Body);
    }

    [Fact]
    public void NoCodeIsHandedOutBeforeTheWizardHasOne()
    {
        // An exchange server left running with an empty code must not answer an empty
        // request — that would be a payload for anyone who asks.
        Assert.False(PairingExchangeProtocol.Answer("GET /pair?code= HTTP/1.1", "", Uri).IsGranted);
        Assert.False(PairingExchangeProtocol.Answer("GET /pair HTTP/1.1", "", Uri).IsGranted);
        Assert.False(PairingExchangeProtocol.Answer("GET /pair?code=ABCDEFGHJKLM HTTP/1.1", "ABCD", Uri).IsGranted);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1")]
    [InlineData("GET /index.html HTTP/1.1")]
    [InlineData("GET /pair/ HTTP/1.1")]
    [InlineData("GET /PAIR?code=ABCDEFGHJKLM HTTP/1.1")]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("\0\0\0")]
    public void AnythingButThePairPathIsNotFound(string requestLine)
    {
        var answer = PairingExchangeProtocol.Answer(requestLine, Code, Uri);

        Assert.Equal(404, answer.Status);
        Assert.Equal("", answer.Body);
    }

    [Theory]
    [InlineData("POST /pair?code=ABCDEFGHJKLM HTTP/1.1")]
    [InlineData("DELETE /pair?code=ABCDEFGHJKLM HTTP/1.1")]
    public void AMethodThatCouldChangeSomethingIsRefused(string requestLine)
    {
        Assert.Equal(405, PairingExchangeProtocol.Answer(requestLine, Code, Uri).Status);
    }

    [Fact]
    public void ExtraQueryParametersDoNotConfuseTheLookup()
    {
        var answer = PairingExchangeProtocol.Answer(
            "GET /pair?v=1&code=ABCDEFGHJKLM&debug=true HTTP/1.1", Code, Uri);

        Assert.True(answer.IsGranted);
    }

    [Fact]
    public void APercentEncodedCodeIsDecodedBeforeItIsCompared()
    {
        // A code typed with its hyphens and percent-encoded by a client that is not the
        // Mac still has to work; the hyphens wash out in normalisation.
        Assert.True(PairingExchangeProtocol.Answer(
            "GET /pair?code=ABCD%2DEFGH%2DJKLM HTTP/1.1", Code, Uri).IsGranted);
    }

    [Fact]
    public void ARenderedResponseIsWellFormedHttp()
    {
        var text = Encoding.UTF8.GetString(
            PairingExchangeProtocol.Render(PairingExchangeProtocol.Answer(MacRequestLine(Code), Code, Uri)));

        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split > 0);

        var head = text[..split];
        var body = text[(split + 4)..];

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", head, StringComparison.Ordinal);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(Uri)}", head, StringComparison.Ordinal);
        Assert.Equal(Uri, body);
    }

    [Fact]
    public void ARefusalCarriesNoBodyAtAll()
    {
        var text = Encoding.UTF8.GetString(
            PairingExchangeProtocol.Render(PairingExchangeProtocol.Answer(MacRequestLine("MMMM-NNNN-PPPP"), Code, Uri)));

        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 0", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
    }

    // MARK: - End to end over a real socket

    [Fact]
    public async Task AMacStyleRequestGetsThePayloadOverTheWire()
    {
        await using var server = new PairingExchangeServer(0, IPAddress.Loopback);
        // Port 0 lets the OS pick; the wizard uses PairingPayload.ExchangePort instead.
        server.Start();
        server.Code = Code;
        server.Uri = Uri;

        var paired = 0;
        server.Paired += () => Interlocked.Increment(ref paired);

        var body = await FetchAsync(server.Port, $"/pair?code={ShortCode.Normalise(Code)}");

        Assert.Contains("200 OK", body, StringComparison.Ordinal);
        Assert.EndsWith(Uri, body, StringComparison.Ordinal);
        Assert.Equal(1, server.Granted);
        Assert.Equal(1, paired);
    }

    [Fact]
    public async Task AWrongCodeOverTheWireIsCountedAndRefused()
    {
        await using var server = new PairingExchangeServer(0, IPAddress.Loopback);
        server.Start();
        server.Code = Code;
        server.Uri = Uri;

        var body = await FetchAsync(server.Port, "/pair?code=MMMMNNNNPPPP");

        Assert.Contains("403 Forbidden", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hexbridge://", body, StringComparison.Ordinal);
        Assert.Equal(0, server.Granted);
        Assert.Equal(1, server.Refused);
    }

    [Fact]
    public async Task TheServerKeepsServingAfterARefusal()
    {
        // The user mistypes, then types it right. A one-shot listener would strand them.
        await using var server = new PairingExchangeServer(0, IPAddress.Loopback);
        server.Start();
        server.Code = Code;
        server.Uri = Uri;

        await FetchAsync(server.Port, "/pair?code=MMMMNNNNPPPP");
        var second = await FetchAsync(server.Port, $"/pair?code={ShortCode.Normalise(Code)}");

        Assert.Contains("200 OK", second, StringComparison.Ordinal);
        Assert.Equal(1, server.Granted);
    }

    [Fact]
    public async Task RotatingTheCodeInvalidatesTheOldOne()
    {
        await using var server = new PairingExchangeServer(0, IPAddress.Loopback);
        server.Start();
        server.Code = Code;
        server.Uri = Uri;

        server.Code = "MMMM-NNNN-PPPP";
        var stale = await FetchAsync(server.Port, $"/pair?code={ShortCode.Normalise(Code)}");

        Assert.Contains("403 Forbidden", stale, StringComparison.Ordinal);
    }

    private static async Task<string> FetchAsync(int port, string path)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();

        var request = Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUser-Agent: HexBridge/1.0 (macOS)\r\n\r\n");
        await stream.WriteAsync(request);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
