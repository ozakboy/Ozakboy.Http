using System.Diagnostics;

namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 推進假時鐘直到工作完成。取代 <c>Thread.Sleep</c>,讓時間相關的測試不必真的等待。
/// Advances a fake clock until a task completes. It replaces <c>Thread.Sleep</c>, so time-dependent tests never
/// actually wait.
/// </summary>
/// <remarks>
/// <para>
/// 每一步之間以真實時間的一個短節拍隔開,並以真實時間(而非步數)為上限。0.2.0 的版本只用
/// <c>Task.Yield()</c> 分隔、以 200 步為上限:冷啟動(JIT、第一次建立 <c>HttpClient</c>、涵蓋率插樁)期間,
/// 那 200 步在幾毫秒內就用完了 —— 工作還沒掛上它的計時器,時鐘就已經停止推進,計時器永遠不會觸發,
/// 測試卡住才失敗。Telegram 套件踩過同一個坑,這裡採用它驗證過的做法。
/// Each step is separated by a short real-time beat, and the loop is bounded by real time rather than a step
/// count. The 0.2.0 version separated steps with <c>Task.Yield()</c> alone and stopped after 200: during a cold
/// start (JIT, the first <c>HttpClient</c>, coverage instrumentation) those 200 steps were gone within
/// milliseconds — the clock stopped before the work had even registered its timer, the timer never fired, and
/// the test hung before failing. The Telegram package hit the same trap; this adopts its proven fix.
/// </para>
/// <para>
/// 先讓工作跑一個節拍再推進:不需要計時器的工作(大多數成功路徑)在時鐘完全不動的情況下就完成了。
/// The work gets a beat before each advance, so work that needs no timer — most success paths — completes
/// without the clock moving at all.
/// </para>
/// </remarks>
internal static class FakeClockRunner
{
    private static readonly TimeSpan RealTimeBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(1);

    public static async Task<T> RunAsync<T>(FakeTimeProvider clock, Task<T> task, TimeSpan step)
    {
        await AdvanceUntilCompletedAsync(clock, task, step).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    public static async Task RunAsync(FakeTimeProvider clock, Task task, TimeSpan step)
    {
        await AdvanceUntilCompletedAsync(clock, task, step).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    private static async Task AdvanceUntilCompletedAsync(FakeTimeProvider clock, Task task, TimeSpan step)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < RealTimeBound)
        {
            await Task.Delay(Beat).ConfigureAwait(false);
            if (!task.IsCompleted)
            {
                clock.Advance(step);
            }
        }

        if (!task.IsCompleted)
        {
            Assert.Fail($"工作在真實時間 {RealTimeBound} 內沒有完成(假時鐘已推進到 {clock.GetUtcNow():O})。The work did not complete within {RealTimeBound} of real time.");
        }
    }
}
