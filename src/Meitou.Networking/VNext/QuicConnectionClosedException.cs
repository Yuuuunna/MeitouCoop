namespace Meitou.Networking.VNext;

/// <summary>
/// A native connection shutdown, retaining both transport status and the peer's application error code.
/// </summary>
public sealed class QuicConnectionClosedException(uint nativeStatus, ulong applicationErrorCode)
    : IOException($"QUIC connection closed (status {nativeStatus:X8}, application error {applicationErrorCode:X16}).")
{
    /// <summary>
    /// Whether the connection closed without a transport or application error.
    /// </summary>
    public bool IsGraceful => nativeStatus == 0 && applicationErrorCode == 0;
}
