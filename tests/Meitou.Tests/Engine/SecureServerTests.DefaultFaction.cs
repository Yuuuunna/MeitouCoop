using Meitou.Networking;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task UnnamedNewPlayersReceiveDistinctFactionsAndCreationReplayKeepsTheName()
    {
        await using var scene = new Scene(creations: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var names = new HashSet<string>(scene.Data.Factions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 2; i++)
        {
            var credential = scene.Accounts.Create("default-name-" + i);
            await using var client = await Connect(scene, credential, timeout.Token);
            var draft = await InitialDraft(client, "unused", timeout.Token);
            draft = draft with { Request = draft.Request with { FactionName = null } };
            var duplicate = await client.Creation(CreationProtocol.Preview(draft with
            {
                Query = Guid.NewGuid(), Request = draft.Request with { FactionName = scene.Data.Factions[scene.Data.PlayerFaction].Name },
            }), timeout.Token);
            Assert.Equal(CreationFailure.InvalidRequest, duplicate.Failure);
            var preview = await client.Creation(CreationProtocol.Preview(draft with { Query = Guid.NewGuid() }), timeout.Token);
            Assert.Equal(CreationOutcome.Preview, preview.Outcome);
            var commit = await client.Creation(CreationProtocol.Commit(draft with { Query = Guid.NewGuid() }), timeout.Token);
            Assert.Equal(CreationOutcome.Committed, commit.Outcome);
            string name = await scene.Loop.InvokeAsync(() => scene.Data.Factions[commit.Receipt!.Faction].Name, timeout.Token);
            Assert.True(names.Add(name));
            var replay = await client.Creation(CreationProtocol.Commit(draft with { Query = Guid.NewGuid() }), timeout.Token);
            Assert.Equal(commit.Receipt!.Faction, replay.Receipt!.Faction);
            Assert.Equal(name, await scene.Loop.InvokeAsync(() => scene.Data.Factions[replay.Receipt.Faction].Name, timeout.Token));
            Assert.Equal(commit.Receipt.Roles, replay.Receipt.Roles);
            await client.Enter(commit.Receipt.Roles[0], timeout.Token);
        }
    }
}
