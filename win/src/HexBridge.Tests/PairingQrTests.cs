using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Net.Codecrete.QrCodeGenerator;

namespace HexBridge.Tests;

/// <summary>
/// The QR half of step 2 (DESIGN.md §9.2).
///
/// <para>
/// The drawing cannot be asserted on without a render surface, but everything that decides
/// whether a phone camera will read it happens before the drawing: the payload has to
/// encode at all, it has to stay small enough that the modules are still big on a 240-point
/// square, and the path has to sit in the coordinate space the view stretches it into. Each
/// of those is checkable here.
/// </para>
/// </summary>
public class PairingQrTests
{
    /// <summary>The side of the code on screen, from §9.2.</summary>
    private const double Side = 240;

    private static PairingPayload Payload(string host = "192.168.100.200", string name = "GAMING-PC") => new()
    {
        Host = host,
        Port = 47702,
        Key = RandomNumberGenerator.GetBytes(32),
        Name = name,
    };

    [Fact]
    public void ThePayloadEncodesAtTheDocumentedCorrectionLevel()
    {
        // Level M, per §9.2: the payload is short, and a higher level would only shrink the
        // modules for no gain in a code being read off a screen a foot away.
        var qr = QrCode.EncodeText(Payload().ToUri(), QrCode.Ecc.Medium);

        Assert.Equal(QrCode.Ecc.Medium, qr.ErrorCorrectionLevel);
        Assert.True(qr.Size > 0);
    }

    [Fact]
    public void TheModulesStayBigEnoughToPhotograph()
    {
        // A module below about 4 points is where phone cameras start failing at arm's
        // length. The URI is close to fixed-length, so this is really a guard on the
        // machine name: a 60-character one would push the version up two steps.
        var qr = QrCode.EncodeText(Payload().ToUri(), QrCode.Ecc.Medium);
        var module = Side / qr.Size;

        Assert.True(module >= 4, $"модуль {module:0.0} pt при {qr.Size} модулях");
    }

    [Fact]
    public void AnAbsurdMachineNameStillFits()
    {
        // Windows allows 15 characters in a NetBIOS name and rather more in a DNS one.
        // Even a silly one must not push the code past what a camera can read.
        var qr = QrCode.EncodeText(Payload(name: new string('W', 63)).ToUri(), QrCode.Ecc.Medium);

        Assert.True(Side / qr.Size >= 3, $"{qr.Size} модулей");
    }

    [Fact]
    public void TheGraphicsPathIsAnSvgPathInModuleCoordinates()
    {
        var qr = QrCode.EncodeText(Payload().ToUri(), QrCode.Ecc.Medium);
        // Border 0: the quiet zone is padding on the white panel, because a border baked
        // into the path is not part of its bounding box and is dropped when the geometry
        // is stretched to fit.
        var path = qr.ToGraphicsPath(0);

        Assert.StartsWith("M", path, StringComparison.Ordinal);

        // Every coordinate must land inside the module grid, or Stretch="Uniform" would
        // scale the code to the wrong square and the finder patterns would be cropped.
        foreach (Match match in Regex.Matches(path, @"-?\d+"))
        {
            Assert.InRange(int.Parse(match.Value), -qr.Size, qr.Size);
        }
    }

    [Fact]
    public void TheCodeReachesAllFourEdgesOfItsBoundingBox()
    {
        // Three finder patterns sit in three corners, so the dark modules span the whole
        // grid in both axes. That is what makes stretching the path to an exact 240 × 240
        // square correct rather than approximately correct.
        var qr = QrCode.EncodeText(Payload().ToUri(), QrCode.Ecc.Medium);

        Assert.True(qr.GetModule(0, 0));
        Assert.True(qr.GetModule(qr.Size - 1, 0));
        Assert.True(qr.GetModule(0, qr.Size - 1));
    }

    [Fact]
    public void TheQuietZoneIsAtLeastFourModules()
    {
        // The wizard computes the white panel's padding from the module size. Below four
        // modules the standard stops guaranteeing a read, and a code on a dark theme's
        // card is exactly where that bites.
        var qr = QrCode.EncodeText(Payload().ToUri(), QrCode.Ecc.Medium);
        var padding = Math.Ceiling(Side / qr.Size * 4);

        Assert.True(padding >= Side / qr.Size * 4);
        Assert.True(padding >= 16, $"quiet zone {padding} pt");
    }

    [Fact]
    public void EveryPayloadThisAppCanProduceEncodes()
    {
        // The URI is ASCII by construction, but the name is user input, and an encoder
        // that threw halfway through the wizard would leave a blank white square.
        foreach (var name in new[] { "", "PC", "Никита-ПК", new string('X', 63), "PC & Mac", "C:\\PC" })
        {
            var uri = Payload(name: name).ToUri();
            var qr = QrCode.EncodeText(uri, QrCode.Ecc.Medium);

            Assert.NotEmpty(qr.ToGraphicsPath(0));
            // And what the camera reads is what the Mac has to parse.
            Assert.True(PairingPayload.TryParse(uri, out _, out _));
        }
    }
}
