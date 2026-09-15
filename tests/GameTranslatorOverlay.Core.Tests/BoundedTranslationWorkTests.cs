using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class BoundedTranslationWorkTests
{
    private static readonly TimeSpan FailureTimeout = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource<string> Pending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Capacity_two_rejects_a_third_factory_without_queuing_it()
    {
        var work = new BoundedTranslationWork<string>(2);
        var first = Pending();
        var second = Pending();
        var replacement = Pending();
        var rejectedFactoryCalls = 0;

        Assert.True(work.TryStart(() => first.Task, out var firstOperation));
        Assert.True(work.TryStart(() => second.Task, out var secondOperation));
        Assert.Same(first.Task, firstOperation);
        Assert.Same(second.Task, secondOperation);
        Assert.Equal(2, work.PendingCount);
        Assert.False(work.TryStart(() =>
        {
            rejectedFactoryCalls++;
            return replacement.Task;
        }, out var rejectedOperation));
        Assert.Null(rejectedOperation);
        Assert.Equal(0, rejectedFactoryCalls);

        var capacity = work.WaitForCapacityAsync();
        Assert.False(capacity.IsCompleted);
        first.SetResult("first");
        Assert.True(await capacity.WaitAsync(FailureTimeout));
        Assert.Equal(1, work.PendingCount);
        Assert.Equal(0, rejectedFactoryCalls);

        // Freeing every slot and draining still must not start rejected work.
        second.SetResult("second");
        await work.DrainAsync().WaitAsync(FailureTimeout);
        Assert.Equal(0, work.PendingCount);
        Assert.Equal(0, rejectedFactoryCalls);
        Assert.False(replacement.Task.IsCompleted);

        Assert.True(work.TryStart(() =>
        {
            rejectedFactoryCalls++;
            return replacement.Task;
        }, out var explicitlyStarted));
        Assert.Same(replacement.Task, explicitlyStarted);
        Assert.Equal(1, rejectedFactoryCalls);
        replacement.SetResult("requested again explicitly");
        await work.DrainAsync().WaitAsync(FailureTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_or_cancellation_releases_capacity_without_failing_the_waiter(bool cancel)
    {
        var work = new BoundedTranslationWork<string>(2);
        var abandoned = Pending();
        var stillRunning = Pending();
        var replacement = Pending();
        Assert.True(work.TryStart(() => abandoned.Task, out _));
        Assert.True(work.TryStart(() => stillRunning.Task, out _));
        var capacity = work.WaitForCapacityAsync();
        Assert.False(capacity.IsCompleted);

        if (cancel) abandoned.SetCanceled();
        else abandoned.SetException(new InvalidOperationException("abandoned scene"));

        Assert.True(await capacity.WaitAsync(FailureTimeout));
        Assert.Equal(1, work.PendingCount);
        Assert.True(work.TryStart(() => replacement.Task, out var operation));
        Assert.Same(replacement.Task, operation);
        Assert.Equal(2, work.PendingCount);

        stillRunning.SetResult("still running");
        replacement.SetResult("replacement");
        await work.DrainAsync().WaitAsync(FailureTimeout);
        Assert.Equal(0, work.PendingCount);
    }

    [Fact]
    public async Task Full_pending_set_shares_one_capacity_wait_and_refilled_set_gets_a_new_wait()
    {
        var work = new BoundedTranslationWork<string>(2);
        var first = Pending();
        var second = Pending();
        var replacement = Pending();
        Assert.True(work.TryStart(() => first.Task, out _));
        Assert.True(work.TryStart(() => second.Task, out _));

        var originalWait = work.WaitForCapacityAsync();
        Assert.False(originalWait.IsCompleted);
        Assert.Same(originalWait, work.WaitForCapacityAsync());
        Assert.Same(originalWait, work.WaitForCapacityAsync());

        first.SetResult("finished first scene");
        Assert.True(await originalWait.WaitAsync(FailureTimeout));
        Assert.True(work.TryStart(() => replacement.Task, out _));
        Assert.Equal(2, work.PendingCount);

        var refilledWait = work.WaitForCapacityAsync();
        Assert.NotSame(originalWait, refilledWait);
        Assert.False(refilledWait.IsCompleted);
        Assert.Same(refilledWait, work.WaitForCapacityAsync());
        Assert.Same(refilledWait, work.WaitForCapacityAsync());

        second.SetResult("finished second scene");
        Assert.True(await refilledWait.WaitAsync(FailureTimeout));
        replacement.SetResult("latest scene");
        await work.DrainAsync().WaitAsync(FailureTimeout);
        Assert.Equal(0, work.PendingCount);
    }

    [Fact]
    public async Task Capacity_wait_completes_immediately_while_a_slot_is_available()
    {
        var work = new BoundedTranslationWork<string>(2);
        var empty = work.WaitForCapacityAsync();
        Assert.True(empty.IsCompletedSuccessfully);
        Assert.True(await empty);

        var pending = Pending();
        Assert.True(work.TryStart(() => pending.Task, out _));
        var partlyFull = work.WaitForCapacityAsync();
        Assert.True(partlyFull.IsCompletedSuccessfully);
        Assert.True(await partlyFull);
        Assert.Equal(1, work.PendingCount);

        pending.SetResult("finished");
        await work.DrainAsync().WaitAsync(FailureTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drain_observes_abandoned_fault_and_cancellation_and_waits_for_both(bool cancelFirst)
    {
        var work = new BoundedTranslationWork<string>(2);
        var failed = Pending();
        var canceled = Pending();
        Assert.True(work.TryStart(() => failed.Task, out _));
        Assert.True(work.TryStart(() => canceled.Task, out _));

        // Finish one operation before draining: this assertion does not depend
        // on which asynchronous continuation happens to run first.
        if (cancelFirst) canceled.SetCanceled();
        else failed.SetException(new InvalidOperationException("old scene failed"));
        // Neither returned operation is awaited by its former scene.
        var drain = work.DrainAsync();
        Assert.False(drain.IsCompleted);

        if (cancelFirst) failed.SetException(new InvalidOperationException("old scene failed"));
        else canceled.SetCanceled();
        await drain.WaitAsync(FailureTimeout);
        Assert.True(drain.IsCompletedSuccessfully);
        Assert.True(failed.Task.IsFaulted);
        Assert.True(canceled.Task.IsCanceled);
        Assert.Equal(0, work.PendingCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Already_completed_operation_does_not_hold_a_slot(string completion)
    {
        var work = new BoundedTranslationWork<string>(1);
        var completed = Pending();
        switch (completion)
        {
            case "success": completed.SetResult("cached"); break;
            case "fault": completed.SetException(new InvalidOperationException("already failed")); break;
            case "cancel": completed.SetCanceled(); break;
        }

        Assert.True(work.TryStart(() => completed.Task, out var completedOperation));
        Assert.Same(completed.Task, completedOperation);
        var replacement = Pending();
        // TryStart itself must reclaim the slot, without a preceding count read.
        Assert.True(work.TryStart(() => replacement.Task, out var operation));
        Assert.Same(replacement.Task, operation);
        Assert.Equal(1, work.PendingCount);

        replacement.SetResult("new work");
        await work.DrainAsync().WaitAsync(FailureTimeout);
        Assert.Equal(0, work.PendingCount);
    }

    [Fact]
    public async Task Drain_with_no_pending_operations_completes_successfully()
    {
        var work = new BoundedTranslationWork<string>(2);
        var drain = work.DrainAsync();
        Assert.True(drain.IsCompletedSuccessfully);
        await drain;
        Assert.Equal(0, work.PendingCount);
    }
}