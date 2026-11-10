using Meitou.Networking;
using Meitou.Simulation;
namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task ReadyBeforeFinalBulkAckRetainsAuthorizationAfterMotionHistoryRetiresThePublication()
    {
        int publications = 0;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scene = new Scene(publicationObserved: _ =>
        {
            int count = Interlocked.Increment(ref publications);
            if (count >= 32) retired.TrySetResult();
            if (count >= 48) afterAck.TrySetResult();
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        await using var client = await Connect(scene, scene.Actors[0].Credential, token);
        await client.Send(SessionProtocol.Enter(scene.Actors[0].Grant.Entity), token);
        await client.Until(_ => client.Admission.State.Phase == AdmissionPhase.Active && client.State.Replica.Current is not null, token, acknowledge: false);
        ulong originalEpoch = client.ViewEpoch;
        Assert.NotNull(client.LastWithheldBulkAck);
        await retired.Task.WaitAsync(token);
        // The final ACK runs through the separate coalescer after Ready. The old ordinary grant
        // history is now gone; only the still-live transfer pin can authorize this final credit.
        await client.Send(client.LastWithheldBulkAck!, token);
        await afterAck.Task.WaitAsync(token);
        await client.Send(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty), token);
        await client.Until(frame => frame.Kind == WireMessage.Admission, token);
        Assert.Equal(originalEpoch, client.ViewEpoch);
        Assert.Equal(AdmissionPhase.Active, client.Admission.State.Phase);
        Assert.NotNull(client.State.Replica.Current);
    }
}
