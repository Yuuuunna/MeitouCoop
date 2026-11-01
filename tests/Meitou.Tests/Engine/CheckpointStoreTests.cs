using System.Numerics;
using System.Text;
using Meitou.Data.Save;
using Meitou.Runtime;
using Meitou.Tests.Save;

namespace Meitou.Tests.Engine;

public class CheckpointStoreTests
{
    sealed class Scratch : IDisposable
    {
        public string Path { get; } = SaveSamples.Temp("checkpoint");
        public CheckpointStore Store => new(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    static SaveGame World(int money)
    {
        var builder = new SaveBuilder(7);
        builder.AddFaction("player", "Player", true, 1000);
        builder.AddEmptyWorldState();
        var platoon = builder.AddPlatoon("Player_0", "Player", "player", "squad", Vector3.Zero);
        builder.AddCharacter(platoon, "character", "Role", "player", Vector3.One, Quaternion.Identity, true);
        builder.Game.Camera.PlayerMoney = money;
        builder.Game.UpdateDerived();
        return builder.Game;
    }
    static byte[] Metadata(int money) => Encoding.UTF8.GetBytes($"{{\"Version\":1,\"Money\":{money}}}");
    static void Check(StoredCheckpoint checkpoint, int money)
    {
        Assert.Equal(money, checkpoint.World.Camera.PlayerMoney);
        Assert.Equal(Metadata(money), checkpoint.Metadata);
        Assert.Equal("Role", Assert.Single(Assert.Single(checkpoint.World.Platoons).Characters).Name);
    }

    [Fact]
    public void WholeBundleRoundTripsAndReadersCanRetainThePreviousCheckpoint()
    {
        using var scratch = new Scratch();
        Assert.Null(scratch.Store.LoadCurrent());
        var first = scratch.Store.Commit(World(100), Metadata(100));
        var loaded = Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent());
        Assert.Equal(first, loaded.Id);
        Check(loaded, 100);
        var second = scratch.Store.Commit(World(200), Metadata(200));
        Assert.NotEqual(first, second);
        Check(Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent()), 200);
        Check(loaded, 100);
        Assert.True(Directory.Exists(System.IO.Path.Combine(scratch.Path, "checkpoints", first.ToString("N"))));
    }

    [Theory]
    [InlineData(CheckpointStage.DataWritten)]
    [InlineData(CheckpointStage.Sealed)]
    public void InterruptedCommitLeavesThePreviousWorldAndMetadataTogether(CheckpointStage failure)
    {
        using var scratch = new Scratch();
        var first = scratch.Store.Commit(World(100), Metadata(100));
        Assert.Throws<IOException>(() => scratch.Store.Commit(World(200), Metadata(200), stage =>
        {
            if (stage == failure) throw new IOException("Injected process boundary failure");
        }));
        // Reopening uses only the pointer; pending/sealed orphan directories never become current by directory order.
        var reopened = Assert.IsType<StoredCheckpoint>(new CheckpointStore(scratch.Path).LoadCurrent());
        Assert.Equal(first, reopened.Id);
        Check(reopened, 100);
        scratch.Store.Commit(World(300), Metadata(300));
        Check(Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent()), 300);
    }

    [Fact]
    public void InterruptedFirstCommitIsNotMistakenForACommittedWorld()
    {
        using var scratch = new Scratch();
        Assert.Throws<IOException>(() => scratch.Store.Commit(World(100), Metadata(100), stage =>
        {
            if (stage == CheckpointStage.Sealed) throw new IOException("Injected first commit failure");
        }));
        Assert.Null(scratch.Store.LoadCurrent());
    }

    [Theory]
    [InlineData("world/quick.save")]
    [InlineData("multiplayer.json")]
    [InlineData("manifest.json")]
    public void CorruptionIsReportedInsteadOfSilentlyRevertingToAnOlderSuccessfulCheckpoint(string file)
    {
        using var scratch = new Scratch();
        scratch.Store.Commit(World(100), Metadata(100));
        var id = scratch.Store.Commit(World(200), Metadata(200));
        var path = System.IO.Path.Combine(scratch.Path, "checkpoints", id.ToString("N"), file.Replace('/', System.IO.Path.DirectorySeparatorChar));
        File.AppendAllText(path, "corruption");
        Assert.Throws<InvalidDataException>(() => scratch.Store.LoadCurrent());
    }

    [Fact]
    public void ExtraFilesCannotBecomeUnverifiedWorldContent()
    {
        using var scratch = new Scratch();
        var id = scratch.Store.Commit(World(100), Metadata(100));
        var extra = System.IO.Path.Combine(scratch.Path, "checkpoints", id.ToString("N"), "world", "unexpected.save");
        File.WriteAllText(extra, "unverified");
        Assert.Throws<InvalidDataException>(() => scratch.Store.LoadCurrent());
    }

    [Fact]
    public void CompetingWriterCannotPublishWhileTheFirstOwnsTheLock()
    {
        using var scratch = new Scratch();
        scratch.Store.Commit(World(100), Metadata(100));
        scratch.Store.Commit(World(200), Metadata(200), stage =>
        {
            if (stage != CheckpointStage.DataWritten) return;
            Check(Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent()), 100);
            Assert.Throws<IOException>(() => new CheckpointStore(scratch.Path).Commit(World(300), Metadata(300)));
        });
        Check(Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent()), 200);
    }

    [Fact]
    public void InvalidWorldPathsAndMetadataCannotReplaceTheCurrentCheckpoint()
    {
        using var scratch = new Scratch();
        scratch.Store.Commit(World(100), Metadata(100));
        var invalid = World(200);
        invalid.Platoons[0].ContentFile = "../../escaped.platoon";
        Assert.Throws<InvalidDataException>(() => scratch.Store.Commit(invalid, Metadata(200)));
        foreach (string json in new[] { "[]", "{}", "{\"Version\":\"one\"}", "{\"Version\":0}" })
            Assert.Throws<InvalidDataException>(() => scratch.Store.Commit(World(200), Encoding.UTF8.GetBytes(json)));
        Check(Assert.IsType<StoredCheckpoint>(scratch.Store.LoadCurrent()), 100);
    }
}
