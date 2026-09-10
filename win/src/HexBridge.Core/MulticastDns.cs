using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HexBridge;

/// <summary>Record types this responder knows. Everything else is answered with silence.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Srv = 33,
    Any = 255,
}

/// <summary>One question out of an mDNS query packet.</summary>
public readonly record struct DnsQuestion(string Name, DnsRecordType Type, ushort Class)
{
    /// <summary>
    /// True when the querier asked for a unicast answer (the top bit of QCLASS, RFC 6762
    /// §5.4). We answer to multicast either way — a legacy resolver is not a case that
    /// arises between two machines running this app — but the bit has to be masked off
    /// before the class itself is compared, or every question looks like a mismatch.
    /// </summary>
    public bool WantsUnicast => (Class & 0x8000) != 0;

    public ushort ClassValue => (ushort)(Class & 0x7FFF);
}

/// <summary>
/// Just enough of DNS to publish one Bonjour service, and not one byte more.
///
/// <para>
/// The Mac browses for <c>_hexbridge._udp</c> (<c>ReceiverDiscovery.serviceType</c> in
/// <c>mac/Sources/HexBridge/Core/Discovery.swift</c>) and until Windows answers that
/// browse, the Mac's list is honestly empty. Apple's Bonjour for Windows would do this,
/// but it is a separate download most gaming PCs do not have, so the responder is written
/// out: an mDNS answer is a header, a name, and four record types.
/// </para>
///
/// <para>
/// This is a responder, not a resolver. It answers PTR/SRV/TXT/A for its own names and
/// ignores everything else — no cache, no conflict probing, no known-answer suppression.
/// The cost of that simplicity is that two HexBridge PCs on one network with the same
/// machine name would advertise the same instance; the instance name carries the machine
/// name, so that means two PCs called the same thing, which the user has bigger problems
/// with than Bonjour.
/// </para>
/// </summary>
public static class MulticastDns
{
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;

    /// <summary>The service type both sides agree on. UDP, because the data channel is.</summary>
    public const string ServiceType = "_hexbridge._udp.local.";

    /// <summary>The meta-query a browser uses to enumerate service types.</summary>
    public const string ServiceEnumeration = "_services._dns-sd._udp.local.";

    /// <summary>Shared records live long; the ones tied to this host are re-announced often.</summary>
    public const uint SharedTtl = 4500;
    public const uint HostTtl = 120;

    /// <summary>Set on records this host claims as unique, so a stale copy is flushed.</summary>
    private const ushort CacheFlush = 0x8000;
    private const ushort ClassIn = 0x0001;

    // MARK: - Names

