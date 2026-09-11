namespace HexBridge.Tests;

/// <summary>
/// Carrying the settings — the pairing key above all — across an update.
///
/// <para>
/// The config used to live beside the executable. The Setup installer puts each version
/// in a folder of its own and swaps them, so an update left the old folder behind with
/// the key in it and the new one started empty: pair again, on every update. These pin
/// the way out of that, and the one rule that matters while doing it — never lose the
/// file you are migrating.
/// </para>
/// </summary>
public class ConfigMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hexbridge-cfg-{Guid.NewGuid():N}");

    private string Wanted => Path.Combine(_root, "new", "config.json");
    private string Legacy => Path.Combine(_root, "old", "config.json");

    public ConfigMigrationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "old"));
        Directory.CreateDirectory(Path.Combine(_root, "new"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static void Write(string path, string key) =>
        File.WriteAllText(path, $$"""{"Listen":"0.0.0.0:47702","Psk":"{{key}}"}""");

    [Fact]
    public void AnOlderConfigIsBroughtForward()
    {
        var key = ReceiverConfig.GenerateKey();
        Write(Legacy, key);

        var resolved = ReceiverConfig.Resolve(Wanted, [Legacy]);

        Assert.Equal(Wanted, resolved);
        Assert.Equal(key, ReceiverConfig.Load(resolved).Psk);
    }

    /// <summary>
    /// Copied, not moved. A migration that loses the file it was migrating is worse than
    /// no migration at all, and the old copy costs nothing where it lies.
    /// </summary>
    [Fact]
    public void TheOlderConfigIsLeftWhereItWas()
    {
        Write(Legacy, ReceiverConfig.GenerateKey());

        ReceiverConfig.Resolve(Wanted, [Legacy]);

        Assert.True(File.Exists(Legacy));
    }

    [Fact]
    public void AnExistingConfigIsNeverOverwritten()
    {
        var current = ReceiverConfig.GenerateKey();
        Write(Wanted, current);
        Write(Legacy, ReceiverConfig.GenerateKey());

        var resolved = ReceiverConfig.Resolve(Wanted, [Legacy]);

        Assert.Equal(current, ReceiverConfig.Load(resolved).Psk);
    }

    /// <summary>The first that exists wins, so the order of the list is the order of preference.</summary>
    [Fact]
    public void TheFirstOlderConfigFoundIsTheOneTaken()
    {
        var second = Path.Combine(_root, "old", "second.json");
        var wanted = ReceiverConfig.GenerateKey();
        Write(Legacy, wanted);
        Write(second, ReceiverConfig.GenerateKey());

        var resolved = ReceiverConfig.Resolve(Wanted, [Legacy, second]);

        Assert.Equal(wanted, ReceiverConfig.Load(resolved).Psk);
    }

    [Fact]
    public void AFirstRunWithNothingToFindIsStillAFirstRun()
    {
        var resolved = ReceiverConfig.Resolve(Wanted, [Legacy]);

        Assert.Equal(Wanted, resolved);
        Assert.False(File.Exists(resolved));
        Assert.Equal("", ReceiverConfig.Load(resolved).Psk);
    }

    /// <summary>
    /// If the copy cannot be made, the answer is the old file rather than an empty config:
    /// reading the key from where it already is beats starting over.
    /// </summary>
    [Fact]
    public void AnUncopyableConfigIsStillReadWhereItLies()
    {
        var key = ReceiverConfig.GenerateKey();
        Write(Legacy, key);

        var impossible = Path.Combine(_root, "new", "\0bad", "config.json");
        var resolved = ReceiverConfig.Resolve(impossible, [Legacy]);

        Assert.Equal(Legacy, resolved);
        Assert.Equal(key, ReceiverConfig.Load(resolved).Psk);
    }
}
