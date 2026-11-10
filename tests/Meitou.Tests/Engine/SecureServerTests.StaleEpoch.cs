using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task StaleCorrelatedQueryFaultsOnlyItsRequestAndCurrentAdmissionStillWorks()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        await using var client = await Connect(scene, scene.Actors[0].Credential, token);
        await client.Enter(scene.Actors[0].Grant.Entity, token); ulong oldView = client.ViewEpoch;
        await client.Send(new(WireMessage.StateResync, ReadOnlyMemory<byte>.Empty), token);
        await client.Until(frame => frame.Kind == WireMessage.ViewBegin && client.ViewEpoch > oldView, token);
        Guid query = Guid.NewGuid();
        await client.SendStaleControl(CreationProtocol.Catalog(new(query)), oldView, token);
        var rejection = NotDispatchedProtocol.Read(await client.Until(frame => frame.Kind == WireMessage.OperationNotDispatched, token));
        Assert.Equal(WireMessage.CreationCatalogQuery, rejection.Kind); Assert.Equal(query, rejection.Query); Assert.Equal(0ul, rejection.Request);
        await client.Send(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty), token);
        await client.Until(frame => frame.Kind == WireMessage.Admission && client.State.Replica.Current is not null, token);
        Assert.Equal(AdmissionPhase.Active, client.Admission.State.Phase); Assert.Equal(oldView + 1, client.ViewEpoch);
    }
}
