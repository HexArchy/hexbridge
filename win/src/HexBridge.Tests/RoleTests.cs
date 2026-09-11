using System.Text.Json;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Files;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// The role itself: what it does to the config, to which features start, and — the one that
/// would be expensive to get wrong — to every config.json already deployed.
/// </summary>
public class RoleTests
{
    [Fact]
    public void AConfigWrittenBeforeRolesExistedStillTakesTheMicrophone()
    {
        // Verbatim shape of a config from the version that only ever received. The key is
        // assembled rather than spelled out: thirty-two bytes of base64 after "Psk" is
        // precisely what the secret scan in CI hunts for, and it is right to keep hunting
        // for it — teaching it to wave through a file because the file is a test is how a
        // real key eventually gets waved through too.
        var psk = Convert.ToBase64String(Enumerable.Repeat<byte>(0xAB, 32).ToArray());
        var old = $$"""
        {
          "Listen": "0.0.0.0:47702",
          "Psk": "{{psk}}",
          "Output": "wasapi",
          "JitterMs": 60,
          "Gamepad": true
        }
        """;

        var config = JsonSerializer.Deserialize<ReceiverConfig>(old)!;

        Assert.Equal(BridgeRole.Receiver, config.Role);
        Assert.True(new MicrophoneFeature().IsEnabled(config));
        Assert.False(new MicrophoneCaptureFeature().IsEnabled(config));
        Assert.True(new DevicesFeature().IsEnabled(config));

        // And the defaults for everything the sending role added are sane rather than zero:
        // a bitrate of 0 or a gain of 0 would be silence with no error anywhere.
        Assert.Equal("wasapi", config.Input);
        Assert.Equal(32000, config.Bitrate);
        Assert.Equal(1.0f, config.InputGain);
    }

    [Fact]
    public void TheRoleSurvivesBeingWrittenAndReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hexbridge-role-{Guid.NewGuid():N}.json");
        try
        {
            new ReceiverConfig
            {
                Role = BridgeRole.Sender,
                Target = "192.168.1.10:47702",
                Input = "wasapi",
                InputDevice = "Yeti",
            }.Save(path);

            // Stored as a word, not an ordinal: a config file people are told to edit by hand
            // must not encode the most important setting in it as «0» or «1».
            Assert.Contains("\"Sender\"", File.ReadAllText(path));

            var read = ReceiverConfig.Load(path);
            Assert.Equal(BridgeRole.Sender, read.Role);
            Assert.Equal("192.168.1.10:47702", read.Target);
            Assert.Equal("Yeti", read.InputDevice);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExactlyOneHalfOfTheMicrophoneRunsInEitherRole()
    {
        var playing = new MicrophoneFeature();
        var giving = new MicrophoneCaptureFeature();

        var receiver = new ReceiverConfig { Role = BridgeRole.Receiver };
        var sender = new ReceiverConfig { Role = BridgeRole.Sender };

        Assert.True(playing.IsEnabled(receiver));
        Assert.False(giving.IsEnabled(receiver));
        Assert.False(playing.IsEnabled(sender));
        Assert.True(giving.IsEnabled(sender));

        // Same id and same title, because they are the same feature seen from two ends: that
        // is what lets one set of pages and one stats line serve both.
        Assert.Equal(playing.Id, giving.Id);
        Assert.Equal(playing.Title, giving.Title);
    }

    [Fact]
    public void RegisteringBothHalvesIsNotAPacketTypeConflict()
    {
        // The capture half claims nothing: AUDIO only ever leaves it. If it claimed the type
        // it produces, the host would refuse to hold both — and the composition root holds
        // both by design.
        Assert.Empty(new MicrophoneCaptureFeature().HandledTypes);

        var bulk = new BulkHost();
        var host = new ReceiverService(
            new MicrophoneFeature(),
            new MicrophoneCaptureFeature(),
            new DevicesFeature(),
            bulk,
            new ClipboardFeature(bulk),
            new FilesFeature(bulk));

        Assert.Equal(6, host.Features.Count);
    }

    [Fact]
    public void TheClipboardIsTheSameFeatureInBothRoles()
    {
        IFeature clipboard = new ClipboardFeature(new BulkHost());

        // It was written symmetrically and has to stay that way: one class, both directions,
        // one shared channel underneath, and a switch that reads the same in either role.
        Assert.True(clipboard.IsEnabled(new ReceiverConfig { Role = BridgeRole.Receiver, Clipboard = true }));
        Assert.True(clipboard.IsEnabled(new ReceiverConfig { Role = BridgeRole.Sender, Clipboard = true }));
        Assert.False(clipboard.IsEnabled(new ReceiverConfig { Role = BridgeRole.Sender }));
        Assert.Null(clipboard.Unavailable(new ReceiverConfig { Role = BridgeRole.Sender }));
    }

    [Fact]
    public void ForwardingADeviceFromHereIsRefusedWithAReasonRatherThanASwitch()
    {
        var devices = new DevicesFeature();
        var sender = new ReceiverConfig { Role = BridgeRole.Sender, Gamepad = true };

        Assert.False(devices.IsEnabled(sender));

        var reason = devices.Unavailable(sender);
        Assert.NotNull(reason);
        Assert.Contains("USB-дескрипторы", reason);

        // And it says what the machine *can* still do, because «нельзя» with no next step is
        // where a user gives up.
        Assert.Contains("Принимать устройства", reason);
        Assert.Null(devices.Unavailable(new ReceiverConfig { Role = BridgeRole.Receiver, Gamepad = true }));
    }

    [Fact]
    public void GivingWithNoAddressFailsBeforeAnythingIsClaimed()
    {
        var config = new ReceiverConfig
        {
            Role = BridgeRole.Sender,
            Psk = ReceiverConfig.GenerateKey(),
        };

        var error = Assert.Throws<InvalidOperationException>(() => config.ResolvePeer());
        Assert.Contains("адрес", error.Message);
    }

    [Fact]
    public void ARelayWinsOverADirectAddress()
    {
        var config = new ReceiverConfig
        {
            Role = BridgeRole.Sender,
            Target = "127.0.0.1:1111",
            Relay = "127.0.0.1:2222",
        };

        // Configuring a relay is the act of saying «прямо не получится». Preferring the
        // direct address anyway would make the setting do nothing on exactly the networks it
        // was set for.
        Assert.Equal(2222, config.ResolvePeer().Port);
    }

    [Fact]
    public void BothRolesAreNamedForWhatTheMachineDoesRatherThanForPackets()
    {
        foreach (var role in new[] { BridgeRole.Receiver, BridgeRole.Sender })
        {
            var wording = RoleWording.Title(role) + " " + RoleWording.Summary(role);

            // «Отправитель» and «приёмник» describe datagrams. The person choosing is
            // thinking about which machine the microphone is plugged into.
            Assert.DoesNotContain("тправител", wording);
            Assert.DoesNotContain("риёмник", wording);
            Assert.Contains("икрофон", wording);
        }
    }
}