    /// <summary>
    /// Writes a name as length-prefixed labels. Never compressed: a pointer would save
    /// forty bytes in a packet sent once every few minutes, and every parser bug in this
    /// file would then be a bug in the writer as well as the reader.
    /// </summary>
    public static void WriteName(List<byte> output, string name)
    {
        foreach (var label in Labels(name))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"метка «{label}» не помещается в DNS-имя", nameof(name));
            }
            output.Add((byte)bytes.Length);
            output.AddRange(bytes);
        }
        output.Add(0);
    }

    /// <summary>
    /// Splits on unescaped dots. A dot inside a Bonjour instance name is escaped as
    /// <c>\.</c> — machine names like «Никита.ПК» are legal and must stay one label.
    /// </summary>
    public static IReadOnlyList<string> Labels(string name)
    {
        var labels = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        foreach (var ch in name)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            switch (ch)
            {
                case '\\':
                    escaped = true;
                    break;
                case '.':
                    if (current.Length > 0) labels.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0) labels.Add(current.ToString());
        return labels;
    }

    /// <summary>Escapes a machine name so it survives as a single label.</summary>
    public static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace(".", "\\.");

    /// <summary>
    /// Reads a name, following compression pointers. Returns false rather than throwing on
    /// a malformed packet: this socket receives whatever the local network sends it, and a
    /// pointer loop from a broken responder elsewhere must not take the app down.
    /// </summary>
    public static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        var builder = new StringBuilder(64);
        var cursor = offset;
        var afterPointer = -1;
        // A pointer may only ever point backwards, so bounding the hops by the packet
        // length is enough to make a loop terminate.
        var hops = 0;

        while (cursor < packet.Length)
        {
            var length = packet[cursor];

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++hops > packet.Length) break;
                var target = ((length & 0x3F) << 8) | packet[cursor + 1];
                if (afterPointer < 0) afterPointer = cursor + 2;
                if (target >= packet.Length) break;
                cursor = target;
                continue;
            }

            if ((length & 0xC0) != 0) break;

            cursor++;
            if (length == 0)
            {
                offset = afterPointer >= 0 ? afterPointer : cursor;
                name = builder.ToString();
                return true;
            }

            if (cursor + length > packet.Length) break;
            builder.Append(EscapeLabel(Encoding.UTF8.GetString(packet.Slice(cursor, length)))).Append('.');
            cursor += length;
        }

        name = "";
        return false;
    }

    // MARK: - Queries

    /// <summary>
    /// Pulls the questions out of a packet. Answers and authority sections are skipped
    /// entirely — a responder this small has no cache for them to update.
    /// </summary>
    public static bool TryReadQuestions(ReadOnlySpan<byte> packet, out IReadOnlyList<DnsQuestion> questions)
    {
        questions = [];
        if (packet.Length < 12) return false;

        var flags = (ushort)((packet[2] << 8) | packet[3]);
        // QR set means this is somebody else's answer, not a question for us.
        if ((flags & 0x8000) != 0) return false;

        var count = (packet[4] << 8) | packet[5];
        if (count == 0) return false;

        var offset = 12;
        var found = new List<DnsQuestion>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(packet, ref offset, out var name)) return false;
            if (offset + 4 > packet.Length) return false;

            var type = (ushort)((packet[offset] << 8) | packet[offset + 1]);
            var klass = (ushort)((packet[offset + 2] << 8) | packet[offset + 3]);
            offset += 4;
            found.Add(new DnsQuestion(name, (DnsRecordType)type, klass));
        }

        questions = found;
        return true;
    }

    // MARK: - Answers

    /// <summary>
    /// The full advertisement: PTR in the answer section, SRV, TXT and A alongside it, so a
    /// browser has an address without a second round trip.
    /// </summary>
    /// <param name="ttl">
    /// Zero makes this a goodbye packet (RFC 6762 §10.1), which is what lets a Mac drop the
    /// PC from its list the moment the wizard closes instead of an hour later.
    /// </param>
    public static byte[] BuildAnnouncement(
        string instance,
        string host,
        int port,
        IReadOnlyList<IPAddress> addresses,
        IReadOnlyList<KeyValuePair<string, string>> txt,
        uint ttl = SharedTtl)
    {
        var answers = new List<byte>();
        var additional = new List<byte>();
        var answerCount = 0;
        var additionalCount = 0;

        // PTR is a shared record: several instances may legitimately answer the same
        // browse, so it never carries the cache-flush bit.
        WriteRecord(answers, ServiceType, DnsRecordType.Ptr, ClassIn, ttl, rdata =>
            WriteName(rdata, instance));
        answerCount++;

        if (ttl > 0)
        {
            WriteRecord(additional, instance, DnsRecordType.Srv, ClassIn | CacheFlush, HostTtl, rdata =>
            {
                rdata.Add(0); rdata.Add(0);                       // priority
                rdata.Add(0); rdata.Add(0);                       // weight
                rdata.Add((byte)(port >> 8)); rdata.Add((byte)port);
                WriteName(rdata, host);
            });
            additionalCount++;

            WriteRecord(additional, instance, DnsRecordType.Txt, ClassIn | CacheFlush, SharedTtl, rdata =>
                WriteTxt(rdata, txt));
            additionalCount++;

            foreach (var address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                WriteRecord(additional, host, DnsRecordType.A, ClassIn | CacheFlush, HostTtl, rdata =>
                    rdata.AddRange(address.GetAddressBytes()));
                additionalCount++;
            }
        }

        var packet = new List<byte>(answers.Count + additional.Count + 12);
        // ID zero, QR + AA: an unsolicited authoritative answer, which is what an
        // announcement is.
        packet.AddRange([0, 0, 0x84, 0x00, 0, 0]);
        packet.Add((byte)(answerCount >> 8));
        packet.Add((byte)answerCount);
        packet.AddRange([0, 0]);
        packet.Add((byte)(additionalCount >> 8));
        packet.Add((byte)additionalCount);
        packet.AddRange(answers);
        packet.AddRange(additional);
        return [.. packet];
    }

    /// <summary>
    /// True when a question is one this responder should answer: the browse itself, the
    /// meta-query that enumerates service types, or a direct look-up of our own names.
    /// </summary>
    public static bool Matches(DnsQuestion question, string instance, string host)
    {
        if (question.ClassValue != ClassIn && question.ClassValue != 0x00FF) return false;

        return question.Type switch
        {
            DnsRecordType.Ptr or DnsRecordType.Any =>
                Same(question.Name, ServiceType) || Same(question.Name, ServiceEnumeration),
            DnsRecordType.Srv or DnsRecordType.Txt => Same(question.Name, instance),
            DnsRecordType.A => Same(question.Name, host),
            _ => false,
        };
    }

    /// <summary>DNS names are case-insensitive and the trailing root dot is optional.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static void WriteRecord(
        List<byte> output, string name, DnsRecordType type, ushort klass, uint ttl, Action<List<byte>> writeRdata)
    {
        WriteName(output, name);
        output.Add((byte)((ushort)type >> 8));
        output.Add((byte)type);
        output.Add((byte)(klass >> 8));
        output.Add((byte)klass);
        output.Add((byte)(ttl >> 24));
        output.Add((byte)(ttl >> 16));
        output.Add((byte)(ttl >> 8));
        output.Add((byte)ttl);

        var rdata = new List<byte>(32);
        writeRdata(rdata);
        output.Add((byte)(rdata.Count >> 8));
        output.Add((byte)rdata.Count);
        output.AddRange(rdata);
    }

    /// <summary>
    /// TXT rdata: one length-prefixed <c>key=value</c> per entry. An empty set still needs
    /// one zero-length string, because rdata of length zero is not a legal TXT record.
    /// </summary>
    internal static void WriteTxt(List<byte> rdata, IReadOnlyList<KeyValuePair<string, string>> txt)
    {
        if (txt.Count == 0)
        {
            rdata.Add(0);
            return;
        }

        foreach (var (key, value) in txt)
        {
            var bytes = Encoding.UTF8.GetBytes($"{key}={value}");
            if (bytes.Length > 255) continue;
            rdata.Add((byte)bytes.Length);
            rdata.AddRange(bytes);
        }
    }

    /// <summary>
    /// Every IPv4 address this machine can be reached on, loopback and link-local aside.
    /// The first entry is the one the wizard puts in the pairing URI, so the ordering is
    /// not cosmetic: an Ethernet address is preferred over Wi-Fi, and both over anything
    /// a VPN or a hypervisor bridge added.
    /// </summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var found = new List<(int Rank, IPAddress Address)>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var rank = adapter.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2,
                };

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    // 169.254/16 means DHCP failed; a Mac cannot route to it either.
                    if (address.GetAddressBytes() is [169, 254, ..]) continue;
                    found.Add((rank, address));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapters we can read. The wizard falls back to asking the user.
        }

        return [.. found.OrderBy(entry => entry.Rank).Select(entry => entry.Address)];
    }
}

