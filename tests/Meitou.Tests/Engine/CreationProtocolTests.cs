using System.Text;
using System.Text.Json.Nodes;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public class CreationProtocolTests
{
    [Theory]
    [InlineData("Player")]
    [InlineData("Money")]
    [InlineData("Seed")]
    [InlineData("Stats")]
    [InlineData("Equipment")]
    [InlineData("FactionTemplate")]
    public void CreationWireRejectsServerOwnedFields(string field)
    {
        var query = new CreationDraftQuery(Guid.NewGuid(), 1, new(Guid.NewGuid(), 1, 1, "start", "Faction"));
        var frame = CreationProtocol.Commit(query); var json = JsonNode.Parse(frame.Payload.Span)!;
        json["Request"]![field] = 999;
        Assert.Throws<InvalidDataException>(() => CreationProtocol.ReadDraft(frame with { Payload = Encoding.UTF8.GetBytes(json.ToJsonString()) }));
    }
    [Fact]
    public void DuplicateFieldsAndForeignOrDuplicatedReceiptRolesAreRejected()
    {
        var query = new CreationDraftQuery(Guid.NewGuid(), 1, new(Guid.NewGuid(), 1, 1, "start", "Faction"));
        var frame = CreationProtocol.Commit(query);
        string json = Encoding.UTF8.GetString(frame.Payload.Span).Replace("\"Qualification\":1", "\"Qualification\":1,\"Qualification\":2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => CreationProtocol.ReadDraft(frame with { Payload = Encoding.UTF8.GetBytes(json) }));
        var identity = new ReplicationIdentity(Guid.NewGuid(), new(2), 1); var codec = new WorldViewCodec(WorldViewCodecTests.Database());
        var receipt = new CreationReceipt(identity.Player, query.Request.Id, new('A', 64), 1, 1, 7, 0, [new(5)], 0, true);
        var reply = new CreationReply(identity, query.Query, query.Request.Id, 1, CreationOutcome.Committed, Receipt: receipt);
        Assert.Equal(receipt.Roles, CreationProtocol.ReadReply(CreationProtocol.Reply(reply, codec), codec).Receipt!.Roles);
        foreach (var invalid in new[] { receipt with { Player = new(3) }, receipt with { Roles = [new(5), new(5)] }, receipt with { RequestId = Guid.NewGuid() } })
            Assert.Throws<InvalidDataException>(() => CreationProtocol.Reply(reply with { Receipt = invalid }, codec));
    }
}
