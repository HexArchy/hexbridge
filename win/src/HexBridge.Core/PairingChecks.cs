using System.Net;
using System.Net.Sockets;

using HexBridge.Localization;

namespace HexBridge;

/// <summary>What one line of the connection check turned out to be.</summary>
public enum CheckState
{
    /// <summary>Not reached yet. The lines appear one at a time, ≥250 ms apart (§9.4).</summary>
    Pending,

    /// <summary>Running. The only state that shows a spinner.</summary>
    Running,

    Passed,
    Failed,

    /// <summary>Nothing to check — the feature is switched off, which is not a failure.</summary>
    Skipped,
}

/// <summary>One line of §9.4: a state and the short piece of fact beside it.</summary>
public readonly record struct CheckOutcome(CheckState State, string Detail)
{
    public static CheckOutcome Pass(string detail) => new(CheckState.Passed, detail);
    public static CheckOutcome Fail(string detail) => new(CheckState.Failed, detail);
    public static CheckOutcome Skip(string detail) => new(CheckState.Skipped, detail);
}

/// <summary>
/// The six checks from DESIGN.md §9.4, decided over plain values rather than over live
/// objects.
///
/// <para>
/// Written this way on purpose: the wizard's job is to say something true about the link
/// between two machines, and «the fifth line said звук идёт when the receiver had heard
/// nothing» is the kind of bug that only ever shows up in front of a user. Deciding over
/// primitives is what lets every branch be exercised here instead.
/// </para>
///
/// <para>
/// The wording follows §10.1: what happened, why, what to do — never «Ошибка», never an
/// exclamation mark, never blaming the person holding the mouse.
/// </para>
/// </summary>
public static class PairingChecks
{
    /// <summary>How long §9.4 waits for a packet before calling it silence.</summary>
    public static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Five seconds of «скажите что-нибудь вслух», with a countdown.</summary>
    public static readonly TimeSpan SoundWindow = TimeSpan.FromSeconds(5);

    /// <summary>Below this the receiver heard the room, not a voice.</summary>
    public const float SilenceDbfs = -50f;

    /// <summary>1. The address resolves to something the Mac can dial.</summary>
    public static CheckOutcome Address(string? listen)
    {
        if (string.IsNullOrWhiteSpace(listen)) return CheckOutcome.Fail(Strings.Check_Detail_NoAddress);

        try
        {
            var endpoint = ReceiverConfig.ParseEndpoint(listen, PairingPayload.DefaultPort);

            // 0.0.0.0 is a perfectly good thing to listen on and a useless thing to dial,
            // so the line reports the routable address instead of echoing the wildcard.
            if (!Equals(endpoint.Address, IPAddress.Any)) return CheckOutcome.Pass(listen);

            var address = MulticastDns.LocalAddresses().FirstOrDefault();
            return address is null
                ? CheckOutcome.Fail(Strings.Check_Detail_NoNetwork)
                : CheckOutcome.Pass($"{address}:{endpoint.Port}");
        }
        catch (SocketException)
        {
            return CheckOutcome.Fail(Strings.Check_Detail_Unresolved);
        }
        catch (Exception)
        {
            return CheckOutcome.Fail(Loc.F(Strings.Check_Detail_Unparsable, listen));
        }
    }

    /// <summary>2. Packets from the Mac are arriving.</summary>
    public static CheckOutcome Packets(DateTime? lastPacketAt, DateTime now, double? rttMs)
    {
        if (lastPacketAt is not { } seen || now - seen > PacketTimeout)
        {
            return CheckOutcome.Fail(Loc.F(Strings.Check_Detail_NoAnswer, Loc.Seconds(PacketTimeout.TotalSeconds)));
        }

        // §10.1 rule 8: units follow the number across a non-breaking space.
        return CheckOutcome.Pass(rttMs is { } rtt ? Loc.Ms(rtt) : Strings.Check_Detail_Flowing);
    }

    /// <summary>
    /// 3. The two machines are holding the same key.
    ///
    /// This cannot be measured from one side — the proof is that packets are being
    /// accepted at all, since a wrong key makes them fail their tag and vanish. So the
    /// line reports the fingerprint for the user to compare by eye (§10.3), and calls a
    /// key that is not a key what it is.
    /// </summary>
    public static CheckOutcome Keys(string? psk, bool packetsAccepted)
    {
        var fingerprint = PairingPayload.FingerprintOfPsk(psk);
        if (fingerprint is null) return CheckOutcome.Fail(Strings.Check_Detail_NoKey);

        return packetsAccepted
            ? CheckOutcome.Pass(Loc.F(Strings.Check_Detail_Fingerprint, fingerprint))
            : CheckOutcome.Fail(Loc.F(Strings.Check_Detail_KeyMismatch, fingerprint));
    }

    /// <summary>4. The receiver found somewhere to play the audio.</summary>
    public static CheckOutcome Device(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName)
            ? CheckOutcome.Fail(Strings.Check_Detail_NoOutput)
            : CheckOutcome.Pass(deviceName);

    /// <summary>
    /// 4, the other way round: the machine giving its microphone away found one to read.
    ///
    /// A method of its own rather than a flag on <see cref="Device"/>, because the failure is
    /// the whole value of the line, and «установите VB-Audio Virtual Cable» is advice for
    /// somebody with the opposite problem.
    /// </summary>
    public static CheckOutcome Input(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName)
            ? CheckOutcome.Fail(Strings.Check_Detail_NoInput)
            : CheckOutcome.Pass(deviceName);

    /// <summary>
    /// 5. Sound goes all the way through. The only check that proves the whole path, which
    /// is why the wizard asks the user to say something out loud and shows the level
    /// measured where the sound has to arrive.
    /// </summary>
    /// <param name="capturing">
    /// True on the machine holding the microphone, where silence means the microphone is not
    /// being heard at all — a different problem, and a different sentence, from silence at
    /// the far end of a link that is otherwise up.
    /// </param>
    public static CheckOutcome Sound(float peakLinear, bool windowElapsed, bool capturing = false)
    {
        var dbfs = MicrophoneLevel.ToDbfs(peakLinear);
        if (dbfs > SilenceDbfs) return CheckOutcome.Pass(Loc.F(Strings.Check_Detail_Peak, Loc.Dbfs(dbfs)));

        return windowElapsed
            ? CheckOutcome.Fail(capturing ? Strings.Check_Detail_MicSilent : Strings.Check_Detail_Silence)
            : new CheckOutcome(CheckState.Running, Strings.Check_Detail_SaySomething);
    }

    /// <summary>6. The forwarded devices, if the user asked for any.</summary>
    public static CheckOutcome Controller(bool enabled, bool driverInstalled, bool attached, string? product)
    {
        if (!enabled) return CheckOutcome.Skip(Strings.Check_Detail_ForwardingOff);
        if (!driverInstalled) return CheckOutcome.Fail(Strings.Check_Detail_NoDriver);
        return attached
            ? CheckOutcome.Pass(product ?? Strings.Check_Detail_Forwarded)
            : CheckOutcome.Fail(Strings.Check_Detail_NotOnMac);
    }
}

/// <summary>
/// Linear peak to dBFS, duplicated from the microphone feature on purpose: the core must
/// not depend on a feature assembly, and this is four lines of arithmetic rather than a
/// shared abstraction worth building.
/// </summary>
public static class MicrophoneLevel
{
    /// <summary>Digital silence has no decibel value; −99 is the floor the meters use.</summary>
    public const float Floor = -99f;

    public static float ToDbfs(float linear) => linear > 0 ? 20f * MathF.Log10(linear) : Floor;
}