/// <summary>
/// Publishes one <c>_hexbridge._udp</c> instance for as long as it is alive.
///
/// Failure is reported through <see cref="Error"/> rather than thrown: Apple's Bonjour
/// service, if it is installed, already owns UDP 5353 exclusively on some Windows builds,
/// and losing autodiscovery must degrade the wizard to «use the QR code» rather than break
/// it. The Mac's own browser makes the same promise in the other direction.
/// </summary>
public sealed class ServiceAdvertiser : IDisposable
{
    private readonly string _instance;
    private readonly string _host;
    private readonly int _port;
    private readonly IReadOnlyList<IPAddress> _addresses;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _txt;
    private readonly CancellationTokenSource _stop = new();

    private UdpClient? _socket;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// The local addresses the group was joined on, one per interface that took it.
    ///
    /// Joining «the default interface» is not good enough on the machine this runs on. A
    /// gaming PC routinely carries a Hyper-V switch, a WSL adapter and a VPN, any of which
    /// can be the one the stack picks — and an advertisement that goes out on a virtual
    /// network reaches nobody, with no error and nothing in a log to say so. Announcing on
    /// every interface costs three small packets and removes the whole class of «Mac его не
    /// видит, а пинг проходит».
    /// </summary>
    private readonly List<IPAddress> _interfaces = [];

    public string? Error { get; private set; }
    public bool IsPublishing => _socket is not null;

    /// <summary>Answers sent, so the UI can say «Mac нас видит» rather than guess.</summary>
    public int AnswersSent { get; private set; }

    public string InstanceName => _instance;

