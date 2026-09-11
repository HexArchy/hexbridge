using HexBridge.Clipboard;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// Switching one feature must not disturb another.
///
/// <para>
/// Saving any setting used to stop and start everything. Turning the clipboard on
/// therefore tore down the virtual USB device, Windows saw the controller detach, and it
/// did not come back until the cable was pulled out and put in again. That was reported
/// from a real desk, in the middle of a game.
/// </para>
/// </summary>
public class ReconcileTests
{
    private static ReceiverConfig Config(bool clipboard, bool gamepad) => new()
    {
        Role = BridgeRole.Receiver,
        Listen = $"127.0.0.1:{Ports.Free()}",
        Psk = ReceiverConfig.GenerateKey(),
        UsbIpListen = $"127.0.0.1:{Ports.Free()}",
        Clipboard = clipboard,
        Gamepad = gamepad,
        Haptics = false,
    };

    private static ClipboardFeature Clipboard() =>
        new(_ => new FakeClipboardSurface());

    [Fact]
    public async Task TurningTheClipboardOnLeavesTheGamepadAlone()
    {
        var devices = new DevicesFeature();
        await using var receiver = new ReceiverService(devices, Clipboard());

        await receiver.StartAsync(Config(clipboard: false, gamepad: true));
        try
        {
            var before = Assert.IsType<DevicesState>(devices.CaptureState());

            await receiver.ReconcileFeaturesAsync(Config(clipboard: true, gamepad: true));

            var after = Assert.IsType<DevicesState>(devices.CaptureState());

            // The USB/IP server is the device side's hold on the machine. Same port means
            // it was never torn down and rebuilt.
            Assert.Equal(before.ServerListen, after.ServerListen);
        }
        finally
        {
            await receiver.StopAsync();
        }
    }

    [Fact]
    public async Task TurningAFeatureOffStopsOnlyThatOne()
    {
        var devices = new DevicesFeature();
        await using var receiver = new ReceiverService(devices, Clipboard());

        await receiver.StartAsync(Config(clipboard: true, gamepad: true));
        try
        {
            var before = Assert.IsType<DevicesState>(devices.CaptureState());

            await receiver.ReconcileFeaturesAsync(Config(clipboard: false, gamepad: true));

            var after = Assert.IsType<DevicesState>(devices.CaptureState());
            Assert.Equal(before.ServerListen, after.ServerListen);
        }
        finally
        {
            await receiver.StopAsync();
        }
    }
}
