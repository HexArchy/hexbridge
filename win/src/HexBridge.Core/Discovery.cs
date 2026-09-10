using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace HexBridge;

/// <summary>
/// The label a host publishes in its TXT record so a Mac can tell «мой ПК» from «чей-то
/// ПК» without either machine revealing anything (PROTOCOL.md, «Автопоиск хоста»).
///
/// <code>tag = base64url( SHA256("hexbridge-discovery-v1" || PSK)[0..16] )</code>
///
/// <para>
/// It publishes neither the name nor the key. A name proves nothing — the owner of the
/// other HexBridge picks it, and «GAMING-PC» is not a rare choice — so a Mac that trusted
/// names would walk into a stranger's session in any dormitory or co-working space. The
/// tag is a one-way hash of 32 random bytes: it cannot be reversed, and it discloses
/// exactly one fact, «эти двое уже связаны», which anyone watching the link already sees
/// from the traffic.
/// </para>
///
/// <para>
/// The domain string is part of the contract with the Mac —
/// <c>mac/Sources/HexBridgeDiscovery/DiscoveryTag.swift</c> hashes the same bytes in the
/// same order. Change it on one side only and every pair silently stops finding itself.
/// </para>
/// </summary>
public static class DiscoveryTag
{
    public const string Domain = "hexbridge-discovery-v1";

    /// <summary>How much of the digest travels. Sixteen bytes is 128 bits of collision room.</summary>
    public const int Bytes = 16;

    /// <summary>Sixteen bytes as unpadded base64url — always exactly this many characters.</summary>
    public const int Length = 22;

    /// <summary>The tag for a raw 32-byte key.</summary>
    public static string For(ReadOnlySpan<byte> key)
    {
        var domain = Encoding.UTF8.GetBytes(Domain);
        var buffer = new byte[domain.Length + key.Length];
        domain.CopyTo(buffer);
        key.CopyTo(buffer.AsSpan(domain.Length));

        var digest = SHA256.HashData(buffer);
        return PairingPayload.ToBase64Url(digest[..Bytes]);
    }

