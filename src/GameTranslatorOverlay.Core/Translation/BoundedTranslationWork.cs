using System.Diagnostics.CodeAnalysis;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Owns a bounded set of translation tasks, including results no longer awaited
/// by the current scene. Access from a single session loop; tasks may finish on
/// other threads. No queue: a caller without capacity must keep only its latest frame.
/// </summary>
public sealed class BoundedTranslationWork<T>
{
    private readonly int _capacity;
    private readonly List<Task<T>> _pending = [];
    private Task<bool>? _capacityWait;

    public BoundedTranslationWork(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int PendingCount
    {
        get { PruneCompleted(); return _pending.Count; }
    }

    public bool TryStart(Func<Task<T>> start, [NotNullWhen(true)] out Task<T>? operation)
    {
        PruneCompleted();
        if (_pending.Count >= _capacity)
        {
            operation = null;
            return false;
        }
        operation = start();
        _pending.Add(operation);
        return true;
    }

    public Task<bool> WaitForCapacityAsync()
    {
        PruneCompleted();
        if (_pending.Count < _capacity) return Task.FromResult(true);
        // Rapid scene changes abandon their wait, not this shared capacity signal.
        // Reuse it instead of accumulating WhenAny continuations on slow providers.
        if (_capacityWait is null || _capacityWait.IsCompleted)
            _capacityWait = WaitForAnyAsync(_pending.ToArray());
        return _capacityWait;
    }

    private static async Task<bool> WaitForAnyAsync(Task<T>[] pending)
    {
        await Task.WhenAny(pending).ConfigureAwait(false);
        return true;
    }

    // The session cancels its own token before draining. Awaiting abandoned work
    // keeps its lifetime bounded and observes any fault even when it has no viewer.
    public async Task DrainAsync()
    {
        try { await Task.WhenAll(_pending.ToArray()).ConfigureAwait(false); }
        catch (Exception) { /* Individual results no longer belong to a live scene. */ }
        PruneCompleted();
    }

    private void PruneCompleted()
    {
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            var task = _pending[index];
            if (!task.IsCompleted) continue;
            _ = task.Exception; // Observe faults from work abandoned by an old scene.
            _pending.RemoveAt(index);
        }
    }
}
