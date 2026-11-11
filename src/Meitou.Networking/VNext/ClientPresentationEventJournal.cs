using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>One presentation consumer per authenticated session. IDs survive latest-state replacement
/// until explicitly consumed; fences discard private payload while preserving deduplication evidence.</summary>
public sealed class ClientPresentationEventJournal(WorldViewCodec codec)
{
    readonly PendingPresentationEvents _pending = new(codec);
    PresentationEventBatch? _lastSource;
    PresentationEventBatch _current = PresentationEventBatch.Empty;
    ulong _consumed, _observed;
    bool _registered, _started;
    public void Register() => _registered = true;
    public void Observe(PresentationEventBatch source)
    {
        if (ReferenceEquals(_lastSource, source))
            return;
        _lastSource = source;
        _observed = Math.Max(_observed, source.HighWater);
        if (!_registered || !_started)
        {
            _consumed = _observed;
            _pending.Reset();
            _pending.Admit(_consumed);
            _current = new(_consumed, []);
            return;
        }

        _current = _pending.Observe(new(source.HighWater, source.Floaters.Where(value => value.Id > _consumed).ToArray()) { Sounds = source.Sounds.Where(value => value.Id > _consumed).ToArray(), Speech = source.Speech.Where(value => value.Id > _consumed).ToArray(), Blood = source.Blood.Where(value => value.Id > _consumed).ToArray(), Sparks = source.Sparks.Where(value => value.Id > _consumed).ToArray(), });
    }

    public PresentationEventBatch Read()
    {
        if (!_registered)
            throw new InvalidOperationException("Register the presentation event consumer before reading.");
        if (!_started)
        {
            _started = true;
            _consumed = _observed;
            _pending.Reset();
            _pending.Admit(_consumed);
            _current = new(_consumed, []);
        }

        return _current;
    }

    public void Acknowledge(ulong watermark)
    {
        if (watermark > _observed)
            throw new InvalidDataException("Presentation consumer acknowledged an unobserved event watermark.");
        _consumed = Math.Max(_consumed, watermark);
        _pending.Admit(_consumed);
        _current = _pending.Observe(new(_observed, []));
    }

    public void Fence()
    {
        _consumed = Math.Max(_consumed, _observed);
        _pending.Reset();
        _pending.Admit(_consumed);
        _current = new(_consumed, []);
        _lastSource = null;
    }
}