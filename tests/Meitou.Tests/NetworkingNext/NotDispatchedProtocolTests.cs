using Meitou.Networking;
using Meitou.Networking.VNext;
namespace Meitou.Tests.NetworkingNext;

public sealed class NotDispatchedProtocolTests
{
    [Fact]
    public void CorrelationContainsOnlyTypedIdentityAndRejectsMalformedReasons()
    {
        Guid query = Guid.NewGuid(); var frame = NotDispatchedProtocol.Reply(CreationProtocol.Catalog(new(query)));
        Assert.Equal(27, frame.Payload.Length);
        var decoded = NotDispatchedProtocol.Read(frame);
        Assert.Equal(WireMessage.CreationCatalogQuery, decoded.Kind); Assert.Equal(WireMessage.CreationReply, decoded.ReplyKind); Assert.Equal(query, decoded.Query);
        byte[] corrupt = frame.Payload.ToArray(); corrupt[2] = 99;
        Assert.Throws<InvalidDataException>(() => NotDispatchedProtocol.Read(frame with { Payload = corrupt }));
        Assert.Throws<InvalidDataException>(() => NotDispatchedProtocol.Reply(new(WireMessage.Authenticate, "credential"u8.ToArray())));
    }
}
