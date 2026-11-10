using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
namespace Meitou.Tests.Engine;

public partial class WorldCheckpointCoordinatorTests
{
    [Fact]
    public async Task VerifiedAccountBindingCommitsOffOwnerWhileExistingPlayerReceiptsRemainServiceable()
    {
        using var scene = new Scene(); scene.Runtime.Speed = 0;
        var world = scene.Runtime.World; var grant = world.Players.ControlOf(scene.Role)!.Value;
        var first = world.Authority.Open(scene.Player.Id);
        Assert.True(world.Authority.BeginSynchronization(first)); Assert.True(world.Authority.Activate(first));
        var originalRoles = world.Players.KnownRoles(scene.Player.Id).ToArray();
        var accounts = new TokenAccountStore(Path.Combine(scene.Root, "accounts.json"));
        var credential = accounts.Create("new-binding"); var request = new AuthenticationRequest(credential.Account, credential.Token);
        var identity = accounts.Authenticate(request)!.Identity;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pending = 0;
        var sessions = new ServerAccountSessions(accounts, scene.Runtime, scene.Coordinator)
        {
            RegistrationCommitObserver = stage => { if (stage == CheckpointStage.DataWritten) { committing.TrySetResult(); release.Wait(timeout.Token); } }
        };
        await using var loop = new ServerWorldLoop(scene.Runtime);
        var login = sessions.OpenAsync(loop, request, false, () => { pending++; return Task.CompletedTask; }, timeout.Token);
        try
        {
            await committing.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, pending); Assert.False(login.IsCompleted);
            Assert.True(await loop.InvokeAsync(() => world.Players.AccountRequiresCheckpoint(identity), timeout.Token));
            var receipt = world.Authority.Submit(first, 1, new SelectCommand([scene.Role]) { Tick = 0 }, new([grant]));
            await loop.InvokeAsync(scene.Runtime.Synchronize, timeout.Token);
            Assert.Equal(AuthorityStatus.Evaluated, (await receipt.WaitAsync(timeout.Token)).Status);
            Assert.Equal(originalRoles, await loop.InvokeAsync(() => world.Players.KnownRoles(scene.Player.Id).ToArray(), timeout.Token));
        }
        finally { release.Set(); }
        var authenticated = await login;
        Assert.NotNull(authenticated); Assert.True(sessions.IsCurrent(authenticated));
        Assert.False(await loop.InvokeAsync(() => world.Players.AccountRequiresCheckpoint(identity), timeout.Token));
        Assert.NotNull(scene.Store.LoadCurrent());
    }
}
