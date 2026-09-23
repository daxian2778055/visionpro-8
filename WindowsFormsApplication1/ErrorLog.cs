using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;

namespace WindowsFormsApplication1
{
    class ErrorLog
    {
        // ch:R13 日志改「入队 + 后台批量落盘」：原实现每条日志一次全局 lock + Directory.Exists +
        //   File.AppendText(打开/关闭)，相机回调、通讯、IO 线程高频报错时会在全局锁上排队，
        //   反过来拖慢采集与 UI。现在 WriteLog 只做入队(O(1)，不碰磁盘)，由后台 ErrorLogWriter
        //   线程每 500ms 或被唤醒时批量追加；退出路径(FormClosing/ProcessExit)用 FlushPending 兜底。
        //   日志格式与文件路径保持不变(按天分文件)；时间戳在入队时取，更贴近事发时刻。
        private const int MaxQueued = 20000;     // ch:队列封顶：极端刷屏时丢最旧、保最新，内存有界
        private const int FlushIntervalMs = 500; // ch:后台线程最长等待，也即日志最迟可见延迟
        private const int KeepLogDays = 30;      // ch:R14 历史日志按天分文件保留 30 天，否则只增不减

        private static readonly object sync = new object();        // ch:只保护 queue/pending，不做 I/O
        private static readonly Queue<string> queue = new Queue<string>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static readonly object writeLock = new object();   // ch:串行化真正的文件追加(兜底线程可能与后台线程并发)
        private static int pending;                                // ch:已出队但尚未落盘的条数(FlushPending 的完成判据)
        private static DateTime _lastCleanupUtc = DateTime.MinValue; // ch:R14 日志轮转上次执行时刻(24h 节流)
        // ch:与原 File.AppendText 一致：UTF-8 无 BOM(有 BOM 会给新建文件写头，和历史日志文件不一致)
        private static readonly Encoding logEncoding = new UTF8Encoding(false);

        static ErrorLog()
        {
            Thread worker = new Thread(WriterLoop);
            worker.IsBackground = true;
            worker.Name = "ErrorLogWriter";
            worker.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushPending(3000);
            AppDomain.CurrentDomain.DomainUnload += (s, e) => FlushPending(3000);
        }

        private static void WriterLoop()
        {
            while (true)
            {
                wake.WaitOne(FlushIntervalMs);
                FlushPendingCore();
            }
        }

        // ch:取走队列里的全部条目并落盘；pending 在写完(无论成败)后扣减
        private static void FlushPendingCore()
        {
            CleanupOldLogs(); // ch:R14 顺带做日志轮转(内部 24h 节流)，跑在后台线程、不阻塞任何调用方
            List<string> batch = null;
            lock (sync)
            {
                if (queue.Count > 0)
                {
                    batch = new List<string>(queue);
                    queue.Clear();
                }
            }
            if (batch == null)
                return;
            AppendBatch(batch);
            lock (sync) { pending -= batch.Count; }
        }

        private static void AppendBatch(List<string> batch)
        {
            // ch:与原实现一致：日志失败默认吞掉，保证日志记录不引入新的崩溃路径
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory + "Log";
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                string logPath = dir + "\\" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
                lock (writeLock)
                {
                    File.AppendAllLines(logPath, batch, logEncoding);
                }
            }
            catch
            {
            }
        }

        // ch:R14 日志轮转：日志按天分文件、只增不减，长期运行会占满磁盘。
        //   删掉超过 KeepLogDays 的 .txt —— 判据用 LastWriteTime(今天正在写的文件 mtime 必然新鲜，
        //   不会被误删；顺带覆盖那些被改过名的老文件)，全部 try 吞掉，绝不能让清理把程序带崩。
        private static void CleanupOldLogs()
        {
            try
            {
                if ((DateTime.UtcNow - _lastCleanupUtc).TotalHours < 24)
                    return;
                _lastCleanupUtc = DateTime.UtcNow;
                string dir = AppDomain.CurrentDomain.BaseDirectory + "Log";
                if (!Directory.Exists(dir))
                    return;
                DateTime deadline = DateTime.Now.AddDays(-KeepLogDays);
                foreach (string f in Directory.GetFiles(dir, "*.txt"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < deadline)
                            File.Delete(f);
                    }
                    catch { } // ch:单个文件被占用/无权限只跳过，不影响其余清理
                }
            }
            catch
            {
                // ch:清理失败默认吞掉(与日志自身的失败策略一致)
            }
        }

        // ch:等后台线程把队列写完；超时则由调用线程兜底直接落盘(退出路径用)。
        //   不设停止标志：日志线程是后台线程，进程退出时由 ProcessExit 再冲刷一次。
        public static void FlushPending(int timeoutMs)
        {
            try { wake.Set(); } catch { }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs < 0 ? 0 : timeoutMs);
            while (true)
            {
                lock (sync) { if (queue.Count == 0 && pending == 0) return; }
                if (DateTime.UtcNow >= deadline)
                    break;
                Thread.Sleep(20);
            }
            FlushPendingCore(); // ch:超时兜底：调用线程自己把剩余的写掉
        }

        private static void Enqueue(string entry)
        {
            lock (sync)
            {
                while (queue.Count >= MaxQueued)
                {
                    queue.Dequeue();
                    pending--; // ch:被丢弃的最旧一条不再计入待写
                }
                queue.Enqueue(entry);
                pending++;
                // ch:只在「从空到有」时唤醒，避免高频报错时每次入队都打断后台线程；
                //   持续刷屏时由 FlushIntervalMs 定时批量，最迟 500ms 落盘
                if (queue.Count == 1)
                    wake.Set();
            }
        }

        public void WriteLog(string msg)
        {
            // ch:保留原语义：任何异常都不能从日志路径抛出(调用方都在自己的 catch 里兜底)
            try
            {
                // ch:4 行一组一次性入队，保证不同线程的消息不会互相穿插(原实现靠 lock 保证)
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Enqueue("消息" + msg + "/" + "\r\n"
                      + "时间" + now + "/" + "\r\n"
                      + "-------------------------------------------------/" + "\r\n");
            }
            catch
            {
                // ch:日志失败默认吞掉，保证日志记录不引入新的崩溃路径
            }
        }

        public string ReadLog(string path)
        {
            try
            {
                string logPath = path;

                using (StreamReader sr = File.OpenText(logPath))
                {
                    string str = sr.ReadToEnd();
                    sr.Close();
                    return str;
                }
            }
            catch (IOException)
            {
                return "Error：所选日期并没有报告";
            }
        }
    }
}
