namespace HexBridge.Tests;

/// <summary>
/// A Downloads folder of one test's own, emptied when it ends.
///
/// <para>
/// Shared between the tests of the two paths a file can take, because both of them write to
/// a real folder and neither may write to the one belonging to whoever is running the suite.
/// </para>
/// </summary>
internal sealed class TempDownloads : IDisposable
{
    public TempDownloads()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hexbridge-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
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
