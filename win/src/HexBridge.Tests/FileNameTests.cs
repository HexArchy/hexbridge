using System.Text;
using HexBridge.Files;

namespace HexBridge.Tests;

/// <summary>
/// The name on a received file, which docs/PROTOCOL.md says is not to be trusted.
///
/// <para>
/// Every case here is a name the other machine could send on purpose. They are not
/// hypothetical: a receiver that takes a name at its word writes wherever the sender says,
/// and the two ends of this bridge are not always going to be two machines in one flat.
/// </para>
/// </summary>
public class FileNameTests
{
    [Theory]
    // A relative escape, which is the whole reason the rule exists.
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\win.ini", "win.ini")]
    // An absolute Windows path, drive letter and all.
    [InlineData("C:\\Windows\\System32\\x", "x")]
    [InlineData("/etc/shadow", "shadow")]
    // Nothing but structure: there is no name in any of these.
    [InlineData("..", FileNames.Fallback)]
    [InlineData(".", FileNames.Fallback)]
    [InlineData("", FileNames.Fallback)]
    [InlineData(".....", FileNames.Fallback)]
    [InlineData("   ", FileNames.Fallback)]
    [InlineData("/", FileNames.Fallback)]
    // A name that ends in a dot is a different name to Windows than it is to anyone else.
    [InlineData("notes.txt.", "notes.txt")]
    // A NUL is where a name means one thing to C and another to everything above it.
    [InlineData("no\0pe.txt", "nope.txt")]
    [InlineData("line\nbreak.txt", "linebreak.txt")]
    [InlineData("wild*card?.txt", "wildcard.txt")]
    // Still a device in every directory on Windows, and opening it writes to nothing.
    [InlineData("NUL", "_NUL")]
    [InlineData("con.txt", "_con.txt")]
    // The ordinary case, untouched.
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("отчёт за квартал.pdf", "отчёт за квартал.pdf")]
    [InlineData(".gitignore", ".gitignore")]
    public void AReceivedNameIsCutDownToSomethingThatIsOnlyAName(string given, string expected) =>
        Assert.Equal(expected, FileNames.Sanitise(given));

    [Fact]
    public void AnOverLongNameIsShortenedAndKeepsItsExtension()
    {
        var shortened = FileNames.Sanitise(new string('a', 400) + ".png");

        Assert.EndsWith(".png", shortened, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(shortened) <= FileNames.MaxNameBytes);
    }

    [Fact]
    public void ShorteningANameNeverCutsACharacterInHalf()
    {
        // Cyrillic is two bytes a letter, so a cut made in bytes lands mid-character
        // unless somebody stepped back off it.
        var shortened = FileNames.Sanitise(new string('я', 300) + ".txt");

        Assert.EndsWith(".txt", shortened, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(shortened) <= FileNames.MaxNameBytes);
        Assert.DoesNotContain('\uFFFD', shortened);
        Assert.Equal(shortened, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(shortened)));
    }

    [Fact]
    public void AFileIsNeverWrittenOverAndTheNumberGoesBeforeTheExtension()
    {
        using var folder = new TempFolder();

        var first = FileNames.Save(folder.Path, "notes.txt", "один"u8);
        var second = FileNames.Save(folder.Path, "notes.txt", "два"u8);
        var third = FileNames.Save(folder.Path, "notes.txt", "три"u8);

        Assert.Equal("notes.txt", Path.GetFileName(first));
        Assert.Equal("notes (2).txt", Path.GetFileName(second));
        Assert.Equal("notes (3).txt", Path.GetFileName(third));
        Assert.Equal("один", File.ReadAllText(first));
    }

    [Fact]
    public void AHostileNameStillLandsInsideTheFolderItWasGiven()
    {
        using var folder = new TempFolder();
        var outside = Path.Combine(Path.GetDirectoryName(folder.Path)!, "passwd");

        var written = FileNames.Save(folder.Path, "../../etc/passwd", "х"u8);

        Assert.Equal(Path.Combine(folder.Path, "passwd"), written);
        Assert.False(File.Exists(outside), "a name decided which directory was written to");
    }

    [Fact]
    public void AFolderThatIsNotThereYetIsMadeRatherThanRefused()
    {
        using var folder = new TempFolder(create: false);

        var written = FileNames.Save(folder.Path, "notes.txt", "текст"u8);

        Assert.True(File.Exists(written));
    }

    /// <summary>A directory of its own per test, so two of them cannot pick the same name.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hexbridge-names", Guid.NewGuid().ToString("N"));
            if (create) Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A temporary directory that outlives the run is not a failed test.
            }
        }
    }
}
