using Meitou.Simulation;
namespace Meitou.Networking.VNext;

/// <summary>One presentation consumer per authenticated session. IDs survive latest-state replacement
/// until explicitly consumed; fences discard private payload while preserving deduplication evidence.</summary>
public sealed class ClientPresentationEventJournal(WorldViewCodec codec)
{
    readonly PendingPresentationEvents pending = new(codec);
    PresentationEventBatch? lastSource;
    PresentationEventBatch current = PresentationEventBatch.Empty;
    ulong consumed, observed;
    bool registered, started;
    public void Register() => registered = true;
    public void Observe(PresentationEventBatch source)
    {
        if (ReferenceEquals(lastSource, source)) return;
        lastSource = source; observed = Math.Max(observed, source.HighWater);
        if (!registered || !started) { consumed = observed; pending.Reset(); pending.Admit(consumed); current = new(consumed, []); return; }
        current = pending.Observe(new(source.HighWater, source.Floaters.Where(value => value.Id > consumed).ToArray())
        {
            Sounds = source.Sounds.Where(value => value.Id > consumed).ToArray(), Speech = source.Speech.Where(value => value.Id > consumed).ToArray(),
            Blood = source.Blood.Where(value => value.Id > consumed).ToArray(), Sparks = source.Sparks.Where(value => value.Id > consumed).ToArray(),
        });
    }
    public PresentationEventBatch Read()
    {
        if (!registered) throw new InvalidOperationException("Register the presentation event consumer before reading.");
        if (!started) { started = true; consumed = observed; pending.Reset(); pending.Admit(consumed); current = new(consumed, []); }
        return current;
    }
    public void Acknowledge(ulong watermark)
    {
        if (watermark > observed) throw new InvalidDataException("Presentation consumer acknowledged an unobserved event watermark.");
        consumed = Math.Max(consumed, watermark); pending.Admit(consumed); current = pending.Observe(new(observed, []));
    }
    public void Fence()
    {
        consumed = Math.Max(consumed, observed); pending.Reset(); pending.Admit(consumed); current = new(consumed, []); lastSource = null;
    }
}
