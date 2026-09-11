namespace HexBridge.Tests;

/// <summary>
/// The six lines of the connection check (DESIGN.md §9.4).
///
/// The wizard's whole purpose is to say something true about the link between two
/// machines, so «the fifth line said звук идёт while the receiver had heard nothing» is
/// exactly the bug that would otherwise only surface in front of a user.
/// </summary>
public class PairingChecksTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    // MARK: - 1. The address

    [Theory]
    [InlineData("192.168.1.10:47702")]
    [InlineData("127.0.0.1:47702")]
    public void AConcreteAddressPassesAndIsShownAsTyped(string listen)
    {
        var outcome = PairingChecks.Address(listen);

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Equal(listen, outcome.Detail);
    }

    [Fact]
    public void AWildcardIsReportedAsSomethingTheMacCanActuallyDial()
    {
        // 0.0.0.0 is a fine thing to bind and a useless thing to type into a Mac, so the
        // line answers the question the user is really asking.
        var outcome = PairingChecks.Address("0.0.0.0:47702");

        Assert.DoesNotContain("0.0.0.0", outcome.Detail, StringComparison.Ordinal);
        if (outcome.State == CheckState.Passed) Assert.EndsWith(":47702", outcome.Detail, StringComparison.Ordinal);
        else Assert.Contains("сеть", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AMissingAddressFails(string? listen)
    {
        Assert.Equal(CheckState.Failed, PairingChecks.Address(listen).State);
    }

    [Fact]
    public void ANameThatDoesNotResolveFailsRatherThanThrows()
    {
        var outcome = PairingChecks.Address("no-such-host.invalid:47702");

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Detail));
    }

    // MARK: - 2. Packets

    [Fact]
    public void ARecentPacketPassesAndShowsTheRoundTrip()
    {
        var outcome = PairingChecks.Packets(Now.AddMilliseconds(-200), Now, rttMs: 12.4);

        Assert.Equal(CheckState.Passed, outcome.State);
        // §10.1 rule 8: the unit follows the number across a non-breaking space.
        Assert.Equal("12 мс", outcome.Detail);
    }

    [Fact]
    public void PacketsWithNoTimingEstimateStillPass()
    {
        // OneWayDelayMs is null when the two clocks disagree enough to make it meaningless.
        // That is not a failure of the link, so the line must not claim it is.
        var outcome = PairingChecks.Packets(Now, Now, rttMs: null);

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Equal("пакеты идут", outcome.Detail);
    }

    [Fact]
    public void SilenceLongerThanTheTimeoutFails()
    {
        var outcome = PairingChecks.Packets(Now - PairingChecks.PacketTimeout.Add(TimeSpan.FromSeconds(1)), Now, 5);

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.Equal("ответа нет за 3 с", outcome.Detail);
    }

    [Fact]
    public void NoPacketEverSeenFails()
    {
        Assert.Equal(CheckState.Failed, PairingChecks.Packets(null, Now, null).State);
    }

    [Fact]
    public void ThePacketWindowIsExactlyTheDocumentedThree()
    {
        Assert.Equal(3, PairingChecks.PacketTimeout.TotalSeconds);
        // Right on the boundary still counts as arrived.
        Assert.Equal(CheckState.Passed, PairingChecks.Packets(Now - PairingChecks.PacketTimeout, Now, null).State);
    }

    // MARK: - 3. Keys

    [Fact]
    public void AKeyThatIsAcceptingPacketsPassesAndShowsItsFingerprint()
    {
        var psk = ReceiverConfig.GenerateKey();
        var outcome = PairingChecks.Keys(psk, packetsAccepted: true);

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Contains(PairingPayload.FingerprintOfPsk(psk)!, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatHasAcceptedNothingIsUnprovenRatherThanWrong()
    {
        // This used to be reported as a mismatch, and that was an accusation made from no
        // evidence: it is the line directly after «no packets are arriving», so the same
        // one fact was told twice and the second telling named the wrong culprit. Somebody
        // reading it would regenerate a key that was never wrong. The fingerprint still
        // goes out, because comparing the two screens by eye is the only real check there
        // is from one side (§10.3).
        var psk = ReceiverConfig.GenerateKey();
        var outcome = PairingChecks.Keys(psk, packetsAccepted: false);

        Assert.Equal(CheckState.Skipped, outcome.State);
        Assert.DoesNotContain("не совпадают", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(PairingPayload.FingerprintOfPsk(psk)!, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("AAAA")]
    [InlineData(null)]
    public void SomethingThatIsNotAKeyFailsWhateverElseIsTrue(string? psk)
    {
        Assert.Equal(CheckState.Failed, PairingChecks.Keys(psk, packetsAccepted: true).State);
        Assert.Equal(CheckState.Failed, PairingChecks.Keys(psk, packetsAccepted: false).State);
    }

    [Fact]
    public void TheKeyItselfIsNeverPrinted()
    {
        var psk = ReceiverConfig.GenerateKey();

        foreach (var accepted in new[] { true, false })
        {
            Assert.DoesNotContain(psk, PairingChecks.Keys(psk, accepted).Detail, StringComparison.Ordinal);
        }
    }

    // MARK: - 4. The output device

    [Fact]
    public void ANamedDevicePasses()
    {
        var outcome = PairingChecks.Device("Steam Streaming Microphone");

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Equal("Steam Streaming Microphone", outcome.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoDeviceFailsAndSaysWhatToInstall(string? name)
    {
        var outcome = PairingChecks.Device(name);

        Assert.Equal(CheckState.Failed, outcome.State);
        // §10.1 rule 3: what happened, why, what to do.
        Assert.Contains("установите", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - 5. Sound end to end

    [Fact]
    public void AVoicePasses()
    {
        // −18 dBFS is ordinary speech.
        var outcome = PairingChecks.Sound(0.126f, windowElapsed: false);

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Equal("пик -18 dBFS", outcome.Detail);
    }

    [Fact]
    public void SilenceKeepsAskingUntilTheWindowRunsOut()
    {
        var outcome = PairingChecks.Sound(0f, windowElapsed: false);

        Assert.Equal(CheckState.Running, outcome.State);
        Assert.Contains("вслух", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SilenceForTheWholeWindowFails()
    {
        var outcome = PairingChecks.Sound(0f, windowElapsed: true);

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.Equal("тишина на этой машине", outcome.Detail);
    }

    [Fact]
    public void SilenceOnTheMachineHoldingTheMicrophoneIsADifferentProblem()
    {
        // Same measurement, different thing to do about it. On the machine that is supposed
        // to be hearing a voice, silence means the microphone — not the link — and sending
        // somebody to check the network is sending them the wrong way.
        var outcome = PairingChecks.Sound(0f, windowElapsed: true, capturing: true);

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.Contains("микрофон", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("доступ", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMicrophoneIsReportedByNameOrMissedByName()
    {
        Assert.Equal(CheckState.Passed, PairingChecks.Input("Микрофон (Yeti)").State);
        Assert.Equal("Микрофон (Yeti)", PairingChecks.Input("Микрофон (Yeti)").Detail);

        var missing = PairingChecks.Input(null);
        Assert.Equal(CheckState.Failed, missing.State);
        // Not the virtual-cable advice: that is for somebody with the opposite problem.
        Assert.DoesNotContain("VB-Audio", missing.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomNoiseIsNotMistakenForAVoice()
    {
        // A hiss at −60 dBFS is the receiver working and nobody talking; passing on that
        // would make the one end-to-end check meaningless.
        Assert.Equal(CheckState.Failed, PairingChecks.Sound(0.001f, windowElapsed: true).State);
    }

    [Fact]
    public void TheThresholdIsWhereTheDocumentPutIt()
    {
        var justAbove = MathF.Pow(10, (PairingChecks.SilenceDbfs + 1) / 20f);
        var justBelow = MathF.Pow(10, (PairingChecks.SilenceDbfs - 1) / 20f);

        Assert.Equal(CheckState.Passed, PairingChecks.Sound(justAbove, true).State);
        Assert.Equal(CheckState.Failed, PairingChecks.Sound(justBelow, true).State);
    }

    [Fact]
    public void TheSoundWindowIsTheDocumentedFiveSeconds()
    {
        Assert.Equal(5, PairingChecks.SoundWindow.TotalSeconds);
    }

    // MARK: - 6. The controller

    [Fact]
    public void AForwardedControllerPasses()
    {
        var outcome = PairingChecks.Controller(true, driverInstalled: true, attached: true, "DualSense Wireless Controller");

        Assert.Equal(CheckState.Passed, outcome.State);
        Assert.Equal("DualSense Wireless Controller", outcome.Detail);
    }

    [Fact]
    public void ForwardingSwitchedOffIsSkippedAndNotFailed()
    {
        // §10.1 rule 5: a feature the user turned off is not a problem, and must never be
        // painted as one.
        var outcome = PairingChecks.Controller(false, driverInstalled: false, attached: false, null);

        Assert.Equal(CheckState.Skipped, outcome.State);
    }

    [Fact]
    public void AMissingDriverIsNamedBeforeAMissingController()
    {
        // The driver is the thing the user can act on; «устройство не подключено» when the
        // driver was never installed would send them to look at the wrong end of the cable.
        var outcome = PairingChecks.Controller(true, driverInstalled: false, attached: true, "DualSense");

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.StartsWith("драйвер не установлен", outcome.Detail, StringComparison.Ordinal);
        // And says where the button is: a line that only names the problem leaves the
        // reader on a screen that cannot solve it.
        Assert.Contains("Устройства", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverWithNoControllerFails()
    {
        var outcome = PairingChecks.Controller(true, driverInstalled: true, attached: false, null);

        Assert.Equal(CheckState.Failed, outcome.State);
        Assert.Contains("не подключено", outcome.Detail, StringComparison.Ordinal);
    }

    // MARK: - Tone

    [Fact]
    public void NoCheckEverShouts()
    {
        // §10.1 rule 2: not one exclamation mark anywhere in the interface.
        foreach (var detail in AllDetails())
        {
            Assert.DoesNotContain('!', detail);
            // Whole words: «найдено» contains the letters of «ой» and is perfectly calm.
            Assert.DoesNotMatch(@"\b(ой|упс)\b", detail.ToLowerInvariant());
            // §10.1 rule 7: never promise what we do not know.
            Assert.DoesNotContain("позже", detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoCheckBlamesTheUser()
    {
        // §10.1 rule 5: not «вы ввели неверный ключ» but «ключи не совпадают».
        foreach (var detail in AllDetails())
        {
            Assert.DoesNotContain("вы ", detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UnitsAreTiedToTheirNumbers()
    {
        // §10.1 rule 8: a non-breaking space between the number and the unit, so a narrow
        // column never wraps «12» onto one line and «мс» onto the next.
        Assert.Matches(@"\d\u00a0мс", PairingChecks.Packets(Now, Now, 12).Detail);
        Assert.Matches(@"\d\u00a0с", PairingChecks.Packets(null, Now, null).Detail);
        Assert.Matches(@"\d\u00a0dBFS", PairingChecks.Sound(0.126f, true).Detail);
    }

    private static IEnumerable<string> AllDetails()
    {
        var psk = ReceiverConfig.GenerateKey();
        return
        [
            PairingChecks.Address(null).Detail,
            PairingChecks.Address("no-such-host.invalid:1").Detail,
            PairingChecks.Address("nonsense").Detail,
            PairingChecks.Packets(null, Now, null).Detail,
            PairingChecks.Packets(Now, Now, 3).Detail,
            PairingChecks.Keys(null, true).Detail,
            PairingChecks.Keys(psk, false).Detail,
            PairingChecks.Keys(psk, true).Detail,
            PairingChecks.Device(null).Detail,
            PairingChecks.Sound(0, false).Detail,
            PairingChecks.Sound(0, true).Detail,
            PairingChecks.Controller(true, false, false, null).Detail,
            PairingChecks.Controller(true, true, false, null).Detail,
            PairingChecks.Controller(false, false, false, null).Detail,
        ];
    }

    // MARK: - The decibel conversion the meter and the check share

    [Theory]
    [InlineData(1.0f, 0f)]
    [InlineData(0.5f, -6.02f)]
    [InlineData(0.1f, -20f)]
    [InlineData(0f, MicrophoneLevel.Floor)]
    public void LinearToDbfs(float linear, float expected)
    {
        Assert.Equal(expected, MicrophoneLevel.ToDbfs(linear), 2);
    }

    [Fact]
    public void TheCoreAndTheMicrophoneFeatureAgreeOnDecibels()
    {
        // The core cannot depend on a feature assembly, so the four lines of arithmetic
        // exist twice. This is the test that keeps the two copies the same number.
        foreach (var linear in new[] { 0f, 0.001f, 0.01f, 0.126f, 0.5f, 1f })
        {
            Assert.Equal(Microphone.MicrophoneState.ToDbfs(linear), MicrophoneLevel.ToDbfs(linear), 4);
        }
    }
}
