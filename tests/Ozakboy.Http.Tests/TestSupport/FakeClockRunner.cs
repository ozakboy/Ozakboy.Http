namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 推進假時鐘直到工作完成。取代 <c>Thread.Sleep</c>,讓時間相關的測試完全確定性。
/// Advances a fake clock until a task completes. It replaces <c>Thread.Sleep</c> and keeps time-dependent
/// tests fully deterministic.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider.Advance"/> 會同步觸發到期的計時器,但被喚醒的接續工作是排到執行緒集區上的。
/// 每推進一步就讓出一次,等接續工作跑完再判斷是否還要繼續推。
/// <see cref="FakeTimeProvider.Advance"/> fires due timers synchronously, but the continuations it wakes are
/// queued to the thread pool. Yielding after each step lets them run before deciding whether to advance again.
/// </remarks>
internal static class FakeClockRunner
{
    public static async Task<T> RunAsync<T>(FakeTimeProvider clock, Task<T> task, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            clock.Advance(step);
            await Task.Yield();
        }

        return await task.ConfigureAwait(false);
    }

    public static async Task RunAsync(FakeTimeProvider clock, Task task, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            clock.Advance(step);
            await Task.Yield();
        }

        await task.ConfigureAwait(false);
    }
}