    /// <summary>
    /// The tag for a key the way config.json stores it, or null when there is no usable
    /// key. Null is the honest answer for an unpaired machine and is what stops the Mac
    /// from matching anything: see <see cref="DiscoveryMatch.Choose"/>.
    /// </summary>
    public static string? ForPsk(string? psk)
    {
        if (string.IsNullOrWhiteSpace(psk)) return null;
        try
        {
            var key = Convert.FromBase64String(psk);
            return key.Length == 32 ? For(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares two tags. Ordinal and case-sensitive: base64url is case-significant, and
    /// folding case here would make two different keys look like one.
    ///
    /// <para>
    /// A null or malformed tag never matches anything, including another null. «Мы оба не
    /// знаем свою метку» is not evidence of being paired.
    /// </para>
    /// </summary>
    public static bool Same(string? mine, string? theirs)
    {
        if (!IsWellFormed(mine) || !IsWellFormed(theirs)) return false;
        return string.Equals(mine, theirs, StringComparison.Ordinal);
    }

    /// <summary>The shape a tag must have before it is worth comparing at all.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? tag)
    {
        if (tag is null || tag.Length != Length) return false;
        foreach (var ch in tag)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')) return false;
        }
        return true;
    }
}

/// <summary>
/// The four TXT keys and nothing else (PROTOCOL.md, «Что ещё лежит в TXT»).
///
/// <list type="bullet">
///   <item><c>v</c> — protocol version, integer;</item>
///   <item><c>port</c> — the data port, integer;</item>
///   <item><c>name</c> — the host's display name, UTF-8, shown only in the list;</item>
///   <item><c>tag</c> — the label, see <see cref="DiscoveryTag"/>.</item>
/// </list>
///
/// A record with no <c>tag</c> is legal and means «этот хост ещё не спарен ни с кем»: it
/// belongs in the list an unpaired Mac reads, and can never be auto-connected to.
/// </summary>
public static class DiscoveryTxt
{
    public const string VersionKey = "v";
    public const string PortKey = "port";
    public const string NameKey = "name";
    public const string TagKey = "tag";

    /// <summary>
    /// Builds the entries for one advertisement. <paramref name="tag"/> is omitted rather
    /// than published empty when the host has no key: an empty tag would compare equal to
    /// another empty tag on a reader less careful than <see cref="DiscoveryTag.Same"/>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        int version, int port, string name, string? tag)
    {
        var entries = new List<KeyValuePair<string, string>>(4)
        {
            new(VersionKey, version.ToString(CultureInfo.InvariantCulture)),
            new(PortKey, port.ToString(CultureInfo.InvariantCulture)),
            new(NameKey, Trim(name)),
        };
        if (DiscoveryTag.IsWellFormed(tag)) entries.Add(new KeyValuePair<string, string>(TagKey, tag));
        return entries;
    }

    /// <summary>
    /// Reads the entries of a TXT record — each one a raw <c>key=value</c> string, which is
    /// exactly what the wire carries.
    ///
    /// <para>
    /// Returns false rather than throwing. This data arrives from whatever else is on the
    /// local network, and a neighbour's malformed advertisement must cost us one skipped
    /// list entry, not an exception on a browse callback.
    /// </para>
    /// </summary>
    public static bool TryParse(IEnumerable<string> entries, out DiscoveredHost host)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var equals = entry.IndexOf('=');
            // A TXT string with no '=' is a legal boolean attribute in DNS-SD and carries
            // nothing we need; a leading '=' is a key with no name.
            if (equals <= 0) continue;
            var key = entry[..equals];
            // First value wins, per RFC 6763 §6.4 — a duplicate key must not let a later
            // entry overwrite an earlier one.
            if (!values.ContainsKey(key)) values[key] = entry[(equals + 1)..];
        }
        return TryParse(values, out host);
    }

