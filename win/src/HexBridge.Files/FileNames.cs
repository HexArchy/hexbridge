using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using HexBridge.Localization;

namespace HexBridge.Files;

/// <summary>
/// Turning a name that crossed the network into a file on this disk.
///
/// <para>
/// docs/PROTOCOL.md is blunt about this: the name is not to be trusted. It is written by the
/// other machine, it travels as free text, and the side that receives it opens a file with
/// it. So it is cut down to its own last component, stripped of everything the filesystem
/// reads as structure, and checked once more against the folder it is supposed to land in
/// before anything is created. A name that survives none of that becomes <c>file</c>.
/// </para>
///
/// <para>
/// Nothing in here asks the platform what it considers a legal name. The answer differs
/// between Windows and everywhere else, and a receiver that accepted <c>..\\..\\x</c> on one
/// of them and refused it on the other would be a hole that only opens on one machine.
/// </para>
/// </summary>
public static class FileNames
{
    /// <summary>
    /// The longest name that is written to disk, in UTF-8 bytes.
    ///
    /// <para>
    /// A path component is capped at 255 bytes on both NTFS and APFS, and the contract caps
    /// the description at 256 — so the tight one is the filesystem. The margin below it is
    /// for the <c> (123)</c> an already-taken name grows, which is added after this cap and
    /// must not push the result back over the limit.
    /// </para>
    /// </summary>
    public const int MaxNameBytes = 200;

    /// <summary>
    /// A name that is nothing but structure once the structure is removed. Deliberately not
    /// something like «unnamed»: the contract names this one, and both platforms write it.
    /// </summary>
    public const string Fallback = "file";

    /// <summary>An extension longer than this is not an extension, so it is not preserved.</summary>
    private const int MaxExtensionBytes = 32;

    /// <summary>How many «(2)», «(3)» to try before giving up on the name entirely.</summary>
    private const int MaxNumbered = 1000;

    /// <summary>
    /// Everything a filesystem reads as something other than a letter in a name. The path
    /// separators are in here as well as in <see cref="LastComponent"/>, because a name may
    /// carry a separator the other platform does not use as one.
    /// </summary>
    private const string Forbidden = "<>:\"/\\|?*";

    /// <summary>
    /// The DOS device names, which are still devices in every directory on Windows. Opening
    /// <c>NUL</c> for writing succeeds and writes to nothing at all, and <c>CON</c> is worse
    /// — so the name is kept, with a character in front of it that makes it a name again.
    /// </summary>
    private static readonly string[] Devices =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The file name inside a received description: the last component, and nothing the
    /// filesystem would read as more than a name. Never empty, never a path.
    /// </summary>
    public static string Sanitise(string? name)
    {
        var cleaned = Strip(LastComponent(name ?? ""));

        // Windows drops trailing dots and spaces silently, so «x.» and «x» are one file
        // there and two here. Dropping them first is what makes the duplicate check below
        // mean the same thing on both platforms — and it is what turns «..» into nothing.
        cleaned = cleaned.Trim(' ').TrimEnd(' ', '.');
        if (cleaned.Length == 0) return Fallback;

        if (IsDevice(cleaned)) cleaned = "_" + cleaned;
        return Shorten(cleaned);
    }

    /// <summary>
    /// Writes an object into <paramref name="folder"/> under a sanitised
    /// <paramref name="name"/> and returns the full path it landed on.
    ///
    /// <para>
    /// Nothing is ever written over: the file is created with <see cref="FileMode.CreateNew"/>
    /// and a name that is taken is tried again as « (2)», « (3)». The create is what decides,
    /// not a check before it — between «does it exist» and «create it» somebody else's
    /// download finishes, and the whole point of this method is that nobody's does.
    /// </para>
    /// </summary>
    public static string Save(string folder, string? name, ReadOnlySpan<byte> bytes)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Directory.CreateDirectory(root);

        var safe = Sanitise(name);
        for (var attempt = 1; attempt <= MaxNumbered; attempt++)
        {
            var candidate = attempt == 1 ? safe : Numbered(safe, attempt);
            var path = Path.GetFullPath(Path.Combine(root, candidate));

            // The name has already been reduced to something with no structure in it, so
            // this cannot fail. It is here because the cost of being wrong about that is
            // a file written anywhere on the disk the other machine likes.
            if (!Inside(root, path)) throw new IOException(Loc.F(Strings.Err_Files_Outside, candidate));

            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken. Anything else — no room, no permission — is a real failure and
                // belongs to the caller.
            }
        }

        throw new IOException(Loc.F(Strings.Err_Files_NoName, safe));
    }

    /// <summary>
    /// The user's Downloads folder, which is where the contract says files land.
    /// </summary>
    public static string Downloads()
    {
        if (OperatingSystem.IsWindows() && KnownDownloads() is { Length: > 0 } known) return known;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // MARK: - Pieces

    private static string LastComponent(string name)
    {
        var cut = name.LastIndexOfAny(['/', '\\', ':']);
        return cut < 0 ? name : name[(cut + 1)..];
    }

    private static string Strip(string name)
    {
        var kept = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Control characters include the NUL that ends a C string: a name carrying one
            // is a name that means two different things to two layers of the same machine.
            if (c < ' ' || c == (char)0x7F) continue;
            if (Forbidden.Contains(c, StringComparison.Ordinal)) continue;
            kept.Append(c);
        }
        return kept.ToString();
    }

    private static bool IsDevice(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? name : name[..dot];
        return Devices.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cuts an over-long name down, keeping the extension. The extension is what Windows
    /// opens the file with, so a trimmed name that lost its «.png» is a file nothing will
    /// open — which is a worse answer than a shorter name.
    /// </summary>
    private static string Shorten(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxNameBytes) return name;

        var extension = Path.GetExtension(name);
        if (Encoding.UTF8.GetByteCount(extension) > MaxExtensionBytes) extension = "";

        var stem = Encoding.UTF8.GetBytes(name[..^extension.Length]);
        var cut = MaxNameBytes - Encoding.UTF8.GetByteCount(extension);
        // Never in the middle of a character: half a code point is not a shorter name, it
        // is a name with a replacement glyph in it.
        while (cut > 0 && (stem[cut] & 0xC0) == 0x80) cut--;

        var shortened = Encoding.UTF8.GetString(stem, 0, cut).TrimEnd(' ', '.');
        return shortened.Length == 0 ? Fallback + extension : shortened + extension;
    }

    private static string Numbered(string name, int attempt)
    {
        var extension = Path.GetExtension(name);
        return $"{name[..^extension.Length]} ({attempt}){extension}";
    }

    private static bool Inside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Where Windows says Downloads is. Not <c>%USERPROFILE%\Downloads</c>: the folder can
    /// be moved to another drive, and on a machine where it has been, the guess is a folder
    /// that gets created next to the profile and never opened by anybody.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? KnownDownloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var handle = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out handle) == 0
                ? Marshal.PtrToStringUni(handle)
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) Marshal.FreeCoTaskMem(handle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
