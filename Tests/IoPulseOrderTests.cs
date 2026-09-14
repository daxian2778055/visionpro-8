using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VP8CamUnitTests;

/// <summary>
/// IO 脉冲时序回归测试（复刻 P1-07/R6 修复后的语义）。
/// 核心不变量：
///  1) 置高(on)与关断(off)按入队顺序单调 —— 不出现"关断先入队、置高后入队"（那会卡在高电平）。
///  2) Flush 的排空判定基于"outstanding 计数"而非仅队列长度，
///     覆盖"工作线程已出队、尚未执行完"的窗口（否则最后一条 off 未执行就返回）。
///  3) 每个高电平的置位与清除成对，最终空闲时全部关闭。
/// </summary>
public class IoPulseOrderTests
{
    /// <summary>
    /// 用"锁内原子化 状态+入队"的模型复刻 RequestIoPulse / ExpireOneIoPulse，
    /// 记录 IO 动作事件序列以便断言单调性。
    /// </summary>
    private sealed class IoExecutor
    {
        private readonly object _lock = new();
        private bool _okHigh, _ngHigh;
        public readonly List<string> Events = new();

        // P1-07：改状态与入队在同一把锁内 → 顺序单调
        public void RequestPulse(bool okLine, int widthMs)
        {
            lock (_lock)
            {
                if (okLine) { if (!_okHigh) { _okHigh = true; Events.Add("onOk"); } }
                else { if (!_ngHigh) { _ngHigh = true; Events.Add("onNg"); } }
            }
        }

        public void Expire(int now)
        {
            lock (_lock)
            {
                if (_okHigh) { _okHigh = false; Events.Add("offOk"); }
                if (_ngHigh) { _ngHigh = false; Events.Add("offNg"); }
            }
        }

        public bool AnyHigh { get { lock (_lock) return _okHigh || _ngHigh; } }
    }

    [Fact]
    public void OnBeforeOff_HoldsMonotonicOrder()
    {
        var ex = new IoExecutor();
        ex.RequestPulse(okLine: true, widthMs: 100); // onOk
        ex.Expire(now: 1);                             // offOk
        Assert.Equal(new[] { "onOk", "offOk" }, ex.Events);
        Assert.False(ex.AnyHigh);
    }

    [Fact]
    public void NoOffWithoutPriorOn()
    {
        var ex = new IoExecutor();
        ex.Expire(now: 0);
        Assert.Empty(ex.Events);
        Assert.False(ex.AnyHigh);
    }

    [Fact]
    public void RepeatedRequest_DoesNotDuplicateOn()
    {
        var ex = new IoExecutor();
        ex.RequestPulse(true, 100);
        ex.RequestPulse(true, 100);
        ex.RequestPulse(true, 100);
        Assert.Equal(1, ex.Events.Count(e => e == "onOk"));
    }

    [Fact]
    public void OkAndNg_AreIndependentLines()
    {
        var ex = new IoExecutor();
        ex.RequestPulse(okLine: true, 100);   // onOk
        ex.RequestPulse(okLine: false, 100);  // onNg
        ex.Expire(now: 1);                     // offOk, offNg
        Assert.Equal(new[] { "onOk", "onNg", "offOk", "offNg" }, ex.Events);
    }

    // ---------- Flush outstanding 屏障 ----------

    [Fact]
    public void QueueLength_CanBeZeroWhileTaskStillExecuting()
    {
        // 关键点：任务被消费者取走后 队列 Count 立刻归 0，
        // 但任务体（如最后一条 off 关断）仍在执行 → 只查队列长度会误判"已排空"。
        // 正确判据是 outstanding 计数（入队前 +1，执行完 finally 里 -1）。
        int outstanding = 0;
        var q = new BlockingCollection<Action>();

        var worker = Task.Run(() =>
        {
            foreach (var act in q.GetConsumingEnumerable())
            {
                try { act(); }
                finally { Interlocked.Decrement(ref outstanding); }
            }
        });

        Interlocked.Increment(ref outstanding);   // 入队前登记
        q.Add(() => Thread.Sleep(150));           // 模拟最后一条关断执行较慢
        _ = worker; // 消费者后台运行，测试仅验证 outstanding 判据

        Thread.Sleep(30);                          // 让消费者取走（Count 归 0），任务体仍在睡
        Assert.True(q.Count == 0);               // 队列已被取空
        // 任务执行中时 outstanding 仍应 >0，仅查队列长度会误判排空
        Assert.True(Interlocked.CompareExchange(ref outstanding, 0, 0) > 0);

        // 轮询到真正排空
        var deadline = Environment.TickCount + 2000;
        while (Interlocked.CompareExchange(ref outstanding, 0, 0) != 0 && Environment.TickCount < deadline)
            Thread.Sleep(5);
        Assert.Equal(0, Interlocked.CompareExchange(ref outstanding, 0, 0)); // 排空后 outstanding 应为 0

        q.CompleteAdding();
    }

    [Fact]
    public void Flush_DrainsWhenAllTasksCompleted()
    {
        int outstanding = 0;
        var q = new BlockingCollection<Action>();
        var done = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };

        var worker = Task.Run(() =>
        {
            foreach (var act in q.GetConsumingEnumerable())
            {
                try { act(); }
                finally { Interlocked.Decrement(ref outstanding); }
            }
        });

        void Enqueue(Action a, int i) { Interlocked.Increment(ref outstanding); q.Add(a); done[i].Wait(); }
        Enqueue(() => { done[0].Set(); }, 0);
        Enqueue(() => { done[1].Set(); }, 1);
        _ = worker; // 消费者后台运行，测试仅验证 outstanding 判据

        // 两个任务都执行完（各自的 Set 触发），outstanding 归 0 → 可安全排空
        done[0].Wait(); done[1].Wait();
        var deadline = Environment.TickCount + 2000;
        while (Interlocked.CompareExchange(ref outstanding, 0, 0) != 0 && Environment.TickCount < deadline)
            Thread.Sleep(5);
        Assert.Equal(0, Interlocked.CompareExchange(ref outstanding, 0, 0));

        q.CompleteAdding();
    }
}
