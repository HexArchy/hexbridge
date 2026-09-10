using System.Net;
using System.Net.Sockets;
using System.Text;

using HexBridge.Localization;

namespace HexBridge;

/// <summary>
/// The answer to one short-code request, decided without a socket in sight so the rule
/// «right code gets the URI, anything else gets nothing» can be tested directly.
/// </summary>
public readonly record struct ExchangeAnswer(int Status, string Reason, string Body)
{
    public bool IsGranted => Status == 200;
}

/// <summary>
/// The Windows half of the short-code exchange (DESIGN.md §9.1, method 3).
///
/// <para>
/// A twelve-character code cannot carry a 32-byte key, so it is a one-time ticket instead:
/// the wizard holds a listener open for three minutes and hands the full pairing URI to
/// whoever presents the right code. The Mac's client is
/// <c>PairingExchange.fetch</c> in <c>mac/Sources/HexBridge/Core/Pairing.swift</c>, and it
/// pins every detail of what follows:
/// </para>
/// <list type="bullet">
///   <item>plain HTTP, <c>GET /pair?code=…</c>, on <see cref="PairingPayload.ExchangePort"/>;</item>
///   <item>the code arrives already normalised — upper case, alphabet only, no hyphens;</item>
///   <item>the body is the pairing URI as UTF-8 text and nothing else, because the Mac
///         feeds the whole body straight to <c>Pairing.parse(text:)</c>;</item>
///   <item>a wrong or unknown code must answer <b>403</b> or <b>404</b> — those two are the
///         only statuses the Mac turns into «ПК не принял код» rather than into the much
///         vaguer «ПК ответил, но не ссылкой связывания».</item>
/// </list>
/// </summary>
public static class PairingExchangeProtocol
{
    /// <summary>The path the Mac asks for. Anything else is not our business.</summary>
    public const string Path = "/pair";

    /// <summary>What a browser or a port scanner gets: nothing, in as few bytes as possible.</summary>
    public static readonly ExchangeAnswer NotFound = new(404, "Not Found", "");

    private static readonly ExchangeAnswer Refused = new(403, "Forbidden", "");
    private static readonly ExchangeAnswer BadMethod = new(405, "Method Not Allowed", "");

    /// <summary>
    /// Decides what to send back for one HTTP request line, e.g.
    /// <c>GET /pair?code=ABCDEFGHJKLM HTTP/1.1</c>.
    /// </summary>
    /// <param name="requestLine">The first line of the request, without its CRLF.</param>
    /// <param name="expectedCode">The code the wizard is currently showing.</param>
    /// <param name="uri">The payload to hand over when the code matches.</param>
    public static ExchangeAnswer Answer(string? requestLine, string expectedCode, string uri)
    {
        if (string.IsNullOrWhiteSpace(requestLine)) return NotFound;

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return NotFound;

        // HEAD is answered like GET minus the body by the writer below; anything that
        // could change state is refused outright.
        if (!parts[0].Equals("GET", StringComparison.Ordinal)
            && !parts[0].Equals("HEAD", StringComparison.Ordinal))
        {
            return BadMethod;
        }

        var target = parts[1];
        var query = "";
        var mark = target.IndexOf('?');
        if (mark >= 0)
        {
            query = target[(mark + 1)..];
            target = target[..mark];
        }

        if (!target.Equals(Path, StringComparison.Ordinal)) return NotFound;

        var expected = ShortCode.Normalise(expectedCode);
        // An empty expected code would otherwise match an empty request — the wizard is
        // not showing a code, so there is nothing to hand out.
        if (expected.Length != ShortCode.Length) return NotFound;

        var presented = ShortCode.Normalise(Parameter(query, "code"));
        // Fixed-length, non-secret comparison: the code is a one-time ticket guarded by a
        // three-minute window, not a password, but there is no reason to leak its prefix
        // through timing either.
        return TimeSafeEquals(presented, expected)
            ? new ExchangeAnswer(200, "OK", uri)
            : Refused;
    }

    /// <summary>The raw bytes of a whole HTTP/1.1 response, ready for the socket.</summary>
    public static byte[] Render(ExchangeAnswer answer)
    {
        var body = Encoding.UTF8.GetBytes(answer.Body);
        var head = new StringBuilder(160)
            .Append("HTTP/1.1 ").Append(answer.Status).Append(' ').Append(answer.Reason).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        var headBytes = Encoding.ASCII.GetBytes(head);
        var result = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(result, 0);
        body.CopyTo(result, headBytes.Length);
        return result;
    }

    /// <summary>First value of a query parameter, percent-decoded. Missing reads as empty.</summary>
    internal static string Parameter(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            if (!pair[..equals].Equals(name, StringComparison.Ordinal)) continue;

            try
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return "";
            }
        }
        return "";
    }

    private static bool TimeSafeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var difference = 0;
        for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
        return difference == 0;
    }
}

