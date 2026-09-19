using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    static class Program
    {
        /// <summary>
        /// 1代表中文，2代表英文
        /// </summary>
        public static int Language = 1;

        /// <summary>
        /// 是否显示相关的信息
        /// </summary>
        public static bool ShowAuthorInfomation = true;

        // ================= 闪退崩溃转储（MiniDump）=================
        // 说明：托管未处理异常（.NET 抛出的异常）能直接捕获写日志；
        // 但"闪退"通常是原生崩溃（SDK 内部访问冲突/栈溢出/驱动错误），
        // 托管异常处理器抓不到。这里提供三层记录：
        //  1) MiniDumpWriteDump：托管异常处理器中生成 .dmp（含崩溃现场栈）
        //  2) SetUnhandledExceptionFilter：原生崩溃过滤器（尽力而为）
        //  3) 部署时配置 WER LocalDumps（见 WER_LocalDumps.reg，最可靠兜底）
        [StructLayout(LayoutKind.Sequential)]
        struct MINIDUMP_EXCEPTION_INFORMATION
        {
            public uint ThreadId;
            public IntPtr ExceptionPointers;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ClientPointers;
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr hProcess, uint ProcessId, IntPtr hFile,
            uint DumpType, ref MINIDUMP_EXCEPTION_INFORMATION ExceptionParam, IntPtr UserStreamParam, IntPtr CallbackParam);

        [DllImport("dbghelp.dll", SetLastError = true, EntryPoint = "MiniDumpWriteDump")]
        static extern bool MiniDumpWriteDumpNoInfo(IntPtr hProcess, uint ProcessId, IntPtr hFile,
            uint DumpType, IntPtr ExceptionParam, IntPtr UserStreamParam, IntPtr CallbackParam);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        const uint MiniDumpNormal = 0x00000000;
        const uint MiniDumpWithHandleData = 0x00000004;
        const uint MiniDumpWithUnloadedModules = 0x00000020;
        const uint MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
        const uint MiniDumpWithProcessThreadData = 0x00000100;
        const uint MiniDumpWithThreadInfo = 0x00001000;

        static readonly object dumpLock = new object();
        static bool dumpWritten = false;

        static void WriteCrashDump(Exception ex)
        {
            WriteCrashDump(ex, IntPtr.Zero);
        }

        static void WriteCrashDump(Exception ex, IntPtr nativeExceptionPointers)
        {
            try
            {
                lock (dumpLock)
                {
                    if (dumpWritten) return;
                    dumpWritten = true;
                    string dir = AppDomain.CurrentDomain.BaseDirectory + "CrashDump";
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string file = dir + "\\" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dmp";
                    IntPtr expPtr = nativeExceptionPointers;
                    if (expPtr == IntPtr.Zero)
                    {
                        try { expPtr = Marshal.GetExceptionPointers(); } catch { expPtr = IntPtr.Zero; }
                    }
                    uint flags = MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithProcessThreadData | MiniDumpWithThreadInfo;
                    bool ok = false;
                    int lastErr = 0;
                    using (FileStream fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        IntPtr hFile = fs.SafeFileHandle.DangerousGetHandle();
                        IntPtr hProc = Process.GetCurrentProcess().Handle;
                        uint pid = (uint)Process.GetCurrentProcess().Id;
                        if (expPtr != IntPtr.Zero)
                        {
                            MINIDUMP_EXCEPTION_INFORMATION mei = new MINIDUMP_EXCEPTION_INFORMATION();
                            mei.ThreadId = GetCurrentThreadId();
                            mei.ExceptionPointers = expPtr;
                            mei.ClientPointers = false;
                            ok = MiniDumpWriteDump(hProc, pid, hFile, flags, ref mei, IntPtr.Zero, IntPtr.Zero);
                            lastErr = Marshal.GetLastWin32Error();
                        }
                        if (!ok)
                        {
                            ok = MiniDumpWriteDumpNoInfo(hProc, pid, hFile, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            lastErr = Marshal.GetLastWin32Error();
                        }
                        if (!ok)
                        {
                            ok = MiniDumpWriteDumpNoInfo(hProc, pid, hFile, MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            lastErr = Marshal.GetLastWin32Error();
                        }
                        fs.Flush();
                    }
                    ErrorLog MsgErroeLog = new ErrorLog();
                    long size = 0;
                    try { if (File.Exists(file)) size = new FileInfo(file).Length; } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                    if (!ok || size <= 0)
                    {
                        try { if (File.Exists(file) && size <= 0) File.Delete(file); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                        MsgErroeLog.WriteLog("崩溃转储失败: ok=" + ok + " size=" + size + " err=" + lastErr + " 异常:" + (ex == null ? "(原生崩溃)" : ex.ToString()));
                    }
                    else
                    {
                        MsgErroeLog.WriteLog("已生成崩溃转储文件:" + file + " size=" + size + " 异常:" + (ex == null ? "(原生崩溃)" : ex.ToString()));
                    }
                }
            }
            catch (Exception dumpEx)
            {
                try
                {
                    ErrorLog MsgErroeLog = new ErrorLog();
                    MsgErroeLog.WriteLog("崩溃转储异常:" + dumpEx.Message);
                }
                catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
            }
        }

        // ================= 原生崩溃过滤器（尽力而为）=================
        delegate int TopLevelExceptionFilter(IntPtr lpExceptionInfo);

        [DllImport("kernel32.dll")]
        static extern IntPtr SetUnhandledExceptionFilter(TopLevelExceptionFilter lpTopLevelExceptionFilter);

        static TopLevelExceptionFilter filterDelegate; // 保持引用，防止被 GC 回收

        // ch:FirstChanceException 记录限频：检测回调链上偶发异常风暴时，每次捕获堆栈+写盘（毫秒级）会拖慢节拍，
        //    同一 2 秒窗口内只记录第一条（窗口按 Environment.TickCount 差值判断，用 unchecked 减法处理回绕）
        static int _firstChanceLastLogTick = 0;

        static int UnhandledNativeFilter(IntPtr lpExceptionInfo)
        {
            // ch:原生崩溃（SDK 内部访问冲突/非法指令等），尽力记录后终止
            try
            {
                WriteCrashDump(null, lpExceptionInfo);
            }
            catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
            return 1; // EXCEPTION_EXECUTE_HANDLER：交给系统终止进程
        }

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        /// <summary>
        /// ch:P2 单实例互斥量：优先 Global\（跨会话唯一）；非管理员账户无 SeCreateGlobalPrivilege 时
        ///   创建会抛 UnauthorizedAccessException → 降级 Local\（会话级），保证普通用户仍能启动。
        /// </summary>
        static Mutex CreateSingleInstanceMutex(out bool created, ErrorLog log)
        {
            try
            {
                return new Mutex(true, @"Global\ZC_VP8Cam_Mutex", out created);
            }
            catch (Exception exGlobal)
            {
                try
                {
                    Mutex local = new Mutex(true, @"Local\ZC_VP8Cam_Mutex", out created);
                    if (log != null)
                        log.WriteLog("Global\\ 互斥量不可用，已降级 Local\\（单实例保护仅限当前会话）：" + exGlobal.Message);
                    return local;
                }
                catch (Exception exLocal)
                {
                    created = true; // 放弃单实例检查，优先保证程序可启动
                    if (log != null)
                        log.WriteLog("单实例互斥量创建失败，已跳过单实例检查：" + exGlobal.Message + " / " + exLocal.Message);
                    return null;
                }
            }
        }

        [STAThread]
        static void Main()
        {
            bool newMutexCreated = true;
            ErrorLog MsgErroeLog = new ErrorLog();
            // ch:P3-⑥ 固定名称 + Global\ 前缀：原用 Assembly.FullName 属会话级互斥，
            //   多用户/远程桌面/快速切换场景下仍可多开；Global\ 前缀保证跨会话唯一。
            // ch:P2 非管理员账户对 Global\ 无权限会抛异常导致无法启动 → 由 CreateSingleInstanceMutex 自动降级 Local\
            Mutex singleInstance = CreateSingleInstanceMutex(out newMutexCreated, MsgErroeLog);
            using (singleInstance)
            {
                if (!newMutexCreated)
                {
                    MessageBox.Show("程序已启动！请不要启动多个程序");
                    System.Environment.Exit(0);
                }
                else
                {
                    try
                    {
                        MsgErroeLog.WriteLog("====== 软件启动 ======");
                        // ch:注册原生崩溃过滤器（必须在最前面注册）
                        filterDelegate = new TopLevelExceptionFilter(UnhandledNativeFilter);
                        SetUnhandledExceptionFilter(filterDelegate);
                        // ch:记录 NullReferenceException / DirectoryNotFoundException 的完整堆栈（即便被 catch 吞掉也能定位来源）
                        AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
                        {
                            if (e.Exception is NullReferenceException || e.Exception is DirectoryNotFoundException || e.Exception is FileNotFoundException)
                            {
                                try
                                {
                                    // ch:仅过滤 XmlSerializer 探测预生成程序集（TempAssembly）时抛的 FileNotFoundException（正常回退路径），
                                    // ch:限定异常类型+堆栈组合，避免掩盖业务代码中真实的 NRE/DirectoryNotFoundException/FileNotFoundException
                                    string cur = Environment.StackTrace ?? "";
                                    if (cur.Contains("MccDaq") || cur.Contains("Cognex.VisionPro.QuickBuild") || cur.Contains("Cognex.VisionPro.CogSerializer"))
                                        return;
                                    if (e.Exception is FileNotFoundException && cur.Contains("TempAssembly.LoadGeneratedAssembly"))
                                        return;
                                    // ch:限频（白名单过滤之后）：2 秒窗口内只记录第一条，防止异常风暴洪泛
                                    int nowTick = Environment.TickCount;
                                    int lastTick = System.Threading.Interlocked.CompareExchange(ref _firstChanceLastLogTick, 0, 0);
                                    if (lastTick != 0 && unchecked(nowTick - lastTick) < 2000)
                                        return;
                                    System.Threading.Interlocked.Exchange(ref _firstChanceLastLogTick, nowTick);
                                    ErrorLog nreLog = new ErrorLog();
                                    nreLog.WriteLog("异常定位:" + e.Exception.GetType().Name + ":" + e.Exception.Message + " 调用栈:" + cur);
                                }
                                catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                            }
                        };
                        // ch:注册托管异常处理器
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                        AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandleException);
                        Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(Application_ThreadException);
                        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("入口点异常" + ex.Message);
                        MessageBox.Show("入口点异常" + ex.Message);
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new Form1());
                }
            }
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            ErrorLog MsgErroeLog = new ErrorLog();
            MsgErroeLog.WriteLog("未处理task异常" + e.Exception.ToString());
            WriteCrashDump(e.Exception);
            MessageBox.Show("程序后台任务发生异常，详细信息已写入日志文件", "程序异常", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static void CurrentDomain_UnhandleException(object sender, UnhandledExceptionEventArgs e)
        {
            ErrorLog MsgErroeLog = new ErrorLog();
            Exception ex = e.ExceptionObject as Exception;
            MsgErroeLog.WriteLog("未处理异常(进程将终止):" + (ex != null ? ex.ToString() : e.ExceptionObject.ToString()));
            WriteCrashDump(ex);
            MessageBox.Show("程序发生未处理的异常，详细信息已写入日志文件与崩溃转储", "程序异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Exception error = e.Exception as Exception;
            ErrorLog MsgErroeLog = new ErrorLog();
            MsgErroeLog.WriteLog("未处理UI异常" + error);
            WriteCrashDump(error);
            MessageBox.Show("界面操作发生异常，详细信息已写入日志文件与崩溃转储", "程序异常", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
