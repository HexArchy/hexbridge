using System.Runtime.InteropServices;

using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// Noticing that the usbip-win2 driver arrived.
///
/// <para>
/// The point of these is a person sitting in front of the devices page with the installer
/// running in another window. Until this existed the answer was "restart the app", because
/// the client was located once at construction and never again — so the screen that asked
/// for a driver could not tell when it got one.
/// </para>
/// </summary>
public class DriverNoticeTests
{
    private static UsbIpAttacher Attacher(string? path) => new(path, (_, _) => { });

    [Fact]
    public void A_missing_client_stays_missing()
    {
        var attacher = Attacher(Path.Combine(Path.GetTempPath(), "hexbridge-no-such-usbip.exe"));

        Assert.False(attacher.Rescan());
        Assert.False(attacher.IsInstalled);
    }

    [Fact]
    public void A_client_that_appears_mid_session_is_found_without_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hexbridge-usbip-{Guid.NewGuid():N}.exe");
        var attacher = Attacher(path);
        Assert.False(attacher.Rescan());

        File.WriteAllText(path, "");
        try
        {
            // The lookup is throttled so the UI poll can call it on every frame; a person
            // installing a driver will not notice a second, but a test would hang on it.
            Assert.True(Eventually(attacher.Rescan));
            Assert.Equal(path, attacher.ClientPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Once_found_it_is_not_looked_for_again()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hexbridge-usbip-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "");
        var attacher = Attacher(path);
        Assert.True(attacher.Rescan());

        // Deleting it does not un-install anything: usbip.exe is a file we shell out to,
        // and a half-second window where it is gone should not blank the page.
        File.Delete(path);
        Assert.True(attacher.Rescan());
    }

    [Fact]
    public void The_installer_offered_matches_the_machine_not_the_process()
    {
        var url = UsbIpAttacher.InstallerUrl;

        // Our own x64 build runs happily under emulation on ARM64 Windows, and it still
        // needs the ARM64 driver: the question is about the operating system.
        var expected = RuntimeInformation.OSArchitecture is Architecture.Arm64 ? "arm64" : "x64";
        Assert.EndsWith($"USBip-0.9.8.0-{expected}.exe", url);
        Assert.StartsWith("https://github.com/vadimgrn/usbip-win2/releases/download/", url);
    }

    private static bool Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return false;
    }
}
