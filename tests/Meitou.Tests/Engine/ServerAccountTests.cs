using System.Text;
using System.Text.Json.Nodes;
using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class ServerAccountTests
{
    sealed class Store : IDisposable
    {
        public string Root { get; } = SaveSamples.Temp("server-accounts");
        public string Path => System.IO.Path.Combine(Root, "accounts.json");
        public TokenAccountStore Accounts { get; }
        public Store() { Directory.CreateDirectory(Root); Accounts = new(Path); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    static WorldRuntime Runtime() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), []);
    static AuthenticationRequest Request(IssuedCredential c) => new(c.Account, c.Token);

    [Fact]
    public void RandomCredentialsPersistOnlyHashesAndRotationRevocationInvalidateSessions()
    {
        using var store = new Store(); var issued = store.Accounts.Create("Alice");
        Assert.DoesNotContain(issued.Token, File.ReadAllText(store.Path)); Assert.DoesNotContain(issued.Token, issued.ToString());
        var request = Request(issued); Assert.DoesNotContain(issued.Token, request.ToString());
        Assert.Equal(request, AuthenticationProtocol.Decode(AuthenticationProtocol.Encode(request)));
        var principal = Assert.IsType<AuthenticatedAccount>(store.Accounts.Authenticate(request)); Assert.True(store.Accounts.IsCurrent(principal));
        Assert.Null(store.Accounts.Authenticate(request with { Token = new string('0', 64) }));
        Assert.Null(store.Accounts.Authenticate(request with { Account = Guid.NewGuid() }));
        Assert.NotNull(new TokenAccountStore(store.Path).Authenticate(request));
        var rotated = store.Accounts.Rotate(issued.Account);
        Assert.False(store.Accounts.IsCurrent(principal)); Assert.Null(store.Accounts.Authenticate(request));
        var current = Assert.IsType<AuthenticatedAccount>(store.Accounts.Authenticate(Request(rotated)));
        store.Accounts.SetEnabled(issued.Account, false);
        Assert.False(store.Accounts.IsCurrent(current)); Assert.Null(store.Accounts.Authenticate(Request(rotated)));
        store.Accounts.SetEnabled(issued.Account, true);
        Assert.False(store.Accounts.IsCurrent(current)); Assert.NotNull(store.Accounts.Authenticate(Request(rotated)));
    }

    [Fact]
    public void InterruptedCredentialReplacementLeavesPreviousCredentialValidAndCorruptStoreFailsClosed()
    {
        using var store = new Store(); var issued = store.Accounts.Create("Alice");
        var failing = new TokenAccountStore(store.Path, () => throw new IOException("injected before rename"));
        Assert.Throws<IOException>(() => failing.Rotate(issued.Account));
        Assert.NotNull(store.Accounts.Authenticate(Request(issued))); Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
        File.WriteAllText(store.Path, "{broken");
        Assert.Throws<InvalidDataException>(() => store.Accounts.Authenticate(Request(issued)));
        Assert.Throws<InvalidDataException>(() => store.Accounts.Create("replacement"));
        Assert.Equal("{broken", File.ReadAllText(store.Path));
    }

    [Fact]
    public void BindingMustPersistBeforeOpeningAndRetrySurvivesResolverReplacement()
    {
        using var store = new Store(); var issued = store.Accounts.Create("Alice"); using var runtime = Runtime();
        var bad = new ServerAccountSessions(store.Accounts, runtime, () => throw new IOException("injected checkpoint failure"));
        Assert.Throws<IOException>(() => bad.Open(Request(issued)));
        var account = new AccountIdentity("local-token", issued.Account.ToString("N"));
        var player = runtime.World.Players.FindAccount(account)!; Assert.NotNull(player);
        Assert.True(runtime.World.Players.AccountRequiresCheckpoint(account));
        int checkpoints = 0; PlayerRegistryState? saved = null;
        var good = new ServerAccountSessions(store.Accounts, runtime, () => { checkpoints++; saved = runtime.World.Players.Capture(); });
        var first = good.Open(Request(issued))!;
        Assert.Equal(1, checkpoints); Assert.NotNull(saved); Assert.Equal(player.Id, first.Connection.Player);
        Assert.False(runtime.World.Players.AccountRequiresCheckpoint(account)); Assert.Empty(player.Memberships); Assert.Empty(runtime.World.Players.KnownRoles(player.Id));
        Assert.Equal(AdmissionPhase.Preparing, runtime.World.Authority.GetPhase(first.Connection));
        var second = good.Open(Request(issued))!; Assert.Equal(1, checkpoints); Assert.Equal(first.Connection.Player, second.Connection.Player);
        Assert.False(good.IsCurrent(first)); Assert.True(good.IsCurrent(second));
        store.Accounts.SetEnabled(issued.Account, false); Assert.False(good.IsCurrent(second));
        using var restored = Runtime(); restored.World.Players.Restore(saved!, 0);
        Assert.Equal(player.Id, restored.World.Players.FindAccount(account)!.Id);
        Assert.False(restored.World.Players.AccountRequiresCheckpoint(account));
    }

    [Fact]
    public void AccountsCannotClaimUnboundPlayersOrShareBindingsAndMetadataDowngradeCannotInjectThem()
    {
        using var runtime = Runtime(); var world = runtime.World;
        var legacy = world.Players.Register(new(2));
        var identity = new AccountIdentity("local-token", Guid.NewGuid().ToString("N"));
        var (newcomer, created) = world.Players.ResolveAccount(identity);
        Assert.True(created); Assert.NotEqual(legacy.Id, newcomer.Id);
        Assert.Throws<InvalidOperationException>(() => world.Players.BindAccount(legacy.Id, identity));
        Assert.Throws<InvalidOperationException>(() => world.Players.BindAccount(newcomer.Id, identity with { Subject = "different" }));
        var data = SyntheticTown.Data(SyntheticTown.Database()); var bytes = PlayerRegistrySave.Capture(world, data);
        var node = JsonNode.Parse(bytes)!; node["Version"] = 5; node["Authority"]!["Version"] = 2;
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.PrepareCatalogue(data, Encoding.UTF8.GetBytes(node.ToJsonString())));
        foreach (var player in node["Authority"]!["Players"]!.AsArray()) player!["Account"] = null;
        using var target = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        PlayerRegistrySave.Restore(target, data, Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.Null(target.Players.FindAccount(identity)); Assert.NotNull(target.Players.Find(newcomer.Id));
    }

    [Fact]
    public void InvalidPersistedBindingsCannotPartiallyRestorePlayers()
    {
        using var runtime = Runtime();
        var a = new AccountIdentity("local-token", "a"); var b = new AccountIdentity("local-token", "b");
        runtime.World.Players.ResolveAccount(a); runtime.World.Players.ResolveAccount(b);
        var valid = runtime.World.Players.Capture();
        foreach (var account in new[] { a, new AccountIdentity("INVALID", "b"), new AccountIdentity("local-token", " b ") })
        {
            var records = valid.Players.ToArray(); records[^1] = records[^1] with { Account = account };
            using var target = Runtime();
            Assert.Throws<InvalidDataException>(() => target.World.Players.Restore(valid with { Players = records }, 0));
            Assert.Single(target.World.Players.All); Assert.False(target.World.Players.RequiresExplicitControl);
        }
    }

    [Fact]
    public void AuthenticationWireCannotSupplyPlayerIdentityAndDuplicateFieldsAreRejected()
    {
        var request = new AuthenticationRequest(Guid.NewGuid(), new string('A', 64));
        var frame = AuthenticationProtocol.Encode(request);
        var node = JsonNode.Parse(frame.Payload.Span)!; node["Player"] = 1;
        Assert.Throws<InvalidDataException>(() => AuthenticationProtocol.Decode(frame with { Payload = Encoding.UTF8.GetBytes(node.ToJsonString()) }));
        string duplicate = Encoding.UTF8.GetString(frame.Payload.Span).Replace("\"Token\":", "\"Account\":\"" + Guid.NewGuid() + "\",\"Token\":", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => AuthenticationProtocol.Decode(frame with { Payload = Encoding.UTF8.GetBytes(duplicate) }));
    }
}