    public ServiceAdvertiser(
        string machineName,
        int port,
        IReadOnlyList<IPAddress>? addresses = null,
        IReadOnlyList<KeyValuePair<string, string>>? txt = null)
    {
        var label = MulticastDns.EscapeLabel(Trim(machineName));
        _instance = $"{label}.{MulticastDns.ServiceType}";
        _host = $"{Sanitise(machineName)}.local.";
        _port = port;
        _addresses = addresses ?? MulticastDns.LocalAddresses();
        _txt = txt ?? [];
    }

    public void Start()
    {
        if (_socket is not null) return;

        try
        {
            var socket = new UdpClient();
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastDns.Port));

            foreach (var local in MulticastDns.LocalAddresses())
            {
                try
                {
                    socket.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MulticastDns.Group, local));
                    _interfaces.Add(local);
                }
                catch (SocketException)
                {
                    // An adapter that went away between being enumerated and being joined,
                    // or one that does not do multicast. The others still work.
                }
            }

            if (_interfaces.Count == 0) socket.JoinMulticastGroup(MulticastDns.Group);

            // RFC 6762 §11: mDNS is sent with IP TTL 255, and responders are entitled to
            // drop anything else. `Ttl` is the unicast one and does not cover this.
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.Ttl = 255;
            _socket = socket;
            Error = null;
        }
        catch (SocketException ex)
        {
            Error = $"порт {MulticastDns.Port} занят другой службой — {ex.Message}";
            return;
        }

        _loop = Task.Run(() => ListenAsync(_stop.Token));
        _ = Task.Run(() => AnnounceAsync(_stop.Token));
    }

    /// <summary>
    /// RFC 6762 §8.3 asks for two to eight announcements a second apart. Two is enough
    /// between a Mac and a PC in the same room and does not spam a busy network.
    /// </summary>
    private async Task AnnounceAsync(CancellationToken token)
    {
        for (var i = 0; i < 2 && !token.IsCancellationRequested; i++)
        {
            Send(MulticastDns.BuildAnnouncement(_instance, _host, _port, _addresses, _txt));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(token);
            }
            catch (Exception)
            {
                return;
            }

            if (!MulticastDns.TryReadQuestions(received.Buffer, out var questions)) continue;
            if (!questions.Any(q => MulticastDns.Matches(q, _instance, _host))) continue;

            Send(MulticastDns.BuildAnnouncement(_instance, _host, _port, _addresses, _txt));
            AnswersSent++;
        }
    }

    private void Send(byte[] packet)
    {
        var socket = _socket;
        if (socket is null) return;

        var target = new IPEndPoint(MulticastDns.Group, MulticastDns.Port);

        if (_interfaces.Count == 0)
        {
            try
            {
                socket.Send(packet, packet.Length, target);
            }
            catch (Exception)
            {
                // A network that came and went. The next announcement will find out.
            }
            return;
        }

        foreach (var local in _interfaces)
        {
            try
            {
                // The outgoing interface has to be chosen per send: a socket has one
                // IP_MULTICAST_IF, and leaving it at the stack's default is what puts the
                // advertisement on a Hyper-V switch nobody is listening to.
                socket.Client.SetSocketOption(
                    SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.Send(packet, packet.Length, target);
            }
            catch (Exception)
            {
                // One adapter unplugged mid-announcement must not silence the others.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_socket is not null)
        {
            // Goodbye first, while the socket is still open: without it the Mac keeps the
            // PC in its list until the TTL runs out.
            Send(MulticastDns.BuildAnnouncement(_instance, _host, _port, _addresses, _txt, ttl: 0));
        }

        _stop.Cancel();
        _socket?.Dispose();
        _socket = null;
        _stop.Dispose();
        _ = _loop;
    }

    /// <summary>A Bonjour instance name is at most 63 UTF-8 bytes.</summary>
    private static string Trim(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "HexBridge" : value.Trim();
        while (Encoding.UTF8.GetByteCount(name) > 63) name = name[..^1];
        return name;
    }

    /// <summary>
    /// A host name is not an instance name: it has to survive being typed into a resolver,
    /// so it keeps letters, digits and hyphens and nothing else.
    /// </summary>
    private static string Sanitise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            else if (ch is '-' or ' ' or '_') builder.Append('-');
        }

        var name = builder.ToString().Trim('-');
        return name.Length == 0 ? "hexbridge" : name[..Math.Min(name.Length, 63)];
    }
}
