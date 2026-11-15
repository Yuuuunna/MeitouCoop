using System.Security.Cryptography;
using System.Text;
using Meitou.Diagnostics;
using Meitou.Networking;
using Meitou.Server;

namespace Meitou.Tests.Engine;

/// <summary>
/// Operator-upload framing, log filtering and receiver failure isolation without a native transport dependency.
/// </summary>
public sealed class DiagnosticTransferTests
{
    /// <summary>
    /// Running logs use the host's file, preserve useful text beyond the 512-line summary and omit secret-like lines.
    /// </summary>
    [Fact]
    public void RuntimeLogIncludesFullAvailableFileWithCredentialAndPathFiltering()
    {
        using var scratch = new Scratch();
        string path = Path.Combine(scratch.Root, "log.txt");
        string lines = string.Join('\n', Enumerable.Range(0, 600).Select(i => "event " + i));
        File.WriteAllText(path, lines + "\nAuthorization: Bearer secret-value\nloaded C:\\Users\\Alice\\asset.mesh\nlast event\n");
        var log = DiagnosticRuntimeLog.Read(path);
        Assert.Equal("sanitized", log.Status);
        string text = Encoding.UTF8.GetString(log.Content!);
        Assert.Contains("event 0", text);
        Assert.Contains("event 599", text);
        Assert.Contains("last event", text);
        Assert.DoesNotContain("secret-value", text);
        Assert.DoesNotContain("Alice", text);
        Assert.Null(DiagnosticRuntimeLog.Read(Path.Combine(scratch.Root, "missing")).Content);
    }

    /// <summary>
    /// Invalid lengths, IDs and offsets are rejected before the receiver creates storage.
    /// </summary>
    [Fact]
    public void OperatorProtocolRejectsUnboundedOffersAndMalformedChunks()
    {
        Guid report = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.Collect(Guid.Empty));
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.Offer(new(report, DiagnosticTransferStatus.Ready,
            checked((int)DiagnosticStore.MaximumBytes + 1), new byte[32])));
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.Offer(new(report, DiagnosticTransferStatus.Disabled, 42, new byte[32])));
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.Pull(report, 1));
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.Chunk(report, 0, new byte[DiagnosticTransferProtocol.ChunkBytes + 1]));
        var chunk = DiagnosticTransferProtocol.Chunk(report, 0, [1, 2, 3]);
        Assert.Equal(report, DiagnosticTransferProtocol.ReadChunk(chunk).Report);
        chunk[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => DiagnosticTransferProtocol.ReadChunk(chunk));
    }

    /// <summary>
    /// A corrupt or mis-scoped file is never published as a successful artifact, and partial bytes are removed.
    /// </summary>
    [Theory]
    [InlineData("hash")]
    [InlineData("scope")]
    [InlineData("offset")]
    public async Task ReceiverRejectsInvalidEvidenceWithoutPublishingPartialFiles(string fault)
    {
        using var scratch = new Scratch();
        Guid report = Guid.NewGuid();
        var scope = new DiagnosticScope(Guid.NewGuid(), 1, 1);
        var recorder = new DiagnosticRecorder();
        var manifest = recorder.Capture(report, Guid.NewGuid(), "client", "test", fault == "scope" ? scope with { Connection = 2 } : scope, "test");
        string source = new DiagnosticStore(scratch.Root).Save(manifest);
        byte[] bytes = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        byte[] digest = SHA256.HashData(bytes);
        if (fault == "hash") digest[0] ^= 1;
        ServerDiagnosticPeer? peer = null;
        Task Send(WireFrame frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (frame.Kind == WireMessage.DiagnosticCollect)
                peer!.ReceiveOffer(DiagnosticTransferProtocol.Offer(new(report, DiagnosticTransferStatus.Ready, bytes.Length, digest)));
            else if (frame.Kind == WireMessage.DiagnosticPull)
                peer!.ReceiveChunk(DiagnosticTransferProtocol.Chunk(report, fault == "offset" ? DiagnosticTransferProtocol.ChunkBytes : 0, bytes));
            return Task.CompletedTask;
        }

        using var receiver = peer = new ServerDiagnosticPeer(scope, Send);
        var result = await receiver.CollectAsync(report, scratch.Root, TestContext.Current.CancellationToken);
        Assert.Equal("Failed:InvalidDataException", result.Status);
        Assert.Null(result.File);
        Assert.False(File.Exists(Path.Combine(scratch.Root, "client-1-1.zip")));
        Assert.Empty(Directory.GetFiles(scratch.Root, "*.part"));
    }

    /// <summary>
    /// Silent clients time out independently; authenticated disconnects complete pending collection without waiting for that deadline.
    /// </summary>
    [Fact]
    public async Task ReceiverDistinguishesTimeoutFromDisconnectedPlayer()
    {
        using var scratch = new Scratch();
        var scope = new DiagnosticScope(Guid.NewGuid(), 1, 1);
        using var silent = new ServerDiagnosticPeer(scope, (_, _) => Task.CompletedTask, timeout: TimeSpan.FromMilliseconds(1));
        var timedOut = await silent.CollectAsync(Guid.NewGuid(), scratch.Root, TestContext.Current.CancellationToken);
        Assert.Equal("Timeout", timedOut.Status);

        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var disconnecting = new ServerDiagnosticPeer(scope, (_, _) =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        });
        var pending = disconnecting.CollectAsync(Guid.NewGuid(), scratch.Root, TestContext.Current.CancellationToken);
        await requested.Task.WaitAsync(TestContext.Current.CancellationToken);
        disconnecting.Dispose();
        Assert.Equal("Disconnected", (await pending).Status);
        Assert.Empty(Directory.GetFiles(scratch.Root));
    }

    /// <summary>
    /// Test-owned storage, separate from real game logs and server reports.
    /// </summary>
    sealed class Scratch : IDisposable
    {
        /// <summary>
        /// Unique root created by this fixture.
        /// </summary>
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "meitou-transfer-" + Guid.NewGuid().ToString("N"))).FullName;
        /// <summary>
        /// Removes only this fixture's directory after pending work has completed.
        /// </summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
