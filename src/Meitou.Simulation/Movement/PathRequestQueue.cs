namespace Meitou.Simulation.Movement;

/// <summary>
/// Coalesces queued requests in place, so held steering cannot continually move a character to the back of the queue.
/// Player work has bounded priority; an already running search is never interrupted.
/// </summary>
internal sealed class PathRequestQueue
{
    /// <summary>
    /// Maximum consecutive player requests while ordinary work is waiting.
    /// </summary>
    const int UrgentBurstLimit = 4;

    /// <summary>
    /// Protects queue membership, replacement, shutdown and blocking consumers.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Player requests in their original enqueue order.
    /// </summary>
    readonly LinkedList<PathRequest> _urgent = new();

    /// <summary>
    /// Other requests in their original enqueue order.
    /// </summary>
    readonly LinkedList<PathRequest> _ordinary = new();

    /// <summary>
    /// The sole queued node per character; running requests are no longer members.
    /// </summary>
    readonly Dictionary<CharacterId, LinkedListNode<PathRequest>> _queued = [];

    /// <summary>
    /// Consecutive priority selections since the last ordinary selection.
    /// </summary>
    int _urgentRun;

    /// <summary>
    /// No more submissions are accepted; consumers drain remaining requests before returning null.
    /// </summary>
    bool _completed;

    /// <summary>
    /// Replaces a character's queued payload without losing its place, returning the superseded request if present.
    /// </summary>
    internal PathRequest? Enqueue(PathRequest request, bool urgent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            if (_queued.TryGetValue(request.Character, out var node))
            {
                var previous = node.Value;
                node.Value = request;
                if (urgent && node.List == _ordinary)
                {
                    _ordinary.Remove(node);
                    _urgent.AddLast(node);
                    Monitor.PulseAll(_gate);
                }
                return previous;
            }

            _queued.Add(request.Character, (urgent ? _urgent : _ordinary).AddLast(request));
            Monitor.PulseAll(_gate);
            return null;
        }
    }

    /// <summary>
    /// Blocks until eligible work is available. The reserved player worker never takes ordinary work, even during shutdown.
    /// </summary>
    internal PathRequest? Take(bool playerOnly = false)
    {
        lock (_gate)
        {
            while ((playerOnly ? _urgent.Count : _queued.Count) == 0 && !_completed) Monitor.Wait(_gate);
            if ((playerOnly ? _urgent.Count : _queued.Count) == 0) return null;

            bool urgent = _urgent.Count > 0 && (playerOnly || _ordinary.Count == 0 || _urgentRun < UrgentBurstLimit);
            var list = urgent ? _urgent : _ordinary;
            var request = list.First!.Value;
            list.RemoveFirst();
            _queued.Remove(request.Character);
            if (!playerOnly) _urgentRun = urgent ? Math.Min(_urgentRun + 1, UrgentBurstLimit) : 0;
            return request;
        }
    }

    /// <summary>
    /// Rejects new work and wakes every idle worker so disposal can join them.
    /// </summary>
    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