    /// <summary>Reads an already-split TXT record.</summary>
    public static bool TryParse(IReadOnlyDictionary<string, string> values, out DiscoveredHost host)
    {
        host = default;

        if (!values.TryGetValue(VersionKey, out var rawVersion)
            || !int.TryParse(rawVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        if (!values.TryGetValue(PortKey, out var rawPort)
            || !int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        // A tag that is not the right shape is dropped rather than kept: carrying it
        // forward would only give a comparison somewhere a chance to accept it.
        var tag = values.TryGetValue(TagKey, out var rawTag) && DiscoveryTag.IsWellFormed(rawTag)
            ? rawTag
            : null;

        host = new DiscoveredHost
        {
            Name = values.TryGetValue(NameKey, out var name) ? Trim(name) : "",
            Port = port,
            Tag = tag,
            Version = version,
        };
        return true;
    }

    /// <summary>
    /// A TXT string is at most 255 bytes including its <c>name=</c> prefix, so the value is
    /// cut to fit — on a UTF-8 boundary, because half a character is not a name.
    /// </summary>
    internal static string Trim(string? value)
    {
        var name = (value ?? "").Trim();
        const int Limit = 255 - 5;  // "name="
        while (Encoding.UTF8.GetByteCount(name) > Limit) name = name[..^1];
        return name;
    }
}

/// <summary>One host seen on the local network, as the browser reports it.</summary>
public readonly record struct DiscoveredHost
{
    /// <summary>Display name from TXT. Proves nothing — see <see cref="DiscoveryTag"/>.</summary>
    public string Name { get; init; }

    /// <summary>Filled in once the browse result has been resolved; empty while it has not.</summary>
    public string Address { get; init; }

    public int Port { get; init; }

    /// <summary>The label, or null when the host published none.</summary>
    public string? Tag { get; init; }

    public int Version { get; init; }

    /// <summary>What goes into <c>config.target</c>. Empty until the address is known.</summary>
    public string Target => string.IsNullOrEmpty(Address) ? "" : $"{Address}:{Port}";
}

/// <summary>
/// Everything heard on the wire so far, folded into a list of hosts. No socket anywhere near
/// it, which is what turns «правильно ли мы читаем чужие ответы» into a question with a test
/// rather than an opinion.
///
/// <para>
/// It keeps a running total on purpose. Our own responder puts PTR, SRV, TXT and A into one
/// packet, but nothing in mDNS promises that: a responder may answer a PTR query with a PTR
/// alone and leave the browser to ask for the rest, and an address record can arrive seconds
/// after the instance it belongs to. A parser that only understood whole announcements would
/// work against this project's own advertiser and against very little else.
/// </para>
/// </summary>
public sealed class DiscoveryScan
{
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Instance
    {
        public string HostName = "";
        public IReadOnlyList<string> Text = [];
    }

    /// <summary>Hosts that have said enough about themselves to be worth showing.</summary>
    public IReadOnlyList<DiscoveredHost> Hosts { get; private set; } = [];

    /// <summary>Folds one packet's records in. True when the visible list changed.</summary>
    public bool Apply(IEnumerable<DnsAnswer> answers)
    {
        foreach (var answer in answers)
        {
            switch (answer.Type)
            {
                case DnsRecordType.Ptr when MulticastDns.Same(answer.Name, MulticastDns.ServiceType):
                    if (answer.Target.Length == 0) break;
                    // A goodbye withdraws the instance outright, rather than leaving it in
                    // the list until a TTL nobody here is tracking runs out.
                    if (answer.IsGoodbye) _instances.Remove(answer.Target);
                    else if (!_instances.ContainsKey(answer.Target)) _instances[answer.Target] = new Instance();
                    break;

                case DnsRecordType.Srv:
                    Of(answer.Name).HostName = answer.Target;
                    break;

                case DnsRecordType.Txt:
                    Of(answer.Name).Text = answer.Text;
                    break;

                case DnsRecordType.A when answer.Address is not null:
                    if (answer.IsGoodbye) _addresses.Remove(answer.Name);
                    else _addresses[answer.Name] = answer.Address;
                    break;
            }
        }

        var next = Rebuild();
        if (next.SequenceEqual(Hosts)) return false;
        Hosts = next;
        return true;
    }

    public void Clear()
    {
        _instances.Clear();
        _addresses.Clear();
        Hosts = [];
    }

    /// <summary>
    /// An SRV or TXT for an instance we never saw a PTR for still describes a HexBridge —
    /// a responder answering a direct query need not repeat the pointer — so the instance
    /// is created rather than the record thrown away.
    /// </summary>
    private Instance Of(string name)
    {
        if (_instances.TryGetValue(name, out var existing)) return existing;
        var created = new Instance();
        _instances[name] = created;
        return created;
    }

    private List<DiscoveredHost> Rebuild()
    {
        var hosts = new List<DiscoveredHost>(_instances.Count);

        foreach (var (_, instance) in _instances)
        {
            // No readable TXT means no version, no port and no tag: nothing to dial and
            // nothing to compare, which is exactly the state a half-heard host is in.
            if (!DiscoveryTxt.TryParse(instance.Text, out var host)) continue;

            if (instance.HostName.Length > 0 && _addresses.TryGetValue(instance.HostName, out var address))
            {
                host = host with { Address = address.ToString() };
            }
            hosts.Add(host);
        }

        // Resolved first, then by name: the list is read top down, and a row with no address
        // yet is a row nothing can be done with.
        hosts.Sort((left, right) =>
        {
            var leftResolved = !string.IsNullOrEmpty(left.Address);
            var rightResolved = !string.IsNullOrEmpty(right.Address);
            if (leftResolved != rightResolved) return leftResolved ? -1 : 1;
            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return hosts;
    }
}

/// <summary>Why the Mac did or did not dial one of the hosts it can see.</summary>
public enum DiscoveryVerdict
{
    /// <summary>A host published our own tag. This is the only value that dials anything.</summary>
    Connect,

    /// <summary>Nothing on this network published our tag.</summary>
    NoMatch,

    /// <summary>This machine has no key yet, so it has no tag to compare and must not guess.</summary>
    Unpaired,
}

/// <summary>The outcome of one pass over the browse results.</summary>
public readonly record struct DiscoveryChoice(DiscoveryVerdict Verdict, DiscoveredHost? Host, string Reason)
{
    public bool ShouldConnect => Verdict == DiscoveryVerdict.Connect;
}

/// <summary>
/// The one rule autodiscovery exists to enforce, with no socket anywhere near it so it can
/// be tested for what it is: a security decision.
///
/// <para>
/// A Mac connects automatically <b>only</b> to a host whose tag equals the tag of its own
/// key. A stranger's host has a different key, so a different tag, so it does not exist as
/// far as this machine is concerned — no matter what name it advertises, how many of them
/// there are, or whether ours is among them.
/// </para>
///
/// <para>
/// A Mac with no key has no tag, and therefore never connects on its own at all. It still
/// gets the list — that is what saves the user typing an address — but turning that list
/// into a pairing needs the short code off the host's screen. Without that rule the first
/// stranger's host on the network would become «свой», which is the whole thing being
/// guarded against.
/// </para>
///
/// <para>
/// Mirrored in <c>mac/Sources/HexBridgeDiscovery/DiscoveryMatch.swift</c>; the two are kept
/// honest by the same vectors on both sides.
/// </para>
/// </summary>
public static class DiscoveryMatch
{
    public static DiscoveryChoice Choose(string? ownTag, IReadOnlyList<DiscoveredHost> hosts)
    {
        if (!DiscoveryTag.IsWellFormed(ownTag))
        {
            return new DiscoveryChoice(
                DiscoveryVerdict.Unpaired,
                null,
                "этот Mac ещё не связан ни с одним ПК — нужен короткий код с экрана ПК");
        }

        foreach (var host in hosts)
        {
            if (!DiscoveryTag.Same(ownTag, host.Tag)) continue;
            // An address is what makes a match usable; a resolved-later result is not a
            // match yet, and taking it would blank out a working target.
            if (string.IsNullOrEmpty(host.Address)) continue;
            return new DiscoveryChoice(DiscoveryVerdict.Connect, host, $"метка совпала: {host.Target}");
        }

        return new DiscoveryChoice(
            DiscoveryVerdict.NoMatch,
            null,
            hosts.Count == 0
                ? "в сети не видно ни одного HexBridge"
                : $"в сети {hosts.Count} HexBridge, но ни один из них не наш");
    }

    /// <summary>
    /// The new value for <c>target</c>, or null when nothing should move.
    ///
    /// <para>
    /// This is the whole of the «переехал на другой IP» fix. The tag is not tied to an
    /// address, so a host that comes back on a different one after the router handed out a
    /// new lease is still the same host, and re-pairing it would be asking the user to fix
    /// something that fixed itself. Returning null when the address has not changed is what
    /// keeps that from restarting the pipeline every browse cycle.
    /// </para>
    /// </summary>
    public static string? Retarget(string? currentTarget, DiscoveryChoice choice)
    {
        if (!choice.ShouldConnect || choice.Host is not { } host) return null;

        var target = host.Target;
        if (target.Length == 0) return null;
        return string.Equals(currentTarget?.Trim(), target, StringComparison.OrdinalIgnoreCase) ? null : target;
    }
}

/// <summary>
/// Keeps one <c>_hexbridge._udp</c> advertisement alive for as long as the receiver is
/// running, and republishes it when what it says stops being true.
///
/// <para>
/// The wizard is not enough. Pairing happens once; the address changes every time the
/// router hands out a new lease, and the Mac has to be able to find the host again then —
/// which it can only do if the host is still advertising long after the wizard closed.
/// That is the difference between «вчера работало, сегодня нет» and a reconnection nobody
/// notices.
/// </para>
///
/// <para>
/// Republishing is a full stop and start: <see cref="ServiceAdvertiser"/> builds its
/// packets from fields fixed at construction, and a goodbye followed by a fresh
/// announcement is also what makes the Mac drop the stale record instead of holding both.
/// </para>
/// </summary>
public sealed class DiscoveryPublisher : IDisposable
{
    private readonly Action<LogLevel, string>? _log;
    private readonly Lock _gate = new();

    private ServiceAdvertiser? _advertiser;
    private (string Name, int Port, string? Tag, string Addresses)? _published;
    private ReceiverConfig? _config;
    private string _machineName = "";
    private bool _watching;
    private bool _disposed;

    public DiscoveryPublisher(Action<LogLevel, string>? log = null) => _log = log;

    /// <summary>Null while nothing is being advertised, which is not an error.</summary>
    public string? Error { get; private set; }

    public bool IsPublishing => _advertiser?.IsPublishing == true;

    /// <summary>The tag currently on the wire, for the diagnostics page.</summary>
    public string? Tag => _published?.Tag;

    /// <summary>
    /// Publishes what this config describes. Cheap to call repeatedly: an advertisement
    /// that already says the right thing is left alone.
    /// </summary>
    public void Publish(ReceiverConfig config, string machineName)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _config = config;
            _machineName = machineName;

            if (!_watching)
            {
                // A laptop that joins Wi-Fi after the app started, a router that hands out
                // a new lease, a VPN going up: the A record we published names an address
                // this machine no longer has, and nothing about that is visible from here.
                // The Mac would find the host and dial a dead address, which looks exactly
                // like «HexBridge сломался».
                NetworkChange.NetworkAddressChanged += OnAddressChanged;
                _watching = true;
            }

            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        if (_config is not { } config) return;

        var port = PortOf(config);
        var tag = DiscoveryTag.ForPsk(config.Psk);
        var addresses = MulticastDns.LocalAddresses();
        var wanted = (
            Name: _machineName,
            Port: port,
            Tag: tag,
            Addresses: string.Join(",", addresses));
        if (_published == wanted && IsPublishing) return;

        StopLocked();

        var advertiser = new ServiceAdvertiser(
            _machineName, port, addresses,
            txt: DiscoveryTxt.Build(PairingPayload.Version, port, _machineName, tag));
        advertiser.Start();

        _advertiser = advertiser;
        _published = wanted;
        Error = advertiser.Error;

        if (Error is not null)
        {
            _log?.Invoke(LogLevel.Warning, $"hexbridge: автопоиск не поднялся — {Error}");
            return;
        }

        _log?.Invoke(
            LogLevel.Info,
            tag is null
                ? $"hexbridge: объявляю себя в сети как «{_machineName}» на порту {port}, без метки — ключа ещё нет"
                : $"hexbridge: объявляю себя в сети как «{_machineName}» на порту {port}, метка {tag}");
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            PublishLocked();
        }
    }

    /// <summary>Takes the advertisement off the network, goodbye packet and all.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _config = null;
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _advertiser?.Dispose();
        _advertiser = null;
        _published = null;
        Error = null;
    }

    private static int PortOf(ReceiverConfig config)
    {
        try
        {
            return ReceiverConfig.ParseEndpoint(config.Listen, PairingPayload.DefaultPort).Port;
        }
        catch (Exception)
        {
            return PairingPayload.DefaultPort;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_watching)
            {
                NetworkChange.NetworkAddressChanged -= OnAddressChanged;
                _watching = false;
            }
            _config = null;
            StopLocked();
        }
    }
}