/// <summary>
/// The three-minute listener behind <see cref="PairingExchangeProtocol"/>.
///
/// A raw <see cref="TcpListener"/> rather than <c>HttpListener</c> on purpose: an
/// HttpListener prefix that is not <c>localhost</c> needs a URL ACL, which needs an
/// elevated <c>netsh</c>, and asking the user for administrator rights in the middle of
/// the pairing wizard would be a worse experience than the manual config it replaces.
/// </summary>
public sealed class PairingExchangeServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Bumped every time a code was accepted and the payload handed over.</summary>
    public int Granted { get; private set; }

    /// <summary>Bumped on every request that presented the wrong code.</summary>
    public int Refused { get; private set; }

    /// <summary>Raised on the listener thread once a Mac has taken the payload.</summary>
    public event Action? Paired;

    /// <summary>The code currently on screen. Rotating it invalidates the old one at once.</summary>
    public string Code { get; set; } = "";

    /// <summary>The payload handed over on a match.</summary>
    public string Uri { get; set; } = "";

    /// <summary>
    /// The port actually bound. Only meaningful once <see cref="Start"/> has returned,
    /// which is what makes port 0 — «let the OS choose» — usable.
    /// </summary>
    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    public PairingExchangeServer(int port, IPAddress? bind = null) =>
        _listener = new TcpListener(bind ?? IPAddress.Any, port);

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptAsync(_stop.Token));
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                // Cancelled, or the listener was closed under us. Either way there is
                // nothing left to accept.
                return;
            }

            // One request at a time. The wizard expects exactly one Mac, and serialising
            // means a stalled client cannot be used to hold connections open in bulk.
            try
            {
                await ServeAsync(client, token);
            }
            catch (Exception)
            {
                // A half-open socket is not worth reporting: the Mac will retry, and the
                // wizard already shows whether a pairing arrived.
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // A client that connects and says nothing must not pin the loop.
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        await using var stream = client.GetStream();
        var requestLine = await ReadRequestAsync(stream, timeout.Token);
        var answer = PairingExchangeProtocol.Answer(requestLine, Code, Uri);

        await stream.WriteAsync(PairingExchangeProtocol.Render(answer), timeout.Token);
        await stream.FlushAsync(timeout.Token);

        // Close the sending half explicitly and let the peer see EOF. Closing the whole
        // socket outright is what Windows turns into an RST when anything is still sitting
        // unread in the receive queue, and an RST throws away the response we just wrote —
        // the client then fails with WSAECONNRESET instead of reading it. macOS is forgiving
        // here, so this only ever showed up on the target platform.
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // The peer may already be gone; the answer was still delivered.
        }

        if (answer.IsGranted)
        {
            Granted++;
            Paired?.Invoke();
        }
        else if (answer.Status == 403)
        {
            Refused++;
        }
    }

    /// <summary>
    /// Returns the request line and consumes the headers that follow it.
    ///
    /// Only the first line carries meaning, but the rest still has to leave the receive
    /// queue: data left unread there is exactly what makes Windows reset the connection on
    /// close. The headers are counted rather than kept, so a client cannot make this process
    /// hold an unbounded string.
    /// </summary>
    private static async Task<string> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var builder = new StringBuilder(128);
        var buffer = new byte[1];
        var consumed = 0;
        var blankRun = 0;
        var inRequestLine = true;

        while (consumed < 8192)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            consumed++;

            var c = (char)buffer[0];
            if (c == '\r') continue;

            if (c == '\n')
            {
                if (inRequestLine)
                {
                    inRequestLine = false;
                    blankRun = 1;
                    continue;
                }

                // A second newline in a row ends the header block.
                if (++blankRun >= 2) break;
                continue;
            }

            blankRun = 0;
            if (inRequestLine && builder.Length < 2048) builder.Append(c);
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // Shutting down; the loop's own exceptions are already swallowed above.
            }
        }
        _stop.Dispose();
    }
}

/// <summary>
/// The client side of the short-code exchange — the half the Mac has always had and Windows
/// has never needed, because Windows was always the machine holding the code.
///
/// <para>
/// It is a deliberate transcription of <c>PairingExchange.fetch</c> in
/// <c>mac/Sources/HexBridge/Core/Pairing.swift</c>, down to which status codes mean «код не
/// подошёл» rather than «ПК ответил не тем». Two clients that disagree about that would
/// disagree in front of a user holding a code that works.
/// </para>
/// </summary>
public static class PairingExchangeClient
{
    /// <summary>Long enough for a machine on the same network, short enough to retry by hand.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>What one attempt turned out to be: a payload, or a sentence to show.</summary>
    public readonly record struct Result(PairingPayload? Payload, string? Error)
    {
        public bool IsOk => Payload is not null;
    }

    /// <summary>
    /// Trades a short code for the full pairing payload. <paramref name="dataPort"/> is the
    /// audio port from the advertisement; the exchange answers one above it.
    /// </summary>
    public static async Task<Result> FetchAsync(
        string host, int dataPort, string code, HttpMessageHandler? handler = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return new Result(null, Strings.Err_Exchange_NoHost);
        if (!ShortCode.IsComplete(code)) return new Result(null, Strings.Err_Exchange_BadCode);

        var port = PairingPayload.ExchangePort(dataPort);
        var url = $"http://{Bracketed(host)}:{port}{PairingExchangeProtocol.Path}?code={ShortCode.Normalise(code)}";

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HexBridge/1.0 (Windows)");

        HttpResponseMessage response;
        string body;
        try
        {
            response = await client.GetAsync(url, token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return new Result(null, Loc.F(Strings.Err_Exchange_Timeout, Loc.Seconds(Timeout.TotalSeconds)));
        }
        catch (HttpRequestException ex)
        {
            return new Result(null, Loc.F(Strings.Err_Exchange_Failed, host, ex.Message));
        }

        // These two and only these two mean the code was wrong. Anything else is a machine
        // that answered but is not the one we were looking for, and saying «неверный код» to
        // that sends the user off retyping a code that was fine.
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return new Result(null, Strings.Err_Exchange_Rejected);
        }

        if (!PairingPayload.TryParse(body, out var payload, out var error))
        {
            return new Result(null, Loc.F(Strings.Err_Exchange_BadReply, error));
        }

        return new Result(payload, null);
    }

    /// <summary>An IPv6 literal has to be bracketed before it can be part of a URL.</summary>
    private static string Bracketed(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
