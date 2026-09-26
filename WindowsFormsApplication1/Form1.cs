using Cognex.VisionPro;
using Cognex.VisionPro.ImageFile;
using Cognex.VisionPro.QuickBuild;
using Cognex.VisionPro.ToolBlock;
using Cognex.VisionPro.ToolGroup;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Cognex.VisionPro.PMAlign;
using demo;
using System.Security.Cryptography;
using QRCodeUtil;
using MvCamCtrl.NET;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using Cognex.VisionPro.Blob;
using Cognex.VisionPro.CalibFix;
using Cognex.VisionPro.Display;
using System.Globalization;

namespace WindowsFormsApplication1
{
    public delegate void initialize_form();
    public delegate void dispaly_record(ICogRecord record_dispaly);
    public partial class Form1 : Form
    {
        Form9[] f9 = new Form9[8];
        public int cuntu;
        public string shuchuqufan = "Accept";
        public int tongji;
        public decimal zhangshu;
        private double feng;
        private double caituhaoshi = 0;
        public bool zhuanpanEn = false;
        private int datajilu = 0;
        private int gongjujilu = 0;
        private bool displayRawImage = false; // ch:true=只贴原图，跳过 CreateLastRunRecord
        private CogRecordDisplay[] _camRecordDisplay = new CogRecordDisplay[10];
        private CogRecordDisplay _liveRecordRenderer; // ch:现场画面隐藏栅格器，结果贴 PictureBox
        private readonly object _ocxPaintLock = new object();
        private readonly ICogRecord[] _pendingOcxRec = new ICogRecord[10];
        private readonly ICogImage[] _pendingRawImg = new ICogImage[10];
        private readonly Myjob[] _pendingOcxJob = new Myjob[10];
        private int _ocxPaintBusy;
        private int _ocxPaintRr;
        private int _ocxPaintingIdx;
        private int _lastOcxPaintMs;
        // ch:R15 上屏总节拍封顶(默认66ms≈15fps)；ch:R18 改运行时可调：输出方式右侧 numDisplayHz(1..100Hz) 换算 1000/Hz 回写本字段
        //   (15→66 与原常量逐毫秒一致)并落 ini camera/display_hz，现场调频率不再改码；volatile：回调/检测线程入队路径读取防御
        private volatile int OcxMinIntervalMs = 66;
        // ch:R15 最近一次真正开拍的时刻：KickOcxPaint 的直接投递/续排/定时器三条入口按它统一限速
        private int _lastOcxKickMs;
        private int _lastUiInputTick;
        private System.Windows.Forms.Timer _ocxYieldTimer;
        private IMessageFilter _uiInputFilter;
        private System.Windows.Forms.Timer _ioPulseTimer;
        private readonly object _ioPulseLock = new object();
        // ch:P1-③ 相机 IO 输出工作线程：把 MV_CC_SetEnumValue/SetBoolValue_NET 这类同步 P/Invoke
        //   从海康取流回调线程和 UI 定时器线程移出。回调/定时器只入队，由该线程串行执行，
        //   避免占用 SDK 取流线程（影响帧节拍）与 UI 线程（界面卡顿）。
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _ioWorkQueue = new System.Collections.Concurrent.BlockingCollection<Action>();
        private Thread _ioWorkerThread;
        // ch:P0-① 检测与取流解耦：海康 SDK 取流回调线程只做像素拷贝 + 建 Bitmap + 通知，VisionPro 检测(getrecord)
        //   在每相机独立工作线程执行。原实现在回调线程内同步 block.Run()（可能几十~几百 ms），期间该相机取流线程
        //   被完全占住 → 高帧率下丢帧。改为工作线程后回调占用缩短为拷贝+入队；是否丢帧仍由 bmp[]!=null 背压维持，
        //   每相机串行（同一线程）保证 VisionPro 工具块单线程语义不变。
        private readonly AutoResetEvent[] _detectSignal = new AutoResetEvent[8];
        private readonly Thread[] _detectThreads = new Thread[8];
        private readonly Myjob[] _pendingDetectJob = new Myjob[8];
        // ch:P0-① 每相机"检测中"门闩：getrecord 尾部会提前释放 Bitmap 槽位，但检测结果的渲染/存图仍引用该帧像素，
        //   必须等到 getrecord 整段返回才允许接收下一帧，否则新帧会覆写同一原生缓冲 → 花屏。0=空闲，1=检测中。
        private readonly int[] _detectBusy = new int[8];
        string wenjianjia = "";
        public UInt32 m_nBufSizeForSaveImage = 0;
        public UInt32 m_nBufSizeForSaveImage2 = 0;
        public UInt32 m_nBufSizeForSaveImage3 = 0;
        public UInt32 m_nBufSizeForSaveImage4 = 0;
        public UInt32 m_nBufSizeForSaveImage5 = 0;
        public UInt32 m_nBufSizeForSaveImage6 = 0;
        public UInt32 m_nBufSizeForSaveImage7 = 0;
        public UInt32 m_nBufSizeForSaveImage8 = 0;
        // ch:R7 已删除 8 个从未使用的 m_pBufForSaveImage / 2..8 死字段（实际使用的是 m_pSaveImageBuf[8] 数组）
        MyCamera.cbOutputExdelegate cbImage;
        // ch:P2 保持历史委托强引用：MV_CC_RegisterImageCallBackEx_NET 会把委托编组为函数指针交给 SDK，
        //   若旧委托实例被 GC 回收而 SDK 仍持有其指针（相机未重新注册），回调会跳到已回收代码 → 进程 AV。
        private static readonly List<MyCamera.cbOutputExdelegate> _cbImageKeepAlive = new List<MyCamera.cbOutputExdelegate>();
        // ch:P2 统一入口：重建回调委托并把新旧实例都登记进 keep-alive 列表
        private void RebuildImageCallback()
        {
            cbImage = new MyCamera.cbOutputExdelegate(ImageCallBack);
            lock (_cbImageKeepAlive) { _cbImageKeepAlive.Add(cbImage); }
        }
        MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        MyCamera.MV_CC_DEVICE_INFO[] m_pDeviceInfo = new MyCamera.MV_CC_DEVICE_INFO[8];
        private MyCamera[] m_MyCamera = new MyCamera[8];
        public string[] camera_name = new string[8] { "1", "2", "3", "4", "5", "6", "7", "8" }; // ch:流程N绑定的相机名（参考正常版本，精确匹配 chUserDefinedName）
        // ch:P3-⑧ 相机句柄锁：由「单把全局锁」改为「每相机一把」。原实现在断线重连时对整段（最多 8 台逐一重开，
        //   单台重试可达 7 秒）独占全局锁，导致其它相机的开始/停止采集以及打开/关闭界面长时间阻塞（UI 假死）。
        //   锁序约定（务必遵守，防死锁）：
        //   ① 需要整机互斥（开/关相机）时按 0→7 顺序 LockAllCameras()，结束后逆序 UnlockAllCameras()；
        //   ② 单相机操作只取 _cameraLocks[i]（Monitor 可重入，同线程再次进入安全）；
        //   ③ 任何路径不得在持有较大下标锁时再去取较小下标锁（全局唯一升序约定）；
        //   ④ 相机锁与图像缓冲锁（m_BufForSaveImageLock → _bmpLocks）之间无任何嵌套，互不交叉。
        private readonly object[] _cameraLocks = new object[] { new object(), new object(), new object(), new object(), new object(), new object(), new object(), new object() };

        private void LockAllCameras()
        {
            for (int i = 0; i < 8; i++)
                Monitor.Enter(_cameraLocks[i]);
        }

        private void UnlockAllCameras()
        {
            for (int i = 7; i >= 0; i--)
                Monitor.Exit(_cameraLocks[i]);
        }

        // ch:P0-1 带超时的全锁获取（升序，符合锁序约定③）：任一锁超时则回滚已取的锁并返回 false，供非阻塞路径跳过本轮
        private bool TryLockAllCameras(int timeoutMs)
        {
            int k = 0;
            for (; k < 8; k++)
            {
                if (!Monitor.TryEnter(_cameraLocks[k], timeoutMs))
                {
                    for (int j = k - 1; j >= 0; j--)
                        Monitor.Exit(_cameraLocks[j]);
                    return false;
                }
            }
            return true;
        }
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
        bool m_bGrabbing1 = false;
        bool m_bGrabbing2 = false;
        bool m_bGrabbing3 = false;
        bool m_bGrabbing4 = false;
        bool m_bGrabbing5 = false;
        bool m_bGrabbing6 = false;
        bool m_bGrabbing7 = false;
        bool m_bGrabbing8 = false;
        int m_nCanOpenDeviceNum;        // ch:设备使用数量 | en:Used Device Number
        int m_nDevNum;        // ch:在线设备数量 | en:Online Device Number
        Thread m_hReceiveThread = null;
        MyCamera.MV_FRAME_OUT_INFO_EX[] m_stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX[8];
        public UInt32[] m_nSaveImageBufSize = new UInt32[8] { 0, 0, 0, 0, 0, 0, 0, 0 };
        public IntPtr[] m_pSaveImageBuf = new IntPtr[8] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero };
        private Object[] m_BufForSaveImageLock = new Object[8];
        MyCamera.MV_CC_DEVICE_INFO[] device1 = new MyCamera.MV_CC_DEVICE_INFO[8];
        int[] m_nFrames = new int[8];      // ch:帧数 | en:Frame Number
        // ch:用于从驱动获取图像的缓存 | en:Buffer for getting image from driver
        UInt32[] m_nBufSizeForDriver = new uint[8];
        IntPtr m_BufForDriver1;

        IntPtr m_BufForDriver2;

        IntPtr m_BufForDriver3;

        IntPtr m_BufForDriver4;

        IntPtr m_BufForDriver5;

        IntPtr m_BufForDriver6;

        IntPtr m_BufForDriver7;
        IntPtr m_BufForDriver8;
        private static Object BufForDriverLock1 = new Object();
        private static Object BufForDriverLock2 = new Object();
        private static Object BufForDriverLock3 = new Object();
        private static Object BufForDriverLock4 = new Object();
        private static Object BufForDriverLock5 = new Object();
        private static Object BufForDriverLock6 = new Object();
        private static Object BufForDriverLock7 = new Object();
        private static Object BufForDriverLock8 = new Object();
        bool[] m_bSaveImg = new bool[8];    // ch:保存图片标志位 | en:Save Image Flag Bit
        IntPtr[] m_hDisplayHandle = new IntPtr[8];
        ErrorLog MsgErroeLog = new ErrorLog();
        public double jiankongshijian;
        public string zhen = "";
        public bool Cun = false;
        public bool NG = false;
        // ch:海康相机线号从 Line0 开始且 IO 线号无需偏移；dahua=false 后 output_cameraN 直接使用
        // ch:配置线号（OK→Line1、NG→Line2，均为输出线），并激活 set_Mode 的海康 LineMode 设置逻辑
        private bool dahua=false;
        // ch:启动/切换方案期间打开失败的相机汇总（用于一次性提示，避免启动时逐台弹窗阻塞）
        private string openFailSummary = "";
        // ch:UI 大华相机开关（Designer 控件 cbDahua，可持久化到 code.ini [canshu] dahua）
        private void cbDahua_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                dahua = cbDahua.Checked;
                canshuIni.WriteString("canshu", "dahua", dahua ? "true" : "false");
                MsgErroeLog.WriteLog("大华相机开关:" + dahua);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("大华开关保存异常:" + ex.Message); }
        }
        public Form1()
        {

            InitializeComponent();
            InitLiveRecordRenderer();
            ApplyCameraDisplayMode();
            // ch:已移除 IrisSkin4 皮肤引擎（其全局窗口钩子对 VisionPro 编辑控件 GetWindowText 崩溃，正常删工具操作即触发）
            // ch:基础视觉优化见下方 InitDefaultAppearance()
            InitDefaultAppearance();
            ForceGongjuJiluOff();
            InstallUiInputFilter();
            System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = false;
            canshuIni.ReadINIFile(AppDomain.CurrentDomain.BaseDirectory + "//code.ini");
            // ch:大华相机开关（IO 线号偏移）从 ini 读取并持久化，默认海康(false，不偏移)
            dahua = canshuIni.ReadString("canshu", "dahua", "false").Replace("\0", "") == "true";
            cbDahua.Checked = dahua; // ch:同步 UI 开关状态（Designer 控件，可在设计器里编辑）
            try { DeviceListAcq(); } catch (Exception ex) { MsgErroeLog.WriteLog("设备枚举异常:" + ex.Message); }
            RebuildImageCallback(); // ch:P2 重建回调并登记强引用（防 GC 后 SDK 仍持旧指针 → AV）
            StartDetectWorkers(); // ch:P0-① 启动每相机检测工作线程（须在注册回调/开始采集之前）
            for (int i = 0; i < 8; ++i)
            {
                m_BufForSaveImageLock[i] = new Object();
            }
            for (int i = 0; i < 8; ++i)
            {
                m_nBufSizeForDriver[i] = 0;
            }
            for (int i = 0; i < 8; ++i)
            {
                m_nFrames[i] = 0;
            }
            Thread jindu = new Thread(new ThreadStart(InitializeJobManager));
            jindu.IsBackground = true;
            frm3 = new Form3(this);//把Form1当参数传过去,在Form2中就可以使用Form1的变量和控件了
            modbustcp = new FormModbus();
            modbusrtu = new FormModbusRtu();
            frm7 = new Form7();//把Form1当参数传过去,在Form2中就可以使用Form1的变量和控件了
            omron = new FormOmron();
            fx = new FormMelsecSerial();
            f1 = new SubSet();

            frm5 = new Form5();
            jindu.Start();

        }
        protected override Point ScrollToControl(Control activeControl)
        {
            return this.AutoScrollPosition;
        }

        // ch:移除皮肤引擎后的基础视觉优化：统一字体、窗体背景与菜单渲染
        private void InitDefaultAppearance()
        {
            try
            {
                this.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
                this.BackColor = System.Drawing.Color.FromArgb(240, 244, 248);
                if (groupBox13 != null)
                {
                    groupBox13.BackColor = this.BackColor;
                    groupBox13.BringToFront();
                }
                if (groupBox2 != null)
                    groupBox2.BackColor = this.BackColor;
                if (listBox2 != null && listBox2.BackColor == Color.Transparent)
                    listBox2.BackColor = this.BackColor;
                if (menuStrip1 != null)
                {
                    menuStrip1.Renderer = new ToolStripProfessionalRenderer(new AppMenuColorTable());
                    menuStrip1.BackColor = System.Drawing.Color.FromArgb(44, 62, 80);
                    menuStrip1.ForeColor = System.Drawing.Color.White;
                    foreach (ToolStripMenuItem top in menuStrip1.Items.OfType<ToolStripMenuItem>())
                    {
                        StyleMenuItem(top, true);
                    }
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        private void checkBoxDisplayRaw_CheckedChanged(object sender, EventArgs e)
        {
            displayRawImage = checkBoxDisplayRaw.Checked;
            try { canshuIni.WriteString("camera", "display_raw", displayRawImage ? "true" : "false"); }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            ApplyCameraDisplayMode();
        }

        private void ApplyDisplayRawFromIni()
        {
            displayRawImage = canshuIni.ReadString("camera", "display_raw", "false").Replace("\0", "") == "true";
            if (checkBoxDisplayRaw.Checked != displayRawImage)
                checkBoxDisplayRaw.Checked = displayRawImage;
            ApplyCameraDisplayMode();
        }

        // ================= 输出方式互斥（ch:R13 合理化） =================
        // ch:现场每次只用一种输出，但原实现是「每相机 4 个 CheckBox + 3 个配置窗使能」共 7 路平行 if，
        //    彼此互不约束：勾错两路就会同时下发（数据重复到 PLC，是现场事故而非性能问题）。
        //    配置窗(tabPage5)上一个原生 ComboBox 统一选择，getrecord 里只放行选中的那一路。
        //    _outputMode = -1 表示「自动(按原配置)」：完全不加锁，行为与改造前一致——这是升级后的默认值，保证零回归。
        private const int OutAuto = -1;            // 自动：按各开关原配置，不加互斥
        private const int OutOff = 0;              // 不输出
        private const int OutIO = 1;               // IO 脉冲
        private const int OutSerial = 2;           // 串口（frm3）
        private const int OutTcp = 3;              // TCP（frm3）
        private const int OutModbusTcp = 4;        // ModbusTCP（frm3）
        private const int OutOmron = 5;            // 欧姆龙 Fins（FormOmron 配置窗）
        private const int OutModbusTcpWin = 6;     // ModbusTCP（FormModbus 配置窗）
        private const int OutModbusRtu = 7;        // ModbusRTU（FormModbusRtu 配置窗）
        private volatile int _outputMode = OutAuto;
        private bool _outputModeSyncing;           // 防止程序赋值 SelectedIndex 与 SelectedIndexChanged 互相触发
        // ch:R19 各 Modbus 数据路「首次触发」日志标志：切输出方式时复位(ApplyOutputMode 尾部)，触发一次记一条，定位 mode4 实际走哪条路
        private volatile bool _logPathSerial, _logPathFrm3, _logPathMtcpWin;

        // ch:把选中的输出方式落到 8 个 job 的对应标志位，并同步旧的 4 组 CheckBox（单一数据源，避免两处各写一份）。
        //   mode<0(自动)时一律不动任何标志位，保持各开关原状。
        private void ApplyOutputMode(int mode, bool persist)
        {
            if (mode < OutAuto || mode > OutModbusRtu)
                mode = OutAuto;
            _outputMode = mode;
            try
            {
                _outputModeSyncing = true;
                if (cbOutputMode != null && cbOutputMode.SelectedIndex != mode + 1)
                    cbOutputMode.SelectedIndex = mode + 1; // -1 → 0(自动)
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("输出方式下拉同步失败:" + ex.Message); }
            finally { _outputModeSyncing = false; }

            if (mode >= 0)
            {
                bool io = (mode == OutIO);
                bool ser = (mode == OutSerial);
                bool tcp = (mode == OutTcp);
                bool mtcp = (mode == OutModbusTcp);
                Myjob[] jobs = { myjob1, myjob2, myjob3, myjob4, myjob5, myjob6, myjob7, myjob8 };
                // 旧界面开关的相机顺序映射（与既有 CheckedChanged 处理器一一对应）
                CheckBox[] cbSer = { checkBox27, checkBox28, checkBox29, checkBox30, checkBox31, checkBox37, checkBox43, checkBox49 };
                CheckBox[] cbTcp = { checkBox5, checkBox9, checkBox15, checkBox19, checkBox33, checkBox39, checkBox45, checkBox51 };
                CheckBox[] cbMtcp = { checkBox24, checkBox23, checkBox21, checkBox17, checkBox32, checkBox38, checkBox44, checkBox50 };
                try
                {
                    for (int i = 0; i < 8; i++)
                    {
                        jobs[i].IO = io;
                        jobs[i].serial = ser;
                        jobs[i].tcp = tcp;
                        jobs[i].modbustcp = mtcp;
                        // 状态相同则不赋值，避免无谓触发既有 CheckedChanged
                        if (cbSer[i].Checked != ser) cbSer[i].Checked = ser;
                        if (cbTcp[i].Checked != tcp) cbTcp[i].Checked = tcp;
                        if (cbMtcp[i].Checked != mtcp) cbMtcp[i].Checked = mtcp;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("输出方式同步旧开关失败:" + ex.Message); }
            }
            if (persist)
            {
                // 立即落盘（与 checkBoxDisplayRaw 同款做法），崩溃也不丢选择
                try { canshuIni.WriteString("camera", "output_mode", mode.ToString()); }
                catch (Exception ex) { MsgErroeLog.WriteLog("输出方式保存失败:" + ex.Message); }
            }
            _logPathSerial = _logPathFrm3 = _logPathMtcpWin = true; // ch:R19 切模式后各 Modbus 数据路首次触发各记一条，现场日志直接看出走了哪条路
            try // ch:R19 记录三路决策状态与数据终端存在性：mode4 再遇无输出时，一行日志即可定位卡在哪一闸
            {
                string term;
                try { lock (myjob1.blockLock) { term = " Outputs(serial=" + myjob1.block.Outputs.Contains("serial") + ",modbustcp=" + myjob1.block.Outputs.Contains("modbustcp") + ")"; } }
                catch { term = " Outputs(未就绪)"; }
                MsgErroeLog.WriteLog("输出方式=" + mode + " 状态: cfgWin(fins_en=" + modbustcp.fins_en + ",chushihua=" + modbustcp.chushihua
                    + ") frm3(IsEnable=" + frm3.IsEnable + ",style=" + frm3.modbus_style + ",fn=" + frm3.gongnengma + ",xie=" + frm3.xie
                    + ") job1(serial=" + myjob1.serial + ",modbustcp=" + myjob1.modbustcp + ")" + term);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("输出方式状态记录失败:" + ex.Message); }
        }

        private void cbOutputMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_outputModeSyncing) return; // 程序赋值 SelectedIndex 引发的回调，ApplyOutputMode 已把状态设好
            try { ApplyOutputMode(cbOutputMode.SelectedIndex - 1, true); }
            catch (Exception ex) { MsgErroeLog.WriteLog("输出方式选择失败:" + ex.Message); }
        }

        // ch:R18 显示频率(上屏节拍)：numDisplayHz(输出方式右侧) 1..100Hz → 1000/Hz ms 回写 OcxMinIntervalMs，
        //   立即生效于 ScheduleOcxPaint 定时器与 KickOcxPaint 收口两处；只动显示刷新率，采集/检测/输出/封程限速(feng) 一律不碰
        private bool _displayHzSyncing;
        private void numDisplayHz_ValueChanged(object sender, EventArgs e)
        {
            if (_displayHzSyncing) return; // 启动回填 ini 引发的回调：值已在回填处设好，避免重复落盘
            try
            {
                int hz = (int)numDisplayHz.Value;
                if (hz < 1) hz = 1;
                OcxMinIntervalMs = 1000 / hz;
                try { canshuIni.WriteString("camera", "display_hz", hz.ToString()); }
                catch (Exception ex) { MsgErroeLog.WriteLog("显示频率保存失败:" + ex.Message); }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("显示频率设置失败:" + ex.Message); }
        }

        // ch:R25 性能统计打印开关：chkPerfPrint(输出方式行右侧) —— 开=异常才打(丢帧率≥1%/占用≥50%/检测≥35ms)+10分钟心跳，
        //   关=完全不打；ini camera/perf_print 默认1。只控制日志落不落盘，埋点计数与窗口复位照常，采集/检测/输出一律不碰
        private bool _perfPrintSyncing;
        private volatile bool _perfPrintOn = true;
        private long _perfLastPrintMs = 0; // ch:R25 上次落日志时刻(_perfSw 毫秒)，心跳判定用；仅在汇总单线程(_perfReporting 闩内)读写
        private void chkPerfPrint_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                _perfPrintOn = chkPerfPrint.Checked;
                if (_perfPrintSyncing) return; // ch:启动回填 ini 引发的回调：值已在回填处设好，避免重复落盘(同 _displayHzSyncing 手法)
                try { canshuIni.WriteString("camera", "perf_print", chkPerfPrint.Checked ? "1" : "0"); }
                catch (Exception ex) { MsgErroeLog.WriteLog("性能打印开关保存失败:" + ex.Message); }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("性能打印开关设置失败:" + ex.Message); }
        }

        // ch:递归设置菜单项颜色：一级白字（深色顶栏），下拉子项黑字（浅色面板）
        private void StyleMenuItem(ToolStripMenuItem item, bool white)
        {
            item.ForeColor = white ? System.Drawing.Color.White : System.Drawing.Color.Black;
            foreach (ToolStripMenuItem sub in item.DropDownItems.OfType<ToolStripMenuItem>())
            {
                StyleMenuItem(sub, false);
            }
        }

        // ch:菜单配色表（深色顶栏主题）
        private class AppMenuColorTable : ProfessionalColorTable
        {
            public override System.Drawing.Color MenuItemSelected { get { return System.Drawing.Color.FromArgb(52, 73, 94); } }
            public override System.Drawing.Color MenuItemBorder { get { return System.Drawing.Color.FromArgb(52, 73, 94); } }
            public override System.Drawing.Color MenuItemSelectedGradientBegin { get { return System.Drawing.Color.FromArgb(52, 73, 94); } }
            public override System.Drawing.Color MenuItemSelectedGradientEnd { get { return System.Drawing.Color.FromArgb(52, 73, 94); } }
            public override System.Drawing.Color MenuBorder { get { return System.Drawing.Color.FromArgb(44, 62, 80); } }
            public override System.Drawing.Color MenuItemPressedGradientBegin { get { return System.Drawing.Color.FromArgb(44, 62, 80); } }
            public override System.Drawing.Color MenuItemPressedGradientEnd { get { return System.Drawing.Color.FromArgb(44, 62, 80); } }
            public override System.Drawing.Color MenuItemPressedGradientMiddle { get { return System.Drawing.Color.FromArgb(44, 62, 80); } }
            public override System.Drawing.Color ToolStripDropDownBackground { get { return System.Drawing.Color.White; } }
            public override System.Drawing.Color ImageMarginGradientBegin { get { return System.Drawing.Color.White; } }
            public override System.Drawing.Color ImageMarginGradientMiddle { get { return System.Drawing.Color.White; } }
            public override System.Drawing.Color ImageMarginGradientEnd { get { return System.Drawing.Color.White; } }
            public override System.Drawing.Color SeparatorDark { get { return System.Drawing.Color.FromArgb(200, 210, 220); } }
            public override System.Drawing.Color SeparatorLight { get { return System.Drawing.Color.White; } }
        }

        Myjob myjob1 = new Myjob();
        Myjob myjob2 = new Myjob();
        Myjob myjob3 = new Myjob();
        Myjob myjob4 = new Myjob();
        Myjob myjob5 = new Myjob();
        Myjob myjob6 = new Myjob();
        Myjob myjob7 = new Myjob();
        Myjob myjob8 = new Myjob();
        public int qqqq;
        int item_sum;
        CogJobManager manager1;
        CogToolGroup group_1;
        CogToolBlock block_1;
        CogToolGroup group_2;
        CogToolBlock block_2;
        CogToolGroup group_3;
        CogToolBlock block_3;
        CogToolGroup group_4;
        CogToolBlock block_4;
        CogToolGroup group_5;
        CogToolBlock block_5;
        CogToolGroup group_6;
        CogToolBlock block_6;
        CogToolGroup group_7;
        CogToolBlock block_7;
        CogToolGroup group_8;
        CogToolBlock block_8;
        public CogToolBlock block_11;
        public CogToolBlock block_12;
        public CogToolBlock block_13;
        public CogToolBlock block_14;
        public CogToolBlock block_21;
        public CogToolBlock block_22;
        public CogToolBlock block_23;
        public CogToolBlock block_24;
        public CogToolBlock block_15;
        public CogToolBlock block_16;
        public CogToolBlock block_17;
        public CogToolBlock block_18;
        public CogToolBlock block_25;
        public CogToolBlock block_26;
        public CogToolBlock block_27;
        public CogToolBlock block_28;
        CogJobIndependent myIndependentJob;
        CogJobIndependent myIndependentJob2;
        CogJobIndependent myIndependentJob3;
        CogJobIndependent myIndependentJob4;
        CogJobIndependent myIndependentJob5;
        CogJobIndependent myIndependentJob6;
        CogJobIndependent myIndependentJob7;
        CogJobIndependent myIndependentJob8;
        string path_1;
        bool yunxing;
        private string daoqi = "";
        int fff;
        int start1;
        private RunLog runLog = new RunLog();
        Frm2 Frm2 = new Frm2();
        TimeSpan ti;
        int time;
        DirectoryInfo info1ok;
        DirectoryInfo info1ng;
        DirectoryInfo info2ok;
        DirectoryInfo info2ng;
        DirectoryInfo info3ok;
        DirectoryInfo info3ng;
        DirectoryInfo info4ok;
        DirectoryInfo info4ng;
        DirectoryInfo info3ok1;
        DirectoryInfo info4ok1;
        DirectoryInfo info1ok1;
        DirectoryInfo info2ok1;
        DirectoryInfo info5ok;
        DirectoryInfo info5ng;
        DirectoryInfo info6ok;
        DirectoryInfo info6ng;
        DirectoryInfo info7ok;
        DirectoryInfo info7ng;
        DirectoryInfo info8ok;
        DirectoryInfo info8ng;
        DirectoryInfo info5ok1;
        DirectoryInfo info6ok1;
        DirectoryInfo info7ok1;
        DirectoryInfo info8ok1;
        int timecount;
        Dictionary<string, int> d1 = new Dictionary<string, int>();
        Dictionary<string, int> d2 = new Dictionary<string, int>();
        Dictionary<string, int> d3 = new Dictionary<string, int>();
        Dictionary<string, int> d4 = new Dictionary<string, int>();
        Dictionary<string, int> d5 = new Dictionary<string, int>();
        Dictionary<string, int> d6 = new Dictionary<string, int>();
        Dictionary<string, int> d7 = new Dictionary<string, int>();
        Dictionary<string, int> d8 = new Dictionary<string, int>();

        // ch:R13 缺陷统计表增量更新（d1..d8 的 8 段重复逻辑收敛到这里）。
        //   原实现每帧：dataGridViewN.Visible=false → Rows.Clear() → 按字典全量重加 → Visible=true，
        //   每帧两次强制重绘(闪烁来源) + O(键数) 行重建；且 Clear/重加发生在 UI 线程 BeginInvoke 里，
        //   高频 NG 时反复触发 DataGridView 整表刷新。
        //   改为「表里已有该行就原地改计数、没有就末尾追加」，行序保持首次出现顺序，
        //   只写 DataTable —— 由 DataSource 绑定的 ListChanged 增量刷新，不再手动开关 Visible。
        //   首行空行：初始化时 Rows.Add(NewRow()) 发生在 Columns.Add 之前，表首恒有一行 DBNull，
        //   原实现靠 Rows.Clear() 顺带清掉；增量更新必须显式剔除，否则表格永远多一行空白。
        //   注：dataGridView1/2 另有切到 myTable1 的 DataSource 分支，这里与原实现一致只写 myTable，不动绑定。
        private void UpdateDefectTable(Dictionary<string, int> d, DataTable tbl, string key)
        {
            try
            {
                if (tbl.Columns.Count > 0 && tbl.Rows.Count > 0)
                {
                    object head = tbl.Rows[0][0];
                    if (head == null || Convert.IsDBNull(head))
                        tbl.Rows.RemoveAt(0); // ch:剔除初始化残留的空行（只可能是那一行，正常数据行 key 不会是 DBNull）
                }
                if (d.ContainsKey(key))
                {
                    d[key] = d[key] + 1;
                    foreach (DataRow r in tbl.Rows)
                    {
                        if (string.Equals(r[0].ToString(), key, StringComparison.Ordinal))
                        {
                            r[1] = d[key].ToString(); // ch:原地改计数，触发行级变更而非整表重建
                            return;
                        }
                    }
                    tbl.Rows.Add(key, d[key].ToString()); // ch:字典有、表里没有(被外部清过)：补回一行
                }
                else
                {
                    d.Add(key, 1);
                    tbl.Rows.Add(key, "1");
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }
        List<FileInfo> ls1 = new List<FileInfo>();
        List<FileInfo> ls2 = new List<FileInfo>();
        string day1;
        string day2;
        int camera_sum;
        private ClassIni canshuIni = new ClassIni();
        private ClassIni duini = new ClassIni();
        #region 初始化
        private void InitializeJobManager()
        {
            try
            {
                try
                {
                    feng = 0;
                    cuntu = 0;
                    tongji = 0;
                    myjob1.ok1 = 0;
                    myjob2.ok1 = 0;
                    myjob3.ok1 = 0;
                    myjob4.ok1 = 0;
                    myjob5.ok1 = 0;
                    myjob6.ok1 = 0;
                    myjob7.ok1 = 0;
                    myjob8.ok1 = 0;
                    myjob1.roi = false;
                    myjob2.roi = false;
                    myjob3.roi = false;
                    myjob4.roi = false;
                    myjob5.roi = false;
                    myjob6.roi = false;
                    myjob7.roi = false;
                    myjob8.roi = false;
                    myjob1.biaotou = "";
                    myjob2.biaotou = "";
                    myjob3.biaotou = "";
                    myjob4.biaotou = "";
                    myjob5.biaotou = "";
                    myjob6.biaotou = "";
                    myjob7.biaotou = "";
                    myjob8.biaotou = "";
                    myjob1.ng1 = 0;
                    myjob2.ng1 = 0;
                    myjob3.ng1 = 0;
                    myjob4.ng1 = 0;
                    myjob5.ng1 = 0;
                    myjob6.ng1 = 0;
                    myjob7.ng1 = 0;
                    myjob8.ng1 = 0;
                    myjob1.IOyanshi = 0;
                    myjob2.IOyanshi = 0;
                    myjob3.IOyanshi = 0;
                    myjob4.IOyanshi = 0;
                    myjob5.IOyanshi = 0;
                    myjob6.IOyanshi = 0;
                    myjob7.IOyanshi = 0;
                    myjob8.IOyanshi = 0;
                    myjob1.en = 0;
                    myjob2.en = 0;
                    myjob3.en = 0;
                    myjob4.en = 0;
                    myjob5.en = 0;
                    myjob6.en = 0;
                    myjob7.en = 0;
                    myjob8.en = 0;
                    myjob1.address = "";
                    myjob2.address = "";
                    myjob3.address = "";
                    myjob4.address = "";
                    myjob5.address = "";
                    myjob6.address = "";
                    myjob7.address = "";
                    myjob8.address = "";
                    myjob1.dlg = new FolderBrowserDialog();
                    myjob2.dlg = new FolderBrowserDialog();
                    myjob1.cuntu = true;
                    myjob2.cuntu = true;
                    myjob3.cuntu = true;
                    myjob4.cuntu = true;
                    myjob1.xuanran = true;
                    myjob2.xuanran = true;
                    myjob3.xuanran = true;
                    myjob4.xuanran = true;
                    myjob1.IO = true;
                    myjob2.IO = true;
                    myjob3.IO = true;
                    myjob4.IO = true;
                    myjob5.cuntu = true;
                    myjob6.cuntu = true;
                    myjob7.cuntu = true;
                    myjob8.cuntu = true;
                    myjob5.xuanran = true;
                    myjob6.xuanran = true;
                    myjob7.xuanran = true;
                    myjob8.xuanran = true;
                    myjob5.IO = true;
                    myjob6.IO = true;
                    myjob7.IO = true;
                    myjob8.IO = true;
                    myjob1.myTable = new DataTable();
                    myjob2.myTable = new DataTable();
                    myjob3.myTable = new DataTable();
                    myjob4.myTable = new DataTable();
                    myjob5.myTable = new DataTable();
                    myjob6.myTable = new DataTable();
                    myjob7.myTable = new DataTable();
                    myjob8.myTable = new DataTable();
                    myjob1.myTable1 = new DataTable();
                    myjob2.myTable1 = new DataTable();
                    myjob1.danwu_cishu = 0;
                    myjob2.danwu_cishu = 0;
                    myjob3.danwu_cishu = 0;
                    myjob4.danwu_cishu = 0;
                    myjob5.danwu_cishu = 0;
                    myjob6.danwu_cishu = 0;
                    myjob7.danwu_cishu = 0;
                    myjob8.danwu_cishu = 0;
                    myjob1.time = 0;
                    myjob2.time = 0;
                    myjob3.time = 0;
                    myjob4.time = 0;
                    myjob5.time = 0;
                    myjob6.time = 0;
                    myjob7.time = 0;
                    myjob8.time = 0;
                    myjob1.changdu = 0;
                    myjob2.changdu = 10;
                    myjob3.changdu = 20;
                    myjob4.changdu = 30;
                    myjob5.changdu = 40;
                    myjob6.changdu = 50;
                    myjob7.changdu = 60;
                    myjob8.changdu = 70;
                    myjob1.state = "";
                    myjob2.state = "";
                    myjob3.state = "";
                    myjob4.state = "";
                    myjob5.state = "";
                    myjob6.state = "";
                    myjob7.state = "";
                    myjob8.state = "";
                    myjob1.triggerMode = "";
                    myjob2.triggerMode = "";
                    myjob3.triggerMode = "";
                    myjob4.triggerMode = "";
                    myjob5.triggerMode = "";
                    myjob6.triggerMode = "";
                    myjob7.triggerMode = "";
                    myjob8.triggerMode = "";
                    myjob1.danwu_time = "";
                    myjob2.danwu_time = "";
                    myjob3.danwu_time = "";
                    myjob4.danwu_time = "";
                    myjob1.triggerZifu = "";
                    myjob2.triggerZifu = "";
                    myjob3.triggerZifu = "";
                    myjob4.triggerZifu = "";
                    myjob5.triggerZifu = "";
                    myjob6.triggerZifu = "";
                    myjob7.triggerZifu = "";
                    myjob8.triggerZifu = "";
                    myjob1.jieshouZifu = "null";
                    myjob2.jieshouZifu = "null";
                    myjob3.jieshouZifu = "null";
                    myjob4.jieshouZifu = "null";
                    myjob5.jieshouZifu = "null";
                    myjob6.jieshouZifu = "null";
                    myjob7.jieshouZifu = "null";
                    myjob8.jieshouZifu = "null";
                    // ch:R2 已删除未接线的 myhandle/myhandle1 订阅及其 IO*OK/NG handler（.vpp 确认无引用）；
                    //   OK/NG 输出统一走 getrecord → RequestIoPulse()
                    myjob1.calib = null;
                    myjob2.calib = null;
                    myjob3.calib = null;
                    myjob4.calib = null;
                    myjob5.calib = null;
                    myjob6.calib = null;
                    myjob7.calib = null;
                    myjob8.calib = null;
                    myjob1.Color = false;
                    myjob2.Color = false;
                    myjob3.Color = false;
                    myjob4.Color = false;
                    myjob5.Color = false;
                    myjob6.Color = false;
                    myjob7.Color = false;
                    myjob8.Color = false;
                    myjob1.tishi = false;
                    myjob2.tishi = false;
                    myjob3.tishi = false;
                    myjob4.tishi = false;
                    myjob5.tishi = false;
                    myjob6.tishi = false;
                    myjob7.tishi = false;
                    myjob8.tishi = false;
                    myjob1.modbustcp = false;
                    myjob2.modbustcp = false;
                    myjob3.modbustcp = false;
                    myjob4.modbustcp = false;
                    myjob5.modbustcp = false;
                    myjob6.modbustcp = false;
                    myjob7.modbustcp = false;
                    myjob8.modbustcp = false;
                    myjob1.modbustemp = false;
                    myjob2.modbustemp = false;
                    myjob3.modbustemp = false;
                    myjob4.modbustemp = false;
                    myjob5.modbustemp = false;
                    myjob6.modbustemp = false;
                    myjob7.modbustemp = false;
                    myjob8.modbustemp = false;
                    myjob1.jiasu = false;
                    myjob2.jiasu = false;
                    myjob3.jiasu = false;
                    myjob4.jiasu = false;
                    myjob5.jiasu = false;
                    myjob6.jiasu = false;
                    myjob7.jiasu = false;
                    myjob8.jiasu = false;
                    camera_sum = 0;
                    myjob1.runtime = 0;
                    myjob2.runtime = 0;
                    myjob3.runtime = 0;
                    myjob4.runtime = 0;
                    myjob5.runtime = 0;
                    myjob6.runtime = 0;
                    myjob7.runtime = 0;
                    myjob8.runtime = 0;
                    myjob1.runcishu = 0;
                    myjob2.runcishu = 0;
                    myjob3.runcishu = 0;
                    myjob4.runcishu = 0;
                    myjob5.runcishu = 0;
                    myjob6.runcishu = 0;
                    myjob7.runcishu = 0;
                    myjob8.runcishu = 0;
                    myjob1.index = -1;
                    myjob2.index = -1;
                    myjob3.index = -1;
                    myjob4.index = -1;
                    myjob5.index = -1;
                    myjob6.index = -1;
                    myjob7.index = -1;
                    myjob8.index = -1;
                    // ch:P2 已删除死字段 myjobN.xianshi（旧降频方案残留，全工程无消费者）
                    myjob1.shijianEn = false;
                    myjob2.shijianEn = false;
                    myjob3.shijianEn = false;
                    myjob4.shijianEn = false;
                    myjob5.shijianEn = false;
                    myjob6.shijianEn = false;
                    myjob7.shijianEn = false;
                    myjob8.shijianEn = false;
                    myjob1.tcp = false;
                    myjob2.tcp = false;
                    myjob3.tcp = false;
                    myjob4.tcp = false;
                    myjob5.tcp = false;
                    myjob6.tcp = false;
                    myjob7.tcp = false;
                    myjob8.tcp = false;
                    myjob1.serial = false;
                    myjob2.serial = false;
                    myjob3.serial = false;
                    myjob4.serial = false;
                    myjob5.serial = false;
                    myjob6.serial = false;
                    myjob7.serial = false;
                    myjob8.serial = false;
                    myjob1.temptu = 0;
                    myjob2.temptu = 0;
                    myjob3.temptu = 0;
                    myjob4.temptu = 0;
                    myjob5.temptu = 0;
                    myjob6.temptu = 0;
                    myjob7.temptu = 0;
                    myjob8.temptu = 0;
                    myjob1.newrecod = null;
                    myjob2.newrecod = null;
                    myjob3.newrecod = null;
                    myjob4.newrecod = null;
                    myjob5.newrecod = null;
                    myjob6.newrecod = null;
                    myjob7.newrecod = null;
                    myjob8.newrecod = null;
                    myjob1.timespace = 100;
                    myjob2.timespace = 100;
                    myjob3.timespace = 100;
                    myjob4.timespace = 100;
                    myjob5.timespace = 100;
                    myjob6.timespace = 100;
                    myjob7.timespace = 100;
                    myjob8.timespace = 100;
                    myjob1.master = 0;
                    myjob2.master = 6;
                    myjob3.master = 12;
                    myjob4.master = 18;
                    myjob5.master = 24;
                    myjob6.master = 30;
                    myjob7.master = 36;
                    myjob8.master = 42;
                    myjob1.trrigersum = 0;
                    myjob2.trrigersum = 0;
                    myjob3.trrigersum = 0;
                    myjob4.trrigersum = 0;
                    myjob5.trrigersum = 0;
                    myjob6.trrigersum = 0;
                    myjob7.trrigersum = 0;
                    myjob8.trrigersum = 0;
                    myjob1.trriger = 0;
                    myjob2.trriger = 0;
                    myjob3.trriger = 0;
                    myjob4.trriger = 0;
                    myjob5.trriger = 0;
                    myjob6.trriger = 0;
                    myjob7.trriger = 0;
                    myjob8.trriger = 0;
                    jiankongshijian = 1000;
                    myjob1.pathhead_ok = @"E:\fu1ok\";
                    myjob2.pathhead_ok = @"E:\fu2ok\";
                    myjob3.pathhead_ok = @"E:\fu3ok\";
                    myjob4.pathhead_ok = @"E:\fu4ok\";
                    myjob1.pathhead_ng = @"E:\fu1ng\";
                    myjob2.pathhead_ng = @"E:\fu2ng\";
                    myjob3.pathhead_ng = @"E:\fu3ng\";
                    myjob4.pathhead_ng = @"E:\fu4ng\";
                    myjob5.pathhead_ok = @"E:\fu5ok\";
                    myjob6.pathhead_ok = @"E:\fu6ok\";
                    myjob7.pathhead_ok = @"E:\fu7ok\";
                    myjob8.pathhead_ok = @"E:\fu8ok\";
                    myjob5.pathhead_ng = @"E:\fu5ng\";
                    myjob6.pathhead_ng = @"E:\fu6ng\";
                    myjob7.pathhead_ng = @"E:\fu7ng\";
                    myjob8.pathhead_ng = @"E:\fu8ng\";
                    day1 = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek);
                    myjob1.fileng = new FileInfo(myjob1.pathhead_ng + day1 + "\\");
                    myjob2.fileng = new FileInfo(myjob2.pathhead_ng + day1 + "\\");
                    myjob1.fileok = new FileInfo(myjob1.pathhead_ok + day1 + "\\");
                    myjob2.fileok = new FileInfo(myjob2.pathhead_ok + day1 + "\\");
                    myjob3.fileng = new FileInfo(myjob3.pathhead_ng + day1 + "\\");
                    myjob3.fileok = new FileInfo(myjob3.pathhead_ok + day1 + "\\");
                    myjob4.fileng = new FileInfo(myjob4.pathhead_ng + day1 + "\\");
                    myjob4.fileok = new FileInfo(myjob4.pathhead_ok + day1 + "\\");
                    myjob5.fileng = new FileInfo(myjob5.pathhead_ng + day1 + "\\");
                    myjob6.fileng = new FileInfo(myjob6.pathhead_ng + day1 + "\\");
                    myjob5.fileok = new FileInfo(myjob5.pathhead_ok + day1 + "\\");
                    myjob6.fileok = new FileInfo(myjob6.pathhead_ok + day1 + "\\");
                    myjob7.fileng = new FileInfo(myjob7.pathhead_ng + day1 + "\\");
                    myjob7.fileok = new FileInfo(myjob7.pathhead_ok + day1 + "\\");
                    myjob8.fileng = new FileInfo(myjob8.pathhead_ng + day1 + "\\");
                    myjob8.fileok = new FileInfo(myjob8.pathhead_ok + day1 + "\\");
                    timecount = 0;
                    info1ok = new DirectoryInfo(myjob1.pathhead_ok + day1 + "\\");
                    info1ng = new DirectoryInfo(myjob1.pathhead_ng + day1 + "\\");
                    info1ok1 = new DirectoryInfo(myjob1.pathhead_ok);
                    info2ok1 = new DirectoryInfo(myjob2.pathhead_ok);
                    info3ok1 = new DirectoryInfo(myjob3.pathhead_ok);
                    info4ok1 = new DirectoryInfo(myjob4.pathhead_ok);
                    info5ok1 = new DirectoryInfo(myjob5.pathhead_ok);
                    info6ok1 = new DirectoryInfo(myjob6.pathhead_ok);
                    info7ok1 = new DirectoryInfo(myjob7.pathhead_ok);
                    info8ok1 = new DirectoryInfo(myjob8.pathhead_ok);
                    string[] trr1;
                    try { trr1 = info1ng.CreationTime.ToString().Split('/', ' '); } catch { trr1 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    string[] trrnow = DateTime.Now.ToString().Split('/', ' ');
                    int bb1 = int.Parse(trr1[0] + trr1[1] + trr1[2]);
                    int bbnow = int.Parse(trrnow[0] + trrnow[1] + trrnow[2]);
                    try
                    {
                        if (bb1 != bbnow)
                        {
                            if (Directory.Exists(myjob1.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob1.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob1.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info2ok = new DirectoryInfo(myjob2.pathhead_ok + day1 + "\\");
                    info2ng = new DirectoryInfo(myjob2.pathhead_ng + day1 + "\\");
                    string[] trr2;
                    try { trr2 = info2ng.CreationTime.ToString().Split('/', ' '); } catch { trr2 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb2 = int.Parse(trr2[0] + trr2[1] + trr2[2]);
                    try
                    {
                        if (bb2 != bbnow)
                        {
                            if (Directory.Exists(myjob2.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob2.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob2.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info3ok = new DirectoryInfo(myjob3.pathhead_ok + day1 + "\\");
                    info3ng = new DirectoryInfo(myjob3.pathhead_ng + day1 + "\\");
                    string[] trr3;
                    try { trr3 = info3ng.CreationTime.ToString().Split('/', ' '); } catch { trr3 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb3 = int.Parse(trr3[0] + trr3[1] + trr3[2]);
                    try
                    {
                        if (bb3 != bbnow)
                        {
                            if (Directory.Exists(myjob3.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob3.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob3.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info4ok = new DirectoryInfo(myjob4.pathhead_ok + day1 + "\\");
                    info4ng = new DirectoryInfo(myjob4.pathhead_ng + day1 + "\\");
                    string[] trr4;
                    try { trr4 = info4ng.CreationTime.ToString().Split('/', ' '); } catch { trr4 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb4 = int.Parse(trr4[0] + trr4[1] + trr4[2]);
                    try
                    {
                        if (bb4 != bbnow)
                        {
                            if (Directory.Exists(myjob4.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob4.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob4.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info5ok = new DirectoryInfo(myjob5.pathhead_ok + day1 + "\\");
                    info5ng = new DirectoryInfo(myjob5.pathhead_ng + day1 + "\\");
                    string[] trr5;
                    try { trr5 = info5ng.CreationTime.ToString().Split('/', ' '); } catch { trr5 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb5 = int.Parse(trr5[0] + trr5[1] + trr5[2]);
                    try
                    {
                        if (bb5 != bbnow)
                        {
                            if (Directory.Exists(myjob5.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob5.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob5.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info6ok = new DirectoryInfo(myjob6.pathhead_ok + day1 + "\\");
                    info6ng = new DirectoryInfo(myjob6.pathhead_ng + day1 + "\\");
                    string[] trr6;
                    try { trr6 = info6ng.CreationTime.ToString().Split('/', ' '); } catch { trr6 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb6 = int.Parse(trr6[0] + trr6[1] + trr6[2]);
                    try
                    {
                        if (bb6 != bbnow)
                        {
                            if (Directory.Exists(myjob6.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob6.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob6.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info7ok = new DirectoryInfo(myjob7.pathhead_ok + day1 + "\\");
                    info7ng = new DirectoryInfo(myjob7.pathhead_ng + day1 + "\\");
                    string[] trr7;
                    try { trr7 = info7ng.CreationTime.ToString().Split('/', ' '); } catch { trr7 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb7 = int.Parse(trr7[0] + trr7[1] + trr7[2]);
                    try
                    {
                        if (bb7 != bbnow)
                        {
                            if (Directory.Exists(myjob7.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob7.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob7.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    info8ok = new DirectoryInfo(myjob8.pathhead_ok + day1 + "\\");
                    info8ng = new DirectoryInfo(myjob8.pathhead_ng + day1 + "\\");
                    string[] trr8;
                    try { trr8 = info8ng.CreationTime.ToString().Split('/', ' '); } catch { trr8 = new string[] { "0", "0", "0" }; } // ch:目录不存在时给默认值，避免启动中断
                    int bb8 = int.Parse(trr8[0] + trr8[1] + trr8[2]);
                    try
                    {
                        if (bb8 != bbnow)
                        {
                            if (Directory.Exists(myjob8.pathhead_ng + day1))
                            {
                                foreach (string f in Directory.GetFileSystemEntries(myjob8.pathhead_ng + day1))
                                {
                                    if (File.Exists(f))
                                    {
                                        //如果有子文件删除文件
                                        File.Delete(f);
                                    }
                                }
                                //删除空文件夹
                                Directory.Delete(myjob8.pathhead_ng + day1);
                            }
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    try
                    {
                        myjob1.number = DirCount(info1ok);
                        myjob1.numberng = DirCount(info1ng);
                        myjob2.number = DirCount(info2ok);
                        myjob2.numberng = DirCount(info2ng);
                        myjob3.number = DirCount(info3ok);
                        myjob3.numberng = DirCount(info3ng);
                        myjob4.number = DirCount(info4ok);
                        myjob4.numberng = DirCount(info4ng);
                        myjob5.number = DirCount(info5ok);
                        myjob5.numberng = DirCount(info5ng);
                        myjob6.number = DirCount(info6ok);
                        myjob6.numberng = DirCount(info6ng);
                        myjob7.number = DirCount(info7ok);
                        myjob7.numberng = DirCount(info7ng);
                        myjob8.number = DirCount(info8ok);
                        myjob8.numberng = DirCount(info8ng);

                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "tu"); };

                time = 6;
                ti = new TimeSpan(time);

                myjob1.out_end = 0;
                myjob2.out_end = 0;
                myjob3.out_end = 0;
                myjob4.out_end = 0;
                myjob5.out_end = 0;
                myjob6.out_end = 0;
                myjob7.out_end = 0;
                myjob8.out_end = 0;
                myjob1.path_number = "1";
                myjob2.path_number = "2";
                myjob3.path_number = "3";
                myjob4.path_number = "4";
                myjob5.path_number = "5";
                myjob6.path_number = "6";
                myjob7.path_number = "7";
                myjob8.path_number = "8";
                start1 = 0;
                fff = 0;
                myjob1.yun = 0;
                myjob2.yun = 0;
                myjob3.yun = 0;
                myjob4.yun = 0;
                myjob5.yun = 0;
                myjob6.yun = 0;
                myjob7.yun = 0;
                myjob8.yun = 0;
                yunxing = false;
                // Thread input = new Thread(new ThreadStart(inputMonitor));
                // Thread output;

                //  input.Start();
                // output.Start();
                string pppp = AppDomain.CurrentDomain.BaseDirectory + "di.vpp";
                path_1 = canshuIni.ReadString("path", "path_1", pppp).Replace("\0", "");
                // string ph = "QuickBuild1附件6.vpp";
                //path_1 = AppDomain.CurrentDomain.BaseDirectory + "di.vpp";
                runLog.CreateDirectoryCsvPath(myjob1.path_number);
                try
                {
                    manager1 = (CogJobManager)CogSerializer.LoadObjectFromFile(path_1);
                }
                catch (Exception ex)
                {
                    path_1 = path_1 + "方案已损坏";
                    MsgErroeLog.WriteLog(ex.Message + "方案加载失败!");
                    // ch:P1-13 原实现后台线程弹无 owner 模态框，可被主窗压底导致本线程永久停等；改封送 UI 带 owner
                    SafeBeginInvoke(new Action(() => MessageBox.Show(this, "方案文件加载失败，请检查方案路径配置：" + ex.Message, "方案加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                }
                int yanshi_ms = 5;
                int.TryParse(canshuIni.ReadString("camera", "yanshi", "5").Replace("\0", ""), out yanshi_ms);
                Thread.Sleep(yanshi_ms);
                // ch:P0-4 以下菜单/标签操作原在 jindu 后台线程直接改 DropDownItems 并挂 Click，与 UI 首帧布局/绘制并发
                //   （CheckForIllegalCrossThreadCalls=false 掩盖）。整体封送 UI 线程执行；此段仅读小文件，放 UI 无耗时问题。
                //   本线程启动于构造函数，可能早于窗口句柄建立：先等句柄（上限 10s）再同步 Invoke，保证菜单必定构建。
                for (int hw = 0; hw < 200 && !IsHandleCreated; hw++)
                    Thread.Sleep(50);
                Invoke(new Action(() =>
                {
                item_sum = this.设置ToolStripMenuItem.DropDownItems.Count;
                StreamReader sr = null;
                try
                {
                    int i = this.设置ToolStripMenuItem.DropDownItems.Count;
                    if (i > item_sum)
                    {
                        for (int j = 0; j < i; j++)
                        {
                            if (j >= item_sum)
                            {
                                this.设置ToolStripMenuItem.DropDownItems.RemoveAt(item_sum);
                            }
                        }
                    }
                    sr = new StreamReader(Path.GetDirectoryName(path_1) + "\\Menu.ini");
                    i = item_sum;
                    while (sr.Peek() >= 0)
                    {
                        menuitem = new ToolStripMenuItem(sr.ReadLine());
                        this.设置ToolStripMenuItem.DropDownItems.Insert(i, menuitem);
                        i++;
                        menuitem.Click += new EventHandler(menuitem_Click);
                    }
                    sr.Dispose();
                    sr.Close();
                }
                catch
                {
                    try
                    {
                        sr.Dispose();
                        sr.Close();
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                sr = null;
                try
                {
                    sr = new StreamReader(Path.GetDirectoryName(path_1) + "\\Menu.ini");
                    int i = 0;
                    while (sr.Peek() >= 0)
                    {
                        i++;
                        sr.ReadLine();
                    }
                    sr.Dispose();
                    sr.Close();
                    if (i > 5)
                    {
                        FileStream stream = null;
                        try
                        {
                            stream = File.Open(Path.GetDirectoryName(path_1) + "\\Menu.ini", FileMode.OpenOrCreate, FileAccess.Write);
                            stream.Seek(0, SeekOrigin.Begin);
                            stream.SetLength(0);
                            stream.Flush();
                            stream.Close();
                        }
                        catch
                        {
                            stream.Flush();
                            stream.Close();
                        }
                    }
                }
                catch
                {
                    try
                    {
                        sr.Dispose();
                        sr.Close();
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

                };
                if (this.设置ToolStripMenuItem.DropDownItems[this.设置ToolStripMenuItem.DropDownItems.Count - 1].Text != path_1)
                {
                    StreamWriter s = new StreamWriter(Path.GetDirectoryName(path_1) + "\\Menu.ini", true);
                    s.WriteLine(path_1);
                    s.Flush();
                    s.Close();
                }
                label75.Text = path_1.ToString().Split('\\').Last();
                try
                {
                    wenjianjia = Path.GetDirectoryName(path_1);
                    MsgErroeLog.WriteLog("方案文件夹:" + wenjianjia);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                }
                })); // ch:P0-4 UI 封送段结束
                try
                {
                    if (manager1.JobCount > 0)
                    {
                        manager1.UserQueueFlush();
                        manager1.FailureQueueFlush();
                        myjob1.job = manager1.Job(0);
                        myIndependentJob = myjob1.job.OwnedIndependent;
                        myjob1.job.ImageQueueFlush();
                        myjob1.Cogbmp = new CogImageFileBMP();
                        myIndependentJob.RealTimeQueueFlush();
                        // path_1 = @".\test.vpp";

                    }
                    frm3.jobsum = manager1.JobCount;
                    if (manager1.JobCount > 1)
                    {


                        myjob2.job = manager1.Job(1);
                        myIndependentJob2 = myjob2.job.OwnedIndependent;
                        myjob2.job.ImageQueueFlush();
                        myjob2.Cogbmp = new CogImageFileBMP();
                        myIndependentJob2.RealTimeQueueFlush();

                    }
                    if (manager1.JobCount > 2)
                    {

                        myjob3.job = manager1.Job(2);
                        myIndependentJob3 = myjob3.job.OwnedIndependent;
                        myjob3.job.ImageQueueFlush();
                        myjob3.Cogbmp = new CogImageFileBMP();
                        myIndependentJob3.RealTimeQueueFlush();

                    }

                    if (manager1.JobCount > 3)
                    {
                        myjob4.job = manager1.Job(3);

                        myIndependentJob4 = myjob4.job.OwnedIndependent;
                        myjob4.job.ImageQueueFlush();
                        myjob4.Cogbmp = new CogImageFileBMP();
                        myIndependentJob4.RealTimeQueueFlush();

                    }
                    if (manager1.JobCount > 4)
                    {
                        myjob5.job = manager1.Job(4);

                        myIndependentJob5 = myjob5.job.OwnedIndependent;
                        myjob5.job.ImageQueueFlush();
                        myjob5.Cogbmp = new CogImageFileBMP();
                        myIndependentJob5.RealTimeQueueFlush();

                    }
                    if (manager1.JobCount > 5)
                    {
                        myjob6.job = manager1.Job(5);

                        myIndependentJob6 = myjob6.job.OwnedIndependent;
                        myjob6.job.ImageQueueFlush();
                        myjob6.Cogbmp = new CogImageFileBMP();
                        myIndependentJob6.RealTimeQueueFlush();

                    }
                    if (manager1.JobCount > 6)
                    {
                        myjob7.job = manager1.Job(6);

                        myIndependentJob7 = myjob7.job.OwnedIndependent;
                        myjob7.job.ImageQueueFlush();
                        myjob7.Cogbmp = new CogImageFileBMP();
                        myIndependentJob7.RealTimeQueueFlush();

                    }
                    if (manager1.JobCount > 7)
                    {
                        myjob8.job = manager1.Job(7);

                        myIndependentJob8 = myjob8.job.OwnedIndependent;
                        myjob8.job.ImageQueueFlush();
                        myjob8.Cogbmp = new CogImageFileBMP();
                        myIndependentJob8.RealTimeQueueFlush();

                    }
                }
                catch
                {
                    MsgErroeLog.WriteLog("无流程");
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "111");
            };
            if (manager1 == null)
            {
                MsgErroeLog.WriteLog("方案加载失败，跳过视觉流程（仍打开相机供手动操作）");
                Frm2.start = 1; // ch:关闭加载进度窗，避免主界面被永久遮挡
                Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族；ch:解锁方案切换门闩，允许后续通过"打开"重新加载方案
                yijing = 1;
                // ch:方案失败不阻断相机：仍执行界面初始化并打开相机（流程部分由 initialize_FormSet 内跳过）
                SafeBeginInvoke(new Action(() => initialize_FormSet()));
                return;
            }
            if (closing) return; // ch:关闭过程中不再启动初始化/自动运行流程
            initialize_form initialize_form1 = new initialize_form(initialize_FormSet);
            SafeBeginInvoke(initialize_form1);
            try
            {
                // ★ 参考正常版本：等待 initialize_FormSet 在 UI 线程完成（qiehuanzhong 归 0）再继续，
                //   避免相机尚未打开时后台线程就启动采集（StartGrabbing 对空句柄静默失败 → 相机"不能操作"）
                // ch:等待上限 30 秒：慢电脑+多相机+大方案的正常初始化可能超过 10 秒，固定 10 秒会误判超时
                int waitMs = 0;
                while (Volatile.Read(ref qiehuanzhong) == 1 && waitMs < 30000 && !closing) // ch:P2 原子读，避免读陈旧 1 白等 30s
                {
                    Thread.Sleep(100);
                    waitMs += 100;
                }
                if (Volatile.Read(ref qiehuanzhong) == 1 && !closing)
                {
                    int openedCnt = 0;
                    for (int k = 0; k < 8; k++) { if (m_MyCamera[k] != null) openedCnt++; }
                    MsgErroeLog.WriteLog("initialize_FormSet 等待30秒超时未完成，继续执行（已打开相机" + openedCnt + "/8）");
                }
                if (closing) return;
                trriger_set();

                Thread.Sleep(50);
                if (closing) return;
                Thread ui = new Thread(new ThreadStart(UI_monitor));
                ui.IsBackground = true;
                ui.Start();
                // comboBox8_SelectedIndexChanged(null, null);
                bnClose.Enabled = true;

                if (closing) return;
                // ch:开机自动运行开关（code.ini [canshu] autorun；1=自动启动采集（默认，保持原行为），0=仅打开相机不启动采集）
                string autorun = canshuIni.ReadString("canshu", "autorun", "1").Replace("\0", "");
                if (autorun == "1")
                    button1_Click(null, null);
                yijing = 1;
            }

            catch (Exception ex)
            {
                yijing = 1;
                MsgErroeLog.WriteLog(ex.Message + "222");
            };
        }
        int yijing = 0;
        // ch:分流程切换后同步运行元数据（Color/tishi/biaotou/zidongbaoguang/calib），防止新流程与旧元数据不匹配
        private static bool saveDirWarned = false;
        // ch:统计目录内文件数，目录不存在时返回 0（防止启动时对未创建的存图目录抛异常）
        private int DirCount(DirectoryInfo di)
        {
            try
            {
                if (di == null || !di.Exists) return 0;
                return di.GetFileSystemInfos().Length;
            }
            catch { return 0; }
        }

        // ch:存图目录创建失败只提示一次，避免每帧刷日志（存图路径固定 E:\，现场需保证 E 盘存在且有写权限）
        private void WarnSaveDirOnce(Exception ex)
        {
            if (!saveDirWarned)
            {
                saveDirWarned = true;
                MsgErroeLog.WriteLog("存图目录创建失败，请检查 E 盘:" + ex.Message);
            }
        }

        private void sync_job_meta(Myjob myjob)
        {
            try
            {
                // ch:P0-3 本方法读 block 的 Inputs/Outputs 并做 calibdrop，ToolBlock 非线程安全，须与该相机检测线程 block.Run 互斥
                lock (myjob.blockLock)
                {
                    if (myjob.block == null) return;
                myjob.biaotou = "";
                myjob.zidongbaoguang = false;
                try
                {
                    string aatemp = myjob.block.Outputs["tishi"].Value.ToString();
                    myjob.tishi = true;
                }
                catch
                {
                    myjob.tishi = false;
                }
                if ((myjob.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                    myjob.Color = false;
                else
                    myjob.Color = true;
                for (int i = 0; i < myjob.block.Outputs.Count; i++)
                {
                    if (myjob.block.Outputs[i].Name.Contains("ji"))
                    {
                        myjob.biaotou += myjob.block.Outputs[i].Name + ",";
                    }
                    if (myjob.block.Outputs[i].Name.Contains("buchang"))
                    {
                        myjob.zidongbaoguang = true;
                    }
                }
                runLog.CreateDirectoryCsvPath(myjob.path_number);
                runLog.CreateCsvPath(myjob.path_number, myjob.biaotou);
                calibdrop(myjob.block, out myjob.calib);
                if (myjob.calib != null)
                {
                    if (myjob.myTable1 == null)
                        myjob.myTable1 = new DataTable();
                    myjob.myTable1.Clear();
                    myjob.myTable1.Columns.Clear();
                    myjob.myTable1.Columns.Add("像素X");
                    myjob.myTable1.Columns.Add("像素Y");
                    myjob.myTable1.Columns.Add("实际X");
                    myjob.myTable1.Columns.Add("实际Y");
                    if (!myjob.calib.Calibration.Calibrated)
                        myjob.calib.Calibration.NumPoints = 9;
                    for (int i = 0; i < myjob.calib.Calibration.NumPoints; i++)
                    {
                        myjob.myTable1.Rows.Add();
                        myjob.myTable1.Rows[i]["像素X"] = myjob.calib.Calibration.GetUncalibratedPointX(i);
                        myjob.myTable1.Rows[i]["像素Y"] = myjob.calib.Calibration.GetUncalibratedPointY(i);
                        myjob.myTable1.Rows[i]["实际X"] = myjob.calib.Calibration.GetRawCalibratedPointX(i);
                        myjob.myTable1.Rows[i]["实际Y"] = myjob.calib.Calibration.GetRawCalibratedPointY(i);
                    }
                }
                } // ch:P0-3 blockLock 段结束
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(myjob.path_number + "分流程元数据同步失败:" + ex.Message);
            }
        }

        private void initialize_FormSet()
        {
            if (closing) return; // ch:关闭过程中不再执行界面初始化/自动开相机
            if (manager1 == null)
            {
                MsgErroeLog.WriteLog("方案加载失败，仅打开相机（跳过流程初始化）");
                // ch:方案失败不阻断相机：仍然打开相机、启动帧率监控，供手动操作（采集/参数/触发/预览）
                try
                {
                    Frm2.start = 1;
                    this.WindowState = FormWindowState.Maximized;
                    checkedListBox1.Enabled = false;
                    Thread zhenlv = new Thread(new ThreadStart(zhenlv_1));
                    zhenlv.IsBackground = true;
                    zhenlv.Start();
                    bnOpen_Click(null, null);
                    display();
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("仅相机打开异常:" + ex.Message);
                }
                Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族；ch:解除后台线程等待，避免初始化流程超时阻塞
                return;
            }
            string pppp = "";
            try
            {
                try
                {
                    pppp = canshuIni.ReadString("camera1", "fen", pppp).Replace("\0", "");
                    if (pppp.Contains("vpp"))
                    {
                        try
                        {
                            myjob1.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\1\\" + pppp);
                            comboBox7.Text = pppp;
                        }
                        catch (Exception ex)
                        {
                            group_1 = myjob1.job.VisionTool as CogToolGroup;
                            block_1 = group_1.Tools["CogToolBlock1"] as CogToolBlock;
                            myjob1.block = block_1.Tools["CogToolBlock1"] as CogToolBlock;
                            MsgErroeLog.WriteLog("分流程" + ex.Message);
                        }
                    }

                    else
                    {
                        group_1 = myjob1.job.VisionTool as CogToolGroup;
                        block_1 = group_1.Tools["CogToolBlock1"] as CogToolBlock;
                        myjob1.block = block_1.Tools["CogToolBlock1"] as CogToolBlock;
                    }
                    try
                    {
                        string aatemp = myjob1.block.Outputs["tishi"].Value.ToString();
                        myjob1.tishi = true;
                    }
                    catch
                    {
                        myjob1.tishi = false;
                    }
                    if ((myjob1.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                        myjob1.Color = false;
                    else
                    {
                        myjob1.Color = true;
                    }
                    for (int i = 0; i < myjob1.block.Outputs.Count; i++)
                    {
                        if (myjob1.block.Outputs[i].Name.Contains("ji"))
                        {
                            myjob1.biaotou += myjob1.block.Outputs[i].Name + ",";
                        }
                        if (myjob1.block.Outputs[i].Name.Contains("buchang"))
                        {
                            myjob1.zidongbaoguang = true;
                        }
                    }
                    runLog.CreateDirectoryCsvPath(myjob1.path_number);
                    //if(myjob1.biaotou.Contains(","))
                    runLog.CreateCsvPath(myjob1.path_number, myjob1.biaotou);
                    calibdrop(myjob1.block, out myjob1.calib);
                    if (myjob1.calib != null)
                    {
                        myjob1.myTable1.Clear();
                        myjob1.myTable1.Columns.Add("像素X");
                        myjob1.myTable1.Columns.Add("像素Y");
                        myjob1.myTable1.Columns.Add("实际X");
                        myjob1.myTable1.Columns.Add("实际Y");
                        if (!myjob1.calib.Calibration.Calibrated)
                            myjob1.calib.Calibration.NumPoints = 9;
                        for (int i = 0; i < myjob1.calib.Calibration.NumPoints; i++)

                        {
                            myjob1.myTable1.Rows.Add();
                            myjob1.myTable1.Rows[i]["像素X"] = myjob1.calib.Calibration.GetUncalibratedPointX(i);
                            myjob1.myTable1.Rows[i]["像素Y"] = myjob1.calib.Calibration.GetUncalibratedPointY(i);
                            myjob1.myTable1.Rows[i]["实际X"] = myjob1.calib.Calibration.GetRawCalibratedPointX(i);
                            myjob1.myTable1.Rows[i]["实际Y"] = myjob1.calib.Calibration.GetRawCalibratedPointY(i);
                        }
                    }
                }
                catch
                {
                    MsgErroeLog.WriteLog("流程1初始化失败");
                }
                //td1 = new Thread(new ThreadStart(getrecord_1));
                //td1.Start();
                listBox2.Items.Add("产品类型:相机1");
                listBox2.Items.Add("检测数:");
                listBox2.Items.Add("OK数:");
                listBox2.Items.Add("NG数:");
                listBox2.Items.Add("合格率:");
                listBox2.Items.Add("~~~~~~~~~");
                listBox1.Items.Add("record");
                listBox1.Items.Add("record");
                listBox1.Items.Add("record");
                listBox1.Items.Add("record");
                listBox1.Items.Add("record");
                listBox3.Items.Add("record");
                listBox3.Items.Add("record");
                listBox3.Items.Add("record");
                listBox3.Items.Add("record");
                listBox3.Items.Add("record");
                listBox7.Items.Add("record");
                listBox7.Items.Add("record");
                listBox7.Items.Add("record");
                listBox7.Items.Add("record");
                listBox7.Items.Add("record");
                listBox6.Items.Add("record");
                listBox6.Items.Add("record");
                listBox6.Items.Add("record");
                listBox6.Items.Add("record");
                listBox6.Items.Add("record");
                listBox14.Items.Add("record");
                listBox14.Items.Add("record");
                listBox14.Items.Add("record");
                listBox14.Items.Add("record");
                listBox14.Items.Add("record");
                listBox15.Items.Add("record");
                listBox15.Items.Add("record");
                listBox15.Items.Add("record");
                listBox15.Items.Add("record");
                listBox15.Items.Add("record");
                listBox18.Items.Add("record");
                listBox18.Items.Add("record");
                listBox18.Items.Add("record");
                listBox18.Items.Add("record");
                listBox18.Items.Add("record");
                listBox17.Items.Add("record");
                listBox17.Items.Add("record");
                listBox17.Items.Add("record");
                listBox17.Items.Add("record");
                listBox17.Items.Add("record");
                dataGridView1.ReadOnly = true;
                dataGridView1.AllowUserToAddRows = false;
                // dataGridView1.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                try
                {
                    if (manager1.JobCount > 1)
                    {
                        listBox2.Items.Add("产品类型:相机2");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera2", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob2.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\2\\" + pppp);
                                comboBox9.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_2 = myjob2.job.VisionTool as CogToolGroup;
                                block_2 = group_2.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob2.block = block_2.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_2 = myjob2.job.VisionTool as CogToolGroup;
                            block_2 = group_2.Tools["CogToolBlock1"] as CogToolBlock;
                            myjob2.block = block_2.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob2.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob2.Color = false;
                        else
                            myjob2.Color = true;
                        //  myjob2.CogFifo = block_2.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                        //td2 = new Thread(new ThreadStart(getrecord_2));
                        // td2.Start();
                        dataGridView2.ReadOnly = true;
                        dataGridView2.AllowUserToAddRows = false;
                        //  dataGridView2.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                        try
                        {
                            string aatemp = myjob2.block.Outputs["tishi"].Value.ToString();
                            myjob2.tishi = true;
                        }
                        catch
                        {
                            myjob2.tishi = false;
                        }
                        for (int i = 0; i < myjob2.block.Outputs.Count; i++)
                        {
                            if (myjob2.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob2.biaotou += myjob2.block.Outputs[i].Name + ",";
                            }
                            if (myjob2.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob2.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob2.path_number);
                        //if (myjob2.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob2.path_number, myjob2.biaotou);
                        calibdrop(myjob2.block, out myjob2.calib);
                        if (myjob2.calib != null)
                        {
                            myjob2.myTable1.Clear();
                            myjob2.myTable1.Columns.Add("像素X");
                            myjob2.myTable1.Columns.Add("像素Y");
                            myjob2.myTable1.Columns.Add("实际X");
                            myjob2.myTable1.Columns.Add("实际Y");
                            if (!myjob2.calib.Calibration.Calibrated)
                                myjob2.calib.Calibration.NumPoints = 9;
                            for (int i = 0; i < myjob2.calib.Calibration.NumPoints; i++)
                            {
                                myjob2.myTable1.Rows.Add();
                                myjob2.myTable1.Rows[i]["像素X"] = myjob2.calib.Calibration.GetUncalibratedPointX(i);
                                myjob2.myTable1.Rows[i]["像素Y"] = myjob2.calib.Calibration.GetUncalibratedPointY(i);
                                myjob2.myTable1.Rows[i]["实际X"] = myjob2.calib.Calibration.GetRawCalibratedPointX(i);
                                myjob2.myTable1.Rows[i]["实际Y"] = myjob2.calib.Calibration.GetRawCalibratedPointY(i);
                            }
                        }
                    }
                    if (manager1.JobCount > 2)
                    {
                        listBox2.Items.Add("产品类型:相机3");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera3", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob3.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\3\\" + pppp);
                                comboBox10.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_3 = myjob3.job.VisionTool as CogToolGroup;
                                block_3 = group_3.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob3.block = block_3.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_3 = myjob3.job.VisionTool as CogToolGroup;
                            block_3 = group_3.Tools["CogToolBlock1"] as CogToolBlock;
                            //  myjob3.CogFifo = block_3.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob3.block = block_3.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob3.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob3.Color = false;
                        else
                            myjob3.Color = true;
                        //td3 = new Thread(new ThreadStart(getrecord_3));
                        // td3.Start();
                        try
                        {
                            string aatemp = myjob3.block.Outputs["tishi"].Value.ToString();
                            myjob3.tishi = true;
                        }
                        catch
                        {
                            myjob3.tishi = false;
                        }
                        for (int i = 0; i < myjob3.block.Outputs.Count; i++)
                        {
                            if (myjob3.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob3.biaotou += myjob3.block.Outputs[i].Name + ",";
                            }
                            if (myjob3.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob3.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob3.path_number);
                        //if (myjob3.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob3.path_number, myjob3.biaotou);
                    }
                    if (manager1.JobCount > 3)
                    {
                        listBox2.Items.Add("产品类型:相机4");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera4", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob4.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\4\\" + pppp);
                                comboBox11.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_4 = myjob4.job.VisionTool as CogToolGroup;
                                block_4 = group_4.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob4.block = block_4.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_4 = myjob4.job.VisionTool as CogToolGroup;
                            block_4 = group_4.Tools["CogToolBlock1"] as CogToolBlock;

                            // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob4.block = block_4.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob4.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob4.Color = false;
                        else
                            myjob4.Color = true;
                        // td4 = new Thread(new ThreadStart(getrecord_4));
                        // td4.Start();
                        try
                        {
                            string aatemp = myjob4.block.Outputs["tishi"].Value.ToString();
                            myjob4.tishi = true;
                        }
                        catch
                        {
                            myjob4.tishi = false;
                        }
                        for (int i = 0; i < myjob4.block.Outputs.Count; i++)
                        {
                            if (myjob4.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob4.biaotou += myjob4.block.Outputs[i].Name + ",";
                            }
                            if (myjob4.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob4.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob4.path_number);
                        // if (myjob4.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob4.path_number, myjob4.biaotou);
                    }
                    if (manager1.JobCount > 4)
                    {
                        listBox2.Items.Add("产品类型:相机5");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera5", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob5.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\5\\" + pppp);
                                comboBox12.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_5 = myjob5.job.VisionTool as CogToolGroup;
                                block_5 = group_5.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob5.block = block_5.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }

                        }
                        else
                        {
                            group_5 = myjob5.job.VisionTool as CogToolGroup;
                            block_5 = group_5.Tools["CogToolBlock1"] as CogToolBlock;

                            // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob5.block = block_5.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob5.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob5.Color = false;
                        else
                            myjob5.Color = true;
                        // td5 = new Thread(new ThreadStart(getrecord_5));
                        //  td5.Start();
                        try
                        {
                            string aatemp = myjob5.block.Outputs["tishi"].Value.ToString();
                            myjob5.tishi = true;
                        }
                        catch
                        {
                            myjob5.tishi = false;
                        }
                        for (int i = 0; i < myjob5.block.Outputs.Count; i++)
                        {
                            if (myjob5.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob5.biaotou += myjob5.block.Outputs[i].Name + ",";
                            }
                            if (myjob5.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob5.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob5.path_number);
                        //   if (myjob5.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob5.path_number, myjob5.biaotou);
                    }
                    if (manager1.JobCount > 5)
                    {
                        listBox2.Items.Add("产品类型:相机6");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera6", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob6.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\6\\" + pppp);
                                comboBox13.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_6 = myjob6.job.VisionTool as CogToolGroup;
                                block_6 = group_6.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob6.block = block_6.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_6 = myjob6.job.VisionTool as CogToolGroup;
                            block_6 = group_6.Tools["CogToolBlock1"] as CogToolBlock;

                            // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob6.block = block_6.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob6.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob6.Color = false;
                        else
                            myjob6.Color = true;
                        // td6 = new Thread(new ThreadStart(getrecord_6));
                        // td6.Start();
                        try
                        {
                            string aatemp = myjob6.block.Outputs["tishi"].Value.ToString();
                            myjob6.tishi = true;
                        }
                        catch
                        {
                            myjob6.tishi = false;
                        }
                        for (int i = 0; i < myjob6.block.Outputs.Count; i++)
                        {
                            if (myjob6.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob6.biaotou += myjob6.block.Outputs[i].Name + ",";
                            }
                            if (myjob6.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob6.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob6.path_number);
                        // if (myjob6.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob6.path_number, myjob6.biaotou);
                    }
                    if (manager1.JobCount > 6)
                    {
                        listBox2.Items.Add("产品类型:相机7");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera7", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob7.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\7\\" + pppp);
                                comboBox14.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_7 = myjob7.job.VisionTool as CogToolGroup;
                                block_7 = group_7.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob7.block = block_7.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_7 = myjob7.job.VisionTool as CogToolGroup;
                            block_7 = group_7.Tools["CogToolBlock1"] as CogToolBlock;

                            // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob7.block = block_7.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob7.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob7.Color = false;
                        else
                            myjob7.Color = true;
                        //  td7 = new Thread(new ThreadStart(getrecord_7));
                        //  td7.Start();
                        try
                        {
                            string aatemp = myjob7.block.Outputs["tishi"].Value.ToString();
                            myjob7.tishi = true;
                        }
                        catch
                        {
                            myjob7.tishi = false;
                        }
                        for (int i = 0; i < myjob7.block.Outputs.Count; i++)
                        {
                            if (myjob7.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob7.biaotou += myjob7.block.Outputs[i].Name + ",";
                            }
                            if (myjob7.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob7.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob7.path_number);
                        //  if (myjob7.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob7.path_number, myjob7.biaotou);
                    }
                    if (manager1.JobCount > 7)
                    {
                        listBox2.Items.Add("产品类型:相机8");
                        listBox2.Items.Add("检测数:");
                        listBox2.Items.Add("OK数:");
                        listBox2.Items.Add("NG数:");
                        listBox2.Items.Add("合格率:");
                        listBox2.Items.Add("~~~~~~~~~");
                        pppp = "";
                        pppp = canshuIni.ReadString("camera8", "fen", pppp).Replace("\0", "");
                        if (pppp.Contains("vpp"))
                        {
                            try
                            {
                                myjob8.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\8\\" + pppp);
                                comboBox15.Text = pppp;
                            }
                            catch (Exception ex)
                            {
                                group_8 = myjob8.job.VisionTool as CogToolGroup;
                                block_8 = group_8.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob8.block = block_8.Tools["CogToolBlock1"] as CogToolBlock;
                                MsgErroeLog.WriteLog("分流程" + ex.Message);
                            }
                        }
                        else
                        {
                            group_8 = myjob8.job.VisionTool as CogToolGroup;
                            block_8 = group_8.Tools["CogToolBlock1"] as CogToolBlock;

                            // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                            myjob8.block = block_8.Tools["CogToolBlock1"] as CogToolBlock;
                        }
                        if ((myjob8.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                            myjob8.Color = false;
                        else
                            myjob8.Color = true;
                        // td8 = new Thread(new ThreadStart(getrecord_8));
                        // td8.Start();
                        try
                        {
                            string aatemp = myjob8.block.Outputs["tishi"].Value.ToString();
                            myjob8.tishi = true;
                        }
                        catch
                        {
                            myjob8.tishi = false;
                        }
                        for (int i = 0; i < myjob8.block.Outputs.Count; i++)
                        {
                            if (myjob8.block.Outputs[i].Name.Contains("ji"))
                            {
                                myjob8.biaotou += myjob8.block.Outputs[i].Name + ",";
                            }
                            if (myjob8.block.Outputs[i].Name.Contains("buchang"))
                            {
                                myjob8.zidongbaoguang = true;
                            }
                        }
                        runLog.CreateDirectoryCsvPath(myjob8.path_number);
                        // if (myjob8.biaotou.Contains(","))
                        runLog.CreateCsvPath(myjob8.path_number, myjob8.biaotou);
                    }
                }
                catch
                {
                    MsgErroeLog.WriteLog("无流程2");
                }
                decimal devalue;
                decimal.TryParse(canshuIni.ReadString("camera", "yanshi", "5"), out devalue);
                numericUpDown1.Value = devalue;
                Thread.Sleep(10);
                Frm2.start = 1;
                this.WindowState = FormWindowState.Maximized;
                checkedListBox1.Enabled = false;
                //CogFrameGrabberGigEs mf2 = new CogFrameGrabberGigEs();//获取已连接相机列表
                //if (mf2.Count == 0)
                //    MessageBox.Show("没有连接到相机！");
                Thread zhenlv = new Thread(new ThreadStart(zhenlv_1));
                zhenlv.IsBackground = true;
                zhenlv.Start();
                bnOpen_Click(null, null);
                display();
                
                // 注意：参数设置已移至 INI 读取之后执行（修复参数设置顺序问题）
                textBox19.Text = canshuIni.ReadString("zhendongpan", "path", "").Replace("\0", "");
                if (canshuIni.ReadString("camera", "cuntu", "存图限制").Replace("\0", "") == "存图限制")
                {
                    comboBox21.SelectedIndex = 0;
                    cuntu = 0;
                }
                else
                {
                    comboBox21.SelectedIndex = 1;
                    cuntu = 1;
                }
                if (canshuIni.ReadString("camera", "tongji", "统计限制").Replace("\0", "") == "统计限制")
                {
                    comboBox22.SelectedIndex = 0;
                    tongji = 0;
                }
                else
                {
                    comboBox22.SelectedIndex = 1;
                    tongji = 1;
                }
                // Replace("\0", "")
                if (canshuIni.ReadString("camera", "qufan", "输出取反").Replace("\0", "") == "输出取反")
                {
                    shuchuqufan = "Accept";
                }
                else
                {
                    button31.Text = "输出取正";
                    shuchuqufan = "Reject";
                }
                // this.dataGridView1.DataSource = myjob1.myTable;//将List的数据绑定到DataGridView中
                // this.dataGridView2.DataSource = myjob2.myTable;//将List的数据绑定到DataGridView中
                if (canshuIni.ReadString("camera1", "biaoge", "false") == "true")
                {

                    checkBox11.CheckState = CheckState.Checked;
                }
                else
                {
                    dataGridView1.ReadOnly = false;
                    dataGridView1.DataSource = myjob1.myTable1;
                    checkBox11.CheckState = CheckState.Unchecked;
                }
                if (canshuIni.ReadString("camera2", "biaoge", "false") == "true")
                    checkBox8.CheckState = CheckState.Checked;
                else
                {
                    dataGridView2.ReadOnly = false;
                    checkBox8.CheckState = CheckState.Unchecked;
                    dataGridView2.DataSource = myjob2.myTable1;
                }

                if (canshuIni.ReadString("camera3", "biaoge", "false") == "true")
                    checkBox56.CheckState = CheckState.Checked;
                else
                    checkBox56.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera4", "biaoge", "false") == "true")
                    checkBox57.CheckState = CheckState.Checked;
                else
                    checkBox57.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera5", "biaoge", "false") == "true")
                    checkBox58.CheckState = CheckState.Checked;
                else
                    checkBox58.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera6", "biaoge", "false") == "true")
                    checkBox59.CheckState = CheckState.Checked;
                else
                    checkBox59.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera7", "biaoge", "false") == "true")
                    checkBox60.CheckState = CheckState.Checked;
                else
                    checkBox60.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera8", "biaoge", "false") == "true")
                    checkBox61.CheckState = CheckState.Checked;
                else
                    checkBox61.CheckState = CheckState.Unchecked;

                if (canshuIni.ReadString("camera1", "shijianEn", "false") == "true")
                    checkBox3.CheckState = CheckState.Checked;
                else
                    checkBox3.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera2", "shijianEn", "false") == "true")
                    checkBox13.CheckState = CheckState.Checked;
                else
                    checkBox13.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera3", "shijianEn", "false") == "true")
                    checkBox18.CheckState = CheckState.Checked;
                else
                    checkBox18.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera4", "shijianEn", "false") == "true")
                    checkBox22.CheckState = CheckState.Checked;
                else
                    checkBox22.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera5", "shijianEn", "false") == "true")
                    checkBox36.CheckState = CheckState.Checked;
                else
                    checkBox36.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera6", "shijianEn", "false") == "true")
                    checkBox42.CheckState = CheckState.Checked;
                else
                    checkBox42.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera7", "shijianEn", "false") == "true")
                    checkBox48.CheckState = CheckState.Checked;
                else
                    checkBox48.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera8", "shijianEn", "false") == "true")
                    checkBox54.CheckState = CheckState.Checked;
                else
                    checkBox54.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera", "NG", "false") == "true")
                    checkBox71.CheckState = CheckState.Checked;
                else
                    checkBox71.CheckState = CheckState.Unchecked;

                if (canshuIni.ReadString("camera", "NG", "false") == "true")
                    checkBox7.CheckState = CheckState.Checked;
                else
                    checkBox7.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera2", "chatu", "false") == "true")
                    checkBox6.CheckState = CheckState.Checked;
                else
                    checkBox6.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera3", "chatu", "false") == "true")
                    checkBox14.CheckState = CheckState.Checked;
                else
                    checkBox14.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera4", "chatu", "false") == "true")
                    checkBox12.CheckState = CheckState.Checked;
                else
                    checkBox12.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera5", "chatu", "false") == "true")
                    checkBox35.CheckState = CheckState.Checked;
                else
                    checkBox35.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera6", "chatu", "false") == "true")
                    checkBox41.CheckState = CheckState.Checked;
                else
                    checkBox41.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera7", "chatu", "false") == "true")
                    checkBox55.CheckState = CheckState.Checked;
                else
                    checkBox55.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera8", "chatu", "false") == "true")
                    checkBox53.CheckState = CheckState.Checked;
                else
                    checkBox53.CheckState = CheckState.Unchecked;
                tbExposure1.Text = canshuIni.ReadString("camera1", "exposure", "1000");
                tbGain1.Text = canshuIni.ReadString("camera1", "gain", "1");
                tbFrameRate1.Text = canshuIni.ReadString("camera1", "rate", "500");
                if (canshuIni.ReadString("camera1", "triggeren", "true") == "true")
                    checkBox5.CheckState = CheckState.Checked;
                else
                    checkBox5.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera1", "modbustcp", "false") == "true")
                    checkBox24.CheckState = CheckState.Checked;
                else
                    checkBox24.CheckState = CheckState.Unchecked;

                if (canshuIni.ReadString("camera1", "1", "false") == "true")
                    checkBox1.CheckState = CheckState.Checked;
                else
                    checkBox1.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera1", "2", "false") == "true")
                    checkBox2.CheckState = CheckState.Checked;
                else
                    checkBox2.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera", "datajilu", "false") == "true")
                    checkBox25.CheckState = CheckState.Checked;
                else
                {
                    checkBox25.CheckState = CheckState.Unchecked;
                    checkBox25_CheckedChanged(null, null);
                }
                // ch:开机/自动运行默认关闭工具块栏和 gongjukuai，不从 ini 恢复勾选；运行中再勾才启用
                ForceGongjuJiluOff();
                ApplyDisplayRawFromIni();
                textBox3.Text = canshuIni.ReadString("camera1", "outtime", "100");
                if (canshuIni.ReadString("camera1", "serial", "true") == "true")
                    checkBox27.CheckState = CheckState.Checked;
                else
                    checkBox27.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera2", "serial", "true") == "true")
                    checkBox28.CheckState = CheckState.Checked;
                else
                    checkBox28.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera3", "serial", "true") == "true")
                    checkBox29.CheckState = CheckState.Checked;
                else
                    checkBox29.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera4", "serial", "true") == "true")
                    checkBox30.CheckState = CheckState.Checked;
                else
                    checkBox30.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera5", "serial", "true") == "true")
                    checkBox31.CheckState = CheckState.Checked;
                else
                    checkBox31.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera6", "serial", "true") == "true")
                    checkBox37.CheckState = CheckState.Checked;
                else
                    checkBox37.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera7", "serial", "true") == "true")
                    checkBox43.CheckState = CheckState.Checked;
                else
                    checkBox43.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera8", "serial", "true") == "true")
                    checkBox49.CheckState = CheckState.Checked;
                else
                    checkBox49.CheckState = CheckState.Unchecked;
                tbExposure2.Text = canshuIni.ReadString("camera2", "exposure", "1000");
                tbGain2.Text = canshuIni.ReadString("camera2", "gain", "1");
                tbFrameRate2.Text = canshuIni.ReadString("camera2", "rate", "500");
                if (canshuIni.ReadString("camera2", "triggeren", "true") == "true")
                    checkBox9.CheckState = CheckState.Checked;
                else
                    checkBox9.CheckState = CheckState.Unchecked;

                if (canshuIni.ReadString("camera2", "modbustcp", "false") == "true")
                    checkBox23.CheckState = CheckState.Checked;
                else
                    checkBox23.CheckState = CheckState.Unchecked;
                tbExposure3.Text = canshuIni.ReadString("camera3", "exposure", "1000");
                tbGain3.Text = canshuIni.ReadString("camera3", "gain", "1");
                tbFrameRate3.Text = canshuIni.ReadString("camera3", "rate", "500");
                if (canshuIni.ReadString("camera3", "triggeren", "true") == "true")
                    checkBox15.CheckState = CheckState.Checked;
                else
                    checkBox15.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera3", "modbustcp", "false") == "true")
                    checkBox21.CheckState = CheckState.Checked;
                else
                    checkBox21.CheckState = CheckState.Unchecked;
                tbExposure4.Text = canshuIni.ReadString("camera4", "exposure", "1000");
                tbGain4.Text = canshuIni.ReadString("camera4", "gain", "1");
                tbFrameRate4.Text = canshuIni.ReadString("camera4", "rate", "500");
                if (canshuIni.ReadString("camera4", "triggeren", "true") == "true")
                    checkBox19.CheckState = CheckState.Checked;
                else
                    checkBox19.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera4", "modbustcp", "false") == "true")
                    checkBox17.CheckState = CheckState.Checked;
                else
                    checkBox17.CheckState = CheckState.Unchecked;

                tbExposure5.Text = canshuIni.ReadString("camera5", "exposure", "1000");
                tbGain5.Text = canshuIni.ReadString("camera5", "gain", "1");
                tbFrameRate5.Text = canshuIni.ReadString("camera5", "rate", "500");
                if (canshuIni.ReadString("camera5", "triggeren", "true") == "true")
                    checkBox33.CheckState = CheckState.Checked;
                else
                    checkBox33.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera5", "modbustcp", "false") == "true")
                    checkBox32.CheckState = CheckState.Checked;
                else
                    checkBox32.CheckState = CheckState.Unchecked;

                tbExposure6.Text = canshuIni.ReadString("camera6", "exposure", "1000");
                tbGain6.Text = canshuIni.ReadString("camera6", "gain", "1");
                tbFrameRate6.Text = canshuIni.ReadString("camera6", "rate", "500");
                if (canshuIni.ReadString("camera6", "triggeren", "true") == "true")
                    checkBox39.CheckState = CheckState.Checked;
                else
                    checkBox39.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera6", "modbustcp", "false") == "true")
                    checkBox38.CheckState = CheckState.Checked;
                else
                    checkBox38.CheckState = CheckState.Unchecked;


                tbExposure7.Text = canshuIni.ReadString("camera7", "exposure", "1000");
                tbGain7.Text = canshuIni.ReadString("camera7", "gain", "1");
                tbFrameRate7.Text = canshuIni.ReadString("camera7", "rate", "500");
                if (canshuIni.ReadString("camera7", "triggeren", "true") == "true")
                    checkBox45.CheckState = CheckState.Checked;
                else
                    checkBox45.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera7", "modbustcp", "false") == "true")
                    checkBox44.CheckState = CheckState.Checked;
                else
                    checkBox44.CheckState = CheckState.Unchecked;

                tbExposure8.Text = canshuIni.ReadString("camera8", "exposure", "1000");
                tbGain8.Text = canshuIni.ReadString("camera8", "gain", "1");
                tbFrameRate8.Text = canshuIni.ReadString("camera8", "rate", "500");
                if (canshuIni.ReadString("camera8", "triggeren", "true") == "true")
                    checkBox51.CheckState = CheckState.Checked;
                else
                    checkBox51.CheckState = CheckState.Unchecked;
                if (canshuIni.ReadString("camera8", "modbustcp", "false") == "true")
                    checkBox50.CheckState = CheckState.Checked;
                else
                    checkBox50.CheckState = CheckState.Unchecked;

                decimal.TryParse(canshuIni.ReadString("time", "feng", "100"), out devalue);
                devalue = ClampToUpDown(numericUpDown22, devalue); // ch:P1-5 先夹取再赋值/派生
                numericUpDown22.Value = devalue;
                feng = double.Parse(devalue.ToString());

                // ch:R13 读取全局输出方式；键缺失/非法 → -1(自动)，与升级前行为完全一致（零回归）
                int outModeIni;
                if (!int.TryParse(canshuIni.ReadString("camera", "output_mode", "").Replace("\0", ""), out outModeIni)
                    || outModeIni < OutAuto || outModeIni > OutModbusRtu)
                    outModeIni = OutAuto;
                ApplyOutputMode(outModeIni, false);

                // ch:R18 读取显示频率；键缺失/非法 → 15Hz(=66ms)，与升级前常量行为完全一致（零回归）
                int hzIni;
                if (!int.TryParse(canshuIni.ReadString("camera", "display_hz", "").Replace("\0", ""), out hzIni)
                    || hzIni < (int)numDisplayHz.Minimum || hzIni > (int)numDisplayHz.Maximum)
                    hzIni = 15;
                _displayHzSyncing = true;
                try { numDisplayHz.Value = hzIni; }
                finally { _displayHzSyncing = false; }
                OcxMinIntervalMs = 1000 / hzIni;

                // ch:R25 读取性能统计打印开关；键缺失/非法 → 默认开(1)，与升级前(恒打印)行为衔接
                string perfPrintIni = canshuIni.ReadString("camera", "perf_print", "1").Replace("\0", "");
                bool perfPrintOnIni = perfPrintIni != "0";
                _perfPrintSyncing = true;
                try { chkPerfPrint.Checked = perfPrintOnIni; }
                finally { _perfPrintSyncing = false; }
                _perfPrintOn = perfPrintOnIni;

                decimal.TryParse(canshuIni.ReadString("time", "IOyanshi", "0"), out devalue);
                devalue = ClampToUpDown(numericUpDown5, devalue); // ch:P1-5
                numericUpDown5.Value = devalue;

                decimal.TryParse(canshuIni.ReadString("cuntu", "zhangshu", "200"), out devalue);
                devalue = ClampToUpDown(numericUpDown4, devalue); // ch:P1-5 越界(如 0<Min)曾抛异常吞掉后续启动
                zhangshu = devalue;
                numericUpDown4.Value = devalue;

                //this.FormBorderStyle = FormBorderStyle.FixedSingle;
                decimal.TryParse(canshuIni.ReadString("time", "NG", "1000"), out devalue);
                devalue = ClampToUpDown(numericUpDown2, devalue); // ch:P1-5
                jiankongshijian = (double)devalue;
                numericUpDown2.Value = devalue;

                // ★ INI 读取完成后，设置相机参数（修复参数设置顺序问题）
                MsgErroeLog.WriteLog("首次启动：INI读取完成，开始设置相机参数");
                trriger_set();
                baoguang_set();
                Thread.Sleep(100);
                
                // 设置各相机参数到硬件（ch:P2 收紧：仅对使能(en==1)的相机自动下发，屏蔽相机即使物理在线也不写参数）
                if (m_MyCamera[0] != null && myjob1.en == 1)
                {
                    bnSetParam_Click(null, null);
                    bnGetParam_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机1参数已设置");
                }
                if (m_MyCamera[1] != null && myjob2.en == 1 && manager1 != null && manager1.JobCount > 1)
                {
                    bnSetParam2_Click(null, null);
                    bnGetParam2_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机2参数已设置");
                }
                if (m_MyCamera[2] != null && myjob3.en == 1 && manager1 != null && manager1.JobCount > 2)
                {
                    bnSetParam3_Click(null, null);
                    bnGetParam3_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机3参数已设置");
                }
                if (m_MyCamera[3] != null && myjob4.en == 1 && manager1 != null && manager1.JobCount > 3)
                {
                    bnSetParam4_Click(null, null);
                    bnGetParam4_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机4参数已设置");
                }
                if (m_MyCamera[4] != null && myjob5.en == 1 && manager1 != null && manager1.JobCount > 4)
                {
                    bnSetParam5_Click(null, null);
                    bnGetParam5_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机5参数已设置");
                }
                if (m_MyCamera[5] != null && myjob6.en == 1 && manager1 != null && manager1.JobCount > 5)
                {
                    bnSetParam6_Click(null, null);
                    bnGetParam6_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机6参数已设置");
                }
                if (m_MyCamera[6] != null && myjob7.en == 1 && manager1 != null && manager1.JobCount > 6)
                {
                    bnSetParam7_Click(null, null);
                    bnGetParam7_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机7参数已设置");
                }
                if (m_MyCamera[7] != null && myjob8.en == 1 && manager1 != null && manager1.JobCount > 7)
                {
                    bnSetParam8_Click(null, null);
                    bnGetParam8_Click(null, null);
                    MsgErroeLog.WriteLog("首次启动：相机8参数已设置");
                }
                MsgErroeLog.WriteLog("首次启动：相机参数设置完成");


                myjob1.myTable.Clear();
                DataRow dr = myjob1.myTable.NewRow();
                myjob1.myTable.Rows.Add(dr);
                myjob1.myTable.Columns.Add("种类", typeof(String));
                myjob1.myTable.Columns.Add("数量", typeof(String));
                myjob2.myTable.Clear();
                DataRow dr2 = myjob2.myTable.NewRow();
                myjob2.myTable.Rows.Add(dr2);
                myjob2.myTable.Columns.Add("种类", typeof(String));
                myjob2.myTable.Columns.Add("数量", typeof(String));
                myjob3.myTable.Clear();
                DataRow dr3 = myjob3.myTable.NewRow();
                myjob3.myTable.Rows.Add(dr3);
                myjob3.myTable.Columns.Add("种类", typeof(String));
                myjob3.myTable.Columns.Add("数量", typeof(String));
                myjob4.myTable.Clear();
                DataRow dr4 = myjob4.myTable.NewRow();
                myjob4.myTable.Rows.Add(dr4);
                myjob4.myTable.Columns.Add("种类", typeof(String));
                myjob4.myTable.Columns.Add("数量", typeof(String));
                myjob5.myTable.Clear();
                DataRow dr5 = myjob5.myTable.NewRow();
                myjob5.myTable.Rows.Add(dr5);
                myjob5.myTable.Columns.Add("种类", typeof(String));
                myjob5.myTable.Columns.Add("数量", typeof(String));
                myjob6.myTable.Clear();
                DataRow dr6 = myjob6.myTable.NewRow();
                myjob6.myTable.Rows.Add(dr6);
                myjob6.myTable.Columns.Add("种类", typeof(String));
                myjob6.myTable.Columns.Add("数量", typeof(String));
                myjob7.myTable.Clear();
                DataRow dr7 = myjob7.myTable.NewRow();
                myjob7.myTable.Rows.Add(dr7);
                myjob7.myTable.Columns.Add("种类", typeof(String));
                myjob7.myTable.Columns.Add("数量", typeof(String));
                myjob8.myTable.Clear();
                DataRow dr8 = myjob8.myTable.NewRow();
                myjob8.myTable.Rows.Add(dr8);
                myjob8.myTable.Columns.Add("种类", typeof(String));
                myjob8.myTable.Columns.Add("数量", typeof(String));
                // this.dataGridView1.DataSource = myjob1.myTable;//将List的数据绑定到DataGridView中
                // this.dataGridView2.DataSource = myjob2.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView3.DataSource = myjob3.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView4.DataSource = myjob4.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView5.DataSource = myjob5.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView6.DataSource = myjob6.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView7.DataSource = myjob7.myTable;//将List的数据绑定到DataGridView中
                this.dataGridView8.DataSource = myjob8.myTable;//将List的数据绑定到DataGridView中
                
                // 注意：参数设置已移至 initialize_FormSet 中，在相机打开后执行
                // 这里只设置UI状态，不设置相机参数
                bnStartGrab1.Enabled = false;
                bnStopGrab1.Enabled = true;
                try
                {
                    if (manager1.JobCount > 1)
                    {
                        bnStartGrab2.Enabled = false;
                        bnStopGrab2.Enabled = true;
                    }
                    if (manager1.JobCount > 2)
                    {
                        bnStartGrab3.Enabled = false;
                        bnStopGrab3.Enabled = true;
                    }
                    if (manager1.JobCount > 3)
                    {
                        bnStartGrab4.Enabled = false;
                        bnStopGrab4.Enabled = true;
                    }
                    if (manager1.JobCount > 4)
                    {
                        bnStartGrab5.Enabled = false;
                        bnStopGrab5.Enabled = true;
                    }
                    if (manager1.JobCount > 5)
                    {
                        bnStartGrab6.Enabled = false;
                        bnStopGrab6.Enabled = true;
                    }
                    if (manager1.JobCount > 6)
                    {
                        bnStartGrab7.Enabled = false;
                        bnStopGrab7.Enabled = true;
                    }
                    if (manager1.JobCount > 7)
                    {
                        bnStartGrab8.Enabled = false;
                        bnStopGrab8.Enabled = true;
                    }
                }
                catch
                {
                    MsgErroeLog.WriteLog("无流程3");
                }

                int geshu = int.Parse(canshuIni.ReadString("canshu", "geshu", "0"));
                if (geshu > 0)
                {
                    for (int i = 0; i < geshu; i++)
                    {
                        comboBox38.Items.Add(canshuIni.ReadString("canshu", (i + 1).ToString(), " "));
                    }
                    comboBox38.Text = canshuIni.ReadString("canshu", "xuanze", " ");
                }
                Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "000");
                // Frm2.start = 1;
                Thread.Sleep(100);
                //if(ex.Message.Contains("未能找到文件"))
                //textBoxSolutionPath.Text = "无方案!!!!";
                Frm2.start = 1;
                Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族


            };
        }
        Dictionary<string, string> dict1 = new Dictionary<string, string>();
        #endregion
        #region 帧率检测
        // ch:帧率统计周期（秒）缓存：zhenlv_1 工作线程不得直接读 numericUpDown6（UI 控件），
        //    由循环末尾经 SafeBeginInvoke 异步刷新缓存值；int 赋值原子，跨线程读写安全
        private int _zhenlvIntervalSec = 5;
        private void zhenlv_1()
        {
            while (!closing)
            {
                try
                {
                    int now = 0;
                    int forword = myjob1.sum;
                    int sec = _zhenlvIntervalSec;
                    if (sec < 1) sec = 1;
                    Thread.Sleep(sec * 1000 - 5);
                    now = myjob1.sum;
                    zhen = ((now - forword) * 1.00F / sec).ToString();
                    // ch:异步刷新周期设置（原实现直接读 numericUpDown6.Value，工作线程读 UI 控件不安全）
                    SafeBeginInvoke(new Action(() => { _zhenlvIntervalSec = decimal.ToInt32(numericUpDown6.Value); }));
                }
                catch { break; } // ch:窗体销毁后控件访问异常即退出
            }

        }
        #endregion
        #region 创建设备列表
        private void DeviceListAcq()
        {
            // ch:创建设备列表 | en:Create Device List
            System.GC.Collect();
            cbDeviceList.Items.Clear();
            m_pDeviceList.nDeviceNum = 0;
            int nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
            if (0 != nRet)
            {
                ShowErrorMsg("Enumerate devices fail!", 0);
                return;
            }
            m_nDevNum = (int)m_pDeviceList.nDeviceNum;
            tbDevNum.Text = m_nDevNum.ToString("d");
            tbUseNum.Text = m_nDevNum.ToString("d");
            MsgErroeLog.WriteLog("枚举到相机设备 " + m_nDevNum + " 台");
            // ch:在窗体列表中显示设备名 | en:Display device name in the form list
            int cameraid = 0;
            for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
            {
                MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(m_pDeviceList.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO));
                MsgErroeLog.WriteLog("枚举设备#" + (i + 1) + ":" + DevInfoString(ref device));
                if (device.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {

                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)MyCamera.ByteToStruct(device.SpecialInfo.stGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));

                    if (gigeInfo.chUserDefinedName != "")
                    {
                        cbDeviceList.Items.Add("GEV: " + gigeInfo.chUserDefinedName + " (" + gigeInfo.chSerialNumber + ")");
                    }
                    else
                    {
                        cbDeviceList.Items.Add("GEV: " + gigeInfo.chManufacturerName + " " + gigeInfo.chModelName + " (" + gigeInfo.chSerialNumber + ")");
                    }
                }
                else if (device.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    MyCamera.MV_USB3_DEVICE_INFO usbInfo = (MyCamera.MV_USB3_DEVICE_INFO)MyCamera.ByteToStruct(device.SpecialInfo.stUsb3VInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    if (usbInfo.chUserDefinedName != "")
                    {
                        cbDeviceList.Items.Add("U3V: " + usbInfo.chUserDefinedName + " (" + usbInfo.chSerialNumber + ")");
                    }
                    else
                    {
                        cbDeviceList.Items.Add("U3V: " + usbInfo.chManufacturerName + " " + usbInfo.chModelName + " (" + usbInfo.chSerialNumber + ")");
                    }
                }
            }

            // ch:选择第一项 | en:Select the first item
            if (m_pDeviceList.nDeviceNum != 0)
            {
                cbDeviceList.SelectedIndex = 0;
            }


        }
        #endregion
        #region 显示错误信息
        private void ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                errorMsg = csMessage;
            }
            else
            {
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            switch (nErrorNum)
            {
                case MyCamera.MV_E_HANDLE: errorMsg += " Error or invalid handle "; break;
                case MyCamera.MV_E_SUPPORT: errorMsg += " Not supported function "; break;
                case MyCamera.MV_E_BUFOVER: errorMsg += " Cache is full "; break;
                case MyCamera.MV_E_CALLORDER: errorMsg += " Function calling order error "; break;
                case MyCamera.MV_E_PARAMETER: errorMsg += " Incorrect parameter "; break;
                case MyCamera.MV_E_RESOURCE: errorMsg += " Applying resource failed "; break;
                case MyCamera.MV_E_NODATA: errorMsg += " No data "; break;
                case MyCamera.MV_E_PRECONDITION: errorMsg += " Precondition error, or running environment changed "; break;
                case MyCamera.MV_E_VERSION: errorMsg += " Version mismatches "; break;
                case MyCamera.MV_E_NOENOUGH_BUF: errorMsg += " Insufficient memory "; break;
                case MyCamera.MV_E_UNKNOW: errorMsg += " Unknown error "; break;
                case MyCamera.MV_E_GC_GENERIC: errorMsg += " General error "; break;
                case MyCamera.MV_E_GC_ACCESS: errorMsg += " Node accessing condition error "; break;
                case MyCamera.MV_E_ACCESS_DENIED: errorMsg += " No permission "; break;
                case MyCamera.MV_E_BUSY: errorMsg += " Device is busy, or network disconnected "; break;
                case MyCamera.MV_E_NETER: errorMsg += " Network error "; break;
            }

            MessageBox.Show(errorMsg, "PROMPT");
        }
        #endregion
        #region 判断黑白彩色
        private Boolean IsMonoData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                    return true;

                default:
                    return false;
            }
        }

        /************************************************************************
         *  @fn     IsColorData()
         *  @brief  判断是否是彩色数据
         *  @param  enGvspPixelType         [IN]           像素格式
         *  @return 成功，返回0；错误，返回-1 
         ************************************************************************/
        private Boolean IsColorData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_YUYV_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YCBCR411_8_CBYYCRYY:
                    return true;

                default:
                    return false;
            }
        }
        #endregion
        private void inputMonitor()
        {

        }
        private void outputMonitor()
        {

        }
        protected void getCode()
        {
            this.Invoke(new Action(() =>
            {
                // ch:R10-12 旧二维码位图被覆盖前未释放，重新授权/重跑 getCode 会累积 GDI 位图句柄泄漏
                Image oldPic = pictureBox1.Image;
                pictureBox1.Image = QRCodeHelper.GetQRCodeBmp(identifier("Win32_DiskDrive", "Signature") + "M" + identifier("Win32_DiskDrive", "TotalHeads"));
                if (oldPic != null && !ReferenceEquals(oldPic, pictureBox1.Image))
                    oldPic.Dispose();
            }));
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            int dayt = 0;
            int dayz = 0;
            string zhongjian = "22";

            string code = canshuIni.ReadString("code1", "code2", "");
            if (code == "")
            {
                Thread.Sleep(200);
                code = canshuIni.ReadString("code1", "code2", "");
                if (code == "")
                {
                    MessageBox.Show("未读到");
                }
            }
            try
            {
                duini.ReadINIFile("C:\\Program Files\\test.ini");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + ":1488");
            }
            string code1 = duini.ReadString("1", "2", "");
            string code2 = duini.ReadString("1", "3", "");
            string code3 = duini.ReadString("1", "4", "");
            Thread.Sleep(20);
            try
            {
                if (int.Parse(code1) - int.Parse(code3) <= 1)
                {
                    daoqi = "软件剩余时间1天，请联系厂家!";
                }
                else
                    daoqi = "";
            }
            catch (Exception ex)
            { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            if (code != "")
            {
                try
                {
                    try
                    {
                        dayt = int.Parse(code.Substring(int.Parse(code.Substring(code.Length - 2)), 5)) - ((int)DateTime.Now.ToOADate());
                    }
                    catch
                    {
                        try
                        {
                            code = canshuIni.ReadString("code1", "code2", "");
                            dayt = int.Parse(code.Substring(int.Parse(code.Substring(code.Length - 2)), 5)) - ((int)DateTime.Now.ToOADate());
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message + ":1501:" + code);
                        }
                    }
                    if (dayt >= 0)
                    {
                        zhongjian = code.Substring(int.Parse(code.Substring(code.Length - 2)), 5);
                    }
                    else
                        zhongjian = DateTime.Now.ToOADate().ToString();
                    if (int.Parse(code1) > ((int)DateTime.Now.ToOADate()) && int.Parse(code2) <= ((int)DateTime.Now.ToOADate()) && int.Parse(code3) <= ((int)DateTime.Now.ToOADate()))
                    {
                        dayz = 1;
                    }
                    else
                        dayz = 0;
                    if (int.Parse(code3) <= ((int)DateTime.Now.ToOADate()))
                        duini.WriteString("1", "4", ((int)DateTime.Now.ToOADate()).ToString());
                }

                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + ":1520:" + code1);
                }
            }
            try
            {
                day2 = GetCPUSerialnumber(zhongjian);
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + ":对比");
            };
            if (dayz == 0)
            {
                checkedListBox1.SetItemChecked(0, false);
                checkedListBox1.SetItemChecked(1, false);
                checkedListBox1.SetItemChecked(2, false);
                checkedListBox1.SetItemChecked(3, false);
                checkedListBox1.SetItemChecked(4, false);
                checkedListBox1.SetItemChecked(5, false);
                checkedListBox1.SetItemChecked(6, false);
                checkedListBox1.SetItemChecked(7, false);
                button2.Enabled = false;
                button1.Enabled = false;
                label12.Text = "加密中";
                button5.Visible = true;
                textBox4.Visible = true;
                pictureBox1.Visible = true;
                tableLayoutPanel1.Visible = false;
                getCode();
            }
            else
            {
                checkedListBox1.SetItemChecked(0, true);
                checkedListBox1.SetItemChecked(1, true);
                checkedListBox1.SetItemChecked(2, true);
                checkedListBox1.SetItemChecked(3, true);
                checkedListBox1.SetItemChecked(4, true);
                checkedListBox1.SetItemChecked(5, true);
                checkedListBox1.SetItemChecked(6, true);
                checkedListBox1.SetItemChecked(7, true);
                button2.Enabled = true;
                button1.Enabled = true;
                Frm2.Show();
                // ch:P1-6 若启动加载已先完成（门闩已归 0、Frm2 已被关过），此处 start=0 会让进度窗永不被定时器关闭 → 常驻遮挡。
                //   按当前门闩状态决定：仍在加载才 0，否则直接 1（Frm2 计时器会自行关闭）。
                Frm2.start = (Volatile.Read(ref qiehuanzhong) == 0) ? 1 : 0;
            }
            this.DesktopLocation = new Point(150, 150);

            label8.BringToFront();
            f1.getData += new SubSet.GetSeletionData(DataChangef1);
            f1.getData2 += new SubSet.GetSeletionData2(DataChangef2);
            f1.Show();
            f1.Visible = false;
            frm3.getData += new Form3.GetSeletionData(DataChange);
            frm3.Show();
            frm3.Visible = false;
            frm7.getData += new Form7.GetSeletionData(DataChange_mes);
            frm7.Show();
            frm7.Visible = false;
            omron.getData += new FormOmron.GetSeletionData(DataChange_fins);
            omron.Show();
            omron.Visible = false;
            modbustcp.getData += new FormModbus.GetSeletionData(DataChange_modbustcp);
            modbustcp.Show();
            modbustcp.Visible = false;
            modbusrtu.getData += new FormModbusRtu.GetSeletionData(DataChange_modbusrtu);
            modbusrtu.Show();
            modbusrtu.Visible = false;
            fx.Show();
            fx.Visible = false;
            frm5.Show();
            frm5.Visible = false;
            // ch:性能埋点自检（新增）：仅当环境变量 PERF_SELFTEST=1 时，在无相机下用桩数据验证埋点报表
            if (System.Environment.GetEnvironmentVariable("PERF_SELFTEST") == "1")
            {
                RunPerfSelfCheck();
            }
        }
        private void DataChange_mes(object sender, WindowsFormsApplication1.Form7.SelectionChangedEventArgs e)
        {
        }

        SubSet f1;
        private static string GetCPUSerialnumber(string ttt)
        {
            string cpuSerialnumber = string.Empty;
            using (MD5 md5Hash = MD5.Create())
            {
                byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(identifier("Win32_DiskDrive", "Signature") + "M" + identifier("Win32_DiskDrive", "TotalHeads")));
                byte[] data1 = new byte[] { 0x16, 0xa2, 0xa8 };
                // byte[] data2 = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(strID));
                byte[] data2 = md5Hash.ComputeHash(Encoding.UTF8.GetBytes("123"));
                StringBuilder sBuilder = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {

                    sBuilder.Append(data[i].ToString("x2"));
                }
                sBuilder.Append(ttt);
                for (int i = 0; i < data2.Length; i++)
                {
                    sBuilder.Append(data2[i].ToString("x2"));
                }
                sBuilder.Append(data.Length * 2);
                string id = sBuilder.ToString();
                cpuSerialnumber = id;
            }
            return cpuSerialnumber;
        }
        /// <summary>
        /// 获取硬件标识符
        /// </summary>
        /// <param name="wmiClass"></param>
        /// <param name="wmiProperty"></param>
        /// <returns></returns>
        private static string identifier(string wmiClass, string wmiProperty)
        {
            string result = "";
            try
            {
                System.Management.ManagementClass mc =
             new System.Management.ManagementClass(wmiClass);
                System.Management.ManagementObjectCollection moc = mc.GetInstances();
                foreach (System.Management.ManagementObject mo in moc)
                {
                    //Only get the first one
                    if (result == "")
                    {
                        try
                        {
                            if (mo[wmiProperty] != null)
                            {
                                result = mo[wmiProperty].ToString();
                                break;
                            }
                        }
                        catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                    }
                }
            }
            catch (Exception ex)
            {
                // ch:R10-10 启动/初始化链路上弹窗会卡住线程且无上下文，改记日志
                new ErrorLog().WriteLog(ex.ToString() + ":获取");
            };
            return result;
        }
        private void button1_Click(object sender, EventArgs e)
        {
            // ch:autorun（1265 行）从 InitializeJobManager 后台线程调用本处理器，方法开头大量直接
            //    操作控件——过去依赖 CheckForIllegalCrossThreadCalls=false 放行，属无锁 UI 写竞态。
            //    统一封送到 UI 线程执行（与手动点击一致），后台线程立即返回不阻塞初始化流程。
            //    注意：jindu 线程在 Form1 构造函数中启动，autorun 触发时句柄可能尚未创建，
            //    需等待句柄就绪（最多 5 秒），否则 SafeBeginInvoke 会静默丢弃导致开机不自动运行。
            if (InvokeRequired)
            {
                for (int w = 0; w < 250 && !IsHandleCreated && !closing && !IsDisposed; w++)
                    Thread.Sleep(20);
                if (IsHandleCreated && !IsDisposed && !closing)
                    SafeBeginInvoke(new Action(() => button1_Click(sender, e)));
                else
                    MsgErroeLog.WriteLog("autorun 放弃：窗体句柄未就绪或正在关闭");
                return;
            }
            if (manager1 == null)
            {
                MessageBox.Show("方案未加载成功，无法运行检测！请先检查方案文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ResetMyJobOutputs(); // ch:运行前全量复位 IO 输出标志（参考正常版本，原只复位 myjob1）
            try
            {
                存图测试1ToolStripMenuItem.Checked = false;
                listBox4.Items.Clear();
                listBox5.Items.Clear();
                listBox8.Items.Clear();
                listBox9.Items.Clear();
                listBox10.Items.Clear();
                listBox11.Items.Clear();
                listBox12.Items.Clear();
                listBox13.Items.Clear();
                ForceGongjuJiluOff();
                myjob1.dlg.Dispose();
                // ch:倒序遍历关闭，配合 FormClosed 中的 frm6.Remove 避免集合修改导致跳项/越界
                for (int i = frm6.Count - 1; i >= 0; i--)
                {
                    try { frm6[i].Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                frm6.Clear();
            }
            catch (Exception ex)
            {
                //   MessageBox.Show(ex.Message);
            }
            try

            {
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button1.BackColor = Color.Green;
                    button1.Text = "运行中";
                    button11.BackColor = Color.LightGreen;
                    bnClose.Enabled = false; // ch:运行期间禁止关闭设备，防与取图回调并发释放内存
                }));


                fff = 0;
                if (checkedListBox1.GetItemChecked(0) && myjob1.yun == 0)
                {
                    bnStopGrab1.Enabled = false;
                    //  cbSoftTrigger1.Enabled = false;

                    myjob1.yun = 1;
                    myjob1.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[0] == null)
                    {
                        myjob1.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing1 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[0].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    bnStartGrab1.Enabled = false;
                    bnStopGrab1.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob1.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机1失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize1 = stParam.nCurValue;
                    if (nPayloadSize1 > m_nBufSizeForDriver[0])
                    {
                        if (m_BufForDriver1 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver1);
                        }
                        m_nBufSizeForDriver[0] = nPayloadSize1;
                        m_BufForDriver1 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[0]);
                    }

                    if (m_BufForDriver1 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机1失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo1 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    m_stFrameInfo[0].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[0].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[0]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[0].MV_CC_StartGrabbing_NET();
                    }

                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing1 = false;
                        myjob1.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合
                }
                    } // ch:相机1 启动 else 闭合

                bnGetLineSel1.Enabled = true;
                bnSetLineSel1.Enabled = true;
                bnGetLineMode1.Enabled = true;
                bnSetLineMode1.Enabled = true;
                checkBox4.Enabled = true;
                // cbSoftTrigger1.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob1.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机1运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(1) && myjob2.yun == 0 && manager1.JobCount > 1)
                {
                    bnStopGrab2.Enabled = false;
                    // cbSoftTrigger2.Enabled = false;

                    myjob2.yun = 1;
                    myjob2.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[1] == null)
                    {
                        myjob2.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing2 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[1].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob2.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机2失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize2 = stParam.nCurValue;
                    if (nPayloadSize2 > m_nBufSizeForDriver[1])
                    {
                        if (m_BufForDriver2 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver2);
                        }
                        m_nBufSizeForDriver[1] = nPayloadSize2;
                        m_BufForDriver2 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[1]);
                    }

                    if (m_BufForDriver2 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机2失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo2 = new MyCamera.MV_FRAME_OUT_INFO_EX();
                    // ReceiveThreadProcess1();
                    m_stFrameInfo[1].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[1].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[1]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[1].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab2.Enabled = false;
                    bnStopGrab2.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing2 = false;
                        myjob2.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合
                }
                    } // ch:相机2 启动 else 闭合
                bnGetLineSel2.Enabled = true;
                bnSetLineSel2.Enabled = true;
                bnGetLineMode2.Enabled = true;
                bnSetLineMode2.Enabled = true;
                checkBox10.Enabled = true;
                // cbSoftTrigger2.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob2.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机2运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(2) && myjob3.yun == 0 && manager1.JobCount > 2)
                {
                    bnStopGrab3.Enabled = false;
                    //   cbSoftTrigger3.Enabled = false;

                    myjob3.yun = 1;
                    myjob3.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[2] == null)
                    {
                        myjob3.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing3 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[2].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob3.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机3失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize3 = stParam.nCurValue;
                    if (nPayloadSize3 > m_nBufSizeForDriver[2])
                    {
                        if (m_BufForDriver3 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver3);
                        }
                        m_nBufSizeForDriver[2] = nPayloadSize3;
                        m_BufForDriver3 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[2]);
                    }

                    if (m_BufForDriver3 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机3失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo3 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[2].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[2].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[2]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[2].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab3.Enabled = false;
                    bnStopGrab3.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing3 = false;
                        myjob3.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机3 启动 else 闭合
                bnGetLineSel3.Enabled = true;
                bnSetLineSel3.Enabled = true;
                tabControl1.Enabled = true;
                bnSetLineMode3.Enabled = true;
                checkBox16.Enabled = true;
                //  cbSoftTrigger3.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob3.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机3运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(3) && myjob4.yun == 0 && manager1.JobCount > 3)
                {
                    bnStopGrab4.Enabled = false;
                    //  cbSoftTrigger4.Enabled = false;

                    myjob4.yun = 1;
                    myjob4.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[3] == null)
                    {
                        myjob4.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing4 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[3].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob4.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机4失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize4 = stParam.nCurValue;
                    if (nPayloadSize4 > m_nBufSizeForDriver[3])
                    {
                        if (m_BufForDriver4 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver4);
                        }
                        m_nBufSizeForDriver[3] = nPayloadSize4;
                        m_BufForDriver4 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[3]);
                    }

                    if (m_BufForDriver4 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机4失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo4 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[3].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[3].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[3]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[3].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab4.Enabled = false;
                    bnStopGrab4.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing4 = false;
                        myjob4.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机4 启动 else 闭合
                bnGetLineSel4.Enabled = true;
                bnSetLineSel4.Enabled = true;
                bnGetLineMode4.Enabled = true;
                bnSetLineMode4.Enabled = true;
                checkBox20.Enabled = true;
                //  cbSoftTrigger4.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob4.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机4运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(4) && myjob5.yun == 0 && manager1.JobCount > 4)
                {
                    bnStopGrab5.Enabled = false;
                    //  cbSoftTrigger1.Enabled = false;

                    myjob5.yun = 1;
                    myjob5.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[4] == null)
                    {
                        myjob5.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing5 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[4].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);

                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob5.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机5失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize5 = stParam.nCurValue;
                    if (nPayloadSize5 > m_nBufSizeForDriver[4])
                    {
                        if (m_BufForDriver5 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver5);
                        }
                        m_nBufSizeForDriver[4] = nPayloadSize5;
                        m_BufForDriver5 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[4]);
                    }

                    if (m_BufForDriver5 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机5失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo5 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[4].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[4].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[4]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[4].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab5.Enabled = false;
                    bnStopGrab5.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing5 = false;
                        myjob5.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机5 启动 else 闭合

                bnGetLineSel5.Enabled = true;
                bnSetLineSel5.Enabled = true;
                bnGetLineMode5.Enabled = true;
                bnSetLineMode5.Enabled = true;
                checkBox34.Enabled = true;
                // cbSoftTrigger1.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob5.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机5运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(5) && myjob6.yun == 0 && manager1.JobCount > 5)
                {
                    bnStopGrab6.Enabled = false;
                    // cbSoftTrigger2.Enabled = false;

                    myjob6.yun = 1;
                    myjob6.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[5] == null)
                    {
                        myjob6.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing6 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[5].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob6.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机6失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize6 = stParam.nCurValue;
                    if (nPayloadSize6 > m_nBufSizeForDriver[5])
                    {
                        if (m_BufForDriver6 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver6);
                        }
                        m_nBufSizeForDriver[5] = nPayloadSize6;
                        m_BufForDriver6 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[5]);
                    }

                    if (m_BufForDriver6 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机6失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo6 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[5].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[5].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[5]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[5].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab6.Enabled = false;
                    bnStopGrab6.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing6 = false;
                        myjob6.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机6 启动 else 闭合
                bnGetLineSel6.Enabled = true;
                bnSetLineSel6.Enabled = true;
                bnGetLineMode6.Enabled = true;
                bnSetLineMode6.Enabled = true;
                checkBox40.Enabled = true;
                // cbSoftTrigger2.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob6.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机6运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(6) && myjob7.yun == 0 && manager1.JobCount > 6)
                {
                    bnStopGrab7.Enabled = false;
                    //   cbSoftTrigger3.Enabled = false;

                    myjob7.yun = 1;
                    myjob7.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[6] == null)
                    {
                        myjob7.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing7 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[6].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob7.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机7失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize7 = stParam.nCurValue;
                    if (nPayloadSize7 > m_nBufSizeForDriver[6])
                    {
                        if (m_BufForDriver7 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver7);
                        }
                        m_nBufSizeForDriver[6] = nPayloadSize7;
                        m_BufForDriver7 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[6]);
                    }

                    if (m_BufForDriver7 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机7失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo7 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[6].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[6].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[6]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[6].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab7.Enabled = false;
                    bnStopGrab7.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing7 = false;
                        myjob7.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机7 启动 else 闭合
                bnGetLineSel7.Enabled = true;
                bnSetLineSel7.Enabled = true;
                bnGetLineMode7.Enabled = true;
                bnSetLineMode7.Enabled = true;
                checkBox46.Enabled = true;
                //  cbSoftTrigger3.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob7.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机7运行" + ex.Message);
            };
            try
            {
                if (checkedListBox1.GetItemChecked(7) && myjob8.yun == 0 && manager1.JobCount > 7)
                {
                    bnStopGrab8.Enabled = false;
                    //  cbSoftTrigger4.Enabled = false;

                    myjob8.yun = 1;
                    myjob8.trriger = 0;
                    yunxing = true;
                    //m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    // ch:标志位置位true | en:Set position bit true
                    if (m_MyCamera[7] == null)
                    {
                        myjob8.yun = 0; // ch:相机未打开：本台跳过，继续后续相机（参考正常版本，不中断整体运行）
                    }
                    else
                    {
                    m_bGrabbing8 = true;
                    MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                    int nRet = m_MyCamera[7].MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ShowErrorMsg("Get PayloadSize failed", nRet);
                        myjob8.yun = 0;
                        // yunxing = false;
                        // return; // ch:相机8失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {

                    nPayloadSize8 = stParam.nCurValue;
                    if (nPayloadSize8 > m_nBufSizeForDriver[7])
                    {
                        if (m_BufForDriver8 != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(m_BufForDriver8);
                        }
                        m_nBufSizeForDriver[7] = nPayloadSize8;
                        m_BufForDriver8 = Marshal.AllocHGlobal((Int32)m_nBufSizeForDriver[7]);
                    }

                    if (m_BufForDriver8 == IntPtr.Zero)
                    {
                        //myjob1.yun = 0;
                        //yunxing = false;
                        // return; // ch:相机8失败不阻断后续相机（参考正常版本）
                    }
                    else
                    {
                    stFrameInfo8 = new MyCamera.MV_FRAME_OUT_INFO_EX();

                    // ReceiveThreadProcess1();
                    m_stFrameInfo[7].nFrameLen = 0;//取流之前先清除帧长度
                    m_stFrameInfo[7].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                    // ch:开始采集 | en:Start Grabbing
                    lock (_cameraLocks[7]) // ch:P3-⑧ 每相机独立锁；与 bnOpen/断线重连按相机互斥
                    {
                        nRet = m_MyCamera[7].MV_CC_StartGrabbing_NET();
                    }
                    bnStartGrab8.Enabled = false;
                    bnStopGrab8.Enabled = true;
                    if (MyCamera.MV_OK != nRet)
                    {
                        m_bGrabbing8 = false;
                        myjob8.yun = 0;
                        // yunxing = false;
                        ShowErrorMsg("Start Grabbing Fail!", nRet);

                    }
                    }
                    } // ch:PayloadSize 成功 else 闭合

                }
                    } // ch:相机8 启动 else 闭合
                bnGetLineSel8.Enabled = true;
                bnSetLineSel8.Enabled = true;
                bnGetLineMode8.Enabled = true;
                bnSetLineMode8.Enabled = true;
                checkBox52.Enabled = true;
                //  cbSoftTrigger4.Enabled = true;
            }
            catch (Exception ex)
            {
                myjob8.yun = 0;
                // yunxing = false;
                MsgErroeLog.WriteLog("相机8运行" + ex.Message);
            };
            yunxing = true;
           
            checkedListBox1.Enabled = false;
            if (myjob1.yun == 0 && myjob2.yun == 0 && myjob3.yun == 0 && myjob4.yun == 0 && myjob5.yun == 0 && myjob6.yun == 0 && myjob7.yun == 0 && myjob8.yun == 0)
            {
                myjob1.yun = 0;
                myjob2.yun = 0;
                myjob3.yun = 0;
                myjob4.yun = 0;
                myjob5.yun = 0;
                myjob6.yun = 0;
                myjob7.yun = 0;
                myjob8.yun = 0;
                checkedListBox1.Enabled = true;
                yunxing = false;
               
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                   button11.BackColor = Color.Green;
                }));

                if (checkedListBox1.SelectedIndices.Count == 0)
                    MsgErroeLog.WriteLog("请选择要开启的相机");
            }

        }
        public static int Diaohuan(int cc)
        {
            int da;
            int xiao;
            xiao = cc / 65536;
            da = cc % 65536;
            cc = da * 65536 + xiao;
            return cc;
        }

        int _lastGroupRunLogMs = 0;
        // ch:存图测试加载的 jpg/png 多为 32bpp，直接 new CogImage8Grey(Bitmap) 会失败并被吞掉，
        // ch:随后 Input 为空，方案 ToolBlock 脚本 GroupRun 空引用，界面线程上会弹窗。
        private ICogImage CreateCogImageFromBitmap(Bitmap src, bool color)
        {
            if (src == null)
                return null;
            if (color)
            {
                if (src.PixelFormat == PixelFormat.Format24bppRgb)
                    return new CogImage24PlanarColor(src);
                using (Bitmap b24 = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(b24))
                        g.DrawImage(src, 0, 0, src.Width, src.Height);
                    return new CogImage24PlanarColor(b24);
                }
            }
            if (src.PixelFormat == PixelFormat.Format8bppIndexed)
                return new CogImage8Grey(src);
            Bitmap use = src;
            Bitmap owned = null;
            if (src.PixelFormat != PixelFormat.Format24bppRgb)
            {
                owned = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(owned))
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
                use = owned;
            }
            try
            {
                CogImage24PlanarColor c24 = new CogImage24PlanarColor(use);
                return CogImageConvert.GetIntensityImage(c24, 0, 0, c24.Width, c24.Height);
            }
            finally
            {
                if (owned != null)
                    owned.Dispose();
            }
        }

        private bool TryAssignBlockInput(Myjob myjob, Bitmap src)
        {
            if (myjob == null || myjob.block == null || src == null)
                return false;
            if (!myjob.block.Inputs.Contains("Input"))
            {
                MsgErroeLog.WriteLog("相机" + myjob.path_number + "流程没有Input端子");
                return false;
            }
            try
            {
                SetBlockInputSafe(myjob, "Input", CreateCogImageFromBitmap(src, myjob.Color));
                return myjob.block.Inputs["Input"].Value != null;
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog("相机" + myjob.path_number + "传图失败:" + ex.Message);
                return false;
            }
        }

        // ch:P1-5 ini 值越界会抛 ArgumentOutOfRangeException 被外层大 catch 吞掉（后续启动步骤全部丢失），赋值前先夹到控件范围
        private static decimal ClampToUpDown(NumericUpDown up, decimal v)
        {
            if (v < up.Minimum) return up.Minimum;
            if (v > up.Maximum) return up.Maximum;
            return v;
        }
        // ch:P0 工具块输入安全设置：事件线程（通讯/轮询回调）直接写 block.Inputs 会与检测线程 block.Run() 并发。
        //   统一走本方法，按 job 粒度加锁，与 block.Run() 的临界区互斥。
        private void SetBlockInputSafe(Myjob job, string inputName, object value)
        {
            if (job == null || job.block == null || string.IsNullOrEmpty(inputName))
                return;
            lock (job.blockLock)
            {
                try { job.block.Inputs[inputName].Value = value; }
                catch (Exception ex) { MsgErroeLog.WriteLog("设置工具块输入失败[" + inputName + "]:" + ex.Message); }
            }
        }

        private void getrecord(Myjob myjob)
        {
            try
            {
                bool tempyanshi = true;
                int jobnumber = 0;
                string cuowu1;
                string fins = "无";
                string temptime = "";
                ICogRecord temprecord;
                ICogImage tempimage;
                string tempout1;
                string tempout2;
                string modbustcps = "无";
                string modbusrtus = "无";
                bool inputAssignFailed = false; // ch:P1-02 传图失败标志（在线取图异常 或 通讯触发 TryAssignBlockInput 失败）；与 runError 一起构成故障帧
                CogImageFileBMP tempfilebmp = myjob.Cogbmp;
                //if (myjob.IOyanshi == 0)
                //    tempyanshi = false;
                if (((yunxing == true && myjob.yun == 1) || myjob.trriger == 1) && myjob.en == 1 && myjob.block != null)
                {

                    if (myjob.job.State == CogJobStateConstants.Stopped)
                    {

                        if ((!myjob.trrigerEn) && (myjob.trriger == 0) || (myjob.trriger == 0 && myjob.trrigerEn))
                        {

                            try
                            {

                                #region 取图
                                switch (myjob.path_number)
                                {
                                    case "1":
                                        if (bmp[0] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[0]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[0]));
                                            

                                        }

                                        break;
                                    case "2":
                                        if (bmp[1] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[1]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[1]));
                                        }
                                        break;
                                    case "3":
                                        if (bmp[2] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[2]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[2]));
                                        }
                                        break;
                                    case "4":
                                        if (bmp[3] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[3]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[3]));
                                        }
                                        break;
                                    case "5":
                                        if (bmp[4] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[4]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[4]));
                                        }
                                        break;
                                    case "6":
                                        if (bmp[5] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[5]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[5]));
                                        }
                                        break;
                                    case "7":
                                        if (bmp[6] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[6]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[6]));
                                        }
                                        break;
                                    case "8":
                                        if (bmp[7] != null)
                                        {
                                            if (myjob.Color)
                                                SetBlockInputSafe(myjob, "Input", new CogImage24PlanarColor(bmp[7]));
                                            else
                                                SetBlockInputSafe(myjob, "Input", new CogImage8Grey(bmp[7]));
                                        }
                                        break;
                                }
                                #endregion

                            }
                            catch (Exception ex)
                            {
                                // ch:P1-02 在线取图（构造/赋 Input）失败 → 本帧图像无效，置故障标志，本帧按故障(999)处理，不再沿用旧图
                                inputAssignFailed = true;
                                MsgErroeLog.WriteLog("相机" + myjob.path_number + " 在线取图失败:" + ex.Message + "，本帧按故障(999)处理");
                            };

                        }

                        if (myjob.trriger == 1 || (bmp[int.Parse(myjob.path_number) - 1] != null))
                        {
                            jobnumber = myjob.numberng;

                            cuowu1 = "";
                            if (myjob.trriger == 1)
                            {
                                try
                                {
                                    if (!TryAssignBlockInput(myjob, myjob.img))
                                    {
                                        myjob.trriger = 0;
                                        try { if (myjob.img != null) myjob.img.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                        myjob.img = null;
                                        inputAssignFailed = true; // ch:P1-02 通讯触发传图失败 → 故障帧(999)
                                    }
                                }
                                catch (Exception ex)
                                {
                                    myjob.trriger = 0;
                                    inputAssignFailed = true; // ch:P1-02 通讯触发传图异常 → 故障帧(999)
                                    MsgErroeLog.WriteLog("异常:" + ex.Message);
                                }
                            }
                            else
                            {
                                #region 传图
                                switch (myjob.path_number)
                                {
                                    case "1":

                                        if (myjob1.triggerMode != "连续运行")
                                        {
                                            myjob1.numberng++;

                                            if (myjob1.numberng > 999)
                                                myjob1.numberng = 0;
                                       
                                            jobnumber = myjob1.numberng;
                                        }
                                        break;
                                    case "2":

                                        if (myjob2.triggerMode != "连续运行")
                                        {
                                            myjob2.numberng++;
                                            if (myjob2.numberng > 999)
                                                myjob2.numberng = 0;
                                            jobnumber = myjob2.numberng;
                                        }
                                        break;
                                    case "3":

                                        if (myjob3.triggerMode != "连续运行")
                                        {
                                            myjob3.numberng++;
                                            if (myjob3.numberng > 999)
                                                myjob3.numberng = 0;
                                            jobnumber = myjob3.numberng;
                                        }
                                        break;
                                    case "4":

                                        if (myjob4.triggerMode != "连续运行")
                                        {
                                            myjob4.numberng++;
                                            if (myjob4.numberng > 999)
                                                myjob4.numberng = 0;
                                            jobnumber = myjob4.numberng;
                                        }
                                        break;
                                    case "5":

                                        if (myjob5.triggerMode != "连续运行")
                                        {
                                            myjob5.numberng++;
                                            if (myjob5.numberng > 999)
                                                myjob5.numberng = 0;
                                            jobnumber = myjob5.numberng;
                                        }
                                        break;
                                    case "6":

                                        if (myjob6.triggerMode != "连续运行")
                                        {
                                            myjob6.numberng++;
                                            if (myjob6.numberng > 999)
                                                myjob6.numberng = 0;
                                            jobnumber = myjob6.numberng;
                                        }
                                        break;
                                    case "7":

                                        if (myjob7.triggerMode != "连续运行")
                                        {
                                            myjob7.numberng++;
                                            if (myjob7.numberng > 999)
                                                myjob7.numberng = 0;
                                            jobnumber = myjob7.numberng;
                                        }
                                        break;
                                    case "8":

                                        if (myjob8.triggerMode != "连续运行")
                                        {
                                            myjob8.numberng++;
                                            if (myjob8.numberng > 999)
                                                myjob8.numberng = 0;
                                            jobnumber = myjob8.numberng;
                                        }
                                        break;
                                }
                                #endregion
                            }

                            // ch:检测计数（参考正常版本）：Run 期间计数，切换方案时排空等待，防止 Shutdown 与 Run 并发
                            Bitmap offlineBmp = (myjob.trriger == 1) ? myjob.img : null;
                            Interlocked.Increment(ref _detectingCount);
                            bool runError = false;
                            long perfRunT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            try
                            {
                                if (inputAssignFailed) // ch:R2-supplement 传图失败/无效输入：跳过 Run，不执行旧图，直接记故障帧
                                {
                                    runError = true;
                                    MsgErroeLog.WriteLog("相机" + myjob.path_number + " 传图失败，跳过 block.Run()，本帧按故障(999)处理");
                                }
                                else
                                {
                                    // ch:P0 与 SetBlockInputSafe 共用同一把 blockLock：通讯/轮询线程写 block.Inputs
                                    //   不得与本地 block.Run()/RunStatus 读取并发（VisionPro CogToolBlock 非线程安全）
                                    lock (myjob.blockLock)
                                    {
                                        try
                                        {
                                            myjob.block.Run();
                                            if (myjob.block.RunStatus != null && myjob.block.RunStatus.Result == CogToolResultConstants.Error)
                                            {
                                                runError = true;
                                                int now = Environment.TickCount;
                                                if (now - _lastGroupRunLogMs > 3000)
                                                {
                                                    _lastGroupRunLogMs = now;
                                                    MsgErroeLog.WriteLog("相机" + myjob.path_number + "方案脚本:" + myjob.block.RunStatus.Message);
                                                }
                                            }
                                        }
                                        catch (Exception runEx)
                                        {
                                            // ch:R2 block.Run 直接抛异常（非 RunStatus.Error）也视为故障帧，走 999/NG 路径
                                            runError = true;
                                            MsgErroeLog.WriteLog("相机" + myjob.path_number + " block.Run 异常:" + runEx.Message + "，本帧按故障(999)处理");
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                // ch:统计 VisionPro 检测耗时（与回调总耗时对比即可看出非检测开销占多少）
                                int perfRi;
                                if (int.TryParse(myjob.path_number, out perfRi) && perfRi >= 1 && perfRi <= 8)
                                {
                                    System.Threading.Interlocked.Add(ref _perfRunTicks[perfRi - 1],
                                        System.Diagnostics.Stopwatch.GetTimestamp() - perfRunT0);
                                    _perfRunCount[perfRi - 1]++;
                                    // ch:P1 同步尾部计时起点（block.Run 刚结束）；终点在 DetectWorkerLoop 收到 getrecord 返回处
                                    _perfTailStart[perfRi - 1] = System.Diagnostics.Stopwatch.GetTimestamp();
                                }
                                Interlocked.Decrement(ref _detectingCount);
                                if (offlineBmp != null)
                                {
                                    try { offlineBmp.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                    if (myjob.img == offlineBmp)
                                        myjob.img = null;
                                }
                            }

                            // ch:P0 读取 block.Outputs 同样需在 blockLock 内，避免与通讯线程写 Inputs 并发
                            lock (myjob.blockLock)
                            {
                            if (myjob.tishi)
                            {
                                try
                                {
                                    cuowu1 = myjob.block.Outputs["tishi"].Value.ToString();
                                }
                                catch
                                {
                                    cuowu1 = "NG";
                                }
                            }

                            else
                                cuowu1 = "NG";
                            if (myjob.block.Outputs.Contains("Output") && myjob.block.Outputs["Output"].Value != null)
                                tempout1 = myjob.block.Outputs["Output"].Value.ToString();
                            else
                                tempout1 = "Reject";
                            if (myjob.block.Outputs.Contains("Output1") && myjob.block.Outputs["Output1"].Value != null)
                                tempout2 = myjob.block.Outputs["Output1"].Value.ToString();
                            else
                                tempout2 = "Reject";
                            }

                            if (myjob.trriger != 1)
                            {
                                #region 传图
                                // ch:检测已结束（block.Run 完成后）由属主线程释放本帧位图（统一走锁保护路径）
                                switch (myjob.path_number)
                                {
                                    case "1":
                                        ReleaseBmpSlotNow(0);
                                        break;
                                    case "2":
                                        ReleaseBmpSlotNow(1);
                                        break;
                                    case "3":
                                        ReleaseBmpSlotNow(2);
                                        break;
                                    case "4":
                                        ReleaseBmpSlotNow(3);
                                        break;
                                    case "5":
                                        ReleaseBmpSlotNow(4);
                                        break;
                                    case "6":
                                        ReleaseBmpSlotNow(5);
                                        break;
                                    case "7":
                                        ReleaseBmpSlotNow(6);
                                        break;
                                    case "8":
                                        ReleaseBmpSlotNow(7);
                                        break;
                                }
                                #endregion
                            }

                           
                            // Thread.Sleep(15);
                            // ch:P0 跳过 Run(传图失败)或首帧 RunStatus 为空时 runtime 显式赋 0：既避免 NRE 被外层 catch 吞掉导致整帧结果丢失，也避免沿用旧耗时误判超时 Reject
                            double runProcessingTime = (inputAssignFailed || myjob.block.RunStatus == null) ? 0 : myjob.block.RunStatus.ProcessingTime;
                            switch (myjob.path_number)
                            {
                                case "1":
                                    myjob1.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob1.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }

                                    break;
                                case "2":
                                    myjob2.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob2.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "3":
                                    myjob3.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob3.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "4":
                                    myjob4.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob4.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "5":
                                    myjob5.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob5.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "6":
                                    myjob6.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob6.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "7":
                                    myjob7.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob7.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                                case "8":
                                    myjob8.runtime = runProcessingTime;
                                    if (NG)
                                    {
                                        if (myjob8.runtime >= jiankongshijian)
                                        {
                                            tempout1 = "Reject";
                                            tempout2 = "Reject";
                                            cuowu1 = "NG";
                                        }
                                    }
                                    break;
                            }
                            #region 流程封
                            // ch:P1-03 故障帧 = 脚本报错(runError) 或 传图失败(inputAssignFailed)；故障帧对该相机已配置的每个输出通道统一发 999（数据通道）/ NG 脉冲（IO），非故障帧行为不变
                            bool faultThisFrame = runError || inputAssignFailed;
                            if (faultThisFrame) // ch:R1 故障帧强制最终判定 Reject/NG：统计/存图/CSV 全按故障，杜绝"发999+计OK+存OK夹"
                            {
                                tempout1 = "Reject";
                                tempout2 = "Reject";
                                cuowu1 = "NG";
                            }
                            // ch:P1-04 结果快照：检测线程一次性捕获通讯/曝光端子值；Task.Run 后台线程改用这些不可变快照，避免下一帧改写 ToolBlock 后串帧
                            // ch:P2-① 快照读取同样纳入 blockLock：与 block.Run / SetBlockInputSafe 串行，消除锁外读 Outputs
                            // ch:R13 输出方式互斥闸（配置窗 ComboBox）：outMode<0(自动) 不加锁，行为与改造前一致；
                            //   否则只有被选中的那一路为 true，其余 6 路在 getrecord 里直接不执行，杜绝多路同时下发。
                            int outMode = _outputMode;
                            bool outIO = (outMode < 0 || outMode == OutIO);
                            bool outSerial = (outMode < 0 || outMode == OutSerial);
                            bool outTcp = (outMode < 0 || outMode == OutTcp);
                            bool outMtcp = (outMode < 0 || outMode == OutModbusTcp);
                            bool outOmron = (outMode < 0 || outMode == OutOmron);
                            bool outMtcpWin = (outMode < 0 || outMode == OutModbusTcpWin);
                            bool outMrtu = (outMode < 0 || outMode == OutModbusRtu);
                            string snapTcp, snapSerial, snapMtcp, snapBuchang;
                            // ch:P2-① CSV 用不可变快照：检测线程锁内一次性拼好 "ji*" 输出串，Task 不再回读 block.Outputs，消除把 N+1 帧结果记到 N 帧行
                            string jiSnapshot = "";
                            lock (myjob.blockLock)
                            {
                            try { snapTcp = (outTcp && myjob.tcp) ? (faultThisFrame ? "999" : (myjob.block.Outputs.Contains("tcp") && myjob.block.Outputs["tcp"].Value != null ? myjob.block.Outputs["tcp"].Value.ToString() : "")) : ""; } catch { snapTcp = ""; } // ch:R13 只在本路真的会发时才读 Output（持 blockLock，关键路径上）
                            try { snapSerial = ((outSerial && myjob.serial) || (outMode == OutModbusTcp && myjob.modbustcp)) ? (faultThisFrame ? "999" : (myjob.block.Outputs.Contains("serial") && myjob.block.Outputs["serial"].Value != null ? myjob.block.Outputs["serial"].Value.ToString() : "")) : ""; } catch { snapSerial = ""; } // ch:R13 + ch:R19 mode4 下数据若在 serial 终端也读出（见串口分支的 mode4 回退子句）
                            try { snapMtcp = (outMtcp && myjob.modbustcp) ? (faultThisFrame ? "999" : (myjob.block.Outputs.Contains("modbustcp") && myjob.block.Outputs["modbustcp"].Value != null ? myjob.block.Outputs["modbustcp"].Value.ToString() : "")) : ""; } catch { snapMtcp = ""; } // ch:R13
                            try { snapBuchang = (myjob.block.Outputs.Contains("buchang") && myjob.block.Outputs["buchang"].Value != null ? myjob.block.Outputs["buchang"].Value.ToString() : "0"); } catch { snapBuchang = "0"; }
                            try
                            {
                                if (datajilu == 1)
                                {
                                    StringBuilder sbJi = new StringBuilder();
                                    for (int i = 0; i < myjob.block.Outputs.Count; i++)
                                    {
                                        if (myjob.block.Outputs[i].Name.Contains("ji"))
                                        {
                                            sbJi.Append(myjob.block.Outputs[i].Value);
                                            sbJi.Append(",");
                                        }
                                    }
                                    jiSnapshot = sbJi.ToString();
                                }
                            }
                            catch { jiSnapshot = ""; }
                            }

                            if (outIO && myjob.IO) // ch:R13 输出方式互斥闸
                            {
                                switch (myjob.path_number)
                                {
                                    case "1": if (myjob1.runtime >= jiankongshijian) myjob1.runcishu++; break;
                                    case "2": if (myjob2.runtime >= jiankongshijian) myjob2.runcishu++; break;
                                    case "3": if (myjob3.runtime >= jiankongshijian) myjob3.runcishu++; break;
                                    case "4": if (myjob4.runtime >= jiankongshijian) myjob4.runcishu++; break;
                                    case "5": if (myjob5.runtime >= jiankongshijian) myjob5.runcishu++; break;
                                    case "6": if (myjob6.runtime >= jiankongshijian) myjob6.runcishu++; break;
                                    case "7": if (myjob7.runtime >= jiankongshijian) myjob7.runcishu++; break;
                                    case "8": if (myjob8.runtime >= jiankongshijian) myjob8.runcishu++; break;
                                }
                                if (faultThisFrame)
                                    RequestIoPulse(myjob, false); // ch:P1-03 故障帧：IO 统一放 NG 脉冲（拦截），不放行
                                else
                                {
                                    if (tempout1 == shuchuqufan)
                                        RequestIoPulse(myjob, true);
                                    if (tempout2 == shuchuqufan)
                                        RequestIoPulse(myjob, false);
                                }
                            }
                            #endregion // ch:流程封
                            if (outTcp && myjob.tcp) // ch:R13 输出方式互斥闸
                            {
                                Task.Run(() =>
                                {
                                    try
                                    {
                                    #region TCP输出
                                    
                                    if (faultThisFrame)
                                        frm3.changeok("999"); // ch:P1-03 故障帧：TCP 通道发 999
                                    else
                                        frm3.changeok(snapTcp); // ch:P1-04 用检测线程快照，避免串帧
                                    #endregion
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            if ((outSerial && myjob.serial) // ch:P1 判断提到 Task.Run 外（串口关闭时不再每帧派发空 Task）；ch:R13 加输出方式互斥闸
                                || (outMode == OutModbusTcp && myjob.modbustcp && snapSerial != "" // ch:R19 mode4 数据路回退：现场 Modbus 数据在 Outputs["serial"]、自动模式实际经串口分支的寄存器写子路径(frm3.mdcan)出数，mode4 原把它关死致静默
                                && !(modbustcp.fins_en && modbustcp.chushihua)   //   配置窗活跃 → 让 4638(FormModbus) 独发
                                && !frm3.IsEnable))                               //   ch:R20 4732 闸开(IsEnable)就不碰——互斥按两组闸门输入裁决、与数据有无无关，杜绝 IsEnable 真但终端空时 4732 写零与本回退真值互覆的理论双写；三路必居其一可证明不双写。进入时 myjob.modbustcp 必真(4572 子闸)故只走寄存器写、绝不发原始串口文本
                            {
                                if (_logPathSerial) { _logPathSerial = false; MsgErroeLog.WriteLog("Modbus数据路:串口分支寄存器子路径 mode=" + _outputMode + " myjob.modbustcp=" + myjob.modbustcp); } // ch:R19 每次切模式首次触发记一条
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        #region 串口输出
                                        string[] out1temp = snapSerial.Split(','); // ch:P1-04 用检测线程快照
                                        string out1temp1 = snapSerial; // ch:P1-04/P1-03 snapSerial 已含 故障帧=999 / 正常=serial 快照
                                        int changdu_temp = 0;
                                        if (out1temp1.Contains(','))
                                        {
                                            changdu_temp = myjob.changdu;
                                        }
                                        if (!myjob.modbustcp)
                                            frm3.changeok(out1temp1);
                                        else
                                        {
                                            if (out1temp1 != "30000")
                                            {
                                                int indata;
                                                ushort add_temp = 0;
                                                ushort jinzhi = 1;
                                                if (frm3.gongnengma == "06")
                                                {
                                                    ushort.TryParse((int.Parse(frm3.xie) + int.Parse(myjob.path_number) + changdu_temp - 1).ToString(), out add_temp);

                                                }
                                                else if (frm3.gongnengma == "10")
                                                {
                                                    ushort.TryParse((int.Parse(frm3.xie) + 2 * (int.Parse(myjob.path_number) + changdu_temp - 1)).ToString(), out add_temp);
                                                    jinzhi = 2;
                                                }
                                                myjob.address = add_temp.ToString();
                                                for (ushort i = 0; i < out1temp.Length; i++)
                                                {
                                                    byte[] tempdata;
                                                    string a = "";
                                                    int.TryParse(out1temp[i], out indata);
                                                    frm3.Getint(indata, out tempdata);
                                                    if (frm3.gongnengma == "06")
                                                        frm3.mdcan.WriteRegister(1, (ushort)(add_temp + i * jinzhi), indata, out a);
                                                    else if (frm3.gongnengma == "10")
                                                        frm3.mdcan.WriteRegisters(1, (ushort)(add_temp + i * jinzhi), tempdata, out a);
                                                    // ch:后台 Task 写 frm3 控件，转 UI 线程
                                                    if (frm3.InvokeRequired)
                                                        frm3.BeginInvoke(new Action(() => frm3.texSend.Text = a));
                                                    else
                                                        frm3.texSend.Text = a;
                                                }
                                            }
                                        }
                                        #endregion
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            if (outOmron && omron.fins_en && omron.chushihua) // ch:R13 输出方式互斥闸
                            {
                                try
                                {
                                    lock (myjob.blockLock) // ch:P2-① 检测线程读 Outputs 与 Run 互斥，避免并发访问非线程安全对象
                                    {
                                        fins = faultThisFrame ? "999" : myjob.block.Outputs["fins"].Value.ToString(); // ch:P1-03 故障帧：欧姆龙通道发 999
                                    }
                                }
                                catch
                                {
                                    fins = "无";
                                }
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        // ch:R4 登记与 xie 消费由 Omron 窗体内部同一把锁保护
                                        omron.WriteCameraResult(int.Parse(myjob.path_number), fins); // ch:R4 登记+消费同一把锁，避免同一相机相邻帧覆盖 pending
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            if ((outMtcpWin || outMtcp) && modbustcp.fins_en && modbustcp.chushihua) // ch:R13 输出方式互斥闸；mode4「ModbusTCP输出」=ModbusTCP 总闸：配置窗实现与 frm3 实现(见下方 outMtcp 分支)由 fins_en&&chushihua 二选一，原只认 outMtcpWin 导致 mode4 遇配置窗活跃时双闸全灭、静默不输出
                            {
                                if (_logPathMtcpWin) { _logPathMtcpWin = false; MsgErroeLog.WriteLog("Modbus数据路:配置窗FormModbus mode=" + _outputMode); } // ch:R19 每次切模式首次触发记一条
                                try
                                {
                                    lock (myjob.blockLock) // ch:P2-① 检测线程读 Outputs 与 Run 互斥，避免并发访问非线程安全对象
                                    {
                                        modbustcps = faultThisFrame ? "999" : myjob.block.Outputs["modbustcp"].Value.ToString(); // ch:P1-03 故障帧：ModbusTCP 通道发 999
                                    }
                                }
                                catch
                                {
                                    modbustcps = "无";
                                }
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        modbustcp.WriteCameraResult(int.Parse(myjob.path_number), modbustcps);
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            if (outMrtu && modbusrtu.fins_en && modbusrtu.chushihua) // ch:R13 输出方式互斥闸
                            {
                                try
                                {
                                    lock (myjob.blockLock) // ch:P2-① 检测线程读 Outputs 与 Run 互斥，避免并发访问非线程安全对象
                                    {
                                        modbusrtus = faultThisFrame ? "999" : myjob.block.Outputs["modbusrtu"].Value.ToString(); // ch:P1-03 故障帧：ModbusRTU 通道发 999
                                    }
                                }
                                catch
                                {
                                    modbusrtus = "无";
                                }
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        // 登记与 xie_wu 消费由 RTU 窗体内部同一把锁保护，避免同一相机相邻帧覆盖 pending。
                                        modbusrtu.WriteCameraResult(int.Parse(myjob.path_number), modbusrtus);
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            lock (myjob.blockLock) // ch:P2-① 读 Inputs 与 SetBlockInputSafe 写 Inputs 互斥，避免并发访问非线程安全对象
                            {
                                tempimage = (ICogImage)myjob.block.Inputs["Input"].Value;
                            }
                            if (myjob.zidongbaoguang)
                            {
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        int camIdx = int.Parse(myjob.path_number) - 1;
                                        if (camIdx < 0 || camIdx > 7) return;
                                        string expText = "";
                                        switch (camIdx)
                                        {
                                        case 0: expText = tbExposure1.Text; break;
                                        case 1: expText = tbExposure2.Text; break;
                                        case 2: expText = tbExposure3.Text; break;
                                        case 3: expText = tbExposure4.Text; break;
                                        case 4: expText = tbExposure5.Text; break;
                                        case 5: expText = tbExposure6.Text; break;
                                        case 6: expText = tbExposure7.Text; break;
                                        case 7: expText = tbExposure8.Text; break;
                                        }
                                        float baoguang_temp = float.Parse(expText) * (255 - float.Parse(snapBuchang) / 255); // ch:P1-04 用检测线程快照
                                        m_MyCamera[camIdx].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                                        // ch:读取相机曝光范围并限制，避免补偿值超出相机允许范围导致 Set 失败
                                        MyCamera.MVCC_FLOATVALUE fval = new MyCamera.MVCC_FLOATVALUE();
                                        if (m_MyCamera[camIdx].MV_CC_GetFloatValue_NET("ExposureTime", ref fval) == MyCamera.MV_OK)
                                        {
                                        if (baoguang_temp < fval.fMin) baoguang_temp = fval.fMin;
                                        if (baoguang_temp > fval.fMax) baoguang_temp = fval.fMax;
                                        }
                                        int nRet = m_MyCamera[camIdx].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                                        if (nRet != MyCamera.MV_OK)
                                        {
                                        MsgErroeLog.WriteLog("Set Exposure Time Fail!+1+" + nRet);
                                        }
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                });
                            }
                            if (gongjujilu == 1 && (myjob.trrigerEn == true || myjob.triggerMode == "通讯触发"))
                            {
                                Task.Run(() =>
                                {
                                    gongjukuai(myjob.path_number, tempout1);
                                });
                            }
                            if (outMtcp && myjob.modbustcp && frm3.IsEnable && !(modbustcp.fins_en && modbustcp.chushihua)) // ch:R13 输出方式互斥闸
                            {
                                if (_logPathFrm3) { _logPathFrm3 = false; MsgErroeLog.WriteLog("Modbus数据路:frm3标准路(4732) mode=" + _outputMode + " snapMtcp=" + (snapMtcp == "" ? "空" : "有")); } // ch:R19 每次切模式首次触发记一条
                                Task.Run(() =>

                               {

                                   try
                                   {
                                       #region modbustcp
                                       string[] out1temp = snapMtcp.Split(','); // ch:P1-04 用检测线程快照
                                       int changdu_temp = 0;
                                       if (snapMtcp.Contains(',')) // ch:P1-04 用检测线程快照
                                       {
                                           changdu_temp = myjob.changdu;
                                       }
                                       if (frm3.modbus_style == "float")
                                       {
                                           myjob.address = (int.Parse(frm3.xie) + 2 * (int.Parse(myjob.path_number) + changdu_temp - 1)).ToString();
                                           for (int i = 0; i < out1temp.Length; i++)
                                           {
                                               float Pdata;
                                               float.TryParse(out1temp[i], out Pdata);

                                               if (!myjob.modbustemp)
                                                   myjob.modbustemp = true;

                                               object temp_pd = frm3.Getfloat(Pdata);
                                               frm3.modbus_duxie((int.Parse(frm3.xie) + 2 * (int.Parse(myjob.path_number) + changdu_temp - 1) + 2 * i).ToString(), ref temp_pd, 1);
                                               myjob.modbustemp = false;
                                           }
                                       }
                                       else if (frm3.modbus_style == "int")
                                       {
                                           int jinzhi = 1;
                                           if (frm3.gongnengma == "10")
                                           {
                                               jinzhi = 2;
                                               myjob.address = (int.Parse(frm3.xie) + 2 * (int.Parse(myjob.path_number) + changdu_temp - 1)).ToString();
                                           }
                                           else if (frm3.gongnengma == "06")
                                           {
                                               myjob.address = (int.Parse(frm3.xie) + int.Parse(myjob.path_number) + changdu_temp - 1).ToString();
                                           }
                                           for (int i = 0; i < out1temp.Length; i++)
                                           {
                                               int Pdata;
                                               short s_Pdata;
                                               byte[] tempdata;
                                               int.TryParse(out1temp[i], out Pdata);

                                               if (!myjob.modbustemp)
                                                   myjob.modbustemp = true;
                                               if (frm3.gongnengma == "10")
                                               {

                                                   object temp_pd = frm3.Getint(Pdata, out tempdata);
                                                   frm3.modbus_duxie((int.Parse(frm3.xie) + 2 * (int.Parse(myjob.path_number) + changdu_temp - 1) + jinzhi * i).ToString(), ref temp_pd, 1);
                                               }
                                               else if (frm3.gongnengma == "06")
                                               {
                                                   short.TryParse(out1temp[i], out s_Pdata);

                                                   object temp_pd = s_Pdata;
                                                   frm3.modbus_duxie((int.Parse(frm3.xie) + int.Parse(myjob.path_number) + changdu_temp - 1 + jinzhi * i).ToString(), ref temp_pd, 1);
                                               }
                                               myjob.modbustemp = false;
                                           }
                                       }
                                       #endregion
                                   }
                                   catch (Exception ex)
                                   {
                                       myjob.modbustemp = false;
                                       MsgErroeLog.WriteLog("ModbusTCP frm3分支(4732)写回异常 cam=" + myjob.path_number + ":" + ex.Message); // ch:R19 原静默吞异常，mode4 无输出时现场无法定位
                                   }
                               });
                            }
                            if (myjob.trrigerEn == true)
                            {
                                temptime = DateTime.Now.ToString("HH:mm:ss"); // ch:P1 原 ToLongTimeString() 在 en-US 等区域带 "PM"，后续 int.Parse 会抛异常导致存图静默失败
                            }
                            #region 总数计算
                            switch (myjob.path_number)
                            {
                                case "1":
                                    myjob1.trriger = 0;
                                    myjob1.sum++;

                                    break;
                                case "2":
                                    myjob2.trriger = 0;
                                    myjob2.sum++;
                                    break;
                                case "3":
                                    myjob3.trriger = 0;
                                    myjob3.sum++;

                                    break;
                                case "4":
                                    myjob4.trriger = 0;
                                    myjob4.sum++;
                                    //myjob.sum++;
                                    break;
                                case "5":
                                    myjob5.trriger = 0;
                                    myjob5.sum++;
                                    // myjob.sum++;
                                    break;
                                case "6":
                                    myjob6.trriger = 0;
                                    myjob6.sum++;
                                    // myjob.sum++;
                                    break;
                                case "7":
                                    myjob7.trriger = 0;
                                    myjob7.sum++;
                                    // myjob.sum++;
                                    break;
                                case "8":
                                    myjob8.trriger = 0;
                                    myjob8.sum++;
                                    // myjob.sum++;
                                    break;
                            }
                            #endregion
                            Task.Run(() =>
                            {
                                try
                                {


                                    if (tempout1 == "Accept") // ch:P1-04 用检测线程快照 tempout1（故障帧=Reject 不计 OK）
                                    {
                                        #region 合格数计算
                                        switch (myjob.path_number)
                                        {
                                            case "1":
                                                myjob1.oksum++;
                                                // myjob.oksum++;
                                                break;
                                            case "2":
                                                myjob2.oksum++;
                                                //myjob.oksum++;
                                                break;
                                            case "3":
                                                myjob3.oksum++;
                                                //  myjob.oksum++;
                                                break;
                                            case "4":
                                                myjob4.oksum++;
                                                // myjob.oksum++;
                                                break;
                                            case "5":
                                                myjob5.oksum++;
                                                // myjob.oksum++;
                                                break;
                                            case "6":
                                                myjob6.oksum++;
                                                // myjob.oksum++;
                                                break;
                                            case "7":
                                                myjob7.oksum++;
                                                //  myjob.oksum++;
                                                break;
                                            case "8":
                                                myjob8.oksum++;
                                                // myjob.oksum++;
                                                break;
                                        }
                                        // label_10++;                                                   
                                        #endregion

                                    }
                                    else
                                    {
                                        if (yunxing)
                                        {
                                            if (myjob.trrigerEn == true || myjob.triggerMode == "通讯触发")
                                            {
                                                #region 最近5次记录
                                                switch (myjob.path_number)
                                                {
                                                    case "1":

                                                        if (checkBox7.CheckState == CheckState.Checked)
                                                        {
                                                         
                                                            setbox5(listBox1.Items[3].ToString(), 4);
                                                            setbox5(listBox1.Items[2].ToString(), 3);
                                                            setbox5(listBox1.Items[1].ToString(), 2);
                                                            setbox5(listBox1.Items[0].ToString(), 1);
                                                            setbox5(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }
                                                        break;
                                                    case "2":
                                                        if (checkBox6.CheckState == CheckState.Checked)
                                                        {
                                                            setbox6(listBox3.Items[3].ToString(), 4);
                                                            setbox6(listBox3.Items[2].ToString(), 3);
                                                            setbox6(listBox3.Items[1].ToString(), 2);
                                                            setbox6(listBox3.Items[0].ToString(), 1);
                                                            setbox6(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }
                                                        break;
                                                    case "3":
                                                        if (checkBox14.CheckState == CheckState.Checked)
                                                        {
                                                            setbox7(listBox7.Items[3].ToString(), 4);
                                                            setbox7(listBox7.Items[2].ToString(), 3);
                                                            setbox7(listBox7.Items[1].ToString(), 2);
                                                            setbox7(listBox7.Items[0].ToString(), 1);
                                                            setbox7(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }
                                                        break;
                                                    case "4":

                                                        if (checkBox12.CheckState == CheckState.Checked)
                                                        {
                                                            setbox8(listBox6.Items[3].ToString(), 4);
                                                            setbox8(listBox6.Items[2].ToString(), 3);
                                                            setbox8(listBox6.Items[1].ToString(), 2);
                                                            setbox8(listBox6.Items[0].ToString(), 1);
                                                            setbox8(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }

                                                        break;
                                                    case "5":
                                                        if (checkBox35.CheckState == CheckState.Checked)
                                                        {
                                                            setbox10(listBox14.Items[3].ToString(), 4);
                                                            setbox10(listBox14.Items[2].ToString(), 3);
                                                            setbox10(listBox14.Items[1].ToString(), 2);
                                                            setbox10(listBox14.Items[0].ToString(), 1);
                                                            setbox10(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }

                                                        break;
                                                    case "6":

                                                        if (checkBox41.CheckState == CheckState.Checked)
                                                        {
                                                            setbox11(listBox15.Items[3].ToString(), 4);
                                                            setbox11(listBox15.Items[2].ToString(), 3);
                                                            setbox11(listBox15.Items[1].ToString(), 2);
                                                            setbox11(listBox15.Items[0].ToString(), 1);
                                                            setbox11(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }

                                                        break;
                                                    case "7":

                                                        if (checkBox55.CheckState == CheckState.Checked)
                                                        {
                                                            setbox12(listBox18.Items[3].ToString(), 4);
                                                            setbox12(listBox18.Items[2].ToString(), 3);
                                                            setbox12(listBox18.Items[1].ToString(), 2);
                                                            setbox12(listBox18.Items[0].ToString(), 1);
                                                            setbox12(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }

                                                        break;
                                                    case "8":

                                                        if (checkBox53.CheckState == CheckState.Checked)
                                                        {
                                                            setbox13(listBox17.Items[3].ToString(), 4);
                                                            setbox13(listBox17.Items[2].ToString(), 3);
                                                            setbox13(listBox17.Items[1].ToString(), 2);
                                                            setbox13(listBox17.Items[0].ToString(), 1);
                                                            setbox13(temptime + ":" + cuowu1 + ":" + jobnumber, 0);
                                                        }

                                                        break;
                                                }
                                                #endregion
                                                #region 界面表格统计
                                                switch (myjob.path_number)
                                                {
                                                    case "1":
                                                        if (checkBox11.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d1, myjob1.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "2":
                                                        if (checkBox8.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d2, myjob2.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "3":
                                                        if (checkBox56.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d3, myjob3.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "4":
                                                        if (checkBox57.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d4, myjob4.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "5":
                                                        if (checkBox58.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d5, myjob5.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "6":
                                                        if (checkBox59.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d6, myjob6.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "7":
                                                        if (checkBox60.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d7, myjob7.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                    case "8":
                                                        if (checkBox61.CheckState == CheckState.Checked)
                                                        {
                                                            this.BeginInvoke(new Action(() =>
                                                            {
                                                                UpdateDefectTable(d8, myjob8.myTable, cuowu1); // ch:R13 增量更新：原地改计数/追加，去掉 Visible 开关与 Rows.Clear 全量重建
                                                            }));
                                                        }
                                                        break;
                                                }
                                            }
                                            #endregion
                                            myjob.out_end = 8;
                                        }
                                    }
                                    #region 合格率计算

                                    switch (myjob.path_number)
                                    {
                                        case "1":
                                            myjob1.rate = myjob1.oksum * 1.000f / myjob1.sum;

                                            break;
                                        case "2":
                                            myjob2.rate = myjob2.oksum * 1.000f / myjob2.sum;
                                            break;
                                        case "3":
                                            myjob3.rate = myjob3.oksum * 1.000f / myjob3.sum;
                                            break;
                                        case "4":
                                            myjob4.rate = myjob4.oksum * 1.000f / myjob4.sum;
                                            break;
                                        case "5":
                                            myjob5.rate = myjob5.oksum * 1.000f / myjob5.sum;
                                            break;
                                        case "6":
                                            myjob6.rate = myjob6.oksum * 1.000f / myjob6.sum;
                                            break;
                                        case "7":
                                            myjob7.rate = myjob7.oksum * 1.000f / myjob7.sum;
                                            break;
                                        case "8":
                                            myjob8.rate = myjob8.oksum * 1.000f / myjob8.sum;
                                            break;
                                    }
                                    #endregion


                                }
                                catch (Exception ex)
                                {

                                    // IO 卡输出已移除（原 IOC0640 调用）
                                    MsgErroeLog.WriteLog("统计" + ex.Message + "相机" + myjob.path_number);
                                };
                            });
                            if (yunxing || !yunxing)
                            {
                                Task.Run(() =>
                                {
                                    #region 统计
                                    if (datajilu == 1)
                                    {
                                        try
                                        {
                                            if (myjob.trrigerEn == true || myjob.triggerMode == "通讯触发")
                                            {
                                                // ch:P2-19 用本 Task 局部快照替代共享字段 myjob.tianbiao 接力：
                                                //   同相机两帧 Task 并发时，后写会覆盖前者未处理完的行，导致 N 帧行记成 N+1 帧数据
                                                string csvLine = jiSnapshot;
                                                bool monthCounted = false; // ch:R10-1 月计数是否已落账，回退路径据此防双计
                                                try
                                                {
                                                    if (tempout1 == "Accept")
                                                    {
                                                        runlog1(1, 0, myjob.path_number);
                                                        monthCounted = true;
                                                        if (csvLine.Contains(","))
                                                        {
                                                            csvLine = csvLine.Remove(csvLine.Length - 1, 1);
                                                            runlog2(csvLine, csvLine, myjob.path_number, 1, myjob.biaotou);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        runlog1(0, 1, myjob.path_number);
                                                        monthCounted = true;
                                                        if (csvLine.Contains(","))
                                                        {
                                                            csvLine = csvLine.Remove(csvLine.Length - 1, 1);
                                                            runlog2(csvLine, csvLine, myjob.path_number, 0, myjob.biaotou);
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    MsgErroeLog.WriteLog(ex.Message + "相机" + myjob.path_number + "-22");
                                                    try
                                                    {
                                                        // ch:R10-1 建目录/表头改用本相机 myjob.path_number/biaotou（原硬编码 myjob1，相机2~8 记录失败时数据被建到相机1目录）
                                                        runLog.CreateDirectoryCsvPath(myjob.path_number);
                                                        runLog.CreateCsvPath(myjob.path_number, myjob.biaotou);
                                                        if (tempout1 == "Accept")
                                                        {
                                                            if (!monthCounted) runlog1(1, 0, myjob.path_number); // ch:R10-1 主路径已计数则不重复
                                                            if (csvLine.Contains(","))
                                                                runlog2(csvLine, csvLine, myjob.path_number, 1, myjob.biaotou);
                                                        }
                                                        else
                                                        {
                                                            if (!monthCounted) runlog1(0, 1, myjob.path_number);
                                                            if (csvLine.Contains(","))
                                                                runlog2(csvLine, csvLine, myjob.path_number, 0, myjob.biaotou);
                                                        }
                                                    }
                                                    catch (Exception ex2) { MsgErroeLog.WriteLog("异常:" + ex2.Message); }
                                                };
                                            }
                                        }
                                        catch (Exception ex) { MsgErroeLog.WriteLog("统计外层:" + ex.Message + "相机" + myjob.path_number); };
                                    }
                                    #endregion

                                });
                            }
                            if (myjob.xuanran && !myjob.roi && !runError)
                            {
                                if (!IsCameraDisplayBusy(myjob))
                                {
                                    myjob.jiasu = true;
                                    if (displayRawImage)
                                    {
                                        PictureBox disp = GetCameraPictureBox(myjob.path_number);
                                        ICogImage showImg = tempimage;
                                        if (showImg == null)
                                        {
                                            lock (myjob.blockLock) // ch:P2 Inputs 读同样需与通讯线程 SetBlockInputSafe 写串行（本轮补漏）
                                            {
                                                if (myjob.block.Inputs.Contains("Input"))
                                                    showImg = myjob.block.Inputs["Input"].Value as ICogImage;
                                            }
                                        }
                                        if (showImg != null)
                                            QueueRawImageDisplay(disp, showImg, myjob);
                                        else
                                            myjob.jiasu = false;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            lock (myjob.blockLock) // ch:P2 CreateLastRunRecord 读 block 内部状态，需与通讯线程 Inputs 写串行（本轮补漏）
                                            {
                                                temprecord = myjob.block.CreateLastRunRecord().SubRecords[0];
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            myjob.jiasu = false;
                                            temprecord = null;
                                            MsgErroeLog.WriteLog("CreateLastRunRecord:" + ex.Message);
                                        }
                                        if (temprecord != null)
                                            QueueRecordDisplay(temprecord, myjob);
                                        else
                                            myjob.jiasu = false;
                                    }
                                }
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        int quexian_ = int.Parse(myjob.path_number) - 1;
                                        if (f9[quexian_] != null)
                                        {
                                            if (f9[quexian_].Inspect1 != null)
                                            {
                                                if (f9[quexian_].input_tu == null)
                                                {
                                                    f9[quexian_].input_tu = f9[quexian_].Inspect1.CreateCurrentRecord().SubRecords["TrainedPatternImage"];
                                                    f9[quexian_].trian_tu = f9[quexian_].Inspect1.CreateLastRunRecord().SubRecords[f9[quexian_].comboBox1.Text];
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        MsgErroeLog.WriteLog("缺陷:" + ex.Message);
                                    }
                                });
                            }
                            if ((myjob.cuntu || cuntu == 1) && yunxing)
                            {
                                Task.Run(() =>
                                {
                                    if (myjob.trrigerEn == true || myjob.triggerMode == "通讯触发" || cuntu == 1)
                                    {
                                        try
                                        {
                                            if (!myjob.fileok.Directory.Exists)
                                            {
                                                try { Directory.CreateDirectory(myjob.pathhead_ok + day1 + "\\"); }
                                                catch (Exception ex) { WarnSaveDirOnce(ex); }
                                            }
                                            if (!myjob.fileng.Directory.Exists)
                                            {
                                                try { Directory.CreateDirectory(myjob.pathhead_ng + day1 + "\\"); }
                                                catch (Exception ex) { WarnSaveDirOnce(ex); }
                                            }
                                            #region 存OK图
                                            if (myjob.cunok)
                                            {
                                                if (tempout1 == "Accept")//1存图
                                                {
                                                    switch (myjob.path_number)
                                                    {
                                                        case "1":
                                                            if (myjob1.ok1 == 0)
                                                            {
                                                                myjob1.ok1 = 1;
                                                                cuntu_fangfa(tempfilebmp, DirCount(info1ok), jobnumber, myjob1.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob1.ok1 = 0;
                                                            }
                                                            break;
                                                        case "2":
                                                            if (myjob2.ok1 == 0)
                                                            {
                                                                myjob2.ok1 = 1;
                                                                cuntu_fangfa(tempfilebmp, DirCount(info2ok), jobnumber, myjob2.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob2.ok1 = 0;
                                                            }
                                                            break;
                                                        case "3":
                                                            if (myjob3.ok1 == 0)
                                                            {
                                                                myjob3.ok1 = 1;
                                                                cuntu_fangfa(tempfilebmp, DirCount(info3ok), jobnumber, myjob3.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob3.ok1 = 0;
                                                            }
                                                            break;
                                                        case "4":
                                                            if (myjob4.ok1 == 0)
                                                            {
                                                                myjob4.ok1 = 1;
                                                                cuntu_fangfa(tempfilebmp, DirCount(info4ok), jobnumber, myjob4.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob4.ok1 = 0;
                                                            }
                                                            break;
                                                        case "5":
                                                            if (myjob5.ok1 == 0)
                                                            {
                                                                myjob5.ok1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info5ok), jobnumber, myjob5.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob5.ok1 = 0;
                                                            }
                                                            break;
                                                        case "6":
                                                            if (myjob6.ok1 == 0)
                                                            {
                                                                myjob6.ok1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info6ok), jobnumber, myjob6.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob6.ok1 = 0;
                                                            }
                                                            break;
                                                        case "7":
                                                            if (myjob7.ok1 == 0)
                                                            {
                                                                myjob7.ok1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info7ok), jobnumber, myjob7.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob7.ok1 = 0;
                                                            }
                                                            break;
                                                        case "8":
                                                            if (myjob8.ok1 == 0)
                                                            {
                                                                myjob8.ok1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info8ok), jobnumber, myjob8.pathhead_ok, "OK", temptime, tempimage);
                                                                myjob8.ok1 = 0;
                                                            }
                                                            break;
                                                    }
                                                }
                                            }
                                            #endregion
                                            #region 存NG图
                                            if (myjob.cunng)
                                            {
                                                if (tempout1 != "Accept")//1存图
                                                {
                                                    switch (myjob.path_number)
                                                    {
                                                        case "1":
                                                            if (myjob1.ng1 == 0)
                                                            {
                                                                myjob1.ng1 = 1;
                                                                cuntu_fangfa(tempfilebmp, DirCount(info1ng), jobnumber, myjob1.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob1.ng1 = 0;
                                                            };
                                                            break;
                                                        case "2":
                                                            // ch:P1-12 原实现多包了一层 myjob1.ng1 门（复制粘贴错位）：相机1存图标志会吞掉相机2的 NG 图
                                                            if (myjob2.ng1 == 0)
                                                            {
                                                                myjob2.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info2ng), jobnumber, myjob2.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob2.ng1 = 0;
                                                            };
                                                            break;
                                                        case "3":
                                                            if (myjob3.ng1 == 0)
                                                            {
                                                                myjob3.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info3ng), jobnumber, myjob3.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob3.ng1 = 0;
                                                            };
                                                            break;
                                                        case "4":
                                                            if (myjob4.ng1 == 0)
                                                            {
                                                                myjob4.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info4ng), jobnumber, myjob4.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob4.ng1 = 0;
                                                            };
                                                            break;
                                                        case "5":
                                                            if (myjob5.ng1 == 0)
                                                            {
                                                                myjob5.ng1 = 1;


                                                                cuntu_fangfa(tempfilebmp, DirCount(info5ng), jobnumber, myjob5.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob5.ng1 = 0;
                                                            };
                                                            break;
                                                        case "6":
                                                            if (myjob6.ng1 == 0)
                                                            {
                                                                myjob6.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info6ng), jobnumber, myjob6.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob6.ng1 = 0;
                                                            };
                                                            break;
                                                        case "7":
                                                            if (myjob7.ng1 == 0)
                                                            {
                                                                myjob7.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info7ng), jobnumber, myjob7.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob7.ng1 = 0;
                                                            };
                                                            break;
                                                        case "8":
                                                            if (myjob8.ng1 == 0)
                                                            {
                                                                myjob8.ng1 = 1;

                                                                cuntu_fangfa(tempfilebmp, DirCount(info8ng), jobnumber, myjob8.pathhead_ng, cuowu1, temptime, tempimage);
                                                                myjob8.ng1 = 0;
                                                            };
                                                            break;
                                                    }
                                                }
                                            }
                                            #endregion
                                        }
                                        catch (Exception ex)
                                        {
                                            myjob1.ng1 = 0;
                                            myjob2.ng1 = 0;
                                            myjob3.ng1 = 0;
                                            myjob4.ng1 = 0;
                                            myjob1.ok1 = 0;
                                            myjob2.ok1 = 0;
                                            myjob3.ok1 = 0;
                                            myjob4.ok1 = 0;
                                            myjob5.ng1 = 0;
                                            myjob6.ng1 = 0;
                                            myjob7.ng1 = 0;
                                            myjob8.ng1 = 0;
                                            myjob5.ok1 = 0;
                                            myjob6.ok1 = 0;
                                            myjob7.ok1 = 0;
                                            myjob8.ok1 = 0;
                                            MsgErroeLog.WriteLog(ex.Message + "相机" + myjob.path_number + "存图");
                                        };
                                    }
                                });
                            }

                        }
                        else
                        {
                            // switch (myjob.path_number)
                            //  {
                            // case "1":
                            if (!myjob.trrigerEn)
                            {
                                myjob1.danwu_cishu++;
                                this.BeginInvoke(new Action(() =>
                                {
                                    label133.Text = myjob1.danwu_cishu.ToString();
                                }));
                            }
                            //   break;
                            // }

                        }
                        // Thread.Sleep(1);
                    }
                    else
                    {
                        ReleaseBmpByJob(myjob);
                    }
                }
                else
                {
                    ReleaseBmpByJob(myjob);
                }
            }
            catch (Exception ex)
            {
                myjob.trriger = 0;
                ReleaseBmpByJob(myjob);
                MsgErroeLog.WriteLog(ex.Message + "相机" + myjob.path_number);
            };
        }

        public int zhuanhuan = 0;
        private void UI_monitor()
        {
            while (!closing)
            {
                Thread.Sleep(35);
                if (zhuanhuan == 0)
                {
                    zhuanhuan = 1;
                    if (tongji == 1 && !closing && this.IsHandleCreated)
                    {

                        if (!this.IsHandleCreated || this.IsDisposed) return; // ch:窗体销毁中跳过刷新
                        this.BeginInvoke(new EventHandler(delegate {
                            try
                            {
                                // ch:切换方案期间跳过统计刷新（listBox2 正在 Clear/重建），避免 Items 索引越界
                                if (Volatile.Read(ref qiehuanzhong) == 1) return; // ch:P2 原子读（UI 线程，防陈旧值）
                                if (myjob1.triggerMode != "连续运行")
                                {
                                    listBox2.Items[1] = "检测数:" + myjob1.sum.ToString();
                                    listBox2.Items[2] = "OK数:" + myjob1.oksum.ToString();
                                    listBox2.Items[3] = "NG数:" + (myjob1.sum - myjob1.oksum).ToString();
                                    listBox2.Items[4] = "合格率:" + myjob1.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 1 && myjob2.triggerMode != "连续运行")
                                {
                                    listBox2.Items[7] = "检测数:" + myjob2.sum.ToString();
                                    listBox2.Items[8] = "OK数:" + myjob2.oksum.ToString();
                                    listBox2.Items[9] = "NG数:" + (myjob2.sum - myjob2.oksum).ToString();
                                    listBox2.Items[10] = "合格率:" + myjob2.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 2 && myjob3.triggerMode != "连续运行")
                                {
                                    listBox2.Items[13] = "检测数:" + myjob3.sum.ToString();
                                    listBox2.Items[14] = "OK数:" + myjob3.oksum.ToString();
                                    listBox2.Items[15] = "NG数:" + (myjob3.sum - myjob3.oksum).ToString();
                                    listBox2.Items[16] = "合格率:" + myjob3.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 3 && myjob4.triggerMode != "连续运行")
                                {
                                    listBox2.Items[19] = "检测数:" + myjob4.sum.ToString();
                                    listBox2.Items[20] = "OK数:" + myjob4.oksum.ToString();
                                    listBox2.Items[21] = "NG数:" + (myjob4.sum - myjob4.oksum).ToString();
                                    listBox2.Items[22] = "合格率:" + myjob4.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 4 && myjob5.triggerMode != "连续运行")
                                {
                                    listBox2.Items[25] = "检测数:" + myjob5.sum.ToString();
                                    listBox2.Items[26] = "OK数:" + myjob5.oksum.ToString();
                                    listBox2.Items[27] = "NG数:" + (myjob5.sum - myjob5.oksum).ToString();
                                    listBox2.Items[28] = "合格率:" + myjob5.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 5 && myjob6.triggerMode != "连续运行")
                                {
                                    listBox2.Items[31] = "检测数:" + myjob6.sum.ToString();
                                    listBox2.Items[32] = "OK数:" + myjob6.oksum.ToString();
                                    listBox2.Items[33] = "NG数:" + (myjob6.sum - myjob6.oksum).ToString();
                                    listBox2.Items[34] = "合格率:" + myjob6.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 6 && myjob7.triggerMode != "连续运行")
                                {
                                    listBox2.Items[37] = "检测数:" + myjob7.sum.ToString();
                                    listBox2.Items[38] = "OK数:" + myjob7.oksum.ToString();
                                    listBox2.Items[39] = "NG数:" + (myjob7.sum - myjob7.oksum).ToString();
                                    listBox2.Items[40] = "合格率:" + myjob7.rate.ToString("F3");
                                }
                                if (manager1.JobCount > 7 && myjob8.triggerMode != "连续运行")
                                {
                                    listBox2.Items[43] = "检测数:" + myjob8.sum.ToString();
                                    listBox2.Items[44] = "OK数:" + myjob8.oksum.ToString();
                                    listBox2.Items[45] = "NG数:" + (myjob8.sum - myjob8.oksum).ToString();
                                    listBox2.Items[46] = "合格率:" + myjob8.rate.ToString("F3");
                                }
                            }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                        }));
                    }
                }
                else if (!closing && this.IsHandleCreated)
                {
                    zhuanhuan = 0;

                    this.BeginInvoke(new EventHandler(delegate {
                        if (tongji == 1)
                        {
                            if (myjob1.shijianEn && m_nCanOpenDeviceNum > 0)
                            {
                                label153.Text = myjob1.outputok2.ToString();
                                // label71.Text = myjob1.outputok.ToString();
                                label85.Text = zhen;
                                label86.Text = GetLostFrame(0);
                                label134.Text = (m_nFrames[0] - myjob1.sum).ToString();
                            }
                            if (myjob2.shijianEn && m_nCanOpenDeviceNum > 1)
                            {
                                label62.Text = myjob2.address;
                                label52.Text = GetLostFrame(1);
                                label49.Text = (m_nFrames[1] - myjob2.sum).ToString();
                            }
                            if (myjob3.shijianEn && m_nCanOpenDeviceNum > 2)
                            {
                                label23.Text = myjob3.address;
                                label53.Text = GetLostFrame(2);
                                label50.Text = (m_nFrames[2] - myjob3.sum).ToString();
                            }
                            if (myjob4.shijianEn && m_nCanOpenDeviceNum > 3)
                            {
                                label16.Text = myjob4.address;
                                label54.Text = GetLostFrame(3);
                                label51.Text = (m_nFrames[3] - myjob4.sum).ToString();
                            }
                            if (myjob5.shijianEn && m_nCanOpenDeviceNum > 4)
                            {
                                label93.Text = myjob5.address;
                                label90.Text = GetLostFrame(4);
                                label135.Text = (m_nFrames[4] - myjob5.sum).ToString();
                            }
                            if (myjob6.shijianEn && m_nCanOpenDeviceNum > 5)
                            {
                                label103.Text = myjob6.address;
                                label100.Text = GetLostFrame(5);
                                label136.Text = (m_nFrames[5] - myjob6.sum).ToString();
                            }
                            if (myjob7.shijianEn && m_nCanOpenDeviceNum > 6)
                            {
                                label114.Text = myjob7.address;
                                label110.Text = GetLostFrame(6);
                                label137.Text = (m_nFrames[6] - myjob7.sum).ToString();
                            }
                            if (myjob8.shijianEn && m_nCanOpenDeviceNum > 7)
                            {
                                label124.Text = myjob8.address;
                                label121.Text = GetLostFrame(7);
                                label138.Text = (m_nFrames[7] - myjob8.sum).ToString();
                            }
                            label74.Text = cameraState;
                            label77.Text = daoqi;
                            try
                            {
                                label14.Text = frm3.monitor;
                                label15.Text = myjob1.runtime.ToString("F2");
                                label24.Text = myjob2.runtime.ToString("F2");
                                label31.Text = myjob3.runtime.ToString("F2");
                                label39.Text = myjob4.runtime.ToString("F2");
                                label8.Text = myjob1.runtime.ToString("F2");
                                label19.Text = myjob2.runtime.ToString("F2");
                                label30.Text = myjob3.runtime.ToString("F2");
                                label29.Text = myjob4.runtime.ToString("F2");
                                label128.Text = myjob5.runtime.ToString("F2");
                                label129.Text = myjob6.runtime.ToString("F2");
                                label132.Text = myjob7.runtime.ToString("F2");
                                label131.Text = myjob8.runtime.ToString("F2");
                                label58.Text = myjob1.runcishu.ToString();
                                label59.Text = myjob2.runcishu.ToString();
                                label60.Text = myjob3.runcishu.ToString();
                                label61.Text = myjob4.runcishu.ToString();
                            }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                        }
                        if (yunxing == true)
                        {
                            checkBox24.Enabled = false;
                            checkBox23.Enabled = false;
                            checkBox21.Enabled = false;
                            checkBox17.Enabled = false;
                            checkBox32.Enabled = false;
                            checkBox38.Enabled = false;
                            checkBox44.Enabled = false;
                            checkBox50.Enabled = false;
                            打开ToolStripMenuItem.Enabled = false;
                            保存ToolStripMenuItem.Enabled = false;
                            另存为ToolStripMenuItem.Enabled = false;
                            rOI设置ToolStripMenuItem.CheckState = CheckState.Unchecked;
                        }
                        else
                        {
                            checkBox24.Enabled = true;
                            checkBox23.Enabled = true;
                            checkBox21.Enabled = true;
                            checkBox17.Enabled = true;
                            checkBox32.Enabled = true;
                            checkBox38.Enabled = true;
                            checkBox44.Enabled = true;
                            checkBox50.Enabled = true;
                            打开ToolStripMenuItem.Enabled = true;
                            保存ToolStripMenuItem.Enabled = true;
                            另存为ToolStripMenuItem.Enabled = true;
                        }
                        if (frm5.mark == 1)
                        {
                            设置ToolStripMenuItem.Enabled = true;
                            numericUpDown22.Enabled = true;

                            button18.Enabled = true;
                            button31.Enabled = true;
                            button19.Enabled = true;
                            button33.Enabled = true;
                            button34.Enabled = true;
                            button21.Enabled = true;
                            button28.Enabled = true;
                            button29.Enabled = true;
                            button30.Enabled = true;
                        }
                        else
                        {
                            设置ToolStripMenuItem.Enabled = false;
                            numericUpDown22.Enabled = false;
                            button18.Enabled = false;
                            button31.Enabled = false;
                            button19.Enabled = false;
                            button33.Enabled = false;
                            button34.Enabled = false;
                            textBox3.Enabled = false;
                            comboBox1.Enabled = false;
                            comboBox4.Enabled = false;
                            comboBox5.Enabled = false;
                            comboBox8.Enabled = false;
                            comboBox25.Enabled = false;
                            comboBox28.Enabled = false;
                            comboBox31.Enabled = false;
                            comboBox34.Enabled = false;
                            button21.Enabled = false;
                            button28.Enabled = false;
                            button29.Enabled = false;
                            button30.Enabled = false;
                            触发设置ToolStripMenuItem.Checked = false;
                            输出时间ToolStripMenuItem.Checked = false;
                            存图测试1ToolStripMenuItem.Checked = false;

                            rOI设置ToolStripMenuItem.Checked = false;
                        }
                    }));
                }
            }
        }
        private object locker_1 = new object();
        private object locker_2 = new object();
        private object locker_3 = new object();
        private object locker_4 = new object();
        private object locker_5 = new object();
        private object locker_6 = new object();
        private object locker_7 = new object();
        private object locker_8 = new object();
        private object locker = new object();
        private object locker1 = new object();
        private object locker2 = new object();
        private object locker3 = new object();
        private object locker4 = new object();
        private object locker5 = new object();
        private object locker6 = new object();
        private object locker7 = new object();
        private object locker8 = new object();
        private object lockern = new object();
        private object locker1ng = new object();

        private void runlog1(int a, int b, string path)
        {
            lock (locker)
            {
                runLog.WriteDate(a, b, path);

            }
        }

        // ch:R11-1 biaotou 用于跨天/冷启动新建日文件时补写表头（与 CreateCsvPath 同格式）
        private void runlog2(string a, string b, string path, int c, string biaotou = null)
        {
            lock (lockern)
            {
                runLog.WriteDate1(b, path, c, biaotou);

            }
        }
        private volatile bool _ioPulseEnabled = true;

        // ch:P1-③ 启动 IO 工作线程（幂等）
        private void EnsureIoWorker()
        {
            if (_ioWorkerThread != null && _ioWorkerThread.IsAlive)
                return;
            _ioWorkerThread = new Thread(IoWorkerLoop)
            {
                IsBackground = true,
                Name = "CameraIoWorker"
            };
            _ioWorkerThread.Start();
        }

        // ch:R6 待完成任务计数：在入队前登记，覆盖“已出队但尚未开始执行”的窗口；FlushIoWork 以其为排空条件。
        private int _ioWorkOutstanding = 0;
        private void IoWorkerLoop()
        {
            try
            {
                foreach (Action act in _ioWorkQueue.GetConsumingEnumerable())
                {
                    try { act(); }
                    catch (Exception ex) { MsgErroeLog.WriteLog("IO输出异常:" + ex.Message); }
                    finally
                    {
                        System.Threading.Interlocked.Decrement(ref _ioWorkOutstanding);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("IO工作线程退出异常:" + ex.Message); }
        }

        // ch:P1-③ 入队串行执行相机 IO；队列未建立时惰性启动线程
        private void EnqueueIoWork(Action act)
        {
            if (act == null)
                return;
            EnsureIoWorker();
            try
            {
                if (!_ioWorkQueue.IsAddingCompleted)
                {
                    // 先登记再入队，避免消费者已取走任务但尚未递增 executing 时 Flush 误判为空。
                    System.Threading.Interlocked.Increment(ref _ioWorkOutstanding);
                    try
                    {
                        _ioWorkQueue.Add(act);
                    }
                    catch
                    {
                        System.Threading.Interlocked.Decrement(ref _ioWorkOutstanding);
                        throw;
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("IO入队异常:" + ex.Message); }
        }

        // ch:P1-③ 退出前排空 IO 队列，确保脉冲关断（拉低）真正下发，避免残留输出
        // ch:R6 排空必须等"队列空 AND 无正在执行的最后一项"；旧实现只查队列 Count，工作线程取走最后一项未执行完就返回
        private void FlushIoWork(int timeoutMs)
        {
            if (_ioWorkerThread == null)
                return;
            Stopwatch sw = Stopwatch.StartNew();
            int outstanding = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                outstanding = System.Threading.Interlocked.CompareExchange(ref _ioWorkOutstanding, 0, 0);
                if (outstanding == 0)
                    return;
                Thread.Sleep(5);
            }
            MsgErroeLog.WriteLog("P1-07 关闭：IO 队列排空超时(" + timeoutMs + "ms)，剩余未完成项=" + outstanding + "，可能残留输出");
        }

        private void RequestIoPulse(Myjob job, bool okLine)
        {
            if (job == null || !_ioPulseEnabled)
                return;
            int width = job.timespace;
            if (width < 1)
                width = 1;
            int until = Environment.TickCount + width;
            // ch:P1-07 「改 ioXHigh 状态 + 入队置高」在同一把 _ioPulseLock 内原子化；旧实现锁内改状态、锁外 EnqueueIoWork，
            //   开启线程置高尚未入队时到期线程可先入队关断，FIFO 变 [关断,置高] → 最终卡高电平且不再关断。锁内入队后单调有序。
            lock (_ioPulseLock)
            {
                if (!_ioPulseEnabled)
                    return;
                if (okLine)
                {
                    job.ioOkUntil = until;
                    if (!job.ioOkHigh)
                    {
                        job.ioOkHigh = true;
                        EnqueueIoWork(() => SetCameraIoLine(job, true, true));
                    }
                }
                else
                {
                    job.ioNgUntil = until;
                    if (!job.ioNgHigh)
                    {
                        job.ioNgHigh = true;
                        EnqueueIoWork(() => SetCameraIoLine(job, false, true));
                    }
                }
            }
            EnsureIoPulseTimer();
        }

        private void EnsureIoPulseTimer()
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            Action start = delegate
            {
                if (_ioPulseTimer == null)
                {
                    _ioPulseTimer = new System.Windows.Forms.Timer();
                    _ioPulseTimer.Interval = 20;
                    _ioPulseTimer.Tick += delegate { ExpireIoPulses(); };
                }
                if (!_ioPulseTimer.Enabled)
                    _ioPulseTimer.Start();
            };
            if (InvokeRequired)
            {
                try { BeginInvoke(start); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
            else
                start();
        }

        private void ExpireIoPulses()
        {
            if (!_ioPulseEnabled)
                return;
            int now = Environment.TickCount;
            ExpireOneIoPulse(myjob1, now);
            ExpireOneIoPulse(myjob2, now);
            ExpireOneIoPulse(myjob3, now);
            ExpireOneIoPulse(myjob4, now);
            ExpireOneIoPulse(myjob5, now);
            ExpireOneIoPulse(myjob6, now);
            ExpireOneIoPulse(myjob7, now);
            ExpireOneIoPulse(myjob8, now);
            bool any = false;
            lock (_ioPulseLock)
            {
                any = (myjob1 != null && (myjob1.ioOkHigh || myjob1.ioNgHigh))
                    || (myjob2 != null && (myjob2.ioOkHigh || myjob2.ioNgHigh))
                    || (myjob3 != null && (myjob3.ioOkHigh || myjob3.ioNgHigh))
                    || (myjob4 != null && (myjob4.ioOkHigh || myjob4.ioNgHigh))
                    || (myjob5 != null && (myjob5.ioOkHigh || myjob5.ioNgHigh))
                    || (myjob6 != null && (myjob6.ioOkHigh || myjob6.ioNgHigh))
                    || (myjob7 != null && (myjob7.ioOkHigh || myjob7.ioNgHigh))
                    || (myjob8 != null && (myjob8.ioOkHigh || myjob8.ioNgHigh));
            }
            if (!any && _ioPulseTimer != null)
                _ioPulseTimer.Stop();
        }

        private void ExpireOneIoPulse(Myjob job, int now)
        {
            if (job == null)
                return;
            // ch:P1-07 「改状态 + 入队关断」在 _ioPulseLock 内原子化，与开启侧保持 FIFO 单调
            lock (_ioPulseLock)
            {
                if (job.ioOkHigh && job.ioOkUntil != 0 && unchecked(now - job.ioOkUntil) >= 0)
                {
                    job.ioOkHigh = false;
                    job.ioOkUntil = 0;
                    EnqueueIoWork(() => SetCameraIoLine(job, true, false));
                }
                if (job.ioNgHigh && job.ioNgUntil != 0 && unchecked(now - job.ioNgUntil) >= 0)
                {
                    job.ioNgHigh = false;
                    job.ioNgUntil = 0;
                    EnqueueIoWork(() => SetCameraIoLine(job, false, false));
                }
            }
        }

        private void StopAllIoPulses()
        {
            _ioPulseEnabled = false;
            if (_ioPulseTimer != null)
            {
                try { _ioPulseTimer.Stop(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
            StopOneIoPulse(myjob1);
            StopOneIoPulse(myjob2);
            StopOneIoPulse(myjob3);
            StopOneIoPulse(myjob4);
            StopOneIoPulse(myjob5);
            StopOneIoPulse(myjob6);
            StopOneIoPulse(myjob7);
            StopOneIoPulse(myjob8);
        }

        private void StopOneIoPulse(Myjob job)
        {
            if (job == null)
                return;
            // ch:P1-07 关断入队移入 _ioPulseLock 内，原子化状态与入队
            lock (_ioPulseLock)
            {
                job.ioOkUntil = 0;
                job.ioNgUntil = 0;
                if (job.ioOkHigh)
                {
                    job.ioOkHigh = false;
                    EnqueueIoWork(() => SetCameraIoLine(job, true, false));
                }
                if (job.ioNgHigh)
                {
                    job.ioNgHigh = false;
                    EnqueueIoWork(() => SetCameraIoLine(job, false, false));
                }
            }
        }

        private void SetCameraIoLine(Myjob job, bool okLine, bool high)
        {
            if (job == null)
                return;
            uint line = okLine ? 1u : 2u;
            switch (job.path_number)
            {
                case "1": output_camera(line, high); break;
                case "2": output_camera2(line, high); break;
                case "3": output_camera3(line, high); break;
                case "4": output_camera4(line, high); break;
                case "5": output_camera5(line, high); break;
                case "6": output_camera6(line, high); break;
                case "7": output_camera7(line, high); break;
                case "8": output_camera8(line, high); break;
            }
        }

        private void output_camera(uint a, Boolean b)
        {
            lock (locker1)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[0] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet;
                    nRet = m_MyCamera[0].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机1 LineSelector=" + a + " 错误码0x" + ((uint)nRet).ToString("X8"));
                        return;
                    }
                    nRet = m_MyCamera[0].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机1 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("IOO1~~~~~~~~~" + b + ex.Message);
                }

            }
        }
        private void output_camera1ng(uint a, Boolean b)
        {
            lock (locker1ng)
            {
                try
                {
                    if (m_MyCamera[0] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet = m_MyCamera[0].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机1NG LineSelector=" + a + " 错误码0x" + ((uint)nRet).ToString("X8"));
                        return;
                    }
                    nRet = m_MyCamera[0].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机1NG LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

            }
        }
        private void output_camera2(uint a, Boolean b)
        {
            lock (locker2)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[1] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet2 = m_MyCamera[1].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet2)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机2 LineSelector=" + a + " 错误码0x" + ((uint)nRet2).ToString("X8"));
                        return;
                    }
                    nRet2 = m_MyCamera[1].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet2)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机2 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet2).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void output_camera3(uint a, Boolean b)
        {
            lock (locker3)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[2] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet3 = m_MyCamera[2].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet3)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机3 LineSelector=" + a + " 错误码0x" + ((uint)nRet3).ToString("X8"));
                        return;
                    }
                    nRet3 = m_MyCamera[2].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet3)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机3 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet3).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void output_camera4(uint a, Boolean b)
        {
            lock (locker4)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[3] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet4 = m_MyCamera[3].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet4)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机4 LineSelector=" + a + " 错误码0x" + ((uint)nRet4).ToString("X8"));
                        return;
                    }
                    nRet4 = m_MyCamera[3].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet4)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机4 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet4).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void output_camera5(uint a, Boolean b)
        {
            lock (locker5)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[4] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet5 = m_MyCamera[4].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet5)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机5 LineSelector=" + a + " 错误码0x" + ((uint)nRet5).ToString("X8"));
                        return;
                    }
                    nRet5 = m_MyCamera[4].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet5)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机5 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet5).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

            }
        }
        private void output_camera6(uint a, Boolean b)
        {
            lock (locker6)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[5] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet6 = m_MyCamera[5].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet6)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机6 LineSelector=" + a + " 错误码0x" + ((uint)nRet6).ToString("X8"));
                        return;
                    }
                    nRet6 = m_MyCamera[5].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet6)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机6 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet6).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void output_camera7(uint a, Boolean b)
        {
            lock (locker7)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[6] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet7 = m_MyCamera[6].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet7)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机7 LineSelector=" + a + " 错误码0x" + ((uint)nRet7).ToString("X8"));
                        return;
                    }
                    nRet7 = m_MyCamera[6].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet7)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机7 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet7).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void output_camera8(uint a, Boolean b)
        {
            lock (locker8)
            {
                if (dahua)
                    a = a - 1;
                try
                {
                    if (m_MyCamera[7] == null) return; // ch:相机未打开时跳过 IO 输出
                    int nRet8 = m_MyCamera[7].MV_CC_SetEnumValue_NET("LineSelector", a);
                    if (MyCamera.MV_OK != nRet8)
                    {
                        MsgErroeLog.WriteLog("Set Fail1!相机8 LineSelector=" + a + " 错误码0x" + ((uint)nRet8).ToString("X8"));
                        return;
                    }
                    nRet8 = m_MyCamera[7].MV_CC_SetBoolValue_NET("LineInverter", b);
                    if (MyCamera.MV_OK != nRet8)
                    {
                        MsgErroeLog.WriteLog("Set Fail2!相机8 LineSelector=" + a + " LineInverter=" + b + " 错误码0x" + ((uint)nRet8).ToString("X8"));
                        return;
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }
        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try { if (frm3 != null) frm3.CloseResources(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            // ch:兜底释放相机句柄（正常已由 bnClose_Click 释放；此处覆盖异常/未走关闭按钮的场景）。
            //   避免仅靠 Environment.Exit 强杀导致 GigE 会话残留，下次打开报 0x80000203。
            try
            {
                if (m_MyCamera != null)
                {
                    for (int i = 0; i < m_MyCamera.Length; i++)
                    {
                        if (m_MyCamera[i] == null)
                            continue;
                        try { m_MyCamera[i].MV_CC_StopGrabbing_NET(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                        try { m_MyCamera[i].Dispose(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                        m_MyCamera[i] = null;
                    }
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            // ch:兜底释放 VisionPro QuickBuild 会话（正常路径已在 FormClosing 的 finally 中执行）
            try
            {
                if (manager1 != null)
                {
                    manager1.Shutdown();
                    manager1 = null;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("Shutdown 兜底异常:" + ex.Message); }
            FlushIoWork(1500); // ch:P1-③ 最终兜底：进程退出前确保 IO 队列排空
            MsgErroeLog.WriteLog("软件退出完成");
            // ch:最终兜底：确保进程一定退出，避免残留后台线程/句柄导致相机或网络会话被占用
            System.Environment.Exit(0);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {

            if (MessageBox.Show("将要关闭检测，是否继续？", "询问", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                MsgErroeLog.WriteLog("软件关闭");
                closing = true; // ch:通知后台监控线程退出
                // ch:R14 通知配置窗停掉轮询/重连线程(lunxun_monitor、clientmonitor 原为 while(true))，
                //   否则本窗体进入退出清理时它们还在发请求/重连，与关 socket 竞争
                try { if (frm3 != null) frm3.RequestExit(); } catch { }
                // ch:R13 ErrorLog 改异步批量落盘后，退出前显式冲刷队列(ProcessExit 还会再兜一次底)，
                //   否则最后几条错误日志可能随后台线程一起丢
                try { ErrorLog.FlushPending(3000); } catch { }
                try
                {
                    if (_ocxYieldTimer != null)
                        _ocxYieldTimer.Stop();
                    if (_ioPulseTimer != null)
                        _ioPulseTimer.Stop();
                    StopAllIoPulses();
                    FlushIoWork(1500); // ch:P1-③ 退出前排空 IO 队列，确保脉冲拉低真正下发
                    if (_uiInputFilter != null)
                    {
                        Application.RemoveMessageFilter(_uiInputFilter);
                        _uiInputFilter = null;
                    }
                    timer17.Stop(); // ch:停止 IO 节拍任务，避免关闭瞬间输出竞争
                    if (timer2 != null) timer2.Stop(); // ch:停止断线重连轮询，避免关闭期间重连已关闭的相机
                    button11_Click_1(null, null);
                    // ch:输出清零（原经已删除的 IO1OK 遗留路径触发 IO），失败只记日志，不阻断后续相机释放
                    try
                    {
                        lock (myjob1.locker_ok) { myjob1.outputok = 0; myjob1.outputng = 0; myjob1.outputok2 = -1; myjob1.outputng2 = -1; }
                        lock (myjob2.locker_ok) { myjob2.outputok = 0; myjob2.outputng = 0; myjob2.outputok2 = -1; myjob2.outputng2 = -1; }
                        lock (myjob3.locker_ok) { myjob3.outputok = 0; myjob3.outputng = 0; myjob3.outputok2 = -1; myjob3.outputng2 = -1; }
                        lock (myjob4.locker_ok) { myjob4.outputok = 0; myjob4.outputng = 0; myjob4.outputok2 = -1; myjob4.outputng2 = -1; }
                        lock (myjob5.locker_ok) { myjob5.outputok = 0; myjob5.outputng = 0; myjob5.outputok2 = -1; myjob5.outputng2 = -1; }
                        lock (myjob6.locker_ok) { myjob6.outputok = 0; myjob6.outputng = 0; myjob6.outputok2 = -1; myjob6.outputng2 = -1; }
                        lock (myjob7.locker_ok) { myjob7.outputok = 0; myjob7.outputng = 0; myjob7.outputok2 = -1; myjob7.outputng2 = -1; }
                        lock (myjob8.locker_ok) { myjob8.outputok = 0; myjob8.outputng = 0; myjob8.outputok2 = -1; myjob8.outputng2 = -1; }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("关闭输出清零异常:" + ex.Message); }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("关闭流程异常:" + ex.Message);
                }
                finally
                {
                    // ch:相机释放必须执行——即使上面任何一步异常，否则相机会话残留导致下次打开报 0x80000203(无权限)
                    try { bnClose_Click(sender, e); }
                    catch (Exception ex) { MsgErroeLog.WriteLog("关闭相机异常:" + ex.Message); }
                    // ch:释放 VisionPro QuickBuild 会话（JobManager），否则关闭软件后调试会话/文件锁残留，
                    // ch:表现为“还在调试挂着”/vpp 被占用
                    try
                    {
                        if (manager1 != null)
                        {
                            manager1.Shutdown();
                            manager1 = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("Shutdown 异常:" + ex.Message);
                    }
                }

                e.Cancel = false;
            }
            else
            {
                e.Cancel = true;
            }

        }



        private void cogRecordDisplay1_MouseMove(object sender, MouseEventArgs e)
        {
            //x1 = e.X;
            //y1 = e.Y;

        }




        private void cogRecordDisplay1_Click(object sender, EventArgs e)
        {

            //try
            //{
            //    if (x1 != out_x && y1 != out_y)
            //    {
            //        ICogTransform2D myTranform1 = cogRecordDisplay1.GetTransform(cogRecordDisplay1.Image.SelectedSpaceName, ".");
            //        myTranform1.MapPoint(x1, y1, out out_x, out out_y);
            //        Color c = new Color();
            //        Bitmap bitmap = new Bitmap(cogRecordDisplay1.Image.ToBitmap());
            //        c = bitmap.GetPixel((int)out_x, (int)out_y);
            //        label6.Text = string.Format("X={0},Y={1},R={2},G={3},B={4}", out_x, out_y, c.R, c.G, c.B);
            //        bitmap.Dispose();
            //    }
            //}
            //catch (Exception ex)
            //{ MessageBox.Show(ex.Message); }; 
        }



        private delegate void ltbox2(string sum1, string ok1, string rate1, string sum2, string ok2, string rate2, string sum3, string ok3, string rate3, string sum4, string ok4, string rate4);
        private delegate void ltbox5(string aa, int bb);
        private delegate void ltbox6(string aa, int bb);
        private delegate void ltbox7(string aa, int bb);
        private delegate void ltbox8(string aa, int bb);
        private delegate void ltbox10(string aa, int bb);
        private delegate void ltbox11(string aa, int bb);
        private delegate void ltbox12(string aa, int bb);
        private delegate void ltbox13(string aa, int bb);
        private void setbox6(string aa, int bb)
        {
            if (listBox3.InvokeRequired)
            {
                ltbox6 l5 = setbox6;
                listBox3.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox3.Items.Count)
                    listBox3.Items[bb] = aa;
            }
        }
        private void setbox5(string aa, int bb)
        {
            if (listBox1.InvokeRequired)
            {
                ltbox5 l5 = setbox5;
                listBox1.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox1.Items.Count)
                    listBox1.Items[bb] = aa;
            }
        }
        private void setbox7(string aa, int bb)
        {
            if (listBox7.InvokeRequired)
            {
                ltbox7 l5 = setbox7;
                listBox7.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox7.Items.Count)
                    listBox7.Items[bb] = aa;
            }
        }
        private void setbox8(string aa, int bb)
        {
            if (listBox6.InvokeRequired)
            {
                ltbox8 l5 = setbox8;
                listBox6.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox6.Items.Count)
                    listBox6.Items[bb] = aa;
            }
        }
        private void setbox10(string aa, int bb)
        {
            if (listBox14.InvokeRequired)
            {
                ltbox10 l5 = setbox10;
                listBox14.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox14.Items.Count)
                    listBox14.Items[bb] = aa;
            }
        }
        private void setbox11(string aa, int bb)
        {
            if (listBox15.InvokeRequired)
            {
                ltbox11 l5 = setbox11;
                listBox15.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox15.Items.Count)
                    listBox15.Items[bb] = aa;
            }
        }
        private void setbox12(string aa, int bb)
        {
            if (listBox18.InvokeRequired)
            {
                ltbox12 l5 = setbox12;
                listBox18.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox18.Items.Count)
                    listBox18.Items[bb] = aa;
            }
        }
        private void setbox13(string aa, int bb)
        {
            if (listBox17.InvokeRequired)
            {
                ltbox13 l5 = setbox13;
                listBox17.BeginInvoke(l5, aa, bb); // ch:P1 改异步：调用方是检测线程同步尾部的"最近5次记录"刷新（每相机每帧 5 次），同步 Invoke 会让检测线程挂起等 UI 线程
                //   BeginInvoke 仅入队即返回，UI 卡顿不再拖住帧率；提交顺序仍由 UI 消息队列 FIFO 保证（同一相机由单一检测线程顺序提交）
            }
            else
            {
                if (bb >= 0 && bb < listBox17.Items.Count)
                    listBox17.Items[bb] = aa;
            }
        }


        private void Form1_MinimumSizeChanged(object sender, EventArgs e)
        {
            // tabControl1.Size.Width = 212;
            // tabControl1.Size.Height= 212;
            timer1.Enabled = true;

        }


        [System.Runtime.InteropServices.DllImportAttribute("user32.dll")]
        public static extern bool FlashWindow(IntPtr handle, bool bInvert);
        private void timer1_Tick(object sender, EventArgs e)
        {
            FlashWindow(this.Handle, true);
        }

        private void Form1_MaximumSizeChanged(object sender, EventArgs e)
        {
            timer1.Enabled = false;
        }

        private void Form1_Move(object sender, EventArgs e)
        {
            //if (Frm2.start == 1)
            //{
            //    Thread.Sleep(10);
            //    this.WindowState = FormWindowState.Maximized;
            //}
        }
        volatile string cameraState = "";
        private volatile bool reconnecting = false;
        private volatile bool closing = false; // ch:关闭标志，通知后台监控线程退出
        private void timer2_Tick(object sender, EventArgs e)
        {
            if (closing || reconnecting || Volatile.Read(ref qiehuanzhong) == 1) // ch:关闭/切换方案期间暂停断线检测，避免与重载并发（ch:P2 原子读）
                return;
            reconnecting = true; // ch:提前置位，消除相邻两个 400ms tick 间的竞态窗口
            Task.Run(() =>
            {
                // ch:R5 本任务在后台线程执行，断线恢复分支里 this.InvokeRequired 恒为 true，参数重设走 BeginInvoke 封送，
                //   不会在持有 _cameraLocks[i] 期间同步阻塞；else 同步分支实际不可达，仅作兜底保留。
                try
                {
                    int nRet = 1;
                    try
                    {
                    if (yunxing == true)
                    {
                        lock (_cameraLocks[0]) // ch:P3-⑧ 每相机独立锁：单相机重连期间不阻塞其它相机与 UI
                        {
                        if (myjob1.en == 1 && m_MyCamera[0] != null)
                        {
                            if (!m_MyCamera[0].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[0].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[0].MV_CC_CloseDevice_NET();
                                    m_MyCamera[0].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[0], ref device1[myjob1.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[0], 0); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[0], ref device1[myjob1.index], 1); // ch:P1-9
                                }

                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机1断线\r\n";
                                    MsgErroeLog.WriteLog("相机1断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机1重连");
                                    myjob1.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam_Click(null, null);
                                            comboBox1_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing1)
                                                bnStartGrab_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam_Click(null, null);
                                        comboBox1_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing1)
                                            bnStartGrab_Click(null, null);
                                    }
                                    myjob1.state = "";
                                }
                            }
                            else
                                cameraState = "\r\n";
                        }
                        } // ch:P3-⑧ 相机1 锁结束
                        lock (_cameraLocks[1])
                        {
                        if (myjob2.en == 1 && m_MyCamera[1] != null)
                        {
                            if (!m_MyCamera[1].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[1].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[1].MV_CC_CloseDevice_NET();
                                    m_MyCamera[1].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[1], ref device1[myjob2.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[1], 1); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[1], ref device1[myjob2.index], 2); // ch:P1-9
                                }

                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机2断线\r\n";
                                    MsgErroeLog.WriteLog("相机2断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机2重连");
                                    myjob2.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam2_Click(null, null);
                                            comboBox4_SelectedIndexChanged_1(null, null);
                                            if (yunxing && m_bGrabbing2)
                                                bnStartGrab2_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam2_Click(null, null);
                                        comboBox4_SelectedIndexChanged_1(null, null);
                                        if (yunxing && m_bGrabbing2)
                                            bnStartGrab2_Click(null, null);
                                    }
                                    myjob2.state = "";
                                }
                            }
                            else
                                cameraState += "\r\n";
                        }
                        } // ch:P3-⑧ 相机2 锁结束
                        lock (_cameraLocks[2])
                        {
                        if (myjob3.en == 1 && m_MyCamera[2] != null)
                        {
                            if (!m_MyCamera[2].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[2].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[2].MV_CC_CloseDevice_NET();
                                    m_MyCamera[2].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[2], ref device1[myjob3.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[2], 2); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[2], ref device1[myjob3.index], 3); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机3断线\r\n";
                                    MsgErroeLog.WriteLog("相机3断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机3重连");
                                    myjob3.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnGetParam3_Click(null, null);
                                            bnSetParam3_Click(null, null);
                                            comboBox5_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing3)
                                                bnStartGrab3_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnGetParam3_Click(null, null);
                                        bnSetParam3_Click(null, null);
                                        comboBox5_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing3)
                                            bnStartGrab3_Click(null, null);
                                    }
                                    myjob3.state = "";
                                }
                            }
                            else
                                cameraState += "\r\n";
                        }
                        } // ch:P3-⑧ 相机3 锁结束
                        lock (_cameraLocks[3])
                        {
                        if (myjob4.en == 1 && m_MyCamera[3] != null)
                        {
                            if (!m_MyCamera[3].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[3].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[3].MV_CC_CloseDevice_NET();
                                    m_MyCamera[3].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[3], ref device1[myjob4.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[3], 3); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[3], ref device1[myjob4.index], 4); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机4断线\r\n";
                                    MsgErroeLog.WriteLog("相机4断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机4重连");
                                    myjob4.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam4_Click(null, null);
                                            comboBox8_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing4)
                                                bnStartGrab4_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam4_Click(null, null);
                                        comboBox8_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing4)
                                            bnStartGrab4_Click(null, null);
                                    }
                                    myjob4.state = "";
                                }
                            }
                            else
                                cameraState += "";
                        }
                        } // ch:P3-⑧ 相机4 锁结束
                        lock (_cameraLocks[4])
                        {
                        if (myjob5.en == 1 && m_MyCamera[4] != null)
                        {
                            if (!m_MyCamera[4].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[4].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[4].MV_CC_CloseDevice_NET();
                                    m_MyCamera[4].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[4], ref device1[myjob5.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[4], 4); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[4], ref device1[myjob5.index], 5); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机5断线\r\n";
                                    MsgErroeLog.WriteLog("相机5断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机5重连");
                                    myjob5.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam5_Click(null, null);
                                            comboBox25_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing5)
                                                bnStartGrab5_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam5_Click(null, null);
                                        comboBox25_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing5)
                                            bnStartGrab5_Click(null, null);
                                    }
                                    myjob5.state = "";
                                }
                            }
                            else
                                cameraState += "\r\n";
                        }
                        } // ch:P3-⑧ 相机5 锁结束
                        lock (_cameraLocks[5])
                        {
                        if (myjob6.en == 1 && m_MyCamera[5] != null)
                        {
                            if (!m_MyCamera[5].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[5].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[5].MV_CC_CloseDevice_NET();
                                    m_MyCamera[5].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[5], ref device1[myjob6.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[5], 5); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[5], ref device1[myjob6.index], 6); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机6断线\r\n";
                                    MsgErroeLog.WriteLog("相机6断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机6重连");
                                    myjob6.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam6_Click(null, null);
                                            comboBox28_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing6)
                                                bnStartGrab6_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam6_Click(null, null);
                                        comboBox28_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing6)
                                            bnStartGrab6_Click(null, null);
                                    }
                                    myjob6.state = "";
                                }
                            }
                            else
                                cameraState += "\r\n";
                        }
                        } // ch:P3-⑧ 相机6 锁结束
                        lock (_cameraLocks[6])
                        {
                        if (myjob7.en == 1 && m_MyCamera[6] != null)
                        {
                            if (!m_MyCamera[6].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[6].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[6].MV_CC_CloseDevice_NET();
                                    m_MyCamera[6].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[6], ref device1[myjob7.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[6], 6); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[6], ref device1[myjob7.index], 7); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机7断线\r\n";
                                    MsgErroeLog.WriteLog("相机7断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机7重连");
                                    myjob7.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam7_Click(null, null);
                                            comboBox31_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing7)
                                                bnStartGrab7_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam7_Click(null, null);
                                        comboBox31_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing7)
                                            bnStartGrab7_Click(null, null);
                                    }
                                    myjob7.state = "";
                                }
                            }
                            else
                                cameraState += "\r\n";
                        }
                        } // ch:P3-⑧ 相机7 锁结束
                        lock (_cameraLocks[7])
                        {
                        if (myjob8.en == 1 && m_MyCamera[7] != null)
                        {
                            if (!m_MyCamera[7].MV_CC_IsDeviceConnected_NET())
                            {
                                // ch:先停止采集并关闭旧句柄，再重新打开，避免句柄冲突
                                try
                                {
                                    m_MyCamera[7].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[7].MV_CC_CloseDevice_NET();
                                    m_MyCamera[7].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                nRet = OpenDeviceWithRetry(ref m_MyCamera[7], ref device1[myjob8.index]);
                                if (MyCamera.MV_OK == nRet)
                                {
                                    RegisterImageCallBackLogged(m_MyCamera[7], 7); // ch:P2-16
                                    ApplyOptimalPacketSizeAfterReconnect(m_MyCamera[7], ref device1[myjob8.index], 8); // ch:P1-9
                                }
                                if (MyCamera.MV_OK != nRet)
                                {
                                    cameraState = Convert.ToString(nRet, 16) + "相机8断线\r\n";
                                    MsgErroeLog.WriteLog("相机8断线重连失败:" + Convert.ToString(nRet, 16));
                                }
                                else
                                {
                                    MsgErroeLog.WriteLog("相机8重连");
                                    myjob8.state = "";
                                    // ch:参数/触发模式/启动采集转 UI 线程执行（这些处理器内部操作大量控件与 SDK）
                                    if (this.InvokeRequired)
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            bnSetParam8_Click(null, null);
                                            comboBox34_SelectedIndexChanged(null, null);
                                            if (yunxing && m_bGrabbing8)
                                                bnStartGrab8_Click(null, null);
                                        }));
                                    else
                                    {
                                        bnSetParam8_Click(null, null);
                                        comboBox34_SelectedIndexChanged(null, null);
                                        if (yunxing && m_bGrabbing8)
                                            bnStartGrab8_Click(null, null);
                                    }
                                    myjob8.state = "";
                                }
                            }
                            else
                                cameraState += "";
                        }
                        } // ch:P3-⑧ 相机8 锁结束
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("断线检测异常:" + ex.Message);
                }
                if (!cameraState.Contains("相"))
                    cameraState = "";
                }
                finally
                {
                    reconnecting = false;
                }
            });
            if (day1 != System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek))
            {
                day1 = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek);
                myjob1.fileng = new FileInfo(myjob1.pathhead_ng + day1 + "\\");
                myjob2.fileng = new FileInfo(myjob2.pathhead_ng + day1 + "\\");
                myjob1.fileok = new FileInfo(myjob1.pathhead_ok + day1 + "\\");
                myjob2.fileok = new FileInfo(myjob2.pathhead_ok + day1 + "\\");
                myjob3.fileng = new FileInfo(myjob3.pathhead_ng + day1 + "\\");
                myjob4.fileng = new FileInfo(myjob4.pathhead_ng + day1 + "\\");
                myjob3.fileok = new FileInfo(myjob3.pathhead_ok + day1 + "\\");
                myjob4.fileok = new FileInfo(myjob4.pathhead_ok + day1 + "\\");
                info1ok = new DirectoryInfo(myjob1.pathhead_ok + day1 + "\\");
                info1ng = new DirectoryInfo(myjob1.pathhead_ng + day1 + "\\");
                info2ok = new DirectoryInfo(myjob2.pathhead_ok + day1 + "\\");
                info2ng = new DirectoryInfo(myjob2.pathhead_ng + day1 + "\\");
                info3ok = new DirectoryInfo(myjob3.pathhead_ok + day1 + "\\");
                info3ng = new DirectoryInfo(myjob3.pathhead_ng + day1 + "\\");
                info4ok = new DirectoryInfo(myjob4.pathhead_ok + day1 + "\\");
                info4ng = new DirectoryInfo(myjob4.pathhead_ng + day1 + "\\");
                myjob5.fileng = new FileInfo(myjob5.pathhead_ng + day1 + "\\");
                myjob6.fileng = new FileInfo(myjob6.pathhead_ng + day1 + "\\");
                myjob5.fileok = new FileInfo(myjob5.pathhead_ok + day1 + "\\");
                myjob6.fileok = new FileInfo(myjob6.pathhead_ok + day1 + "\\");
                myjob7.fileng = new FileInfo(myjob7.pathhead_ng + day1 + "\\");
                myjob8.fileng = new FileInfo(myjob8.pathhead_ng + day1 + "\\");
                myjob7.fileok = new FileInfo(myjob7.pathhead_ok + day1 + "\\");
                myjob8.fileok = new FileInfo(myjob8.pathhead_ok + day1 + "\\");
                info5ok = new DirectoryInfo(myjob5.pathhead_ok + day1 + "\\");
                info5ng = new DirectoryInfo(myjob5.pathhead_ng + day1 + "\\");
                info6ok = new DirectoryInfo(myjob6.pathhead_ok + day1 + "\\");
                info6ng = new DirectoryInfo(myjob6.pathhead_ng + day1 + "\\");
                info7ok = new DirectoryInfo(myjob7.pathhead_ok + day1 + "\\");
                info7ng = new DirectoryInfo(myjob7.pathhead_ng + day1 + "\\");
                info8ok = new DirectoryInfo(myjob8.pathhead_ok + day1 + "\\");
                info8ng = new DirectoryInfo(myjob8.pathhead_ng + day1 + "\\");
                Task.Run(() =>
                {
                    daoqi_jiankong();
                });

            }
            if (this.WindowState == FormWindowState.Minimized)
            {

                timer1.Enabled = true;
            }
            else
                timer1.Enabled = false;
            // });
        }

        private void button2_Click_1(object sender, EventArgs e)
        {
            button11.Enabled = true;
            myjob1.trriger = 0;
        }

        private void timer3_Tick(object sender, EventArgs e)
        {
            if (button11.Enabled == true)
                timecount++;
            if (timecount >= 3)
            {
                button11.Enabled = false;
                timecount = 0;
            }
        }


        private void timer4_Tick(object sender, EventArgs e)
        {
            this.Invoke(new Action(() =>
            {
                label3.Text = DateTime.Now.ToLongTimeString().ToString();
            }));
        }

        private void timer5_Tick(object sender, EventArgs e)
        {
            int now = 0;
            int forword = myjob1.sum;
            // Thread.Sleep(10000);
            now = myjob1.sum;
            this.Invoke(new Action(() =>
            {
                label84.Text = ((now - forword) * 1.00F / 10).ToString();
            }));
        }
        Form3 frm3;
        Form5 frm5;
        Form7 frm7;
        FormOmron omron;
        FormMelsecSerial fx;
        FormModbus modbustcp;
        FormModbusRtu modbusrtu;

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob1.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 sender==null 即重连恢复调用，须绕过登录门（state 仅被赋 ""，原条件恒 false）
            {
                try
                {
                    if (comboBox1.Text == "连续运行")
                    {
                        if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger1.Enabled = false;
                        bnTriggerExec1.Enabled = false;
                    }
                    else if (comboBox1.Text == "触发拍照" || comboBox1.Text == "通讯触发")
                    {

                        if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger1.Checked || comboBox1.Text == "通讯触发")
                        {
                            if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing1)
                            {
                                bnTriggerExec1.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger1.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换1");
                }
                myjob1.triggerMode = comboBox1.Text;
            }
        }
        private void RecvInfo(string str)
        {
            textBox1.Text = str;
        }
        private void DataChange(object sender, Form3.SelectionChangedEventArgs e)
        {

            SafeBeginInvoke(new Action(() => { textBox1.Text = e.Selection; })); // ch:通讯线程触发，UI 写必须封送
            // ch:P0-3 每相机独立 try + blockLock：单相机异常/无 block 不再连坐其余相机；Inputs 读与写同锁
            // ch:P0-3 切换/加载期间（门闩=1）加载器锁外在重写 block 及其终端，本线程让路，避免与其并发
            if (Volatile.Read(ref qiehuanzhong) != 1)
            {
                Myjob[] djobs = new Myjob[] { myjob1, myjob2, myjob3, myjob4, myjob5, myjob6, myjob7, myjob8 };
            for (int di = 0; di < 8; di++)
            {
                if (manager1.JobCount <= di) break;
                Myjob dj = djobs[di];
                try
                {
                    lock (dj.blockLock)
                    {
                        if (dj.block != null && dj.block.Inputs.Contains("jieshou"))
                        {
                            SetBlockInputSafe(dj, "jieshou", e.Selection);
                        }
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("DataChange cam" + (di + 1) + " jieshou 异常:" + ex.Message); }
            }
            } // ch:P0-3 门闩跳过段结束
            if (e.Selection == myjob1.triggerZifu)
            {
                myjob1.jieshouZifu = myjob1.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[0] != null)
                    nRet = m_MyCamera[0].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob2.triggerZifu)
            {
                myjob2.jieshouZifu = myjob2.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[1] != null)
                    nRet = m_MyCamera[1].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob3.triggerZifu)
            {
                myjob3.jieshouZifu = myjob3.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[2] != null)
                    nRet = m_MyCamera[2].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob4.triggerZifu)
            {
                myjob4.jieshouZifu = myjob4.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[3] != null)
                    nRet = m_MyCamera[3].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob5.triggerZifu)
            {
                myjob5.jieshouZifu = myjob5.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[4] != null)
                    nRet = m_MyCamera[4].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob6.triggerZifu)
            {
                myjob6.jieshouZifu = myjob6.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[5] != null)
                    nRet = m_MyCamera[5].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob7.triggerZifu)
            {
                myjob7.jieshouZifu = myjob7.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[6] != null)
                    nRet = m_MyCamera[6].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
            if (e.Selection == myjob8.triggerZifu)
            {
                myjob8.jieshouZifu = myjob8.triggerZifu;
                // ch:触发命令 | en:Trigger command
                int nRet = MyCamera.MV_E_PARAMETER;
                if (m_MyCamera[7] != null)
                    nRet = m_MyCamera[7].MV_CC_SetCommandValue_NET("TriggerSoftware");
                if (MyCamera.MV_OK != nRet)
                {
                    MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                }
            }
        }
        private void DataChange_fins(object sender, FormOmron.SelectionChangedEventArgs e)
        {
            if (e.Camera == "1")
            {
                try
                {
                    if (myjob1.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob1, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob1.triggerZifu)
                {
                    myjob1.jieshouZifu = myjob1.triggerZifu;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[0] != null)
                        nRet = m_MyCamera[0].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }


            }
            if (e.Camera == "2")
            {
                try
                {
                    if (myjob2.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob2, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob2.triggerZifu)
                {
                    myjob2.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[1] != null)
                        nRet = m_MyCamera[1].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "3")
            {
                try
                {
                    if (myjob3.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob3, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob3.triggerZifu)
                {
                    myjob3.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[2] != null)
                        nRet = m_MyCamera[2].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "4")
            {
                try
                {
                    if (myjob4.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob4, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob4.triggerZifu)
                {

                    myjob4.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[3] != null)
                        nRet = m_MyCamera[3].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "5")
            {
                try
                {
                    if (myjob5.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob5, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob5.triggerZifu)
                {

                    myjob5.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[4] != null)
                        nRet = m_MyCamera[4].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "6")
            {
                try
                {
                    if (myjob6.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob6, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob6.triggerZifu)
                {

                    myjob6.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[5] != null)
                        nRet = m_MyCamera[5].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "7")
            {
                try
                {
                    if (myjob7.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob7, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob7.triggerZifu)
                {

                    myjob7.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[6] != null)
                        nRet = m_MyCamera[6].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera == "8")
            {
                try
                {
                    if (myjob8.block.Inputs.Contains("fins"))
                    {
                        SetBlockInputSafe(myjob8, "fins", e.Selection);
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (e.Selection == myjob8.triggerZifu)
                {

                    myjob8.jieshouZifu = e.Selection;
                    // ch:触发命令 | en:Trigger command
                    int nRet = MyCamera.MV_E_PARAMETER;
                    if (m_MyCamera[7] != null)
                        nRet = m_MyCamera[7].MV_CC_SetCommandValue_NET("TriggerSoftware");
                    if (MyCamera.MV_OK != nRet)
                    {
                        MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                    }
                }

            }
            if (e.Camera.Contains("10"))
            {
                if (path_1 != omron.lujing.Replace("\0", "") && qiehuanzhong == 0)
                {
                    if (omron.camera_dic[10][2] == "true")
                    {
                        if (!omron.camera_dic[10][1].Contains("无"))
                        {
                            omron.SetSwitchPending(10);
                        }

                        Task.Run(() =>
                        {
                            // ch:P2 必须 try/finally 复位：原实现若 xinghao_qiehuan 抛异常，omron.qiehuanzhong 永久停在 1 → 后续 cam10 切换事件不再触发
                            try { xinghao_qiehuan(omron.lujing.Replace("\0", "")); }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                            finally { omron.qiehuanzhong = 0; }
                        });
                    }
                    else
                    {
                        // ch:P2 内层未启用(反馈使能非 true)也必须复位回执闩：否则该窗体轮询线程门控永久为 1，cam10 切换事件不再触发
                        omron.qiehuanzhong = 0;
                    }
                }
                else
                {
                    if (!omron.camera_dic[10][1].Contains("无"))
                    {
                        omron.SetSwitchPending(10);
                    }
                    omron.qiehuanzhong = 0;
                }
            }
            SafeBeginInvoke(() =>
            {
                SafeBeginInvoke(new Action(() => { textBox1.Text = e.Selection; })); // ch:通讯线程触发，UI 写必须封送
            });
        }
        private void DataChange_modbustcp(object sender, FormModbus.SelectionChangedEventArgs e)
        {
            if (yunxing)
            {
                if (e.Camera == "1")
                {
                    try
                    {
                        if (myjob1.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob1, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob1.triggerZifu)
                    {
                        myjob1.jieshouZifu = myjob1.triggerZifu;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[0] != null)
                            nRet = m_MyCamera[0].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }


                }
                if (e.Camera == "2")
                {
                    try
                    {
                        if (myjob2.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob2, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob2.triggerZifu)
                    {
                        myjob2.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[1] != null)
                            nRet = m_MyCamera[1].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "3")
                {
                    try
                    {
                        if (myjob3.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob3, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob3.triggerZifu)
                    {
                        myjob3.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[2] != null)
                            nRet = m_MyCamera[2].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "4")
                {
                    try
                    {
                        if (myjob4.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob4, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob4.triggerZifu)
                    {

                        myjob4.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[3] != null)
                            nRet = m_MyCamera[3].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "5")
                {
                    try
                    {
                        if (myjob5.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob5, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob5.triggerZifu)
                    {

                        myjob5.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[4] != null)
                            nRet = m_MyCamera[4].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "6")
                {
                    try
                    {
                        if (myjob6.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob6, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob6.triggerZifu)
                    {

                        myjob6.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[5] != null)
                            nRet = m_MyCamera[5].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "7")
                {
                    try
                    {
                        if (myjob7.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob7, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob7.triggerZifu)
                    {

                        myjob7.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[6] != null)
                            nRet = m_MyCamera[6].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "8")
                {
                    try
                    {
                        if (myjob8.block.Inputs.Contains("modbustcp"))
                        {
                            SetBlockInputSafe(myjob8, "modbustcp", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob8.triggerZifu)
                    {

                        myjob8.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[7] != null)
                            nRet = m_MyCamera[7].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
            }
            if (e.Camera.Contains("10"))
            {
                if (path_1 != modbustcp.lujing.Replace("\0", "") && qiehuanzhong == 0)
                {
                    if (modbustcp.camera_dic[10][2] == "true")
                    {
                        if (!modbustcp.camera_dic[10][1].Contains("无"))
                        {
                            modbustcp.SetSwitchPending(10);
                        }

                        Task.Run(() =>
                        {
                            // ch:P2 必须 try/finally 复位：原实现若 xinghao_qiehuan 抛异常，modbustcp.qiehuanzhong 永久停在 1 → 后续 cam10 切换事件不再触发
                            try { xinghao_qiehuan(modbustcp.lujing.Replace("\0", "")); }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                            finally { modbustcp.qiehuanzhong = 0; }
                        });
                    }
                    else
                    {
                        // ch:P2 内层未启用(反馈使能非 true)也必须复位回执闩：否则该窗体轮询线程门控永久为 1，cam10 切换事件不再触发
                        modbustcp.qiehuanzhong = 0;
                    }
                }
                else
                {
                    if (!modbustcp.camera_dic[10][1].Contains("无"))
                    {
                        modbustcp.SetSwitchPending(10);
                    }
                    modbustcp.qiehuanzhong = 0;
                }
            }
            SafeBeginInvoke(() =>
            {
                SafeBeginInvoke(new Action(() => { textBox1.Text = e.Selection; })); // ch:通讯线程触发，UI 写必须封送
            });
        }
        private void DataChange_modbusrtu(object sender, FormModbusRtu.SelectionChangedEventArgs e)
        {
            if (yunxing)
            {
                if (e.Camera == "1")
                {
                    try
                    {
                        if (myjob1.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob1, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob1.triggerZifu)
                    {
                        myjob1.jieshouZifu = myjob1.triggerZifu;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[0] != null)
                            nRet = m_MyCamera[0].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }


                }
                if (e.Camera == "2")
                {
                    try
                    {
                        if (myjob2.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob2, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob2.triggerZifu)
                    {
                        myjob2.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[1] != null)
                            nRet = m_MyCamera[1].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "3")
                {
                    try
                    {
                        if (myjob3.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob3, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob3.triggerZifu)
                    {
                        myjob3.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[2] != null)
                            nRet = m_MyCamera[2].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "4")
                {
                    try
                    {
                        if (myjob4.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob4, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob4.triggerZifu)
                    {

                        myjob4.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[3] != null)
                            nRet = m_MyCamera[3].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "5")
                {
                    try
                    {
                        if (myjob5.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob5, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob5.triggerZifu)
                    {

                        myjob5.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[4] != null)
                            nRet = m_MyCamera[4].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "6")
                {
                    try
                    {
                        if (myjob6.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob6, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob6.triggerZifu)
                    {

                        myjob6.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[5] != null)
                            nRet = m_MyCamera[5].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "7")
                {
                    try
                    {
                        if (myjob7.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob7, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob7.triggerZifu)
                    {

                        myjob7.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[6] != null)
                            nRet = m_MyCamera[6].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
                if (e.Camera == "8")
                {
                    try
                    {
                        if (myjob8.block.Inputs.Contains("modbusrtu"))
                        {
                            SetBlockInputSafe(myjob8, "modbusrtu", e.Selection);
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                    if (e.Selection == myjob8.triggerZifu)
                    {

                        myjob8.jieshouZifu = e.Selection;
                        // ch:触发命令 | en:Trigger command
                        int nRet = MyCamera.MV_E_PARAMETER;
                        if (m_MyCamera[7] != null)
                            nRet = m_MyCamera[7].MV_CC_SetCommandValue_NET("TriggerSoftware");
                        if (MyCamera.MV_OK != nRet)
                        {
                            MsgErroeLog.WriteLog("Trigger Software Fail!---" + nRet);
                        }
                    }

                }
            }
            if (e.Camera.Contains("10"))
            {
                if (path_1 != modbusrtu.lujing.Replace("\0", "") && qiehuanzhong == 0)
                {
                    if (modbusrtu.camera_dic[10][2] == "true")
                    {
                        if (!modbusrtu.camera_dic[10][1].Contains("无"))
                        {
                            modbusrtu.SetSwitchPending(10);
                        }

                        Task.Run(() =>
                        {
                            // ch:P2 必须 try/finally 复位：原实现若 xinghao_qiehuan 抛异常，modbusrtu.qiehuanzhong 永久停在 1 → 后续 cam10 切换事件不再触发
                            try { xinghao_qiehuan(modbusrtu.lujing.Replace("\0", "")); }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                            finally { modbusrtu.qiehuanzhong = 0; }
                        });
                    }
                    else
                    {
                        // ch:P2 内层未启用(反馈使能非 true)也必须复位回执闩：否则该窗体轮询线程门控永久为 1，cam10 切换事件不再触发
                        modbusrtu.qiehuanzhong = 0;
                    }
                }
                else
                {
                    if (!modbusrtu.camera_dic[10][1].Contains("无"))
                    {
                        modbusrtu.SetSwitchPending(10);
                    }
                    modbusrtu.qiehuanzhong = 0;
                }
            }
            SafeBeginInvoke(() =>
            {
                SafeBeginInvoke(new Action(() => { textBox1.Text = e.Selection; })); // ch:通讯线程触发，UI 写必须封送
            });
        }
        public delegate void changedata(string data);
        public event changedata changedata_event;

        private void button9_Click(object sender, EventArgs e)
        {
            changedata_event?.Invoke(textBox1.Text);
            frm3.textBox9.Text = textBox1.Text;
        }

        private void button8_Click_1(object sender, EventArgs e)
        {
            ShowPictureList(textBox2, listBox4);
        }
        private void ShowPictureList(TextBox text, ListBox list)
        {
            if (yunxing == false)
            {
                // ch:R10-11 原用前 Dispose 会令复用的对话框实例处于已释放状态，ShowDialog 必抛 ObjectDisposedException；
                // 唯一 Dispose 保留在设置关闭处（button1_Click 内）
                if (myjob1.dlg.ShowDialog() == DialogResult.OK)
                {
                    string dir = myjob1.dlg.SelectedPath;

                    text.Text = dir;
                    // 清空显示
                    list.Items.Clear();

                    // 遍历所有的文件，检查文件名后缀
                    string[] fff = Directory.GetFiles(dir);
                    foreach (string f in fff)
                    {
                        if (f.EndsWith(".jpg")
                            || f.EndsWith(".jpeg")
                            || f.EndsWith(".png") || f.EndsWith(".bmp"))
                        {
                            // 取得文件名
                            PictureListItem item = new PictureListItem();
                            item.filePath = f;
                            item.name = Path.GetFileName(f);
                            // 加到列表框显示
                            list.Items.Add(item);
                        }
                    }

                    // 默认打开第一个文件显示
                    if (list.Items.Count > 0)
                        list.SetSelected(0, true);
                }

            }

        }
        private void Showjob(ComboBox box, int job_number)
        {

            // 遍历所有的文件，检查文件名后缀
            box.Items.Clear();
            string[] fff = Directory.GetFiles(wenjianjia + "\\" + job_number);
            foreach (string f in fff)
            {
                if (f.EndsWith(".vpp"))
                {
                    // 加到列表框显示
                    box.Items.Add(Path.GetFileName(f));
                }
            }
        }
        class PictureListItem
        {
            public string name;
            public string filePath;

            public override string ToString()
            {
                return name;
            }
        }

        private void listBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox4.SelectedItem;
                if (item == null) return;
                myjob1.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    _ioPulseEnabled = true;
                    myjob1.trriger = 1;
                    getrecord(myjob1);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

        }
        int trriger1_temp = 0;
        int trriger2_temp = 0;
        int trriger3_temp = 0;
        int trriger4_temp = 0;
        int trriger5_temp = 0;
        int trriger6_temp = 0;
        int trriger7_temp = 0;
        int trriger8_temp = 0;
        private void button10_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox4.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob1.img = new Bitmap(item.filePath);
                    if (myjob1.trriger == 0)
                    {
                        myjob1.trriger = 1;
                        // ch:P2 离线单图检测移后台：getrecord 含 Run/IO/存图，UI 线程直接跑会冻结界面（实时链路本就跑在检测线程）
                        Task.Run(() => { try { getrecord(myjob1); } catch (Exception ex) { MsgErroeLog.WriteLog("离线单图检测异常:" + ex.Message); } });
                    }
                    _ioPulseEnabled = true;
                    trriger1_temp = 1;
                    timer7.Interval = int.Parse(textBox6.Text);
                    timer7.Enabled = true;
                }
            }
            // picture_trigger(listBox4,ref timer7, textBox6,ref myjob1, out int trriger1_temp);
        }
        private void picture_trigger(ListBox list, ref System.Windows.Forms.Timer tim, TextBox text, ref Myjob myjob, out int trriger_temp)
        {
            trriger_temp = 0;
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)list.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                }
                else
                {
                    myjob.img = new Bitmap(item.filePath);
                    if (myjob.trriger == 0)
                    {
                        myjob.trriger = 1;
                        // ch:P2 离线单图检测移后台：getrecord 含 Run/IO/存图，UI 线程直接跑会冻结界面（ref 参数不能进 lambda，先取本地副本）
                        Myjob jobLocal = myjob;
                        Task.Run(() => { try { getrecord(jobLocal); } catch (Exception ex) { MsgErroeLog.WriteLog("离线单图检测异常:" + ex.Message); } });
                    }
                    trriger_temp = 1;
                    tim.Interval = int.Parse(text.Text);
                    tim.Enabled = true;
                }
            }
        }
        CogPMAlignTool pma;//PMA工具全局变量
        private void 设置ROIToolStripMenuItem_Click(object sender, EventArgs e)
        {


        }
        CogRectangle rect;
        private void menuitem_Click(object sender, EventArgs e)
        {
            label75.Text = sender.ToString().Split('\\').Last();
            try
            {
                wenjianjia = Path.GetDirectoryName(path_1);
                MsgErroeLog.WriteLog("方案文件夹:" + wenjianjia);
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message);
            }
            path_1 = sender.ToString();
            StreamReader sr = null;
            try
            {
                sr = new StreamReader(Path.GetDirectoryName(path_1) + "\\Menu.ini");
                int i = 0;
                while (sr.Peek() >= 0)
                {
                    i++;
                    sr.ReadLine();
                }
                sr.Dispose();
                sr.Close();
                if (i > 5)
                {
                    FileStream stream = null;
                    try
                    {
                        stream = File.Open(Path.GetDirectoryName(path_1) + "\\Menu.ini", FileMode.OpenOrCreate, FileAccess.Write);
                        stream.Seek(0, SeekOrigin.Begin);
                        stream.SetLength(0);
                        stream.Flush();
                        stream.Close();
                    }
                    catch
                    {
                        stream.Flush();
                        stream.Close();
                    }
                }
            }
            catch
            {
                try
                {
                    sr.Dispose();
                    sr.Close();
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

            };
            if (this.设置ToolStripMenuItem.DropDownItems[this.设置ToolStripMenuItem.DropDownItems.Count - 1].Text != path_1)
            {
                StreamWriter s = new StreamWriter(Path.GetDirectoryName(path_1) + "\\Menu.ini", true);
                s.WriteLine(path_1);
                s.Flush();
                s.Close();
            }
            xinghao_qiehuan("");
        }
        ToolStripMenuItem menuitem;
        private void 设置ToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
        bool blnDraw;//判断是否绘制
        Point start; //画框的起始点
        Point end222;//画框的结束点
        Point start_1;
        Point end_1;
        System.Drawing.Rectangle rect_1;//矩形
        ICogTransform2D myTranform;
        private void cogRecordDisplay2_MouseDown(object sender, MouseEventArgs e)
        {
            //try
            //{
            //    if (!blnDraw)
            //    {
            //        myTranform = cogRecordDisplay2.GetTransform(cogRecordDisplay2.Image.SelectedSpaceName, "*");
            //        myTranform.MapPoint(x2, y2, out out_x, out out_y);
            //        start.X = (int)out_x;
            //        start.Y = (int)out_y;
            //        //Invalidate();
            //        start_1 = e.Location;

            //        blnDraw = true;
            //    }
            //}
            //catch
            //{ }
        }
        Point tempEndPoint;
        private void cogRecordDisplay2_MouseUp(object sender, MouseEventArgs e)
        {
            //if (blnDraw)
            //{
            //    try
            //    {
            //        if (pma != null)
            //        {
            //            myTranform = cogRecordDisplay2.GetTransform(cogRecordDisplay2.Image.SelectedSpaceName, "*");
            //            myTranform.MapPoint(x2, y2, out out_x, out out_y);
            //            end222.X = (int)out_x;
            //            end222.Y = (int)out_y;
            //            tempEndPoint = end222; //记录框的位置和大小


            //            rect.SetXYWidthHeight(Math.Min(start.X, tempEndPoint.X), Math.Min(start.Y, tempEndPoint.Y), Math.Abs(start.X - tempEndPoint.X), Math.Abs(start.Y - tempEndPoint.Y));
            //            // rect.SetCenterWidthHeight(Math.Min(start.X, tempEndPoint.X), Math.Min(start.Y, tempEndPoint.Y), Math.Abs(start.X - tempEndPoint.X), Math.Abs(start.Y - tempEndPoint.Y));
            //            pma.SearchRegion = rect;
            //            //允许调整搜索区域大小
            //            rect.GraphicDOFEnable = CogRectangleDOFConstants.Position | CogRectangleDOFConstants.Size;
            //            //允许鼠标选择搜索区域
            //            rect.Interactive = true;
            //            CogRectangle mRectangle = new CogRectangle();
            //            mRectangle.X = Math.Min(start.X, tempEndPoint.X);
            //            mRectangle.Y = Math.Min(start.Y, tempEndPoint.Y);
            //            mRectangle.Width = Math.Abs(start.X - tempEndPoint.X);
            //            mRectangle.Height = Math.Abs(start.Y - tempEndPoint.Y);
            //            //添加到图像中
            //            cogRecordDisplay2.StaticGraphics.Add(mRectangle, "mRectangle");
            //            pma = null;
            //        }
            //    }
            //    catch(Exception ex)
            //    { MsgErroeLog.WriteLog(ex.Message); };
            //}
            //blnDraw = false;

        }
        bool check = false;
        bool check2 = false;
        bool check3 = false;
        bool check4 = false;
        private void DataChangef1(object sender, WindowsFormsApplication1.SubSet.SelectionChangedEventArgs e)
        {
            check = e.Check;
            string select = e.Selection;
            CogToolBlock toolblock = e.Toolblock;
            Thread.Sleep(20);
            if (toolblock != null)
            {
                roiset1(toolblock, select, check);
            }
        }
        private void DataChangef2(object sender, WindowsFormsApplication1.SubSet.SelectionChangedEventArgs2 e)
        {
            check2 = e.Check;
            string select = e.Selection;
            CogToolBlock toolblock = e.Toolblock;
            Thread.Sleep(20);
            if (toolblock != null)
            {
                roiset1(toolblock, select, check2);
            }
        }
        private void cogRecordDisplay2_MouseMove(object sender, MouseEventArgs e)
        {
            //x2 = e.X;
            //y2 = e.Y;
            //try
            //{
            //    if (blnDraw)
            //    {
            //        if (e.Button != MouseButtons.Left)//判断是否按下左键
            //            return;

            //        end_1 = e.Location;
            //        //设置搜索区域





            //      //rect_1.Location = new Point(
            //      //Math.Min(start_1.X, end_1.X),
            //      //Math.Min(start_1.Y, end_1.Y));
            //      //rect_1.Size = new Size(
            //      //Math.Abs(start_1.X - end_1.X),
            //      //Math.Abs(start_1.Y - end_1.Y));
            //     // Invalidate();
            //      }
            //    }
            //    catch(Exception ex)
            //{
            //    MsgErroeLog.WriteLog(ex.Message);     
            //};


        }

        private void cogRecordDisplay2_Click(object sender, EventArgs e)
        {
            //try
            //{
            //    if (x1 != out_x && y1 != out_y)
            //    {
            //        ICogTransform2D myTranform1 = cogRecordDisplay2.GetTransform(cogRecordDisplay2.Image.SelectedSpaceName, "*");
            //        myTranform1.MapPoint(x2, y2, out out_x, out out_y);
            //        Color c = new Color();
            //        Bitmap bitmap = new Bitmap(cogRecordDisplay2.Image.ToBitmap());
            //        c = bitmap.GetPixel((int)out_x, (int)out_y);
            //        label6.Text = string.Format("X={0},Y={1},R={2},G={3},B={4}", out_x, out_y, c.R, c.G, c.B);
            //        bitmap.Dispose();
            //    }
            //}
            //catch (Exception ex)
            //{ MessageBox.Show(ex.Message); }; 
        }

        private void 保存ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                保存ToolStripMenuItem.Enabled = false;
                // ch:运行中保存会与回调线程的工具块执行并发序列化，先停止采集再保存
                if (yunxing)
                {
                    button11_Click_1(null, null);
                    Thread.Sleep(300);
                }
                CogSerializer.SaveObjectToFile(manager1, path_1);
                保存ToolStripMenuItem.Enabled = true;
                MessageBox.Show("保存方案成功!" + (yunxing ? "（已停止运行，请重新开始检测）" : ""));
            }
            catch
            {
                保存ToolStripMenuItem.Enabled = true;
                MessageBox.Show("保存方案失败!");
            }
        }

        private void button11_Click(object sender, EventArgs e)
        {

        }

        private void comboBox1_TextChanged(object sender, EventArgs e)
        {

        }

        private void comboBox1_SelectedValueChanged(object sender, EventArgs e)
        {

        }

        private void dataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }

        // ch:复位所有相机 IO 输出标志（参考正常版本），防止输出线保持高电平无法恢复
        private void ResetMyJobOutputs()
        {
            try
            {
                StopAllIoPulses();
                myjob1.outputok = 0; myjob1.outputng = 0; myjob1.outputok2 = -1; myjob1.outputng2 = -1;
                myjob2.outputok = 0; myjob2.outputng = 0; myjob2.outputok2 = -1; myjob2.outputng2 = -1;
                myjob3.outputok = 0; myjob3.outputng = 0; myjob3.outputok2 = -1; myjob3.outputng2 = -1;
                myjob4.outputok = 0; myjob4.outputng = 0; myjob4.outputok2 = -1; myjob4.outputng2 = -1;
                myjob5.outputok = 0; myjob5.outputng = 0; myjob5.outputok2 = -1; myjob5.outputng2 = -1;
                myjob6.outputok = 0; myjob6.outputng = 0; myjob6.outputok2 = -1; myjob6.outputng2 = -1;
                myjob7.outputok = 0; myjob7.outputng = 0; myjob7.outputok2 = -1; myjob7.outputng2 = -1;
                myjob8.outputok = 0; myjob8.outputng = 0; myjob8.outputok2 = -1; myjob8.outputng2 = -1;
                _ioPulseEnabled = true;
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("复位IO输出异常:" + ex.Message); }
        }

        // ch:安全 BeginInvoke（参考正常版本）：窗口销毁/释放期间静默忽略，避免抛异常
        private void SafeBeginInvoke(Action action)
        {
            try
            {
                if (!closing && this.IsHandleCreated && !this.IsDisposed)
                    this.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }
        private void SafeBeginInvoke(Delegate method, params object[] args)
        {
            try
            {
                if (!closing && this.IsHandleCreated && !this.IsDisposed)
                    this.BeginInvoke(method, args);
            }
            catch (InvalidOperationException) { }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        private void button11_Click_1(object sender, EventArgs e)
        {
            try
            {
                checkBox70.CheckState = CheckState.Checked;
                bnClose.Enabled = true; // ch:停止后允许关闭设备
                if (m_bGrabbing1 == true && m_MyCamera[0] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing1 = false;
                    // m_hReceiveThread.Join();
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[0].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab1.Enabled = false;
                bnStopGrab1.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.Text = "运行";
                    button1.BackColor = Color.LightGreen;
                }));

                fff = 0;
                myjob1.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab1.Enabled = false;
                bnStopGrab1.Enabled = false;
                myjob1.yun = 0;
                MsgErroeLog.WriteLog("相机1停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing2 == true && m_MyCamera[1] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing2 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[1].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab2.Enabled = false;
                bnStopGrab2.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.Text = "运行";
                    button1.BackColor = Color.LightGreen;
                }));


                fff = 0;
                myjob2.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab2.Enabled = false;
                bnStopGrab2.Enabled = false;
                myjob2.yun = 0;
                MsgErroeLog.WriteLog("相机2停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing3 == true && m_MyCamera[2] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing3 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[2].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab3.Enabled = false;
                bnStopGrab3.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob3.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab3.Enabled = false;
                bnStopGrab3.Enabled = false;
                myjob3.yun = 0;
                MsgErroeLog.WriteLog("相机3停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing4 == true && m_MyCamera[3] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing4 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[3].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab4.Enabled = false;
                bnStopGrab4.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob4.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab4.Enabled = false;
                bnStopGrab4.Enabled = false;
                myjob4.yun = 0;
                MsgErroeLog.WriteLog("相机4停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing5 == true && m_MyCamera[4] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing5 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[4].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab5.Enabled = false;
                bnStopGrab5.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob5.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab5.Enabled = false;
                bnStopGrab5.Enabled = false;
                myjob5.yun = 0;
                MsgErroeLog.WriteLog("相机5停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing6 == true && m_MyCamera[5] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing6 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[5].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab6.Enabled = false;
                bnStopGrab6.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob6.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab6.Enabled = false;
                bnStopGrab6.Enabled = false;
                myjob6.yun = 0;
                MsgErroeLog.WriteLog("相机6停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing7 == true && m_MyCamera[6] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing7 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[6].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab7.Enabled = false;
                bnStopGrab7.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob7.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab7.Enabled = false;
                bnStopGrab7.Enabled = false;
                myjob7.yun = 0;
                MsgErroeLog.WriteLog("相机7停止" + ex.Message);
            };
            try
            {
                if (m_bGrabbing8 == true && m_MyCamera[7] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing8 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[7].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }

                bnStartGrab8.Enabled = false;
                bnStopGrab8.Enabled = false;
                this.Invoke(new Action(() => {
                    // 更新UI操作
                    button11.BackColor = Color.Green;
                    button1.BackColor = Color.LightGreen;
                    button1.Text = "运行";
                }));


                fff = 0;
                myjob8.yun = 0;

            }
            catch (Exception ex)
            {
                bnStartGrab8.Enabled = false;
                bnStopGrab8.Enabled = false;
                myjob8.yun = 0;
                MsgErroeLog.WriteLog("相机8停止" + ex.Message);
            };
            yunxing = false;
            try { modbustcp.CloseXieWuSocket(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            ResetMyJobOutputs(); // ch:停止时复位所有相机 IO 输出（参考正常版本），防止输出线保持高电平无法恢复
            
            checkedListBox1.Enabled = true;
        }

        private void button3_KeyDown(object sender, KeyEventArgs e)
        {
            // button3.BackColor = Color.Green;
        }

        private void button3_KeyUp(object sender, KeyEventArgs e)
        {
            // button3.BackColor = Color.LightGreen;
        }

        private void 触发设置ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (触发设置ToolStripMenuItem.CheckState == CheckState.Checked)
            {
                comboBox1.Enabled = false;
                comboBox4.Enabled = false;
                comboBox5.Enabled = false;
                comboBox8.Enabled = false;
                comboBox25.Enabled = false;
                comboBox28.Enabled = false;
                comboBox31.Enabled = false;
                comboBox34.Enabled = false;
                button21.Enabled = false;
                button28.Enabled = false;
                button29.Enabled = false;
                button30.Enabled = false;
                触发设置ToolStripMenuItem.Checked = false;
            }
            else
            {
                comboBox1.Enabled = true;
                comboBox4.Enabled = true;
                comboBox5.Enabled = true;
                comboBox8.Enabled = true;
                comboBox25.Enabled = true;
                comboBox28.Enabled = true;
                comboBox31.Enabled = true;
                comboBox34.Enabled = true;
                button21.Enabled = true;
                button28.Enabled = true;
                button29.Enabled = true;
                button30.Enabled = true;
                触发设置ToolStripMenuItem.Checked = true;

            }

        }

        private void 存图测试1ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (存图测试1ToolStripMenuItem.CheckState == CheckState.Checked)
            {
                listBox4.Visible = false;
                textBox2.Visible = false;
                listBox5.Visible = false;
                textBox10.Visible = false;
                listBox8.Visible = false;
                textBox12.Visible = false;
                listBox9.Visible = false;
                textBox17.Visible = false;
                listBox10.Visible = false;
                listBox11.Visible = false;
                listBox12.Visible = false;
                listBox13.Visible = false;
                textBox22.Visible = false;
                textBox28.Visible = false;
                textBox34.Visible = false;
                textBox40.Visible = false;
                button8.Visible = false;
                button10.Visible = false;
                button13.Visible = false;
                button23.Visible = false;
                button24.Visible = false;
                button25.Visible = false;
                button22.Visible = false;
                button26.Visible = false;
                button27.Visible = false;
                button37.Visible = false;
                button38.Visible = false;
                button39.Visible = false;
                button36.Visible = false;
                button40.Visible = false;
                button41.Visible = false;
                button52.Visible = false;
                button53.Visible = false;
                button54.Visible = false;
                button65.Visible = false;
                button66.Visible = false;
                button67.Visible = false;
                button78.Visible = false;
                button79.Visible = false;
                button80.Visible = false;
                dataGridView1.Visible = true;
                dataGridView2.Visible = true;
                dataGridView3.Visible = true;
                dataGridView4.Visible = true;
                dataGridView5.Visible = true;
                dataGridView6.Visible = true;
                dataGridView7.Visible = true;
                dataGridView8.Visible = true;
                checkBox11.Visible = true;
                checkBox8.Visible = true;
                checkBox56.Visible = true;
                checkBox57.Visible = true;
                checkBox58.Visible = true;
                checkBox59.Visible = true;
                checkBox60.Visible = true;
                checkBox61.Visible = true;
                存图测试1ToolStripMenuItem.Checked = false;
            }
            else
            {
                listBox5.Visible = true;
                textBox10.Visible = true;
                listBox4.Visible = true;
                textBox2.Visible = true;
                listBox8.Visible = true;
                textBox12.Visible = true;
                listBox9.Visible = true;
                textBox17.Visible = true;
                listBox10.Visible = true;
                listBox11.Visible = true;
                listBox12.Visible = true;
                listBox13.Visible = true;
                textBox22.Visible = true;
                textBox28.Visible = true;
                textBox34.Visible = true;
                textBox40.Visible = true;
                button8.Visible = true;
                button10.Visible = true;
                button13.Visible = true;
                button23.Visible = true;
                button24.Visible = true;
                button25.Visible = true;
                button22.Visible = true;
                button26.Visible = true;
                button27.Visible = true;
                button37.Visible = true;
                button38.Visible = true;
                button39.Visible = true;
                button36.Visible = true;
                button40.Visible = true;
                button41.Visible = true;
                button52.Visible = true;
                button53.Visible = true;
                button54.Visible = true;
                button65.Visible = true;
                button66.Visible = true;
                button67.Visible = true;
                button78.Visible = true;
                button79.Visible = true;
                button80.Visible = true;
                dataGridView1.Visible = false;
                dataGridView2.Visible = false;
                dataGridView3.Visible = false;
                dataGridView4.Visible = false;
                dataGridView5.Visible = false;
                dataGridView6.Visible = false;
                dataGridView7.Visible = false;
                dataGridView8.Visible = false;
                checkBox11.Visible = false;
                checkBox8.Visible = false;
                checkBox56.Visible = false;
                checkBox57.Visible = false;
                checkBox58.Visible = false;
                checkBox59.Visible = false;
                checkBox60.Visible = false;
                checkBox61.Visible = false;
                存图测试1ToolStripMenuItem.Checked = true;

            }
        }

        private void 输出时间ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (输出时间ToolStripMenuItem.CheckState == CheckState.Checked)
            {
                textBox3.Enabled = false;
                输出时间ToolStripMenuItem.Checked = false;
            }
            else
            {
                textBox3.Enabled = true;
                输出时间ToolStripMenuItem.Checked = true;

            }
        }

        private void rOI设置ToolStripMenuItem_Click(object sender, EventArgs e)
        {

            if (rOI设置ToolStripMenuItem.CheckState == CheckState.Checked)
            {
                pma = null;
                cra.Interactive = false;
                cra = null;
                rOI设置ToolStripMenuItem.Checked = false;
                HideRoiRendererIfHost(pictureBoxCam1);
            }
            else
            {
                try
                {
                    pma = myjob1.block.Tools["CogPMAlignTool1"] as CogPMAlignTool;
                }
                catch
                {
                    pma = myjob1.block.Tools["模版匹配"] as CogPMAlignTool;
                }
                rOI设置ToolStripMenuItem.Checked = true;
                cra = new CogRectangleAffine();
                cra = (CogRectangleAffine)pma.SearchRegion;
                cra.Interactive = true;
                cra.SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                cra.SelectedSpaceName = "#";//设置形状的坐标空间
                cra.GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                PrepareRoiDisplay(pictureBoxCam1, myjob1.block, true).InteractiveGraphics.Add(cra, "roi", false);
            }
        }
        private void roiset2(CogToolBlock blocktool, ICogTool tool, bool check, CogRecordDisplay record)
        {
            CogPMAlignTool Pmatemp1;
            CogBlobTool Blobtemp1;
            CogPMAlignMultiTool multipm;
            Dictionary<string, ICogRegion> cra2 = new Dictionary<string, ICogRegion>();
            cra2.Add("Cognex.VisionPro.CogRectangleAffine", new CogRectangleAffine());
            cra2.Add("Cognex.VisionPro.CogPolygon", new CogPolygon());
            cra2.Add("Cognex.VisionPro.CogRectangle", new CogRectangle());
            cra2.Add("Cognex.VisionPro.CogCircle", new CogCircle());
            if (check == true)
            {
                if (tool is CogPMAlignTool)
                {
                    Pmatemp1 = tool as CogPMAlignTool;
                    switch (Pmatemp1.SearchRegion.GetType().ToString())

                    {
                        case "Cognex.VisionPro.CogRectangleAffine":
                            cra2[Pmatemp1.SearchRegion.GetType().ToString()] = (CogRectangleAffine)Pmatemp1.SearchRegion;
                            ((CogRectangleAffine)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogRectangleAffine)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangleAffine)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedSpaceName = Pmatemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogRectangleAffine)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangleAffine)cra2[Pmatemp1.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogPolygon":
                            cra2[Pmatemp1.SearchRegion.GetType().ToString()] = (CogPolygon)Pmatemp1.SearchRegion;
                            ((CogPolygon)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogPolygon)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogPolygon)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedSpaceName = Pmatemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogPolygon)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogPolygonDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogPolygon)cra2[Pmatemp1.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogRectangle":
                            cra2[Pmatemp1.SearchRegion.GetType().ToString()] = (CogRectangle)Pmatemp1.SearchRegion;
                            ((CogRectangle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogRectangle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedSpaceName = Pmatemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogRectangle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogRectangleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogCircle":
                            cra2[Pmatemp1.SearchRegion.GetType().ToString()] = (CogCircle)Pmatemp1.SearchRegion;
                            ((CogCircle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogCircle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogCircle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).SelectedSpaceName = Pmatemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogCircle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogCircleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogCircle)cra2[Pmatemp1.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        default:
                            break;
                    };

                }
                else if (tool is CogBlobTool)
                {
                    Blobtemp1 = tool as CogBlobTool;
                    switch (Blobtemp1.Region.GetType().ToString())

                    {
                        case "Cognex.VisionPro.CogRectangleAffine":
                            cra2[Blobtemp1.Region.GetType().ToString()] = (CogRectangleAffine)Blobtemp1.Region;
                            ((CogRectangleAffine)cra2[Blobtemp1.Region.GetType().ToString()]).Interactive = true;
                            ((CogRectangleAffine)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangleAffine)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedSpaceName = Blobtemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogRectangleAffine)cra2[Blobtemp1.Region.GetType().ToString()]).GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangleAffine)cra2[Blobtemp1.Region.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogPolygon":
                            cra2[Blobtemp1.Region.GetType().ToString()] = (CogPolygon)Blobtemp1.Region;
                            ((CogPolygon)cra2[Blobtemp1.Region.GetType().ToString()]).Interactive = true;
                            ((CogPolygon)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogPolygon)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedSpaceName = Blobtemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogPolygon)cra2[Blobtemp1.Region.GetType().ToString()]).GraphicDOFEnable = CogPolygonDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogPolygon)cra2[Blobtemp1.Region.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogRectangle":
                            cra2[Blobtemp1.Region.GetType().ToString()] = (CogRectangle)Blobtemp1.Region;
                            ((CogRectangle)cra2[Blobtemp1.Region.GetType().ToString()]).Interactive = true;
                            ((CogRectangle)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangle)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedSpaceName = Blobtemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogRectangle)cra2[Blobtemp1.Region.GetType().ToString()]).GraphicDOFEnable = CogRectangleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangle)cra2[Blobtemp1.Region.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogCircle":
                            cra2[Blobtemp1.Region.GetType().ToString()] = (CogCircle)Blobtemp1.Region;
                            ((CogCircle)cra2[Blobtemp1.Region.GetType().ToString()]).Interactive = true;
                            ((CogCircle)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogCircle)cra2[Blobtemp1.Region.GetType().ToString()]).SelectedSpaceName = Blobtemp1.InputImage.SelectedSpaceName;//设置形状的坐标空间
                            ((CogCircle)cra2[Blobtemp1.Region.GetType().ToString()]).GraphicDOFEnable = CogCircleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogCircle)cra2[Blobtemp1.Region.GetType().ToString()]), "roi", false);
                            break;
                        default:
                            break;
                    };
                }
                else if (tool is CogPMAlignMultiTool)
                {
                    multipm = tool as CogPMAlignMultiTool;
                    switch (multipm.SearchRegion.GetType().ToString())

                    {
                        case "Cognex.VisionPro.CogRectangleAffine":
                            cra2[multipm.SearchRegion.GetType().ToString()] = (CogRectangleAffine)multipm.SearchRegion;
                            ((CogRectangleAffine)cra2[multipm.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogRectangleAffine)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangleAffine)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedSpaceName = ".";//设置形状的坐标空间
                            ((CogRectangleAffine)cra2[multipm.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangleAffine)cra2[multipm.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogPolygon":
                            cra2[multipm.SearchRegion.GetType().ToString()] = (CogPolygon)multipm.SearchRegion;
                            ((CogPolygon)cra2[multipm.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogPolygon)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogPolygon)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedSpaceName = ".";//设置形状的坐标空间
                            ((CogPolygon)cra2[multipm.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogPolygonDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogPolygon)cra2[multipm.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogRectangle":
                            cra2[multipm.SearchRegion.GetType().ToString()] = (CogRectangle)multipm.SearchRegion;
                            ((CogRectangle)cra2[multipm.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogRectangle)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogRectangle)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedSpaceName = ".";//设置形状的坐标空间
                            ((CogRectangle)cra2[multipm.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogRectangleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogRectangle)cra2[multipm.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        case "Cognex.VisionPro.CogCircle":
                            cra2[multipm.SearchRegion.GetType().ToString()] = (CogCircle)multipm.SearchRegion;
                            ((CogCircle)cra2[multipm.SearchRegion.GetType().ToString()]).Interactive = true;
                            ((CogCircle)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                            ((CogCircle)cra2[multipm.SearchRegion.GetType().ToString()]).SelectedSpaceName = ".";//设置形状的坐标空间
                            ((CogCircle)cra2[multipm.SearchRegion.GetType().ToString()]).GraphicDOFEnable = CogCircleDOFConstants.All;
                            record.InteractiveGraphics.Add(((CogCircle)cra2[multipm.SearchRegion.GetType().ToString()]), "roi", false);
                            break;
                        default:
                            break;
                    };
                }
            }
        }
        private void roiset1(CogToolBlock blocktool, string name, bool check)
        {
            CogPMAlignTool Pmatemp1;
            CogBlobTool Blobtemp1;
            if (check == true)
            {
                if (name.Contains("CogPMAlignTool") || name.Contains("定位") || name.Contains("模板"))
                {
                    Pmatemp1 = blocktool.Tools[name] as CogPMAlignTool;
                    cra = new CogRectangleAffine();
                    cra = (CogRectangleAffine)Pmatemp1.SearchRegion;
                    cra.Interactive = true;
                    cra.SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                    cra.SelectedSpaceName = "#";//设置形状的坐标空间
                    cra.GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                    PrepareRoiDisplay(pictureBoxCam1, blocktool, true).InteractiveGraphics.Add(cra, "roi", false);
                }
                if (name.Contains("CogBlobTool") || name.Contains("斑点"))
                {
                    Blobtemp1 = blocktool.Tools[name] as CogBlobTool;
                    cra = new CogRectangleAffine();
                    cra = (CogRectangleAffine)Blobtemp1.Region;
                    cra.Interactive = true;
                    cra.SelectedColor = CogColorConstants.Yellow;//选中时图形的颜色
                    cra.SelectedSpaceName = "#";//设置形状的坐标空间
                    cra.GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
                    PrepareRoiDisplay(pictureBoxCam1, blocktool, true).InteractiveGraphics.Add(cra, "roi", false);
                }
            }
            else
            {
                cra.Interactive = false;
                cra = null;
                HideRoiRendererIfHost(pictureBoxCam1);
            }
        }
        private void 注销ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            frm5.mark = 0;
            this.Invoke(new Action(() =>
            {
                设置ToolStripMenuItem.Enabled = false;
                numericUpDown22.Enabled = false;
                textBox3.Enabled = false;
                comboBox1.Enabled = false;
                comboBox4.Enabled = false;
                comboBox5.Enabled = false;
                comboBox8.Enabled = false;
                button18.Enabled = false;
                button31.Enabled = false;
                button19.Enabled = false;
                button33.Enabled = false;
                button34.Enabled = false;
                button21.Enabled = false;
                button28.Enabled = false;
                button29.Enabled = false;
                button30.Enabled = false;
                触发设置ToolStripMenuItem.Checked = false;
                输出时间ToolStripMenuItem.Checked = false;
                存图测试1ToolStripMenuItem.Checked = false;

                rOI设置ToolStripMenuItem.Checked = false;
                foreach (Control item in this.panel1.Controls)
                {
                    if (item is Form)
                    {
                        ((Form)item).Close();
                    }
                }

                groupBox1.Visible = true;
                groupBox5.Visible = true;
                groupBox7.Visible = true;
                groupBox8.Visible = true;
                groupBox18.Visible = true;
                groupBox19.Visible = true;
                groupBox21.Visible = true;
                groupBox22.Visible = true;
            }));
        }

        private void 用户登录ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (frm5.Visible == false)
                frm5.Visible = true;
            else
                frm5.Visible = false;
        }

        private void timer6_Tick(object sender, EventArgs e)
        {
            if (button1.BackColor == Color.Green)
                frm5.monitor = 1;
            else
                frm5.monitor = 0;
            //this.Invoke(new Action(() =>
            // {

            // }));
        }

        private void button5_Click_1(object sender, EventArgs e)
        {
            if ((int)DateTime.Now.ToOADate() > 44460 && textBox4.Text.Trim().Length > 10)
            {
                try
                {
                    int time111;
                    canshuIni.WriteString("code1", "code2", textBox4.Text.Trim());
                    time111 = int.Parse(textBox4.Text.Trim().Substring(int.Parse(textBox4.Text.Trim().Substring(textBox4.Text.Trim().Length - 2)), 5));
                    duini.WriteString("1", "4", ((int)DateTime.Now.ToOADate()).ToString());
                    duini.WriteString("1", "3", ((int)DateTime.Now.ToOADate()).ToString());
                    duini.WriteString("1", "2", time111.ToString());
                    MessageBox.Show("解码结束，请重启软件!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }
        CogRectangleAffine cra;

        private void rOIToolStripMenuItem_Click(object sender, EventArgs e)
        {
            cra = new CogRectangleAffine();
            cra = (CogRectangleAffine)pma.SearchRegion;
            // cra.SetCenterLengthsRotationSkew(50, 50, 100, 100, 0, 0);
            cra.Interactive = true;
            cra.GraphicDOFEnable = CogRectangleAffineDOFConstants.All;
            PrepareRoiDisplay(pictureBoxCam1, myjob1.block, true).InteractiveGraphics.Add(cra, "roi", false);
        }

        private void 存图测试2ToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void 另存为ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveFileDialog openFileDialog = new SaveFileDialog();
            openFileDialog.RestoreDirectory = true;
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                if (openFileDialog.FileName != "")
                {
                    if (!openFileDialog.FileName.Contains(".vpp"))
                    {
                        openFileDialog.FileName = openFileDialog.FileName + ".vpp";
                        path_1 = openFileDialog.FileName;
                    }
                    else
                        path_1 = openFileDialog.FileName;
                }
                if (label75.Text != openFileDialog.FileName.Split('\\').Last())
                {
                    label75.Text = openFileDialog.FileName.Split('\\').Last();
                }
                try
                {
                    wenjianjia = Path.GetDirectoryName(path_1);
                    MsgErroeLog.WriteLog("方案文件夹:" + wenjianjia);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                }
            }
            else
                return;
            // ch:运行中保存会与回调线程的工具块执行并发序列化，先停止采集再保存
            if (yunxing)
            {
                button11_Click_1(null, null);
                Thread.Sleep(300);
            }
            CogSerializer.SaveObjectToFile(manager1, path_1);
            MessageBox.Show("保存方案成功!" + (yunxing ? "（已停止运行，请重新开始检测）" : ""));
        }

        private void 打开ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                label75.Text = openFileDialog.FileName.Split('\\').Last();
                path_1 = openFileDialog.FileName;
                try
                {
                    wenjianjia = Path.GetDirectoryName(path_1);
                    MsgErroeLog.WriteLog("方案文件夹:" + wenjianjia);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                }
            }
            else
                return;
            StreamReader sr = null;
            try
            {
                sr = new StreamReader(Path.GetDirectoryName(path_1) + "\\Menu.ini");
                int i = 0;
                while (sr.Peek() >= 0)
                {
                    i++;
                    sr.ReadLine();
                }
                sr.Dispose();
                sr.Close();
                if (i > 5)
                {
                    FileStream stream = null;
                    try
                    {
                        stream = File.Open(Path.GetDirectoryName(path_1) + "\\Menu.ini", FileMode.OpenOrCreate, FileAccess.Write);
                        stream.Seek(0, SeekOrigin.Begin);
                        stream.SetLength(0);
                        stream.Flush();
                        stream.Close();
                    }
                    catch
                    {
                        stream.Flush();
                        stream.Close();
                    }
                }
            }
            catch
            {
                try
                {
                    sr.Dispose();
                    sr.Close();
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }

            };
            if (this.设置ToolStripMenuItem.DropDownItems[this.设置ToolStripMenuItem.DropDownItems.Count - 1].Text != path_1)
            {
                StreamWriter s = new StreamWriter(Path.GetDirectoryName(path_1) + "\\Menu.ini", true);
                s.WriteLine(path_1);
                s.Flush();
                s.Close();
            }
            xinghao_qiehuan("");
        }
        int qiehuanzhong = 1;
        private int _detectingCount = 0; // ch:正在执行 block.Run 的检测帧数（参考正常版本，切换方案排空等待用，Interlocked 操作）
        private int _lastDropLogMs = 0; // ch:回调丢帧限频日志时间戳（防呆，避免静默丢帧无法排查）

        // ================= 性能统计（新增） =================
        // ch:多相机高频触发时用于定位 CPU 瓶颈，每 5 秒汇总一次写入日志（日志本身有锁且失败静默，不影响主流程）。
        //    关键指标「占用」= 回调累计耗时 / 窗口时长，即海康 SDK 取流线程被本程序占用的比例，
        //    接近 100% 说明该相机的采集线程已饱和，再提频只会丢帧。
        private static readonly long[] _perfCbTicks = new long[8];    // ch:回调累计耗时(ticks)
        private static readonly int[] _perfCbCount = new int[8];      // ch:回调次数
        private static readonly int[] _perfDrop = new int[8];         // ch:被背压丢弃的帧数
        private static readonly long[] _perfRunTicks = new long[8];   // ch:block.Run 累计耗时(ticks)
        private static readonly int[] _perfRunCount = new int[8];     // ch:block.Run 次数
        // ch:P1 同步尾部埋点：block.Run 结束 → getrecord 返回 之间的耗时，即那 12 个 Task.Run 真正为帧率省下的上限。
        //    feng=0（不限速）时这段直接进入帧周期，是判断"Task.Run 能否内联/能否合并"的唯一依据。
        private static readonly long[] _perfTailTicks = new long[8];  // ch:同步尾部累计耗时(ticks)
        private static readonly int[] _perfTailCount = new int[8];    // ch:同步尾部采样次数
        private static readonly long[] _perfTailStart = new long[8];  // ch:尾部起点 ticks（每相机由单一检测线程串行执行，天然无并发）
        private static long _perfWinStart = 0;                        // ch:统计窗口起点(ms)
        private static int _perfReporting = 0;                        // ch:窗口汇总单线程保护
        private static readonly System.Diagnostics.Stopwatch _perfSw = System.Diagnostics.Stopwatch.StartNew();
        private static readonly long[] _lastProcMs = new long[8];  // ch:每相机上次开始处理的时刻(ms)，封程限速用
        private void xinghao_qiehuan(string a)
        {
            // ch:P2 方案切换闩改原子 check-then-set：原 if(==0){=1;} 是 TOCTOU——
            //   三条通讯通道的 cam10 事件可各自通过外层预筛(==0)后同时进到这里，导致两个 xinghao_qiehuan 并发做 Shutdown/重载。
            //   权威闩由 CompareExchange 把守；外层 7463/7700/7937 的 ==0 仅作预筛，保持不变。
            if (System.Threading.Interlocked.CompareExchange(ref qiehuanzhong, 1, 0) == 0)
            {
                if (a.Contains(".vpp"))
                {
                    label75.Text = a.Split('\\').Last();
                    path_1 = a;
                    try
                    {
                        wenjianjia = Path.GetDirectoryName(path_1);
                        MsgErroeLog.WriteLog("方案文件夹:" + wenjianjia);
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog(ex.Message);
                    }
                }
                if (yunxing == true)
                    button11_Click_1(null, null);
                yunxing = false;

                if (omron.qiehuanzhong == 0)
                {
                    Frm2 = new Frm2();
                    Frm2.Show();
                    Frm2.start = 0;
                }
                DeviceListAcq();
                bnClose_Click(null, null);
                Task.Run(() =>
                {
                    myjob1.baoguang = 0;
                    myjob2.baoguang = 0;
                    myjob3.baoguang = 0;
                    myjob4.baoguang = 0;
                    myjob5.baoguang = 0;
                    myjob6.baoguang = 0;
                    myjob7.baoguang = 0;
                    myjob8.baoguang = 0;
                    int bbtemp = 7368;
                    try
                    {
                        listBox2.Visible = false;
                        listBox2.Items.Clear();
                        // ch:排空：等待正在执行的检测帧（block.Run）结束（最长 3 秒），防止 Shutdown 与 Run 并发（参考正常版本）
                        if (!WaitDetectDrain(5000))
                            MsgErroeLog.WriteLog("P1-06 切换：检测排空超时(5000ms)，仍有在途检测帧；Shutdown 可能与检测并发");
                        try
                        {
                            manager1.Shutdown();
                            Thread.Sleep(500);
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog("关闭方案失败!--" + ex.Message);
                        }
                        try
                        {
                            manager1 = (CogJobManager)CogSerializer.LoadObjectFromFile(path_1);
                        }
                        catch (Exception ex)
                        {
                            label75.Text = label75.Text + "方案已损坏";
                            MsgErroeLog.WriteLog(ex.Message + "加载方案失败");
                            manager1 = null;
                            this.BeginInvoke(new Action(() =>
                                MessageBox.Show("方案文件加载失败，请检查方案路径配置：" + ex.Message, "方案加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error)
                            ));
                        }
                        try
                        {
                            manager1.UserQueueFlush();
                            manager1.FailureQueueFlush();
                            myjob1.job = manager1.Job(0);
                            myIndependentJob = myjob1.job.OwnedIndependent;
                            myjob1.job.ImageQueueFlush();
                            try { if (myjob1.Cogbmp != null) myjob1.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                            myjob1.Cogbmp = new CogImageFileBMP();
                            myIndependentJob.RealTimeQueueFlush();
                            listBox2.Items.Add("产品类型:相机1");
                            listBox2.Items.Add("检测数:");
                            listBox2.Items.Add("OK数:");
                            listBox2.Items.Add("NG数:");
                            listBox2.Items.Add("合格率:");
                            listBox2.Items.Add("~~~~~~~~~");

                            group_1 = myjob1.job.VisionTool as CogToolGroup;
                            block_1 = group_1.Tools["CogToolBlock1"] as CogToolBlock;
                            myjob1.block = block_1.Tools["CogToolBlock1"] as CogToolBlock;
                            // path_1 = @".\test.vpp";
                            pictureBoxCam1.Invalidate();
                            if ((myjob1.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                myjob1.Color = false;
                            else
                                myjob1.Color = true;
                            try
                            {
                                string aatemp = myjob1.block.Outputs["tishi"].Value.ToString();
                                myjob1.tishi = true;
                            }
                            catch
                            {
                                myjob1.tishi = false;
                            }
                            try
                            {
                                calibdrop(myjob1.block, out myjob1.calib);
                                if (myjob1.calib != null)
                                {
                                    myjob1.myTable1.Clear();
                                    myjob1.myTable1.Columns.Add("像素X");
                                    myjob1.myTable1.Columns.Add("像素Y");
                                    myjob1.myTable1.Columns.Add("实际X");
                                    myjob1.myTable1.Columns.Add("实际Y");
                                    if (!myjob1.calib.Calibration.Calibrated)
                                        myjob1.calib.Calibration.NumPoints = 9;
                                    for (int i = 0; i < myjob1.calib.Calibration.NumPoints; i++)

                                    {
                                        myjob1.myTable1.Rows.Add();
                                        myjob1.myTable1.Rows[i]["像素X"] = myjob1.calib.Calibration.GetUncalibratedPointX(i);
                                        myjob1.myTable1.Rows[i]["像素Y"] = myjob1.calib.Calibration.GetUncalibratedPointY(i);
                                        myjob1.myTable1.Rows[i]["实际X"] = myjob1.calib.Calibration.GetRawCalibratedPointX(i);
                                        myjob1.myTable1.Rows[i]["实际Y"] = myjob1.calib.Calibration.GetRawCalibratedPointY(i);
                                    }
                                }
                            }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                            if (manager1.JobCount > 1)
                            {
                                myjob2.job = manager1.Job(1);
                                myIndependentJob = myjob2.job.OwnedIndependent;
                                myjob2.job.ImageQueueFlush();
                                try { if (myjob2.Cogbmp != null) myjob2.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob2.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机2");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");

                                group_2 = myjob2.job.VisionTool as CogToolGroup;
                                block_2 = group_2.Tools["CogToolBlock1"] as CogToolBlock;
                                myjob2.block = block_2.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob2.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob2.Color = false;
                                else
                                    myjob2.Color = true;
                                //  myjob2.CogFifo = block_2.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                //td2 = new Thread(new ThreadStart(getrecord_2));
                                //td2.Start();
                                // dataGridView2.ReadOnly = true;
                                // dataGridView2.AllowUserToAddRows = false;
                                //  dataGridView2.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                                try
                                {
                                    string aatemp = myjob2.block.Outputs["tishi"].Value.ToString();
                                    myjob2.tishi = true;
                                }
                                catch
                                {
                                    myjob2.tishi = false;
                                }
                                try
                                {
                                    calibdrop(myjob2.block, out myjob2.calib);
                                    if (myjob2.calib != null)
                                    {
                                        myjob2.myTable1.Clear();
                                        myjob2.myTable1.Columns.Add("像素X");
                                        myjob2.myTable1.Columns.Add("像素Y");
                                        myjob2.myTable1.Columns.Add("实际X");
                                        myjob2.myTable1.Columns.Add("实际Y");
                                        if (!myjob2.calib.Calibration.Calibrated)
                                            myjob2.calib.Calibration.NumPoints = 9;
                                        for (int i = 0; i < myjob2.calib.Calibration.NumPoints; i++)
                                        {
                                            myjob2.myTable1.Rows.Add();
                                            myjob2.myTable1.Rows[i]["像素X"] = myjob2.calib.Calibration.GetUncalibratedPointX(i);
                                            myjob2.myTable1.Rows[i]["像素Y"] = myjob2.calib.Calibration.GetUncalibratedPointY(i);
                                            myjob2.myTable1.Rows[i]["实际X"] = myjob2.calib.Calibration.GetRawCalibratedPointX(i);
                                            myjob2.myTable1.Rows[i]["实际Y"] = myjob2.calib.Calibration.GetRawCalibratedPointY(i);
                                        }
                                    }
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                            }
                            if (manager1.JobCount > 2)
                            {
                                myjob3.job = manager1.Job(2);
                                myIndependentJob = myjob3.job.OwnedIndependent;
                                myjob3.job.ImageQueueFlush();
                                try { if (myjob3.Cogbmp != null) myjob3.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob3.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机3");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                group_3 = myjob3.job.VisionTool as CogToolGroup;
                                bbtemp = 7462;
                                block_3 = group_3.Tools["CogToolBlock1"] as CogToolBlock;
                                bbtemp = 7464;
                                //  myjob3.CogFifo = block_3.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob3.block = block_3.Tools["CogToolBlock1"] as CogToolBlock;
                                bbtemp = 7467;
                                if ((myjob3.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob3.Color = false;
                                else
                                    myjob3.Color = true;
                                bbtemp = 7472;
                                //td3 = new Thread(new ThreadStart(getrecord_3));
                                //td3.Start();
                                bbtemp = 7475;
                                try
                                {
                                    string aatemp = myjob3.block.Outputs["tishi"].Value.ToString();
                                    myjob3.tishi = true;
                                }
                                catch
                                {
                                    myjob3.tishi = false;
                                }
                            }
                            if (manager1.JobCount > 3)
                            {
                                myjob4.job = manager1.Job(3);
                                myIndependentJob = myjob4.job.OwnedIndependent;
                                myjob4.job.ImageQueueFlush();
                                try { if (myjob4.Cogbmp != null) myjob4.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob4.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机4");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                group_4 = myjob4.job.VisionTool as CogToolGroup;
                                block_4 = group_4.Tools["CogToolBlock1"] as CogToolBlock;

                                // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob4.block = block_4.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob4.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob4.Color = false;
                                else
                                    myjob4.Color = true;
                                // td4 = new Thread(new ThreadStart(getrecord_4));
                                // td4.Start();
                                try
                                {
                                    string aatemp = myjob4.block.Outputs["tishi"].Value.ToString();
                                    myjob4.tishi = true;
                                }
                                catch
                                {
                                    myjob4.tishi = false;
                                }
                            }
                            if (manager1.JobCount > 4)
                            {
                                myjob5.job = manager1.Job(4);
                                myIndependentJob = myjob5.job.OwnedIndependent;
                                myjob5.job.ImageQueueFlush();
                                try { if (myjob5.Cogbmp != null) myjob5.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob5.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机5");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                //listBox14.Items.Add("record");
                                //listBox14.Items.Add("record");
                                //listBox14.Items.Add("record");
                                //listBox14.Items.Add("record");
                                //listBox14.Items.Add("record");
                                group_5 = myjob5.job.VisionTool as CogToolGroup;
                                block_5 = group_5.Tools["CogToolBlock1"] as CogToolBlock;

                                // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob5.block = block_5.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob5.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob5.Color = false;
                                else
                                    myjob5.Color = true;
                                // td5 = new Thread(new ThreadStart(getrecord_5));
                                // td5.Start();
                                try
                                {
                                    string aatemp = myjob5.block.Outputs["tishi"].Value.ToString();
                                    myjob5.tishi = true;
                                }
                                catch
                                {
                                    myjob5.tishi = false;
                                }
                            }
                            if (manager1.JobCount > 5)
                            {
                                myjob6.job = manager1.Job(5);
                                myIndependentJob = myjob6.job.OwnedIndependent;
                                myjob6.job.ImageQueueFlush();
                                try { if (myjob6.Cogbmp != null) myjob6.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob6.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机6");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                //listBox15.Items.Add("record");
                                //listBox15.Items.Add("record");
                                //listBox15.Items.Add("record");
                                //listBox15.Items.Add("record");
                                //listBox15.Items.Add("record");
                                group_6 = myjob6.job.VisionTool as CogToolGroup;
                                block_6 = group_6.Tools["CogToolBlock1"] as CogToolBlock;

                                // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob6.block = block_6.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob6.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob6.Color = false;
                                else
                                    myjob6.Color = true;
                                // td6 = new Thread(new ThreadStart(getrecord_6));
                                // td6.Start();
                                try
                                {
                                    string aatemp = myjob6.block.Outputs["tishi"].Value.ToString();
                                    myjob6.tishi = true;
                                }
                                catch
                                {
                                    myjob6.tishi = false;
                                }
                            }
                            if (manager1.JobCount > 6)
                            {
                                myjob7.job = manager1.Job(6);
                                myIndependentJob = myjob7.job.OwnedIndependent;
                                myjob7.job.ImageQueueFlush();
                                try { if (myjob7.Cogbmp != null) myjob7.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob7.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机7");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                //listBox18.Items.Add("record");
                                //listBox18.Items.Add("record");
                                //listBox18.Items.Add("record");
                                //listBox18.Items.Add("record");
                                //listBox18.Items.Add("record");
                                group_7 = myjob7.job.VisionTool as CogToolGroup;
                                block_7 = group_7.Tools["CogToolBlock1"] as CogToolBlock;

                                // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob7.block = block_7.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob7.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob7.Color = false;
                                else
                                    myjob7.Color = true;
                                // td7 = new Thread(new ThreadStart(getrecord_7));
                                // td7.Start();
                                try
                                {
                                    string aatemp = myjob7.block.Outputs["tishi"].Value.ToString();
                                    myjob7.tishi = true;
                                }
                                catch
                                {
                                    myjob7.tishi = false;
                                }
                            }
                            if (manager1.JobCount > 7)
                            {
                                myjob8.job = manager1.Job(7);
                                myIndependentJob = myjob8.job.OwnedIndependent;
                                myjob8.job.ImageQueueFlush();
                                try { if (myjob8.Cogbmp != null) myjob8.Cogbmp.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                myjob8.Cogbmp = new CogImageFileBMP();
                                myIndependentJob.RealTimeQueueFlush();
                                listBox2.Items.Add("产品类型:相机8");
                                listBox2.Items.Add("检测数:");
                                listBox2.Items.Add("OK数:");
                                listBox2.Items.Add("NG数:");
                                listBox2.Items.Add("合格率:");
                                listBox2.Items.Add("~~~~~~~~~");
                                //listBox17.Items.Add("record");
                                //listBox17.Items.Add("record");
                                //listBox17.Items.Add("record");
                                //listBox17.Items.Add("record");
                                //listBox17.Items.Add("record");
                                group_8 = myjob8.job.VisionTool as CogToolGroup;
                                block_8 = group_8.Tools["CogToolBlock1"] as CogToolBlock;

                                // myjob4.CogFifo = block_4.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                                myjob8.block = block_8.Tools["CogToolBlock1"] as CogToolBlock;
                                if ((myjob8.block.Inputs["Input"]).ValueType == typeof(CogImage8Grey))
                                    myjob8.Color = false;
                                else
                                    myjob8.Color = true;
                                // td8 = new Thread(new ThreadStart(getrecord_8));
                                // td8.Start();
                            }
                        }
                        catch
                        {
                            MsgErroeLog.WriteLog("无流程4");
                        }
                        listBox2.Visible = true;
                        // ch:方案 JobCount 不足的相机清空 job/block，防止执行悬垂的旧 block（参考正常版本：en 按 JobCount 收敛）
                        if (manager1.JobCount < 8) { myjob8.job = null; myjob8.block = null; }
                        if (manager1.JobCount < 7) { myjob7.job = null; myjob7.block = null; }
                        if (manager1.JobCount < 6) { myjob6.job = null; myjob6.block = null; }
                        if (manager1.JobCount < 5) { myjob5.job = null; myjob5.block = null; }
                        if (manager1.JobCount < 4) { myjob4.job = null; myjob4.block = null; }
                        if (manager1.JobCount < 3) { myjob3.job = null; myjob3.block = null; }
                        if (manager1.JobCount < 2) { myjob2.job = null; myjob2.block = null; }
                        if (manager1.JobCount < 1) { myjob1.job = null; myjob1.block = null; }

                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog(ex.Message + "切换方案1:" + bbtemp);
                    };
                    try
                    {
                        //group_1 = myjob1.job.VisionTool as CogToolGroup;
                        //block_1 = group_1.Tools["CogToolBlock1"] as CogToolBlock;
                        //myjob1.block = block_1.Tools["CogToolBlock1"] as CogToolBlock;
                        //  myjob1.CogFifo = block_1.Tools["CogAcqFifoTool1"] as CogAcqFifoTool;
                        //if (myjob1.CogFifo.Operator.OwnedTriggerParams.TriggerModel == CogAcqTriggerModelConstants.Manual)
                        //    comboBox1.Text = "连续运行";
                        //if (myjob1.CogFifo.Operator.OwnedTriggerParams.TriggerModel == CogAcqTriggerModelConstants.Auto)
                        //    comboBox1.Text = "触发拍照";

                        //Frm2.start = 1;
                        // yunxing = true;
                        checkedListBox1.Enabled = false;
                        if (omron.qiehuanzhong == 0)
                        {
                            Frm2.start = 1;
                        }
                        //  Frm2.Close();
                        //CogFrameGrabberGigEs mf2 = new CogFrameGrabberGigEs();//获取已连接相机列表
                        // if (mf2.Count == 0)
                        //    MessageBox.Show("没有连接到相机！");
                        bnOpen_Click(null, null);

                        //this.FormBorderStyle = FormBorderStyle.FixedSingle;
                        trriger_set();
                        myjob1.baoguang = 0;
                        myjob2.baoguang = 0;
                        myjob3.baoguang = 0;
                        myjob4.baoguang = 0;
                        myjob5.baoguang = 0;
                        myjob6.baoguang = 0;
                        myjob7.baoguang = 0;
                        myjob8.baoguang = 0;
                        baoguang_set();
                        Thread.Sleep(100);
                        bnClose.Enabled = true;

                        // ch:P2 收紧：切换方案后的自动参数下发同样只对使能相机执行（取流按钮状态维持原逻辑）
                        if (myjob1.en == 1)
                        {
                            bnSetParam_Click(null, null);
                            bnGetParam_Click(null, null);// ch:获取参数 | en:Get parameters
                        }
                        bnStartGrab1.Enabled = false;
                        bnStopGrab1.Enabled = true;
                        if (manager1.JobCount > 1)
                        {
                            bnStartGrab2.Enabled = false;
                            bnStopGrab2.Enabled = true;
                            if (myjob2.en == 1)
                            {
                                bnSetParam2_Click(null, null);
                                bnGetParam2_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 2)
                        {
                            bnStartGrab3.Enabled = false;
                            bnStopGrab3.Enabled = true;
                            if (myjob3.en == 1)
                            {
                                bnSetParam3_Click(null, null);
                                bnGetParam3_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 3)
                        {
                            bnStartGrab4.Enabled = false;
                            bnStopGrab4.Enabled = true;
                            if (myjob4.en == 1)
                            {
                                bnSetParam4_Click(null, null);
                                bnGetParam4_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 4)
                        {
                            bnStartGrab5.Enabled = false;
                            bnStopGrab5.Enabled = true;
                            if (myjob5.en == 1)
                            {
                                bnSetParam5_Click(null, null);
                                bnGetParam5_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 5)
                        {
                            bnStartGrab6.Enabled = false;
                            bnStopGrab6.Enabled = true;
                            if (myjob6.en == 1)
                            {
                                bnSetParam6_Click(null, null);
                                bnGetParam6_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 6)
                        {
                            bnStartGrab7.Enabled = false;
                            bnStopGrab7.Enabled = true;
                            if (myjob7.en == 1)
                            {
                                bnSetParam7_Click(null, null);
                                bnGetParam7_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        if (manager1.JobCount > 7)
                        {
                            bnStartGrab8.Enabled = false;
                            bnStopGrab8.Enabled = true;
                            if (myjob8.en == 1)
                            {
                                bnSetParam8_Click(null, null);
                                bnGetParam8_Click(null, null);// ch:获取参数 | en:Get parameters
                            }
                        }
                        comboBox38_TextChanged(null, null);

                        button1_Click(null, null);
                        Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族
                        // display();
                    }
                    catch (Exception ex)
                    {
                        Thread.Sleep(2000);
                        //if (ex.Message.Contains("未能找到文件"))
                        //    textBoxSolutionPath.Text = "无方案!!!!";
                        if (omron.qiehuanzhong == 0)
                        {
                            Frm2.start = 1;
                        }
                        //Frm2.Close();
                        Volatile.Write(ref qiehuanzhong, 0); // ch:P2 统一 Volatile 族
                        MsgErroeLog.WriteLog(ex.Message + "切换方案2");
                    };
                });
            }
        }
        private void 配置工具ToolStripMenuItem_Click(object sender, EventArgs e)
        {

            OpenToolBlockEditor(myjob1.block);
        }

        private void 配置相机2ToolStripMenuItem_Click(object sender, EventArgs e)
        {

            OpenToolBlockEditor(myjob2.block);

        }


        private void 设置ToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            //pma = myjob2.block.Tools["CogPMAlignTool1"] as CogPMAlignTool;
            //rect= new CogRectangle();
            StreamReader sr = null;
            try
            {

                int i = this.设置ToolStripMenuItem.DropDownItems.Count;
                if (i > item_sum)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (j >= item_sum)
                        {
                            this.设置ToolStripMenuItem.DropDownItems.RemoveAt(item_sum);
                        }
                    }
                }
                // Thread.Sleep(10);
                sr = new StreamReader(Path.GetDirectoryName(path_1) + "\\Menu.ini");
                i = item_sum;
                while (sr.Peek() >= 0)
                {
                    //string item_temp = sr.ReadLine();
                    //int end_temp=0;
                    //for (int j = 0; j < this.设置ToolStripMenuItem.DropDownItems.Count; j++)
                    //{
                    //    end_temp = 0;
                    //    if (item_temp == this.设置ToolStripMenuItem.DropDownItems[j].Text)
                    //    {
                    //        end_temp = 1;
                    //    }

                    //    if (end_temp == 0)
                    //    {
                    menuitem = new ToolStripMenuItem(sr.ReadLine());
                    this.设置ToolStripMenuItem.DropDownItems.Insert(i, menuitem);
                    i++;
                    menuitem.Click += new EventHandler(menuitem_Click);
                    //    }
                    //}

                }
                sr.Dispose();
                sr.Close();
            }
            catch
            {
                try
                {
                    sr.Dispose();
                    sr.Close();
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
        }


        private void button13_Click(object sender, EventArgs e)
        {
            trriger1_temp = 0;
            timer7.Enabled = false;
            StopAllIoPulses();
        }

        private void textBox6_TextChanged(object sender, EventArgs e)
        {
            try
            {
                timer7.Interval = int.Parse(textBox6.Text);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void timer7_Tick(object sender, EventArgs e)
        {
            if (myjob1.trriger == 0)
            {
                if (trriger1_temp == 1)
                {
                    int count = listBox4.Items.Count;
                    int select = listBox4.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox4.SelectedIndex = select + 1;
                        }
                        else
                            listBox4.SelectedIndex = 0;
                    }));
                }

            }
        }


        private void timer8_Tick(object sender, EventArgs e)
        {
            if (myjob2.trriger == 0)
            {
                if (trriger2_temp == 1)
                {
                    int count = listBox5.Items.Count;
                    int select = listBox5.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox5.SelectedIndex = select + 1;
                        }
                        else
                            listBox5.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void button7_Click_3(object sender, EventArgs e)
        {
            trriger2_temp = 0;
            timer8.Enabled = false;
            StopAllIoPulses();
        }


        private void timer9_Tick(object sender, EventArgs e)
        {

        }
        ICogRecord aaaa;
        private void timer10_Tick(object sender, EventArgs e)
        {
            try
            {
                if (frm3.lujing != null && frm3.lujing.Length >= 3)
                {
                    xinghao_qiehuan(frm3.lujing);
                    frm3.lujing = "";
                    frm3.zifu = "";
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void button15_Click(object sender, EventArgs e)
        {
            trriger3_temp = 0;
            timer10.Enabled = false;
            StopAllIoPulses();
        }

        private void button18_Click(object sender, EventArgs e)
        {
            trriger4_temp = 0;
            timer11.Enabled = false;
            StopAllIoPulses();
        }

        private void timer11_Tick(object sender, EventArgs e)
        {
            if (myjob3.trriger == 0)
            {
                if (trriger3_temp == 1)
                {
                    int count = listBox8.Items.Count;
                    int select = listBox8.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox8.SelectedIndex = select + 1;
                        }
                        else
                            listBox8.SelectedIndex = 0;
                    }));
                }

            }
        }



        private void listBox1_MouseDown(object sender, MouseEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    string[] time111 = listBox1.SelectedItem.ToString().Split(':');
                    int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                    string ttt2 = time111[3];
                    int ttt3 = int.Parse(time111[4]);
                    Process myProc = null;
                    myProc = Process.Start(myjob1.pathhead_ng + day1 + "\\" + ttt1 + ttt2 + "#" + ttt3 + ".bmp");//开启一个进程
                    try
                    {
                        myProc.Kill();//关闭一个进程
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "图片显示1"); };
            });
        }


        private void bnEnum_Click(object sender, EventArgs e)
        {
            DeviceListAcq();
        }

        // ch:提取设备标识（类型/名称/SN/IP）用于日志定位
        private string DevInfoString(ref MyCamera.MV_CC_DEVICE_INFO devInfo)
        {
            try
            {
                if (devInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    MyCamera.MV_GIGE_DEVICE_INFO g = (MyCamera.MV_GIGE_DEVICE_INFO)MyCamera.ByteToStruct(devInfo.SpecialInfo.stGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    UInt32 ip = g.nCurrentIp;
                    string ipStr = ((ip >> 24) & 0xFF) + "." + ((ip >> 16) & 0xFF) + "." + ((ip >> 8) & 0xFF) + "." + (ip & 0xFF);
                    // ch:nNetExport=SDK 记录发现该设备的出口网卡 IP（多网卡时用于确认端点绑定）
                    string exportStr = "";
                    try
                    {
                        UInt32 exp = g.nNetExport;
                        exportStr = ((exp >> 24) & 0xFF) + "." + ((exp >> 16) & 0xFF) + "." + ((exp >> 8) & 0xFF) + "." + (exp & 0xFF);
                    }
                    catch { exportStr = "?"; }
                    return "GigE 名称[" + g.chUserDefinedName + "] 型号[" + g.chModelName + "] SN[" + g.chSerialNumber + "] IP[" + ipStr + "] 出口网卡[" + exportStr + "]";
                }
                else if (devInfo.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    MyCamera.MV_USB3_DEVICE_INFO u = (MyCamera.MV_USB3_DEVICE_INFO)MyCamera.ByteToStruct(devInfo.SpecialInfo.stUsb3VInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    return "USB3 名称[" + u.chUserDefinedName + "] 型号[" + u.chModelName + "] SN[" + u.chSerialNumber + "]";
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("DevInfoString异常:" + ex.Message); }
            return "未知设备 类型0x" + ((uint)devInfo.nTLayerType).ToString("X8");
        }

        // ch:判断相机 IP 是否与本地任一网卡处于同一网段（跨网段时 GVCP 单播不通，
        // ch:SDK 打开返回 0x80000203 端点错误——这不是"被占用"）
        private bool IsSameSubnetWithLocal(string camIpStr)
        {
            try
            {
                System.Net.IPAddress camIp = System.Net.IPAddress.Parse(camIpStr);
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;
                        byte[] cam = camIp.GetAddressBytes();
                        byte[] loc = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        bool same = true;
                        for (int i = 0; i < 4; i++)
                        {
                            if ((cam[i] & mask[i]) != (loc[i] & mask[i])) { same = false; break; }
                        }
                        if (same) return true;
                    }
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            return false;
        }

        // ch:打开设备（含占用等待重试与 GigE 抢占），成功返回 MV_OK。
        // ch:0x80000203=MV_E_ENDPOINT_INVALID(端点无效)：最常见原因是相机 IP 与电脑网卡不在同一网段（GVCP 控制通道不通），
        // ch:其次才是上次程序异常退出后 GigE 会话未释放（约需60秒）或其他程序独占相机。
        // ch:先等待重试；仍失败则尝试抢占模式（仅GigE有效）。
        // ch:P2 可中断等待：返回 true 表示应中断（程序正在关闭）。
        //   OpenDeviceWithRetry 可能在 UI 线程执行（bnOpen_Click 持全部相机锁），固定 Thread.Sleep 会让关窗/退出被拖住。
        private bool WaitInterruptible(int ms)
        {
            int waited = 0;
            while (waited < ms)
            {
                if (closing) return true;
                Thread.Sleep(50);
                waited += 50;
            }
            return closing;
        }

        // ch:P2-16 回调注册失败=该相机完全不出图，原实现忽略返回值只能事后猜；统一走本方法记日志
        private void RegisterImageCallBackLogged(MyCamera cam, int camIdx)
        {
            if (cam == null) return;
            int r = cam.MV_CC_RegisterImageCallBackEx_NET(cbImage, (IntPtr)camIdx);
            if (r != MyCamera.MV_OK)
                MsgErroeLog.WriteLog("相机" + (camIdx + 1) + "注册图像回调失败:0x" + ((uint)r).ToString("X8"));
        }
        // ch:P1-9 重连成功后需与首次打开同样探测/下发 GigE 最佳包大小（原实现只有打开路径做，重连后包大小回 SDK 默认值，帧率骤降）
        private void ApplyOptimalPacketSizeAfterReconnect(MyCamera cam, ref MyCamera.MV_CC_DEVICE_INFO devInfo, int camNo)
        {
            try
            {
                if (cam == null || devInfo.nTLayerType != MyCamera.MV_GIGE_DEVICE) return;
                int nPacketSize = cam.MV_CC_GetOptimalPacketSize_NET();
                if (nPacketSize > 0)
                {
                    int r = cam.MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)nPacketSize);
                    if (r != MyCamera.MV_OK) MsgErroeLog.WriteLog("重连设置相机" + camNo + "包大小失败:0x" + ((uint)r).ToString("X8"));
                }
                else
                    MsgErroeLog.WriteLog("重连探测相机" + camNo + "包大小失败:" + nPacketSize);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("重连包大小异常 cam" + camNo + ":" + ex.Message); }
        }
        private Int32 OpenDeviceWithRetry(ref MyCamera cam, ref MyCamera.MV_CC_DEVICE_INFO devInfo)
        {
            if (cam == null) cam = new MyCamera();
            System.Diagnostics.Stopwatch swOpen = System.Diagnostics.Stopwatch.StartNew();
            string devStr = DevInfoString(ref devInfo);
            MsgErroeLog.WriteLog("开始打开相机:" + devStr);
            Int32 nRet = cam.MV_CC_CreateDevice_NET(ref devInfo);
            MsgErroeLog.WriteLog("CreateDevice#1 返回 0x" + ((uint)nRet).ToString("X8"));
            if (MyCamera.MV_OK != nRet) { MsgErroeLog.WriteLog("相机打开失败(CreateDevice#1) 耗时" + swOpen.ElapsedMilliseconds + "ms"); return nRet; }
            nRet = cam.MV_CC_OpenDevice_NET();
            MsgErroeLog.WriteLog("OpenDevice#1 返回 0x" + ((uint)nRet).ToString("X8"));
            if (nRet == MyCamera.MV_E_ACCESS_DENIED)
            {
                // ch:等待后重试（CreateDevice 内部会先销毁旧句柄再重建）。
                // ch:注意调用点可能在 UI 线程（bnOpen_Click）持 _cameraLocks（整机按序加锁），重试总时长控制在 7 秒内避免界面长冻结；
                // ch:超出重试窗口的 GigE 会话残留由下面的抢占模式兜底。
                for (int retry = 0; retry < 3 && nRet == MyCamera.MV_E_ACCESS_DENIED; retry++)
                {
                    if (WaitInterruptible(2000)) { MsgErroeLog.WriteLog("程序正在关闭，中止相机打开重试"); break; } // ch:P2 可中断，避免关窗被 6 秒重试拖住
                    nRet = cam.MV_CC_CreateDevice_NET(ref devInfo);
                    MsgErroeLog.WriteLog("重试#" + (retry + 1) + " CreateDevice 返回 0x" + ((uint)nRet).ToString("X8"));
                    if (MyCamera.MV_OK != nRet) break;
                    nRet = cam.MV_CC_OpenDevice_NET();
                    MsgErroeLog.WriteLog("重试#" + (retry + 1) + " OpenDevice 返回 0x" + ((uint)nRet).ToString("X8"));
                }
                // ch:独占重试仍被拒 → GigE 尝试抢占模式（MV_ACCESS_ExclusiveWithSwitch=2），接管残留会话
                if (nRet == MyCamera.MV_E_ACCESS_DENIED && devInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    if (!WaitInterruptible(1000)) // ch:P2 可中断
                    {
                    nRet = cam.MV_CC_OpenDevice_NET(2, 0);
                    MsgErroeLog.WriteLog("抢占模式 OpenDevice(2,0) 返回 0x" + ((uint)nRet).ToString("X8"));
                    }
                }
            }
            swOpen.Stop();
            if (nRet == MyCamera.MV_OK)
                MsgErroeLog.WriteLog("相机打开成功:" + devStr + " 耗时" + swOpen.ElapsedMilliseconds + "ms");
            else
            {
                MsgErroeLog.WriteLog("相机打开失败(最终 0x" + ((uint)nRet).ToString("X8") + "):" + devStr + " 耗时" + swOpen.ElapsedMilliseconds + "ms");
                // ch:网络检查：0x80000203 端点错误最常见原因是相机与电脑网卡跨网段（GVCP 不通）
                if (devInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    try
                    {
                        MyCamera.MV_GIGE_DEVICE_INFO g = (MyCamera.MV_GIGE_DEVICE_INFO)MyCamera.ByteToStruct(devInfo.SpecialInfo.stGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        UInt32 ip = g.nCurrentIp;
                        string ipStr = ((ip >> 24) & 0xFF) + "." + ((ip >> 16) & 0xFF) + "." + ((ip >> 8) & 0xFF) + "." + (ip & 0xFF);
                        if (!IsSameSubnetWithLocal(ipStr))
                            MsgErroeLog.WriteLog("网络检查:相机IP[" + ipStr + "]与电脑网卡不在同一网段，打开失败很可能是跨网段导致(GVCP控制通道不通)。请为网卡添加该网段的IP或调整相机IP。");
                        else
                            MsgErroeLog.WriteLog("网络检查:相机IP[" + ipStr + "]与电脑网卡同网段，失败原因非网段问题（可能是会话残留/占用，等待后重试）");
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("网络检查异常:" + ex.Message); }
                }
            }
            return nRet;
        }

        private void bnOpen_Click(object sender, EventArgs e)
        {
            bool bOpened = false;
            // ch:去重：同一相机可能被 SDK 多次枚举（多网卡/重复发现），按名字只打开一次，
            // ch:否则第二次会"销毁重开"导致自占用 0x80000203(无权限)
            HashSet<string> openedCameras = new HashSet<string>();
            int temp1 = 0;
            int temp2 = 0;
            int temp3 = 0;
            int temp4 = 0;
            int temp5 = 0;
            int temp6 = 0;
            int temp7 = 0;
            int temp8 = 0;
            // ch:判断输入格式是否正确 | en:Determine whether the input format is correct
            try
            {
                int.Parse(tbUseNum.Text);
            }
            catch
            {
                ShowErrorMsg("Please enter correct format!", 0);
                return;
            }
            // ch:获取使用设备的数量 | en:Get Used Device Number
            int nCameraUsingNum = int.Parse(tbUseNum.Text);
            // ch:参数检测 | en:Parameters inspection
            if (nCameraUsingNum <= 0)
            {
                nCameraUsingNum = 1;
            }
            if (nCameraUsingNum > 8)
            {
                nCameraUsingNum = 8;
            }
            if (m_pDeviceList.nDeviceNum == 0 || cbDeviceList.SelectedIndex == -1)
            {
                // ch:启动过程中（initialize_FormSet 自动打开）无相机时不弹窗阻塞 UI，只记日志；手动打开时才提示
                if (Volatile.Read(ref qiehuanzhong) == 1)
                    MsgErroeLog.WriteLog("无可用相机设备，跳过打开（启动中不弹窗）");
                else
                    ShowErrorMsg("No device, please select", 0);
                return;
            }
            int nRet = -1;

            LockAllCameras(); // ch:P3-⑧ 整机互斥：按 0→7 顺序取全部相机锁，替代原单把全局锁
            try
            {
            for (int i = 0, j = 0; j < m_nDevNum; ++i, ++j)
            {
                if (i >= 8) break; // ch:device1 只有 8 个槽位，防止枚举超过 8 台时越界
                try
                {
                    // ch:获取选择的设备信息 | en:Get selected device information
                    device1[i] =
                  (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(m_pDeviceList.pDeviceInfo[j],
                                                                typeof(MyCamera.MV_CC_DEVICE_INFO));
                    // m_MyCamera = new MyCamera[4];
                    if (device1[i].nTLayerType == MyCamera.MV_GIGE_DEVICE)
                    {
                        string nnn = "";
                        MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)MyCamera.ByteToStruct(device1[i].SpecialInfo.stGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                        if (gigeInfo.chUserDefinedName == camera_name[0] || gigeInfo.chUserDefinedName == camera_name[1] || gigeInfo.chUserDefinedName == camera_name[2] || gigeInfo.chUserDefinedName == camera_name[3] || gigeInfo.chUserDefinedName == camera_name[4] || gigeInfo.chUserDefinedName == camera_name[5] || gigeInfo.chUserDefinedName == camera_name[6] || gigeInfo.chUserDefinedName == camera_name[7])
                        {
                            if (gigeInfo.chUserDefinedName == camera_name[0])
                            {
                                myjob1.index = i;
                                nnn = "0";
                                temp1 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[1])
                            {
                                myjob2.index = i;
                                nnn = "1";
                                temp2 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[2])
                            {
                                myjob3.index = i;
                                nnn = "2";
                                temp3 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[3])
                            {
                                myjob4.index = i;
                                nnn = "3";
                                temp4 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[4])
                            {
                                myjob5.index = i;
                                nnn = "4";
                                temp5 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[5])
                            {
                                myjob6.index = i;
                                nnn = "5";
                                temp6 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[6])
                            {
                                myjob7.index = i;
                                nnn = "6";
                                temp7 = 1;
                            }
                            if (gigeInfo.chUserDefinedName == camera_name[7])
                            {
                                myjob8.index = i;
                                nnn = "7";
                                temp8 = 1;
                            }
                            // ch:打开设备 | en:Open device
                            if (!openedCameras.Add("SN:" + gigeInfo.chSerialNumber))
                            {
                                // ch:同一相机重复枚举（多网卡/重复发现）→ 跳过，避免"销毁重开"导致 0x80000203 自占用
                                MsgErroeLog.WriteLog("相机" + (int.Parse(nnn) + 1) + "重复枚举，跳过（防止自占用）");
                                continue;
                            }
                            if (null == m_MyCamera[int.Parse(nnn)])
                            {
                                m_MyCamera[int.Parse(nnn)] = new MyCamera();
                                if (null == m_MyCamera[int.Parse(nnn)])
                                {
                                    return;
                                }
                            }
                            else
                            {
                                // ch:相机已打开过，先停流关闭销毁，避免重复打开同一声明 | en:Close old handle first
                                try
                                {
                                    m_MyCamera[int.Parse(nnn)].MV_CC_StopGrabbing_NET();
                                    m_MyCamera[int.Parse(nnn)].MV_CC_CloseDevice_NET();
                                    m_MyCamera[int.Parse(nnn)].MV_CC_DestroyDevice_NET();
                                }
                                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                m_MyCamera[int.Parse(nnn)] = new MyCamera();
                            }

                            nRet = OpenDeviceWithRetry(ref m_MyCamera[int.Parse(nnn)], ref device1[i]);
                            if (MyCamera.MV_OK != nRet)
                            {
                                // ch:0x80000203=MV_E_ACCESS_DENIED：相机被残留进程/其他程序占用，给出可操作提示
                                if (nRet == MyCamera.MV_E_ACCESS_DENIED)
                                {
                                    MsgErroeLog.WriteLog("相机" + (int.Parse(nnn) + 1) + "打开失败(0x80000203 被占用)");
                                    if (Volatile.Read(ref qiehuanzhong) == 1) // ch:P2 原子读
                                    {
                                        // ch:启动/切换中不逐台弹窗阻塞，先汇总，方法末尾一次性提示
                                        openFailSummary += "相机" + (int.Parse(nnn) + 1) + "、";
                                    }
                                    else
                                        MessageBox.Show("相机" + (int.Parse(nnn) + 1) + "打开失败: Error =80000203\r\n" +
                                            "可能原因：\r\n1) 相机IP与电脑网卡不在同一网段（请为网卡添加相机网段的IP，或用MVS将相机IP改为与电脑同网段）；\r\n" +
                                            "2) 相机被其他程序占用（请关闭海康MVS客户端、Vision Studio等）；\r\n" +
                                            "3) 上次程序异常退出后 GigE 会话残留，约需60秒释放，请稍候重试。",
                                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                                else
                                    ShowErrorMsg("相机" + (int.Parse(nnn) + 1) + "打开失败!", nRet);
                                try { m_MyCamera[int.Parse(nnn)].MV_CC_DestroyDevice_NET(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                m_MyCamera[int.Parse(nnn)] = null; // ch:失败置空，避免残留无效句柄
                                openedCameras.Remove("SN:" + gigeInfo.chSerialNumber); // ch:失败移除去重标记，允许重复枚举条目重试
                                continue;
                            }
                            else
                            {
                                m_nCanOpenDeviceNum++;
                                m_pDeviceInfo[i] = device1[i];
                                MsgErroeLog.WriteLog("相机" + (int.Parse(nnn) + 1) + "打开成功:" + DevInfoString(ref device1[i]));
                                // ch:探测网络最佳包大小(只对GigE相机有效) | en:Detection network optimal package size(It only works for the GigE camera)
                                if (device1[i].nTLayerType == MyCamera.MV_GIGE_DEVICE)
                                {
                                    int nPacketSize = m_MyCamera[int.Parse(nnn)].MV_CC_GetOptimalPacketSize_NET();
                                    if (nPacketSize > 0)
                                    {
                                        nRet = m_MyCamera[int.Parse(nnn)].MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)nPacketSize);
                                        if (nRet != MyCamera.MV_OK)
                                        {
                                            ShowErrorMsg("Set Packet Size failed!", nRet);
                                        }
                                    }
                                    else
                                    {
                                        ShowErrorMsg("Get Packet Size failed!", nPacketSize);
                                    }
                                }
                                RegisterImageCallBackLogged(m_MyCamera[int.Parse(nnn)], int.Parse(nnn)); // ch:P2-16
                                bOpened = true;
                                if (m_nCanOpenDeviceNum == nCameraUsingNum)
                                {
                                    break;
                                }
                            }
                        }
                        else
                        {
                            MsgErroeLog.WriteLog("相机" + i + "名称不对:" + gigeInfo.chUserDefinedName);
                        }
                    }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog("相机" + i + "名称不对:" + ex.Message); };

            }
            }
            finally
            {
                UnlockAllCameras();
            }
            if (temp1 == 0)
            {
                m_MyCamera[0] = null;
            }
            if (temp2 == 0)
            {
                m_MyCamera[1] = null;
            }
            if (temp3 == 0)
            {
                m_MyCamera[2] = null;
            }
            if (temp4 == 0)
            {
                m_MyCamera[3] = null;
            }
            if (temp5 == 0)
            {
                m_MyCamera[4] = null;
            }
            if (temp6 == 0)
            {
                m_MyCamera[5] = null;
            }
            if (temp7 == 0)
            {
                m_MyCamera[6] = null;
            }
            if (temp8 == 0)
            {
                m_MyCamera[7] = null;
            }
            // ch:设置采集连续模式 | en:Set Continues Aquisition Mode
            // m_MyCamera.MV_CC_SetEnumValue_NET("AcquisitionMode", (uint)MyCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);
            // m_MyCamera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
            bnGetParam1.Enabled = (m_MyCamera[0] != null);
            bnSetParam1.Enabled = (m_MyCamera[0] != null);
            bnGetParam2.Enabled = (m_MyCamera[1] != null);
            bnSetParam2.Enabled = (m_MyCamera[1] != null);
            bnGetParam3.Enabled = (m_MyCamera[2] != null);
            bnSetParam3.Enabled = (m_MyCamera[2] != null);
            bnGetParam4.Enabled = (m_MyCamera[3] != null);
            bnSetParam4.Enabled = (m_MyCamera[3] != null);
            bnGetParam5.Enabled = (m_MyCamera[4] != null);
            bnSetParam5.Enabled = (m_MyCamera[4] != null);
            bnGetParam6.Enabled = (m_MyCamera[5] != null);
            bnSetParam6.Enabled = (m_MyCamera[5] != null);
            bnGetParam7.Enabled = (m_MyCamera[6] != null);
            bnSetParam7.Enabled = (m_MyCamera[6] != null);
            bnGetParam8.Enabled = (m_MyCamera[7] != null);
            bnSetParam8.Enabled = (m_MyCamera[7] != null);
            bnOpen.Enabled = false;
            bnClose.Enabled = true;
            bnStartGrab1.Enabled = (m_MyCamera[0] != null);
            bnStartGrab2.Enabled = (m_MyCamera[1] != null);
            bnStartGrab3.Enabled = (m_MyCamera[2] != null);
            bnStartGrab4.Enabled = (m_MyCamera[3] != null);
            bnStartGrab5.Enabled = (m_MyCamera[4] != null);
            bnStartGrab6.Enabled = (m_MyCamera[5] != null);
            bnStartGrab7.Enabled = (m_MyCamera[6] != null);
            bnStartGrab8.Enabled = (m_MyCamera[7] != null);
            
            // ★ 相机打开后应用触发模式设置（参考主目录版本）
            ApplyTriggerModesForOpenedCameras();
            
            // ch:启动/切换方案期间打开失败汇总提示（一次性，避免逐台弹窗阻塞初始化）
            if (Volatile.Read(ref qiehuanzhong) == 1 && openFailSummary != "") // ch:P2 原子读
            {
                string failMsg = openFailSummary.TrimEnd('、');
                openFailSummary = "";
                MessageBox.Show("以下相机打开失败（0x80000203）：" + failMsg +
                    "\r\n可能原因：\r\n1) 相机IP与电脑网卡不在同一网段（请为网卡添加相机网段的IP，或用MVS将相机IP改为与电脑同网段）；\r\n" +
                    "2) 相机被其他程序占用（请关闭海康MVS客户端、Vision Studio等）；\r\n" +
                    "3) 上次程序异常退出后 GigE 会话残留，约需60秒释放（请等待1分钟后点击\"打开相机\"重试）。",
                    "相机打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

        }

        private void bnGetParam_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[0] == null) return; // ch:相机未打开时跳过参数读写
            try
            {

                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure1.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain1.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate1.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab1.Enabled = false;

            }


        }
        
        /// <summary>
        /// 根据触发模式设置相机硬件参数（参考主目录版本）
        /// </summary>
        private void ApplyTriggerModeToCameraHardware(int slot)
        {
            if (slot < 0 || slot >= 8 || m_MyCamera[slot] == null) return;
            
            Myjob[] myjobs = new Myjob[] { myjob1, myjob2, myjob3, myjob4, myjob5, myjob6, myjob7, myjob8 };
            CheckBox[] cbSoftTriggers = new CheckBox[] { cbSoftTrigger1, cbSoftTrigger2, cbSoftTrigger3, cbSoftTrigger4, cbSoftTrigger5, cbSoftTrigger6, cbSoftTrigger7, cbSoftTrigger8 };
            Button[] bnTriggerExecs = new Button[] { bnTriggerExec1, bnTriggerExec2, bnTriggerExec3, bnTriggerExec4, bnTriggerExec5, bnTriggerExec6, bnTriggerExec7, bnTriggerExec8 };
            
            Myjob job = myjobs[slot];
            lock (_cameraLocks[slot]) // ch:P3-⑧ 按相机独立锁
            {
                try
                {
                    bool softChecked = cbSoftTriggers[slot].Checked;
                    if (job.triggerMode == "连续运行")
                    {
                        job.trrigerEn = false;
                        m_MyCamera[slot].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTriggers[slot].Enabled = false;
                        bnTriggerExecs[slot].Enabled = false;
                        MsgErroeLog.WriteLog("相机" + (slot + 1) + " 设置触发模式：连续运行(TriggerMode=OFF)");
                    }
                    else if (job.triggerMode == "触发拍照" || job.triggerMode == "通讯触发")
                    {
                        job.trrigerEn = true;
                        m_MyCamera[slot].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);
                        if (softChecked || job.triggerMode == "通讯触发")
                        {
                            m_MyCamera[slot].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                        }
                        else
                        {
                            m_MyCamera[slot].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTriggers[slot].Enabled = true;
                        MsgErroeLog.WriteLog("相机" + (slot + 1) + " 设置触发模式：" + job.triggerMode + "(TriggerMode=ON)");
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("相机" + (slot + 1) + " 触发模式设置异常: " + ex.Message);
                }
            }
        }
        
        /// <summary>
        /// 为所有已打开的相机应用触发模式设置
        /// </summary>
        private void ApplyTriggerModesForOpenedCameras()
        {
            if (manager1 == null) return;
            int max = Math.Min(8, manager1.JobCount);
            for (int i = 0; i < max; i++)
            {
                if (m_MyCamera[i] != null)
                    ApplyTriggerModeToCameraHardware(i);
            }
            MsgErroeLog.WriteLog("触发模式设置完成，共处理 " + max + " 台相机");
        }

        private void bnSetParam_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[0] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure1.Text);
                float.Parse(tbGain1.Text);
                float.Parse(tbFrameRate1.Text);
            }
            catch
            {
                //  ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                MsgErroeLog.WriteLog("相机1开始设置参数: 曝光=" + tbExposure1.Text + ", 增益=" + tbGain1.Text + ", 帧率=" + tbFrameRate1.Text);
                
                int nRet = m_MyCamera[0].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                MsgErroeLog.WriteLog("相机1关闭自动曝光返回: 0x" + ((uint)nRet).ToString("X8"));
                
                float baoguang_temp = float.Parse(tbExposure1.Text);
                if (myjob1.baoguang != 0 && myjob1.block != null && myjob1.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob1, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval1 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[0].MV_CC_GetFloatValue_NET("ExposureTime", ref fval1) == MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机1曝光范围: 最小=" + fval1.fMin + ", 最大=" + fval1.fMax + ", 当前=" + fval1.fCurValue);
                    if (baoguang_temp < fval1.fMin) baoguang_temp = fval1.fMin;
                    if (baoguang_temp > fval1.fMax) baoguang_temp = fval1.fMax;
                    MsgErroeLog.WriteLog("相机1曝光调整后值: " + baoguang_temp);
                }
                nRet = m_MyCamera[0].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                MsgErroeLog.WriteLog("相机1设置曝光返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + baoguang_temp + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机1设置曝光失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    // ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                nRet = m_MyCamera[0].MV_CC_SetEnumValue_NET("GainAuto", 0);
                MsgErroeLog.WriteLog("相机1关闭自动增益返回: 0x" + ((uint)nRet).ToString("X8"));
                
                nRet = m_MyCamera[0].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain1.Text));
                MsgErroeLog.WriteLog("相机1设置增益返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + tbGain1.Text + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机1设置增益失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    //ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[0].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate1.Text));
                MsgErroeLog.WriteLog("相机1设置帧率返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + tbFrameRate1.Text + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机1设置帧率失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
                
                // 验证设置结果
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机1验证曝光: 当前值=" + stParam.fCurValue);
                
                nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机1验证增益: 当前值=" + stParam.fCurValue);
                
                nRet = m_MyCamera[0].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机1验证帧率: 当前值=" + stParam.fCurValue);
                    
                MsgErroeLog.WriteLog("相机1参数设置完成");
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("相机1参数设置异常:" + ex.Message); }
        }

        // ch:采集标志读取（参考正常版本，统一入口）
        private bool IsCameraGrabbing(int camIndex)
        {
            switch (camIndex)
            {
                case 0: return m_bGrabbing1;
                case 1: return m_bGrabbing2;
                case 2: return m_bGrabbing3;
                case 3: return m_bGrabbing4;
                case 4: return m_bGrabbing5;
                case 5: return m_bGrabbing6;
                case 6: return m_bGrabbing7;
                case 7: return m_bGrabbing8;
                default: return false;
            }
        }
        // ch:采集标志设置（参考正常版本，统一入口）
        private void SetCameraGrabbing(int camIndex, bool grabbing)
        {
            switch (camIndex)
            {
                case 0: m_bGrabbing1 = grabbing; break;
                case 1: m_bGrabbing2 = grabbing; break;
                case 2: m_bGrabbing3 = grabbing; break;
                case 3: m_bGrabbing4 = grabbing; break;
                case 4: m_bGrabbing5 = grabbing; break;
                case 5: m_bGrabbing6 = grabbing; break;
                case 6: m_bGrabbing7 = grabbing; break;
                case 7: m_bGrabbing8 = grabbing; break;
            }
        }
        // ch:开始采集前检查（参考正常版本）：相机已打开且网络连接正常，未连接时给出明确提示
        private bool CanStartGrab(int idx)
        {
            if (m_MyCamera[idx] == null)
            {
                MessageBox.Show("相机" + (idx + 1) + "未打开，请先打开相机", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            lock (_cameraLocks[idx]) // ch:P3-⑧ 按相机独立锁
            {
                try
                {
                    if (!m_MyCamera[idx].MV_CC_IsDeviceConnected_NET())
                    {
                        MessageBox.Show("相机" + (idx + 1) + "未连接（网络断开），无法开始采集", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("相机" + (idx + 1) + "连接检查异常:" + ex.Message);
                    MessageBox.Show("相机" + (idx + 1) + "连接检查失败，无法开始采集", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            return true;
        }

        private void bnStartGrab_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing1) return; // ch:防重入（重连恢复/运行启动/手动点击并发时避免二次 StartGrabbing）
            if (!CanStartGrab(0)) return; // ch:取流前检查（参考正常版本）
            try
            {
                // ch:标志位置位true | en:Set position bit true
                m_bGrabbing1 = true;
                // m_hReceiveThread = new Thread(ReceiveThreadProcess);
                // m_hReceiveThread.Start();

                m_stFrameInfo[0].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[0].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[0].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing1 = false;
                    // m_hReceiveThread.Join();
                    ShowErrorMsg("Start Grabbing Fail!", nRet);
                    return;
                }
                bnStartGrab1.Enabled = false;
                bnStopGrab1.Enabled = true;
            }
            catch
            {
                m_bGrabbing1 = false;
                MsgErroeLog.WriteLog("相机1开始采集");
            };
        }
        #region 图像采集
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo1;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo2;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo3;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo4;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo5;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo6;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo7;
        MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo8;
        UInt32 nPayloadSize1;
        UInt32 nPayloadSize2;
        UInt32 nPayloadSize3;
        UInt32 nPayloadSize4;
        UInt32 nPayloadSize5;
        UInt32 nPayloadSize6;
        UInt32 nPayloadSize7;
        UInt32 nPayloadSize8;
        Bitmap[] bmp = new Bitmap[8];

        private PictureBox GetCameraPictureBox(string pathNumber)
        {
            switch (pathNumber)
            {
                case "1": return pictureBoxCam1;
                case "2": return pictureBoxCam2;
                case "3": return pictureBoxCam3;
                case "4": return pictureBoxCam4;
                case "5": return pictureBoxCam5;
                case "6": return pictureBoxCam6;
                case "7": return pictureBoxCam7;
                case "8": return pictureBoxCam8;
                default: return null;
            }
        }

        private CogRecordDisplay GetCameraRecordDisplay(string pathNumber)
        {
            switch (pathNumber)
            {
                case "1": return _camRecordDisplay[1];
                case "2": return _camRecordDisplay[2];
                case "3": return _camRecordDisplay[3];
                case "4": return _camRecordDisplay[4];
                case "5": return _camRecordDisplay[5];
                case "6": return _camRecordDisplay[6];
                case "7": return _camRecordDisplay[7];
                case "8": return _camRecordDisplay[8];
                default: return null;
            }
        }

        private int PictureBoxIndex(PictureBox host)
        {
            if (host == pictureBoxCam1) return 1;
            if (host == pictureBoxCam2) return 2;
            if (host == pictureBoxCam3) return 3;
            if (host == pictureBoxCam4) return 4;
            if (host == pictureBoxCam5) return 5;
            if (host == pictureBoxCam6) return 6;
            if (host == pictureBoxCam7) return 7;
            if (host == pictureBoxCam8) return 8;
            if (host == pictureBoxCam9) return 9;
            return 0;
        }

        private CogRecordDisplay GetRecordDisplayForBox(PictureBox host)
        {
            int idx = PictureBoxIndex(host);
            if (idx == 0)
                return null;
            return _camRecordDisplay[idx];
        }

        private EventHandler RoiDisplayDoubleClick(int idx)
        {
            switch (idx)
            {
                case 1: return cogRecordDisplay1_DoubleClick;
                case 2: return cogRecordDisplay2_DoubleClick;
                case 3: return cogRecordDisplay3_DoubleClick;
                case 4: return cogRecordDisplay4_DoubleClick;
                case 5: return cogRecordDisplay5_DoubleClick;
                case 6: return cogRecordDisplay6_DoubleClick;
                case 7: return cogRecordDisplay7_DoubleClick;
                case 8: return cogRecordDisplay8_DoubleClick;
                case 9: return cogRecordDisplay9_DoubleClick;
                default: return null;
            }
        }

        private bool CameraRoiOn(int idx)
        {
            switch (idx)
            {
                case 1: return myjob1 != null && myjob1.roi;
                case 2: return myjob2 != null && myjob2.roi;
                case 3: return myjob3 != null && myjob3.roi;
                case 4: return myjob4 != null && myjob4.roi;
                case 5: return myjob5 != null && myjob5.roi;
                case 6: return myjob6 != null && myjob6.roi;
                case 7: return myjob7 != null && myjob7.roi;
                case 8: return myjob8 != null && myjob8.roi;
                default: return false;
            }
        }

        private void AttachCameraRecordDisplay(PictureBox box, int idx, EventHandler dblClick)
        {
            if (box == null || box.Parent == null || idx < 1 || idx > 9 || _camRecordDisplay[idx] != null)
                return;
            CogRecordDisplay d = new CogRecordDisplay();
            d.Dock = DockStyle.Fill;
            d.Name = "cogRecordDisplay" + idx;
            d.BackColor = Color.FromArgb(255, 60, 60, 60);
            d.CausesValidation = false;
            d.TabStop = false;
            d.Visible = false;
            TableLayoutPanel tlp = box.Parent as TableLayoutPanel;
            if (tlp != null)
            {
                TableLayoutPanelCellPosition pos = tlp.GetPositionFromControl(box);
                tlp.Controls.Add(d);
                if (pos.Column >= 0 && pos.Row >= 0)
                    tlp.SetCellPosition(d, pos);
            }
            else
                box.Parent.Controls.Add(d);
            if (dblClick != null)
                d.DoubleClick += dblClick;
            _camRecordDisplay[idx] = d;
        }

        private void ApplyCameraDisplayMode()
        {
            PictureBox[] boxes = new PictureBox[] { null, pictureBoxCam1, pictureBoxCam2, pictureBoxCam3, pictureBoxCam4, pictureBoxCam5, pictureBoxCam6, pictureBoxCam7, pictureBoxCam8, pictureBoxCam9 };
            for (int i = 1; i <= 9; i++)
            {
                PictureBox box = boxes[i];
                CogRecordDisplay d = _camRecordDisplay[i];
                bool roi = CameraRoiOn(i);
                if (box != null)
                    box.Visible = !roi;
                if (d != null)
                    d.Visible = roi;
                if (roi && d != null)
                    d.BringToFront();
                else if (box != null)
                    box.BringToFront();
            }
            if (groupBox13 != null)
                groupBox13.BringToFront();
        }

        private void InitLiveRecordRenderer()
        {
            if (_liveRecordRenderer != null)
                return;
            _liveRecordRenderer = new CogRecordDisplay();
            _liveRecordRenderer.Visible = false;
            _liveRecordRenderer.Size = new Size(64, 64);
            _liveRecordRenderer.CausesValidation = false;
            _liveRecordRenderer.TabStop = false;
            this.Controls.Add(_liveRecordRenderer);
        }

        private void InitRecordRenderer()
        {
            InitLiveRecordRenderer();
        }

        private void HideRoiRendererIfHost(PictureBox host)
        {
            ApplyCameraDisplayMode();
        }

        private CogRecordDisplay PrepareRoiDisplay(PictureBox host, CogToolBlock block, bool enable)
        {
            InitLiveRecordRenderer();
            int idx = PictureBoxIndex(host);
            if (idx > 0)
                AttachCameraRecordDisplay(host, idx, RoiDisplayDoubleClick(idx));
            CogRecordDisplay record = GetRecordDisplayForBox(host);
            if (record == null)
                return null;
            if (!enable)
            {
                ApplyCameraDisplayMode();
                return record;
            }
            if (host != null)
                host.Visible = false;
            record.Visible = true;
            record.BringToFront();
            try
            {
                ICogRecord rec = null;
                if (block != null)
                {
                    try { rec = block.CreateLastRunRecord().SubRecords[0]; }
                    catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                record.DrawingEnabled = false;
                if (rec != null)
                    record.Record = rec;
                else if (block != null && block.Inputs.Contains("Input"))
                {
                    ICogImage img = block.Inputs["Input"].Value as ICogImage;
                    if (img != null)
                        record.Image = img;
                }
                record.BackColor = Color.FromArgb(255, 60, 60, 60);
                record.DrawingEnabled = true;
                record.Fit(true);
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            return record;
        }

        private Bitmap CopyCogImageToBitmap(ICogImage image)
        {
            // ch:P2-② 移除冗余拷贝：ToBitmap() 已返回一份独立 Bitmap，原 new Bitmap(src) 等于每帧多一次全帧拷贝
            return image.ToBitmap();
        }

        // ch:R15 原图入框前先等比缩到 PictureBox 尺寸(与渲染模式 Fit 语义对齐)：
        //   旧实现把全分辨率位图直接塞给 PictureBox(SizeMode=Normal，1:1 绘制)——为了在几百像素的框里
        //   显示，每帧都要常驻一张十几 MB 整图，超出框的部分还被直接裁掉。缩完后常驻位图降到几百 KB、
        //   整图完整可见、后续任何一次 WM_PAINT 都只处理小图。
        //   所有权约定：入参 full 是刚分配的独占位图，缩放成功即由本方法 Dispose 并交出 small；
        //   装得下/异常则原样交回 full，任何路径都不泄漏也不双重释放。
        private Bitmap DownscaleToBox(Bitmap full, Size boxSize)
        {
            if (full == null)
                return null;
            try
            {
                int bw = boxSize.Width, bh = boxSize.Height;
                if (bw < 8 || bh < 8 || (full.Width <= bw && full.Height <= bh))
                    return full; // ch:装得下就不动，避免多余一次缩放
                double scale = Math.Min(bw / (double)full.Width, bh / (double)full.Height);
                int w = Math.Max(1, (int)(full.Width * scale));
                int h = Math.Max(1, (int)(full.Height * scale));
                Bitmap small = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.FromArgb(255, 60, 60, 60)); // ch:与渲染模式 CogRecordDisplay 背景一致
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, 0, 0, w, h);
                }
                full.Dispose(); // ch:大图刚分配且无人引用，缩放成功立即归还，绝不留十几 MB 常驻
                return small;
            }
            catch (Exception ex)
            {
                new ErrorLog().WriteLog(ex.ToString());
                return full; // ch:缩放失败退回原图——宁可贵也不能黑屏
            }
        }

        private static ICogImage TryGetRecordImage(ICogRecord rec)
        {
            if (rec == null)
                return null;
            ICogImage img = rec.Content as ICogImage;
            if (img != null)
                return img;
            if (rec.SubRecords == null)
                return null;
            for (int i = 0; i < rec.SubRecords.Count; i++)
            {
                img = TryGetRecordImage(rec.SubRecords[i]);
                if (img != null)
                    return img;
            }
            return null;
        }

        private Bitmap RasterizeRecordToBitmap(ICogRecord rec, Size viewSize)
        {
            if (rec == null)
                return null;
            InitLiveRecordRenderer();
            if (_liveRecordRenderer.IsHandleCreated == false)
            {
                try { _liveRecordRenderer.CreateControl(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
            int w = viewSize.Width > 8 ? viewSize.Width : 64;
            int h = viewSize.Height > 8 ? viewSize.Height : 64;
            _liveRecordRenderer.Width = w;
            _liveRecordRenderer.Height = h;
            try
            {
                _liveRecordRenderer.DrawingEnabled = false;
                _liveRecordRenderer.Record = rec;
                _liveRecordRenderer.BackColor = Color.FromArgb(255, 60, 60, 60);
                _liveRecordRenderer.DrawingEnabled = true;
                _liveRecordRenderer.Fit(true);
                // ch:R27 抓图 + 完成度校验（详见 CaptureStableRecordBitmap）：正常帧 1 次抓图即交出所有权
                Bitmap stable = CaptureStableRecordBitmap();
                if (stable != null)
                    return stable;
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            finally
            {
                try { _liveRecordRenderer.Record = null; } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
            // ch:R27 终端兜底：栅格产出不可信（近黑/3 次仍不稳/抓图失败/异常）→ 直读 record 主图像素——
            //   与原图模式同一像素来源（image.ToBitmap() 独立位图，现场实证从不黑），宁可丢叠加图形也不黑屏
            //   （与 DownscaleToBox 同哲学）；缩放对齐 R15 语义，避免全分辨率图塞小框被裁。
            LogRenderFallbackThrottled();
            ICogImage img = TryGetRecordImage(rec);
            if (img == null)
                return null;
            return DownscaleToBox(CopyCogImageToBitmap(img), viewSize);
        }

        // ch:R27 单次抓图：refresh=true 时先 Refresh() 促使隐藏栅格器完成重绘；
        //   返回全新位图（所有权交调用方）或 null（失败/尺寸非法）。CreateContentBitmap 每次返回全新位图，无共享。
        private Bitmap CaptureLiveRecord(bool refresh)
        {
            if (refresh)
            {
                try { _liveRecordRenderer.Refresh(); } catch { }
            }
            Image src = null;
            try
            {
                src = _liveRecordRenderer.CreateContentBitmap(CogDisplayContentBitmapConstants.Display, null, 0);
                if (src == null || src.Width <= 1 || src.Height <= 1)
                {
                    if (src != null)
                    {
                        try { src.Dispose(); } catch { }
                    }
                    return null;
                }
                Bitmap own = src as Bitmap;
                if (own != null)
                {
                    src = null; // ch:所有权交出，finally 不再 Dispose（调用方 SetPictureBoxImage 负责释放）
                    return own;
                }
                return new Bitmap(src); // ch:非 Bitmap 的 Image 兜底，拷贝一次，finally 负责 Dispose
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); return null; }
            finally
            {
                if (src != null)
                {
                    try { src.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
            }
        }

        // ch:R27 抓图稳定器：①首抓后 DiagnoseCapture 完成度诊断，干净(常规路径)1 次抓图直接返回，成本≈旧实现；
        //   ②可疑(近黑/中间黑条)才补抓对比，最多共 3 次——两次逐字节一致=确定性内容：中间黑条若是稳定真实画面
        //   (合法黑边)则采用、近黑则仍不可用；不一致=抓到中间态→取下一次直到干净；
        //   ③3 次仍不稳/稳定仍近黑/抓图失败 → 返回 null，由调用方直读 record 主图兜底。
        private Bitmap CaptureStableRecordBitmap()
        {
            Bitmap settled = CaptureLiveRecord(false);
            if (settled == null)
                return null;
            bool nearBlack, midBand;
            DiagnoseCapture(settled, out nearBlack, out midBand);
            if (!nearBlack && !midBand)
                return settled; // ch:常规路径，与旧行为一致
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Bitmap next = CaptureLiveRecord(true);
                if (next == null)
                    break;
                if (BitmapBytesEqual(settled, next))
                {
                    next.Dispose();
                    if (nearBlack)
                    { // ch:两次一致且仍整幅近黑=确定性坏帧 → 放弃栅格产出，转直读兜底
                        settled.Dispose();
                        return null;
                    }
                    return settled; // ch:一致的中间黑条属稳定真实画面(合法黑边)，不误伤
                }
                settled.Dispose();
                settled = next;
                DiagnoseCapture(settled, out nearBlack, out midBand);
                if (!nearBlack && !midBand)
                    return settled; // ch:已抓到稳定且干净的帧
            }
            if (settled != null)
                settled.Dispose();
            return null;
        }

        // ch:R27 抓图完成度诊断：步长 4 网格、每采样行 5 点。
        //   nearAllBlack = 采样点 ≥99% RGB 全 0（整幅纯黑症状）；
        //   midBlackBand  = 连续 ≥2 个采样行(≥8px)整行全黑、且黑条上方与下方都有内容行
        //                   （顶/底合法黑边只有一侧有内容，被排除）。
        private static void DiagnoseCapture(Bitmap bmp, out bool nearAllBlack, out bool midBlackBand)
        {
            nearAllBlack = false;
            midBlackBand = false;
            if (bmp == null)
                return;
            try
            {
                int w = bmp.Width, h = bmp.Height;
                if (w < 32 || h < 32)
                    return;
                int bpp = Image.GetPixelFormatSize(bmp.PixelFormat) / 8;
                if (bpp < 3)
                    return; // ch:非三/四通道格式不诊断，宁可漏判不误判
                byte[] buf = LockBitsSnapshot(bmp, new Rectangle(0, 0, w, h));
                if (buf == null)
                    return;
                int stride = Math.Abs(buf.Length / h); // ch:按整缓冲均摊还原行跨(仅本方法内索引用)
                int step = 4;
                int rows = (h - 1) / step + 1;
                int[] blackCnt = new int[rows];
                bool[] hasContent = new bool[rows];
                int total = 0, black = 0;
                for (int r = 0; r < rows; r++)
                {
                    int y = r * step;
                    for (int k = 0; k < 5; k++)
                    {
                        int x = k * (w - 1) / 4;
                        int i = y * stride + x * bpp;
                        if (i + 2 >= buf.Length)
                            break;
                        if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0)
                        {
                            blackCnt[r]++;
                            black++;
                        }
                        else
                            hasContent[r] = true;
                        total++;
                    }
                }
                nearAllBlack = total > 0 && (long)black * 100 >= (long)total * 99;
                if (nearAllBlack)
                    return;
                for (int r0 = 0; r0 < rows; )
                {
                    int full = 5;
                    if (blackCnt[r0] < full)
                    {
                        r0++;
                        continue;
                    }
                    int r1 = r0;
                    while (r1 + 1 < rows && blackCnt[r1 + 1] >= full)
                        r1++;
                    if (r1 - r0 >= 1) // ch:连续 ≥2 个采样行
                    {
                        bool above = false, below = false;
                        for (int a = 0; a < r0; a++)
                            if (hasContent[a]) { above = true; break; }
                        for (int b2 = r1 + 1; b2 < rows; b2++)
                            if (hasContent[b2]) { below = true; break; }
                        if (above && below)
                        {
                            midBlackBand = true;
                            return;
                        }
                    }
                    r0 = r1 + 1;
                }
            }
            catch { nearAllBlack = false; midBlackBand = false; }
        }

        // ch:R27 两次抓图逐字节比对：完全一致=内容已稳定；尺寸/格式不同视为不一致(取新帧)
        private static bool BitmapBytesEqual(Bitmap a, Bitmap b)
        {
            if (a == null || b == null)
                return false;
            if (a.Width != b.Width || a.Height != b.Height || a.PixelFormat != b.PixelFormat)
                return false;
            try
            {
                Rectangle rect = new Rectangle(0, 0, a.Width, a.Height);
                byte[] ba = LockBitsSnapshot(a, rect);
                byte[] bb = LockBitsSnapshot(b, rect);
                if (ba == null || bb == null || ba.Length != bb.Length)
                    return false;
                for (int i = 0; i < ba.Length; i++)
                    if (ba[i] != bb[i])
                        return false;
                return true;
            }
            catch { return false; }
        }

        // ch:R27 整幅像素快照(LockBits + Marshal.Copy，无 unsafe)；失败返回 null
        private static byte[] LockBitsSnapshot(Bitmap bmp, Rectangle rect)
        {
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                int stride = Math.Abs(bd.Stride);
                byte[] buf = new byte[stride * rect.Height];
                Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                return buf;
            }
            finally { bmp.UnlockBits(bd); }
        }

        // ch:R27 直读兜底日志(10 秒节流)：正常不出现；现场若 grep 到此行说明栅格器持续产出坏帧，供后续取证
        private int _renderFallbackLastLogMs;
        private void LogRenderFallbackThrottled()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _renderFallbackLastLogMs) < 10000)
                return;
            _renderFallbackLastLogMs = now;
            MsgErroeLog.WriteLog("渲染抓图未稳定/近黑，已直读 record 主图兜底(R27)");
        }

        private void SetPictureBoxImage(PictureBox box, Bitmap bmpNew)
        {
            Image old = box.Image;
            box.Image = bmpNew;
            // ch:P1 Refresh() = Invalidate + 同步 Update（当场重绘）；改 Invalidate 只排一次 WM_PAINT，
            //   省掉一次同步绘制往返。控件属性赋值本身已触发失效，这里保留 Invalidate 仅作兜底。
            box.Invalidate();
            if (old != null)
            {
                try { old.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
        }

        private static int CameraIndexFromPath(string pathNumber)
        {
            int idx;
            if (pathNumber == null || !int.TryParse(pathNumber, out idx) || idx < 1 || idx > 8)
                return 0;
            return idx;
        }

        // ch:每槽位一把锁 + 在途标志（P0 修复）：bmp[i] 由相机回调线程创建、getrecord（同线程）使用后释放，
        //    而关闭/切换流程（ReleaseAllBmpSlots）可能在检测进行中并发调用——旧实现无任何互斥，
        //    检测中 Bitmap 被另一线程 Dispose 属 TOCTOU，是偶发 GDI+ 崩溃根源之一。
        //    约定：属主（回调赋值线程 / getrecord 尾部 / 回调 catch）用 ReleaseBmpSlotNow 强制释放；
        //    外部清理（bnClose/切换）用 ReleaseBmpSlot，在途帧跳过，由在途流程结束时自行释放。
        private readonly object[] _bmpLocks = new object[8] { new object(), new object(), new object(), new object(), new object(), new object(), new object(), new object() };
        private readonly int[] _bmpInFlight = new int[8];

        private void ReleaseBmpSlotInternal(int idx, bool isOwner)
        {
            if (idx < 0 || idx > 7)
                return;
            Bitmap b = null;
            lock (_bmpLocks[idx])
            {
                if (!isOwner && _bmpInFlight[idx] != 0)
                    return; // ch:该帧仍在检测流程中，交由在途流程收尾释放
                b = bmp[idx];
                bmp[idx] = null;
                _bmpInFlight[idx] = 0;
            }
            if (b != null)
            {
                try { b.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
        }

        // ch:外部清理入口（关闭/切换流程）：检测进行中的槽位跳过
        private void ReleaseBmpSlot(int idx)
        {
            ReleaseBmpSlotInternal(idx, false);
        }

        // ch:属主释放入口（getrecord 尾部/回调 catch）：无条件释放并清除在途标志
        private void ReleaseBmpSlotNow(int idx)
        {
            ReleaseBmpSlotInternal(idx, true);
        }

        private void ReleaseBmpByJob(Myjob job)
        {
            int idx = CameraIndexFromPath(job != null ? job.path_number : null);
            if (idx > 0)
                ReleaseBmpSlotNow(idx - 1);
        }

        private void ReleaseAllBmpSlots()
        {
            for (int i = 0; i < 8; i++)
                ReleaseBmpSlot(i);
        }

        // ch:P1-06 关闭/切换前排空检测：等全 8 相机"检测中"门闩(_detectBusy)与 block.Run 计数(_detectingCount)清零。
        //   旧实现仅等 _detectingCount（只覆盖 block.Run，不含传图/输出/存图全程）；覆盖 _detectBusy 更彻底。
        //   超时返回 false，由调用方记告警（不再静默销毁活动对象）。
        private bool WaitDetectDrain(int timeoutMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool running = Interlocked.CompareExchange(ref _detectingCount, 0, 0) > 0;
                bool busy = false;
                for (int i = 0; i < 8; i++)
                    if (System.Threading.Volatile.Read(ref _detectBusy[i]) == 1) { busy = true; break; }
                if (!running && !busy)
                    return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private void DisposeCameraPictureBoxImages()
        {
            PictureBox[] boxes = new PictureBox[] { pictureBoxCam1, pictureBoxCam2, pictureBoxCam3, pictureBoxCam4, pictureBoxCam5, pictureBoxCam6, pictureBoxCam7, pictureBoxCam8, pictureBoxCam9 };
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] == null)
                    continue;
                Image old = boxes[i].Image;
                boxes[i].Image = null;
                if (old != null)
                {
                    try { old.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
            }
        }

        private int CountEnabledRenderCameras()
        {
            int n = 0;
            if (m_bGrabbing1 && myjob1 != null && myjob1.xuanran) n++;
            if (m_bGrabbing2 && myjob2 != null && myjob2.xuanran) n++;
            if (m_bGrabbing3 && myjob3 != null && myjob3.xuanran) n++;
            if (m_bGrabbing4 && myjob4 != null && myjob4.xuanran) n++;
            if (m_bGrabbing5 && myjob5 != null && myjob5.xuanran) n++;
            if (m_bGrabbing6 && myjob6 != null && myjob6.xuanran) n++;
            if (m_bGrabbing7 && myjob7 != null && myjob7.xuanran) n++;
            if (m_bGrabbing8 && myjob8 != null && myjob8.xuanran) n++;
            return n;
        }

        private int CountPendingOcxUnlocked()
        {
            int n = 0;
            for (int i = 1; i <= 8; i++)
            {
                if (_pendingOcxRec[i] != null || _pendingRawImg[i] != null)
                    n++;
            }
            return n;
        }

        private bool IsCameraDisplayBusy(Myjob job)
        {
            if (job == null || job.jiasu)
                return true;
            int idx = CameraIndexFromPath(job.path_number);
            if (idx == 0)
                return false;
            lock (_ocxPaintLock)
            {
                if (_pendingOcxRec[idx] != null || _pendingRawImg[idx] != null)
                    return true;
                if (_ocxPaintingIdx == idx)
                    return true;
            }
            return false;
        }

        private void ClearPendingOcxPaints()
        {
            lock (_ocxPaintLock)
            {
                for (int i = 1; i <= 8; i++)
                {
                    _pendingOcxRec[i] = null;
                    _pendingRawImg[i] = null;
                    Myjob pendingJob = _pendingOcxJob[i];
                    _pendingOcxJob[i] = null;
                    if (pendingJob != null)
                        pendingJob.jiasu = false;
                }
                _ocxPaintBusy = 0;
                _ocxPaintingIdx = 0;
            }
            if (_ocxYieldTimer != null)
                _ocxYieldTimer.Stop();
        }

        private void NoteUiInput()
        {
            _lastUiInputTick = Environment.TickCount;
        }

        private bool UserRecentlyInteracted()
        {
            return unchecked(Environment.TickCount - _lastUiInputTick) < 120;
        }

        private void InstallUiInputFilter()
        {
            if (_uiInputFilter != null)
                return;
            _uiInputFilter = new UiInputFilter(this);
            Application.AddMessageFilter(_uiInputFilter);
        }

        private class UiInputFilter : IMessageFilter
        {
            private readonly Form1 _form;
            public UiInputFilter(Form1 form) { _form = form; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                int msg = m.Msg;
                if ((msg >= 0x0100 && msg <= 0x0109) ||
                    msg == 0x0201 || msg == 0x0202 || msg == 0x0204 || msg == 0x0205 ||
                    msg == 0x0207 || msg == 0x0208 || msg == 0x020A || msg == 0x00A1)
                    _form.NoteUiInput();
                return false;
            }
        }

        private void EnsureOcxYieldTimer()
        {
            if (_ocxYieldTimer != null)
                return;
            _ocxYieldTimer = new System.Windows.Forms.Timer();
            _ocxYieldTimer.Interval = 20;
            _ocxYieldTimer.Tick += delegate
            {
                _ocxYieldTimer.Stop();
                KickOcxPaint();
            };
        }

        private void ScheduleOcxPaint()
        {
            if (closing || IsDisposed || !IsHandleCreated)
                return;
            EnsureOcxYieldTimer();
            // ch:R15 节拍与 Kick 入口的 OcxMinIntervalMs 对齐：66ms ≈ 15fps 上屏封顶(只显示最新帧)。
            //   旧值 8~16ms 意味着 4 相机下约 60~100 次/秒的全分辨率 ToBitmap(分配+整帧转换+Dispose)，
            //   是客户 4 相机 CPU 90+% 的主要水分。用户正在操作、或单次上屏本身 >30ms 时退半拍(132ms)。
            int interval = OcxMinIntervalMs;
            if (UserRecentlyInteracted() || _lastOcxPaintMs >= 30)
                interval = OcxMinIntervalMs * 2;
            _ocxYieldTimer.Interval = interval;
            _ocxYieldTimer.Stop();
            _ocxYieldTimer.Start();
        }

        private bool ShouldYieldBeforeNextPaint()
        {
            if (UserRecentlyInteracted())
                return true;
            if (CountEnabledRenderCameras() >= 4)
                return true;
            if (_lastOcxPaintMs >= 12)
                return true;
            lock (_ocxPaintLock)
                return CountPendingOcxUnlocked() >= 2;
        }

        private void QueueNextOcxPaint()
        {
            if (ShouldYieldBeforeNextPaint())
                ScheduleOcxPaint();
            else
            {
                try { BeginInvoke(new Action(KickOcxPaint)); }
                catch (Exception ex)
                {
                    new ErrorLog().WriteLog(ex.ToString());
                    AbortPendingOcxPaint(); // ch:P2 续排失败同样兜底复位，避免其余相机 jiasu 卡死
                }
            }
        }

        private void QueueRecordDisplay(ICogRecord temprecord, Myjob job)
        {
            if (temprecord == null || job == null)
            {
                if (job != null)
                    job.jiasu = false;
                return;
            }
            int idx = CameraIndexFromPath(job.path_number);
            if (idx == 0)
            {
                job.jiasu = false;
                return;
            }
            lock (_ocxPaintLock)
            {
                _pendingOcxRec[idx] = temprecord;
                _pendingRawImg[idx] = null;
                _pendingOcxJob[idx] = job;
            }
            KickOcxPaint();
        }

        // ch:P2 jiasu 兜底复位：KickOcxPaint 无法投递（句柄未建/窗体已释放/投递异常）时，清空待渲染槽并复位对应相机 jiasu。
        //   否则该相机 IsCameraDisplayBusy 永久 true → 检测线程不再入队渲染 → 该相机画面永久卡死（jiasu 卡死路径）
        private void AbortPendingOcxPaint()
        {
            lock (_ocxPaintLock)
            {
                for (int i = 1; i <= 8; i++)
                {
                    Myjob pj = _pendingOcxJob[i];
                    if (pj == null) continue;
                    _pendingOcxRec[i] = null;
                    _pendingRawImg[i] = null;
                    _pendingOcxJob[i] = null;
                    pj.jiasu = false;
                }
            }
        }

        private void KickOcxPaint()
        {
            if (closing || IsDisposed || !IsHandleCreated)
            {
                AbortPendingOcxPaint(); // ch:P2 无法渲染时兜底复位，避免 jiasu 卡死
                return;
            }
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(KickOcxPaint)); }
                catch (Exception ex)
                {
                    new ErrorLog().WriteLog(ex.ToString());
                    AbortPendingOcxPaint(); // ch:P2 投递失败同上，必须复位 jiasu
                }
                return;
            }
            // ch:R15 上屏总节拍收口：直接投递(Queue*Display→Kick)、续排(QueueNextOcxPaint/FinishOcxPaint
            //   直连)、定时器三条入口都从这里过 —— 旧实现只有定时器路径有节流，直连路径可以绕过，
            //   把全分辨率 ToBitmap 打到 60~100 次/秒。到点前不开新一拍，改为排定时器(只丢刷新率，
            //   pending 槽位不动、采集检测不受任何影响)。
            if (unchecked(Environment.TickCount - _lastOcxKickMs) < OcxMinIntervalMs)
            {
                ScheduleOcxPaint();
                return;
            }
            if (UserRecentlyInteracted())
            {
                ScheduleOcxPaint();
                return;
            }
            ICogRecord rec = null;
            ICogImage rawImg = null;
            Myjob job = null;
            lock (_ocxPaintLock)
            {
                if (_ocxPaintBusy != 0)
                    return;
                for (int n = 0; n < 8; n++)
                {
                    _ocxPaintRr = _ocxPaintRr % 8 + 1;
                    if (_pendingOcxRec[_ocxPaintRr] == null && _pendingRawImg[_ocxPaintRr] == null)
                        continue;
                    rec = _pendingOcxRec[_ocxPaintRr];
                    rawImg = _pendingRawImg[_ocxPaintRr];
                    job = _pendingOcxJob[_ocxPaintRr];
                    _pendingOcxRec[_ocxPaintRr] = null;
                    _pendingRawImg[_ocxPaintRr] = null;
                    _pendingOcxJob[_ocxPaintRr] = null;
                    break;
                }
                if (job == null || (rec == null && rawImg == null))
                    return;
                _ocxPaintBusy = 1;
                _ocxPaintingIdx = _ocxPaintRr;
                _lastOcxKickMs = Environment.TickCount; // ch:R15 从这一刻起 OcxMinIntervalMs 内不再开新一拍
            }
            PictureBox box = GetCameraPictureBox(job.path_number);
            int t0 = Environment.TickCount;
            try
            {
                if (box == null || !box.Visible || CameraRoiOn(CameraIndexFromPath(job.path_number)))
                    return;
                if (WindowState == FormWindowState.Minimized)
                    return; // ch:R15 最小化时无人看画：跳过 ToBitmap/光栅化，这次上屏成本直接归零(finally 照常复位)
                if (!box.IsHandleCreated)
                {
                    try { box.CreateControl(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                Bitmap bmp = null;
                if (rawImg != null)
                    bmp = DownscaleToBox(CopyCogImageToBitmap(rawImg), box.ClientSize); // ch:R15 原图先等比缩到框尺寸
                else
                    bmp = RasterizeRecordToBitmap(rec, box.ClientSize);
                if (bmp != null)
                    SetPictureBoxImage(box, bmp);
            }
            catch (Exception ex)
            {
                job.temptu = 0;
                MsgErroeLog.WriteLog(ex.Message + "相机" + job.path_number + "record");
            }
            finally
            {
                int spent = Environment.TickCount - t0;
                if (spent < 0)
                    spent = 0;
                _lastOcxPaintMs = spent;
                FinishOcxPaint(job);
            }
        }

        private void FinishOcxPaint(Myjob job)
        {
            if (job != null)
                job.jiasu = false;
            bool more = false;
            lock (_ocxPaintLock)
            {
                _ocxPaintBusy = 0;
                _ocxPaintingIdx = 0;
                more = CountPendingOcxUnlocked() > 0;
            }
            if (more)
            {
                if (_lastOcxPaintMs == 0 && !UserRecentlyInteracted())
                {
                    try { BeginInvoke(new Action(KickOcxPaint)); }
                    catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                else
                    QueueNextOcxPaint();
            }
        }


        private void QueueRawImageDisplay(PictureBox display, ICogImage image, Myjob job)
        {
            if (display == null || image == null || job == null)
            {
                if (job != null)
                    job.jiasu = false;
                return;
            }
            int idx = CameraIndexFromPath(job.path_number);
            if (idx == 0)
            {
                job.jiasu = false;
                return;
            }
            lock (_ocxPaintLock)
            {
                _pendingRawImg[idx] = image;
                _pendingOcxRec[idx] = null;
                _pendingOcxJob[idx] = job;
            }
            KickOcxPaint();
        }
        // ch:P0-① 启动每相机检测工作线程（幂等；后台线程，随进程退出）
        private void StartDetectWorkers()
        {
            for (int i = 0; i < 8; i++)
            {
                if (_detectSignal[i] == null)
                    _detectSignal[i] = new AutoResetEvent(false);
                if (_detectThreads[i] != null && _detectThreads[i].IsAlive)
                    continue;
                int idx = i;
                _detectThreads[idx] = new Thread(() => DetectWorkerLoop(idx))
                {
                    IsBackground = true,
                    Name = "DetectWorker" + (idx + 1)
                };
                _detectThreads[idx].Start();
            }
        }

        // ch:P0-① 检测工作线程：等待通知 → 取本次待检 job → getrecord（内部完成检测、输出并在尾部释放 Bitmap 槽位）
        private void DetectWorkerLoop(int idx)
        {
            AutoResetEvent signal = _detectSignal[idx];
            while (!closing)
            {
                try { signal.WaitOne(200); }
                catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                if (closing)
                    break;
                Myjob job = _pendingDetectJob[idx];
                if (job == null)
                    continue;
                _pendingDetectJob[idx] = null;
                // ch:P0-① 置"检测中"门闩：整段 getrecord（含渲染/存图）期间回调端不再接收新帧，等价于原回调线程串行语义
                System.Threading.Volatile.Write(ref _detectBusy[idx], 1);
                try
                {
                    getrecord(job); // ch:属主线程；正常路径由 getrecord 尾部 ReleaseBmpSlotNow 释放槽位
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("检测线程异常 相机" + (idx + 1) + ": " + ex.Message);
                    // ch:异常兜底释放，防止槽位卡死导致该相机后续帧全部被背压丢弃
                    try { ReleaseBmpSlotNow(idx); } catch (Exception ex2) { new ErrorLog().WriteLog(ex2.ToString()); }
                }
                finally
                {
                    // ch:P1 同步尾部采样终点：block.Run 结束 → getrecord 返回（含流程封、输出分发、统计、渲染入队等同步段）。
                    //   这段每多 1ms 就是 feng=0 时每帧少 1ms；日志里的「尾部」列即 Task.Run 的真实收益上限。
                    long tailT0 = System.Threading.Interlocked.Exchange(ref _perfTailStart[idx], 0L);
                    if (tailT0 != 0L)
                    {
                        System.Threading.Interlocked.Add(ref _perfTailTicks[idx],
                            System.Diagnostics.Stopwatch.GetTimestamp() - tailT0);
                        System.Threading.Interlocked.Increment(ref _perfTailCount[idx]);
                    }
                    System.Threading.Volatile.Write(ref _detectBusy[idx], 0);
                }
            }
        }

        private void ImageCallBack(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            // ch:性能埋点：记录本次回调占用海康 SDK 取流线程的时长
            long perfT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int perfIdx = -1;
            if (closing || Volatile.Read(ref qiehuanzhong) == 1)
            {
                if (Volatile.Read(ref qiehuanzhong) == 1 && Environment.TickCount - _lastDropLogMs > 5000)
                {
                    _lastDropLogMs = Environment.TickCount;
                    MsgErroeLog.WriteLog("回调帧被丢弃（qiehuanzhong 长时间未复位，检查切换流程）");
                }
                return;
            }
            try
            {
                int nIndex = (int)pUser;
                if (nIndex < 0 || nIndex > 7)
                    return;
                perfIdx = nIndex;
                ++m_nFrames[nIndex];
                if (bmp[nIndex] != null || System.Threading.Volatile.Read(ref _detectBusy[nIndex]) != 0)
                {
                    // ch:上一帧尚未处理完（含 P0-① 检测线程仍在处理），本帧被丢弃（背压）。统计丢帧数以判断节拍是否超限
                    _perfDrop[nIndex]++;
                    return;
                }
                // ch:封程限速（feng=0 表示不限速）：距本相机上次处理不足 feng 毫秒则直接丢弃返回。
                //    原实现在检测完成后用 Thread.Sleep 补齐周期，会长时间占住海康 SDK 的取流线程；
                //    改为在入口做节拍判定，限速效果不变，但取流线程立即释放。
                //    注意：限速丢弃是预期行为，不计入 _perfDrop（该计数只反映「处理不过来」的丢帧）。
                if (feng > 0)
                {
                    long nowMs = _perfSw.ElapsedMilliseconds;
                    long lastMs = System.Threading.Interlocked.Read(ref _lastProcMs[nIndex]);
                    if (lastMs != 0 && nowMs - lastMs < (long)feng)
                        return;
                    System.Threading.Interlocked.Exchange(ref _lastProcMs[nIndex], nowMs);
                }
                // ch:统一像素路径（P0 修复）：
                //  1) 原实现 Mono8/BGR8/RGB8 直出时直接把 SDK 帧缓冲包进 Bitmap（零拷贝），
                //     SDK 下一帧到达立即覆写像素，检测读到的是被改写的图像；
                //  2) 转换分支只判 IntPtr.Zero 就分配 m_pSaveImageBuf，从不比较新旧尺寸，
                //     运行中改 ROI/分辨率/像素格式后向小缓冲写越界，破坏原生堆。
                //  现统一为：所有格式先经 EnsureSaveImageBuf 保证私有缓冲足够（尺寸变化则重分配，
                //  Free/Alloc 与 bnSaveBmp_Click 读侧共用 m_BufForSaveImageLock 互斥），直出格式用
                //  CopyMemory 拷入，非直出格式用 SDK 转换算子写入；Bitmap 仍包装私有缓冲，
                //  由 bmp[nIndex]!=null 背压 + getrecord 处理完才放行，保证检测期间缓冲不被覆写。
                if (pFrameInfo.nFrameLen == 0)
                    return;
                if (m_MyCamera[nIndex] == null) return;
                if (IsMonoData(pFrameInfo.enPixelType))
                {
                    UInt32 nSaveImageNeedSize = (uint)pFrameInfo.nWidth * pFrameInfo.nHeight;
                    if (nSaveImageNeedSize < pFrameInfo.nFrameLen)
                        nSaveImageNeedSize = pFrameInfo.nFrameLen;
                    lock (m_BufForSaveImageLock[nIndex])
                    {
                        if (!EnsureSaveImageBuf(nIndex, nSaveImageNeedSize))
                            return;
                        if (pFrameInfo.enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
                            CopyMemory(m_pSaveImageBuf[nIndex], pData, pFrameInfo.nFrameLen);
                        else
                            {
                                // ch:P1-12 检查转换返回值（MV_OK==0）：失败时缓冲不可信，不发布旧/未初始化图像，直接丢帧并记日志
                                int cvtRet = ConvertToMono8(m_MyCamera[nIndex], pData, m_pSaveImageBuf[nIndex], pFrameInfo.nHeight, pFrameInfo.nWidth, pFrameInfo.enPixelType);
                                if (cvtRet != 0)
                                {
                                    MsgErroeLog.WriteLog("Mono8 像素转换失败 相机" + (nIndex + 1) + " ret=" + cvtRet + "，本帧丢弃");
                                    return;
                                }
                            }
                        pData = m_pSaveImageBuf[nIndex];
                        // ch:R1 回填帧信息供 bnSaveBmp_Click 存图读取（原从未赋值 → 存出 0×0 空图）。
                        //   本路径缓冲恒为 Mono8（直出或 ConvertToMono8），故按 Mono8 的像素类型与 nFrameLen(=W*H) 落库。
                        m_stFrameInfo[nIndex] = pFrameInfo;
                        m_stFrameInfo[nIndex].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
                        m_stFrameInfo[nIndex].nFrameLen = (uint)pFrameInfo.nWidth * pFrameInfo.nHeight;
                    }
                }
                else if (IsColorData(pFrameInfo.enPixelType))
                {
                    UInt32 nSaveImageNeedSize = (uint)pFrameInfo.nWidth * pFrameInfo.nHeight * 3;
                    if (nSaveImageNeedSize < pFrameInfo.nFrameLen)
                        nSaveImageNeedSize = pFrameInfo.nFrameLen;
                    lock (m_BufForSaveImageLock[nIndex])
                    {
                        if (!EnsureSaveImageBuf(nIndex, nSaveImageNeedSize))
                            return;
                        if (pFrameInfo.enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed)
                            CopyMemory(m_pSaveImageBuf[nIndex], pData, pFrameInfo.nFrameLen);
                        else
                            {
                                // ch:P1-12 检查转换返回值（MV_OK==0）：失败时缓冲不可信，不发布旧/未初始化图像
                                int cvtRet = ConvertToRGB(m_MyCamera[nIndex], pData, pFrameInfo.nHeight, pFrameInfo.nWidth, pFrameInfo.enPixelType, m_pSaveImageBuf[nIndex]);
                                if (cvtRet != 0)
                                {
                                    MsgErroeLog.WriteLog("RGB 像素转换失败 相机" + (nIndex + 1) + " ret=" + cvtRet + "，本帧丢弃");
                                    return;
                                }
                            }
                        pData = m_pSaveImageBuf[nIndex];
                        // ch:R1 回填帧信息供 bnSaveBmp_Click 存图读取（原从未赋值 → 存出 0×0 空图）。
                        //   本路径缓冲恒为 BGR8（直出 BGR8/RGB8 或 ConvertToRGB 输出 BGR8），故非直出时按 BGR8 落库，nFrameLen=W*H*3。
                        m_stFrameInfo[nIndex] = pFrameInfo;
                        // ch:P2-13 彩色缓冲恒为 BGR8（BGR8 直出；RGB8/其余格式均经 ConvertToRGB 转 BGR8），存图一律按 BGR8 落库
                            m_stFrameInfo[nIndex].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                        m_stFrameInfo[nIndex].nFrameLen = (uint)pFrameInfo.nWidth * pFrameInfo.nHeight * 3;
                    }
                }
                else
                {
                    MsgErroeLog.WriteLog("No such pixel type!");
                    return;
                }
                // ch:Bitmap 构造完整后再在锁内登记（旧实现先赋值再设调色板，窗口期可被其他线程 Dispose）
                if (IsMonoData(pFrameInfo.enPixelType))
                {
                    Bitmap tmpBmp = new Bitmap(pFrameInfo.nWidth, pFrameInfo.nHeight, pFrameInfo.nWidth * 1, PixelFormat.Format8bppIndexed, pData);
                    ColorPalette cp = tmpBmp.Palette;
                    for (int i = 0; i < 256; i++)
                        cp.Entries[i] = Color.FromArgb(i, i, i);
                    tmpBmp.Palette = cp;
                    lock (_bmpLocks[nIndex]) { bmp[nIndex] = tmpBmp; _bmpInFlight[nIndex] = 1; }
                }
                else
                {
                    try
                    {
                        Bitmap tmpBmp = new Bitmap(pFrameInfo.nWidth, pFrameInfo.nHeight, pFrameInfo.nWidth * 3, PixelFormat.Format24bppRgb, pData);
                        lock (_bmpLocks[nIndex]) { bmp[nIndex] = tmpBmp; _bmpInFlight[nIndex] = 1; }
                    }
                    catch
                    {
                        MsgErroeLog.WriteLog("Write File Fail!");
                    }
                }
                if (bmp[nIndex] != null)
                {
                    Myjob job = null;
                    switch (nIndex)
                    {
                        case 0: job = myjob1; break;
                        case 1: job = myjob2; break;
                        case 2: job = myjob3; break;
                        case 3: job = myjob4; break;
                        case 4: job = myjob5; break;
                        case 5: job = myjob6; break;
                        case 6: job = myjob7; break;
                        case 7: job = myjob8; break;
                    }
                    if (job != null)
                    {
                        job.jieshouZifu = "null";
                        // ch:P0-① 只登记待检并通知工作线程，检测不再占用 SDK 取流线程
                        _pendingDetectJob[nIndex] = job;
                        try { _detectSignal[nIndex].Set(); }
                        catch (Exception ex) { MsgErroeLog.WriteLog("检测通知异常:" + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog("图像回调异常 相机" + (int)pUser + ": " + ex.Message);
                try
                {
                    int nIndex = (int)pUser;
                    if (nIndex >= 0 && nIndex <= 7)
                    {
                        // ch:属主释放：清除在途标志，防止异常后槽位卡死导致后续帧全部被背压丢弃
                        ReleaseBmpSlotNow(nIndex);
                    }
                }
                catch (Exception ex2) { MsgErroeLog.WriteLog("异常:" + ex2.Message); }
            }
            finally
            {
                // ch:累加本次回调占用取流线程的时长（P0-① 解耦后仅含像素转换 + 建 Bitmap + 入队；检测耗时另由 _perfRunTicks 统计）
                if (perfIdx >= 0 && perfIdx <= 7)
                {
                    System.Threading.Interlocked.Add(ref _perfCbTicks[perfIdx],
                        System.Diagnostics.Stopwatch.GetTimestamp() - perfT0);
                    _perfCbCount[perfIdx]++;
                }
                PerfReportIfNeeded();
            }
        }

        // ch:确保本相机私有帧缓冲能容纳 needSize 字节；已分配尺寸不足（运行中改 ROI/分辨率/像素格式）时
        //    Free 旧块并重分配。调用方需已持有 m_BufForSaveImageLock[nIndex]，防止与 bnSaveBmp_Click
        //    的读侧并发（旧实现从不比较尺寸，缩小后向旧缓冲写入会破坏原生堆）。
        //    P2-② 加固：本方法再持 _bmpLocks[nIndex]，形成固定锁序 m_BufForSaveImageLock → _bmpLocks，
        //    确保"Bitmap 仍包装旧缓冲"与"Free/重分配旧缓冲"互斥（旧代码两把锁互不相干，理论上存在
        //    并发回调 + 运行中改尺寸时 Bitmap 引用已释放缓冲的窗口）。
        //    分配失败返回 false（旧块保留），由调用方直接丢帧。
        private bool EnsureSaveImageBuf(int nIndex, UInt32 needSize)
        {
            if (needSize == 0)
                return false;
            lock (_bmpLocks[nIndex])
            {
                if (m_pSaveImageBuf[nIndex] != IntPtr.Zero && m_nSaveImageBufSize[nIndex] >= needSize)
                    return true;
                IntPtr newBuf = Marshal.AllocHGlobal((int)needSize);
                if (newBuf == IntPtr.Zero)
                    return false;
                if (m_pSaveImageBuf[nIndex] != IntPtr.Zero)
                    Marshal.FreeHGlobal(m_pSaveImageBuf[nIndex]);
                m_pSaveImageBuf[nIndex] = newBuf;
                m_nSaveImageBufSize[nIndex] = needSize;
                return true;
            }
        }

        // ch:性能统计汇总（新增）：每 5 秒输出一次各相机的收帧/丢帧/回调均耗时/检测均耗时/取流线程占用率。
        //    日志位于程序目录 Log\yyyy-MM-dd.txt，可用「读取日志」界面查看。
        private void PerfReportIfNeeded()
        {
            long now = _perfSw.ElapsedMilliseconds;
            long start = System.Threading.Interlocked.Read(ref _perfWinStart);
            if (start == 0)
            {
                System.Threading.Interlocked.CompareExchange(ref _perfWinStart, now, 0);
                return;
            }
            long span = now - start;
            if (span < 5000)
                return;
            // ch:只让一个线程做汇总，其余直接返回
            if (System.Threading.Interlocked.CompareExchange(ref _perfReporting, 1, 0) != 0)
                return;
            try
            {
                if (now - System.Threading.Interlocked.Read(ref _perfWinStart) < 5000)
                    return;
                double freq = (double)System.Diagnostics.Stopwatch.Frequency;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("[性能统计] 窗口").Append((span / 1000.0).ToString("F1")).Append("秒");
                int totalGet = 0, totalDrop = 0, anyBusy = 0;
                bool anySaturate50 = false, anySlow35 = false; // ch:R25 异常判定：任一路占用≥50% / 任一路检测均耗时≥35ms(帧预算40ms)
                for (int i = 0; i < 8; i++)
                {
                    int cnt = _perfCbCount[i];
                    int drop = _perfDrop[i];
                    int rcnt = _perfRunCount[i];
                    int tcnt = _perfTailCount[i];
                    if (cnt == 0 && drop == 0 && rcnt == 0 && tcnt == 0)
                        continue;
                    totalGet += cnt;
                    totalDrop += drop;
                    double cbMs = cnt > 0 ? (_perfCbTicks[i] / freq) * 1000.0 / cnt : 0.0;
                    double runMs = rcnt > 0 ? (_perfRunTicks[i] / freq) * 1000.0 / rcnt : 0.0;
                    double tailMs = tcnt > 0 ? (_perfTailTicks[i] / freq) * 1000.0 / tcnt : 0.0;
                    double busy = (_perfCbTicks[i] / freq) * 1000.0 / span * 100.0;
                    if (busy >= 80.0)
                        anyBusy++;
                    if (busy >= 50.0) anySaturate50 = true; // ch:R25
                    if (runMs >= 35.0) anySlow35 = true;    // ch:R25
                    sb.Append(" | 相机").Append(i + 1)
                      .Append(" 收").Append(cnt)
                      .Append(" 丢").Append(drop)
                      .Append(" 回调").Append(cbMs.ToString("F1")).Append("ms")
                      .Append(" 检测").Append(runMs.ToString("F1")).Append("ms")
                      .Append(" 尾部").Append(tailMs.ToString("F1")).Append("ms")
                      .Append(" 占用").Append(busy.ToString("F0")).Append("%");
                    _perfCbTicks[i] = 0;
                    _perfCbCount[i] = 0;
                    _perfDrop[i] = 0;
                    _perfRunTicks[i] = 0;
                    _perfRunCount[i] = 0;
                    _perfTailTicks[i] = 0;
                    _perfTailCount[i] = 0;
                }
                sb.Append(" | 合计收").Append(totalGet).Append(" 丢").Append(totalDrop);
                if (anyBusy > 0)
                    sb.Append(" [警告:").Append(anyBusy).Append("路取流线程占用>=80%，已达采集瓶颈]");
                // ch:R25 打印规则：开关开=异常才打(丢帧率≥1%/占用≥50%/检测≥35ms)+10分钟心跳；开关关=完全不打。
                //   计数器复位在上面循环里无条件执行、窗口起点照常复位——开关只决定落不落日志，不影响统计本身与告警字段
                string whyR25 = "";
                if (totalGet > 0 && totalDrop * 100 >= totalGet)
                    whyR25 = "丢帧率" + ((double)totalDrop * 100.0 / totalGet).ToString("F1") + "%";
                if (anySaturate50) whyR25 += (whyR25.Length > 0 ? "|" : "") + "占用>=50%";
                if (anySlow35) whyR25 += (whyR25.Length > 0 ? "|" : "") + "检测>=35ms";
                bool anomalyR25 = whyR25.Length > 0;
                bool heartbeatR25 = !anomalyR25 && now - _perfLastPrintMs >= 600000;
                if (_perfPrintOn && (anomalyR25 || heartbeatR25))
                {
                    sb.Append(anomalyR25 ? " [异常:" + whyR25 + "]" : " [心跳]");
                    MsgErroeLog.WriteLog(sb.ToString());
                    _perfLastPrintMs = now;
                }
                System.Threading.Interlocked.Exchange(ref _perfWinStart, now);
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _perfReporting, 0);
            }
        }

        // ch:性能埋点自检（新增）：脱离相机，用桩数据驱动真实 PerfReportIfNeeded，验证日志格式/占用率/告警阈值正确。
        //     启用方式：设置环境变量 PERF_SELFTEST=1 后启动程序（无需连相机），启动后会在日志输出一次 [自检] 报表。
        public void RunPerfSelfCheck()
        {
            try
            {
                double freq = (double)System.Diagnostics.Stopwatch.Frequency;
                // ch:桩数据：模拟 8 路中 3 路有流量，其中相机2 占用≈90% 触发告警阈值。
                // 相机1：收300帧，回调累计2.7s -> 单帧≈9ms，占用≈45%
                // 相机2：收600帧，回调累计5.4s -> 单帧≈9ms，占用≈90%（告警）
                // 相机3：收120帧丢30帧，回调累计0.6s，检测累计1.2s -> 单帧检测≈10ms，占用≈10%
                _perfCbCount[0] = 300; _perfCbTicks[0] = (long)(2.700 * freq);
                _perfCbCount[1] = 600; _perfCbTicks[1] = (long)(5.400 * freq);
                _perfCbCount[2] = 120; _perfDrop[2] = 30; _perfCbTicks[2] = (long)(0.600 * freq);
                _perfRunCount[2] = 120; _perfRunTicks[2] = (long)(1.200 * freq);
                // ch:窗口起点前移 6 秒，使 span>=5000 触发一次真实汇总
                System.Threading.Interlocked.Exchange(ref _perfWinStart, _perfSw.ElapsedMilliseconds - 6000);
                PerfReportIfNeeded();
                MsgErroeLog.WriteLog("[自检] 性能埋点逻辑已运行：上方 [性能统计] 应为桩数据预期输出，请核对 相机2 占用应≈90% 且出现 [警告]。");
            }
            catch (System.Exception ex)
            {
                MsgErroeLog.WriteLog("[自检] 性能埋点自检异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 其他黑白格式转为Mono8
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="pInData">输出图片数据</param>
        /// <param name="pOutData">输出图片数据</param>
        /// <param name="nHeight">高</param>
        /// <param name="nWidth">宽</param>
        /// <param name="nPixelType">像素格式</param>
        /// <returns></returns>
        public Int32 ConvertToMono8(object obj, IntPtr pInData, IntPtr pOutData, ushort nHeight, ushort nWidth, MyCamera.MvGvspPixelType nPixelType)
        {
            if (IntPtr.Zero == pInData || IntPtr.Zero == pOutData)
            {
                return MyCamera.MV_E_PARAMETER;
            }

            int nRet = MyCamera.MV_OK;
            MyCamera device = obj as MyCamera;
            MyCamera.MV_PIXEL_CONVERT_PARAM stPixelConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();

            stPixelConvertParam.pSrcData = pInData;//源数据
            if (IntPtr.Zero == stPixelConvertParam.pSrcData)
            {
                return -1;
            }

            stPixelConvertParam.nWidth = nWidth;//图像宽度
            stPixelConvertParam.nHeight = nHeight;//图像高度
            stPixelConvertParam.enSrcPixelType = nPixelType;//源数据的格式
            stPixelConvertParam.nSrcDataLen = (uint)(nWidth * nHeight * ((((uint)nPixelType) >> 16) & 0x00ff) >> 3);

            stPixelConvertParam.pDstBuffer = pOutData;//转换后的数据
            stPixelConvertParam.enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
            // ch:P0 目标为 Mono8，容量必须按实际 W*H 上报；原实现沿用 RGB8 口径报 W*H*3，
            //   大于调用方为 Mono8 分配的缓冲（W*H）→ SDK 可能超写，破坏原生堆。
            stPixelConvertParam.nDstBufferSize = (uint)(nWidth * nHeight);

            nRet = device.MV_CC_ConvertPixelType_NET(ref stPixelConvertParam);//格式转换
            if (MyCamera.MV_OK != nRet)
            {
                return -1;
            }

            return nRet;
        }

        /// <summary>
        /// 其他彩色格式转为RGB8
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="pSrc"></param>
        /// <param name="nHeight"></param>
        /// <param name="nWidth"></param>
        /// <param name="nPixelType"></param>
        /// <param name="pDst"></param>
        /// <returns></returns>
        public Int32 ConvertToRGB(MyCamera obj, IntPtr pSrc, ushort nHeight, ushort nWidth, MyCamera.MvGvspPixelType nPixelType, IntPtr pDst)
        {
            if (IntPtr.Zero == pSrc || IntPtr.Zero == pDst)
            {
                return MyCamera.MV_E_PARAMETER;
            }

            int nRet = MyCamera.MV_OK;
            MyCamera.MV_PIXEL_CONVERT_PARAM stPixelConvertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();

            stPixelConvertParam.pSrcData = pSrc;//源数据
            if (IntPtr.Zero == stPixelConvertParam.pSrcData)
            {
                return -1;
            }

            stPixelConvertParam.nWidth = nWidth;//图像宽度
            stPixelConvertParam.nHeight = nHeight;//图像高度
            stPixelConvertParam.enSrcPixelType = nPixelType;//源数据的格式
            stPixelConvertParam.nSrcDataLen = (uint)(nWidth * nHeight * ((((uint)nPixelType) >> 16) & 0x00ff) >> 3);

            stPixelConvertParam.nDstBufferSize = (uint)(nWidth * nHeight * ((((uint)MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed) >> 16) & 0x00ff) >> 3);
            stPixelConvertParam.pDstBuffer = pDst;//转换后的数据
            // ch:目标必须为 BGR 顺序：GDI+ 的 Format24bppRgb 内存布局是 B,G,R（与 BMP 一致），
            //    若输出 RGB8_Packed 则 Bitmap 红蓝通道颠倒
            stPixelConvertParam.enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
            stPixelConvertParam.nDstBufferSize = (uint)nWidth * nHeight * 3;

            nRet = obj.MV_CC_ConvertPixelType_NET(ref stPixelConvertParam);//格式转换
            if (MyCamera.MV_OK != nRet)
            {
                return -1;
            }

            return MyCamera.MV_OK;
        }

        // ch:获取丢帧数 | en:Get Throw Frame Number
        private string GetLostFrame(int nIndex)
        {

            if (m_MyCamera[nIndex] != null)
            {
                MyCamera.MV_ALL_MATCH_INFO pstInfo = new MyCamera.MV_ALL_MATCH_INFO();
                if (m_pDeviceInfo[nIndex].nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    MyCamera.MV_MATCH_INFO_NET_DETECT MV_NetInfo = new MyCamera.MV_MATCH_INFO_NET_DETECT();
                    pstInfo.nInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MyCamera.MV_MATCH_INFO_NET_DETECT));
                    pstInfo.nType = MyCamera.MV_MATCH_TYPE_NET_DETECT;
                    int size = Marshal.SizeOf(MV_NetInfo);
                    pstInfo.pInfo = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(MV_NetInfo, pstInfo.pInfo, false);

                    m_MyCamera[nIndex].MV_CC_GetAllMatchInfo_NET(ref pstInfo);
                    MV_NetInfo = (MyCamera.MV_MATCH_INFO_NET_DETECT)Marshal.PtrToStructure(pstInfo.pInfo, typeof(MyCamera.MV_MATCH_INFO_NET_DETECT));

                    string sTemp = MV_NetInfo.nLostFrameCount.ToString();
                    Marshal.FreeHGlobal(pstInfo.pInfo);
                    return sTemp;
                }
                else if (m_pDeviceInfo[nIndex].nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    MyCamera.MV_MATCH_INFO_USB_DETECT MV_NetInfo = new MyCamera.MV_MATCH_INFO_USB_DETECT();
                    pstInfo.nInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MyCamera.MV_MATCH_INFO_USB_DETECT));
                    pstInfo.nType = MyCamera.MV_MATCH_TYPE_USB_DETECT;
                    int size = Marshal.SizeOf(MV_NetInfo);
                    pstInfo.pInfo = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(MV_NetInfo, pstInfo.pInfo, false);

                    m_MyCamera[nIndex].MV_CC_GetAllMatchInfo_NET(ref pstInfo);
                    MV_NetInfo = (MyCamera.MV_MATCH_INFO_USB_DETECT)Marshal.PtrToStructure(pstInfo.pInfo, typeof(MyCamera.MV_MATCH_INFO_USB_DETECT));

                    string sTemp = MV_NetInfo.nErrorFrameCount.ToString();
                    Marshal.FreeHGlobal(pstInfo.pInfo);
                    return sTemp;
                }
                else
                {
                    return "0";
                }
            }
            else
                return "0";

        }
        // ch:去除自定义的像素格式 | en:Remove custom pixel formats
        private bool RemoveCustomPixelFormats(MyCamera.MvGvspPixelType enPixelFormat)
        {
            Int32 nResult = ((int)enPixelFormat) & (unchecked((Int32)0x80000000));
            if (0x80000000 == nResult)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        #endregion

        private void bnStopGrab_Click(object sender, EventArgs e)
        {
            try
            {
                // ch:标志位设为false | en:Set flag bit false
                m_bGrabbing1 = false;
                //  m_hReceiveThread.Join();

                // ch:停止采集 | en:Stop Grabbing
                int nRet = m_MyCamera[0].MV_CC_StopGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    ShowErrorMsg("Stop Grabbing Fail!", nRet);
                }
                bnStartGrab1.Enabled = true;
                bnStopGrab1.Enabled = false;
            }
            catch
            {
                MsgErroeLog.WriteLog("相机1停止采集");
            }
        }

        private void bnClose_Click(object sender, EventArgs e)
        {
            yunxing = false;
            myjob1.yun = 0;
            myjob2.yun = 0;
            myjob3.yun = 0;
            myjob4.yun = 0;
            myjob5.yun = 0;
            myjob6.yun = 0;
            myjob7.yun = 0;
            myjob8.yun = 0;
            ClearPendingOcxPaints();
            if (!WaitDetectDrain(3000))
                MsgErroeLog.WriteLog("P1-06 关闭：检测排空超时(3000ms)，仍有在途检测帧；后续释放缓冲/Shutdown 存在野指针/并发风险");
            ReleaseAllBmpSlots();
            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke(new Action(DisposeCameraPictureBoxImages)); }
                catch { DisposeCameraPictureBoxImages(); }
            }
            else
                DisposeCameraPictureBoxImages();
            // ch:停止采集并关闭、销毁设备 | en:Stop grabbing, close and destroy devices
            LockAllCameras(); // ch:P3-⑧ 整机互斥：按 0→7 顺序取全部相机锁
            try
            {
            try
            {
                for (int i = 0; i < 8; ++i)
                {
                    try
                    {
                        if (m_MyCamera[i] != null)
                        {
                            int r1 = m_MyCamera[i].MV_CC_StopGrabbing_NET();
                            int r2 = m_MyCamera[i].MV_CC_CloseDevice_NET();
                            int r3 = m_MyCamera[i].MV_CC_DestroyDevice_NET();
                            m_MyCamera[i] = null; // ch:释放后置空，防止悬垂引用；日志记录释放结果便于排查
                            MsgErroeLog.WriteLog("关闭相机" + (i + 1) + ": Stop=0x" + ((uint)r1).ToString("X8") + " Close=0x" + ((uint)r2).ToString("X8") + " Destroy=0x" + ((uint)r3).ToString("X8"));
                        }
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
            }
            finally
            {
                // ch:释放采集驱动缓冲（AllocHGlobal 分配的内存必须用 FreeHGlobal 释放）
                IntPtr[] bufs = { m_BufForDriver1, m_BufForDriver2, m_BufForDriver3, m_BufForDriver4, m_BufForDriver5, m_BufForDriver6, m_BufForDriver7, m_BufForDriver8 };
                for (int i = 0; i < 8; ++i)
                {
                    if (bufs[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(bufs[i]);
                    }
                }
                m_BufForDriver1 = IntPtr.Zero;
                m_BufForDriver2 = IntPtr.Zero;
                m_BufForDriver3 = IntPtr.Zero;
                m_BufForDriver4 = IntPtr.Zero;
                m_BufForDriver5 = IntPtr.Zero;
                m_BufForDriver6 = IntPtr.Zero;
                m_BufForDriver7 = IntPtr.Zero;
                m_BufForDriver8 = IntPtr.Zero;
                Array.Clear(m_nBufSizeForDriver, 0, m_nBufSizeForDriver.Length);
                // ch:释放图像转换缓冲 | en:Free image convert buffer
                for (int i = 0; i < 8; ++i)
                {
                    // ch:P0-2 必须持 bufLock：回调热路径在同一锁下向该缓冲 CopyMemory，StopGrabbing 返回不保证回调已出栈
                    lock (m_BufForSaveImageLock[i])
                    {
                        if (m_pSaveImageBuf[i] != IntPtr.Zero && System.Threading.Volatile.Read(ref _bmpInFlight[i]) == 0)
                        {
                            Marshal.FreeHGlobal(m_pSaveImageBuf[i]);
                            m_pSaveImageBuf[i] = IntPtr.Zero;
                            m_nSaveImageBufSize[i] = 0; // ch:P0-2 仅真正释放时清零尺寸（原实现跳过释放也清零，与仍在途的缓冲失配）
                        }
                        else if (m_pSaveImageBuf[i] != IntPtr.Zero)
                            MsgErroeLog.WriteLog("P1-06 关闭：相机" + (i + 1) + " 仍有在途 Bitmap，跳过 HGlobal 释放以防野指针（重开时 EnsureSaveImageBuf 会重新分配）");
                    }
                }
            }
            // ch:清理运行门禁，避免“运行中关设备→重开→无法恢复运行”
            yunxing = false;
            myjob1.yun = 0;
            myjob2.yun = 0;
            myjob3.yun = 0;
            myjob4.yun = 0;
            myjob5.yun = 0;
            myjob6.yun = 0;
            myjob7.yun = 0;
            myjob8.yun = 0;
            checkedListBox1.Enabled = true;
            // ch:重置成员变量 | en:Reset member variable
            m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            m_bGrabbing1 = false;
            m_bGrabbing2 = false;
            m_bGrabbing3 = false;
            m_bGrabbing4 = false;
            m_bGrabbing5 = false;
            m_bGrabbing6 = false;
            m_bGrabbing7 = false;
            m_bGrabbing8 = false;
            m_nCanOpenDeviceNum = 0;
            m_nDevNum = 0;

            //try
            //{
            //    // ch:取流标志位清零 | en:Reset flow flag bit
            //if (m_bGrabbing1 == true)
            //{
            //    m_bGrabbing1 = false;
            //    m_hReceiveThread.Join();
            //}
            //}
            //catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }


            try
            {

                bnGetParam1.Enabled = false;
                bnSetParam1.Enabled = false;
                bnOpen.Enabled = true;
                bnClose.Enabled = false;
                bnTriggerExec1.Enabled = false;
                bnStartGrab1.Enabled = false;
                bnStopGrab1.Enabled = false;
                bnGetParam2.Enabled = false;
                bnSetParam2.Enabled = false;
                bnTriggerExec2.Enabled = false;
                bnStartGrab2.Enabled = false;
                bnGetParam3.Enabled = false;
                bnSetParam3.Enabled = false;
                bnTriggerExec3.Enabled = false;
                bnStartGrab3.Enabled = false;
                bnGetParam4.Enabled = false;
                bnSetParam4.Enabled = false;
                bnTriggerExec4.Enabled = false;
                bnStartGrab4.Enabled = false;
                bnGetParam5.Enabled = false;
                bnSetParam5.Enabled = false;
                bnTriggerExec5.Enabled = false;
                bnStartGrab5.Enabled = false;
                bnGetParam6.Enabled = false;
                bnSetParam6.Enabled = false;
                bnTriggerExec6.Enabled = false;
                bnStartGrab6.Enabled = false;
                bnGetParam7.Enabled = false;
                bnSetParam7.Enabled = false;
                bnTriggerExec7.Enabled = false;
                bnStartGrab7.Enabled = false;
                bnGetParam8.Enabled = false;
                bnSetParam8.Enabled = false;
                bnTriggerExec8.Enabled = false;
                bnStartGrab8.Enabled = false;
                ResetMember();
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
            finally
            {
                UnlockAllCameras();
            }
        }
        public void ResetMember()
        {
            m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            // m_bGrabbing = false;
            m_nCanOpenDeviceNum = 0;
            m_nDevNum = 0;
            DeviceListAcq();
            m_nFrames = new int[8];
            m_bSaveImg = new bool[8];
            RebuildImageCallback(); // ch:P2 重建回调并登记强引用（防 GC 后 SDK 仍持旧指针 → AV）
            //   m_bTimerFlag = false;
            m_hDisplayHandle = new IntPtr[8];
            m_pDeviceInfo = new MyCamera.MV_CC_DEVICE_INFO[8];
        }

        private void cbSoftTrigger_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger1.Checked || comboBox1.Text == "通讯触发")
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing1)
                    {
                        bnTriggerExec1.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec1.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        // ch:软触发统一入口：先校验相机已打开且正在采集中，再发触发命令；
        // ch:失败时给出可读提示（0x80000203=MV_E_ENDPOINT_INVALID 端点错误：常见为相机与电脑跨网段、会话残留或相机未处于触发模式/未采流）
        private void TriggerCamera(int idx)
        {
            if (m_MyCamera[idx] == null)
            {
                MessageBox.Show("相机" + (idx + 1) + "未打开，无法触发", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool grabbing = false;
            switch (idx)
            {
                case 0: grabbing = m_bGrabbing1; break;
                case 1: grabbing = m_bGrabbing2; break;
                case 2: grabbing = m_bGrabbing3; break;
                case 3: grabbing = m_bGrabbing4; break;
                case 4: grabbing = m_bGrabbing5; break;
                case 5: grabbing = m_bGrabbing6; break;
                case 6: grabbing = m_bGrabbing7; break;
                case 7: grabbing = m_bGrabbing8; break;
            }
            if (!grabbing)
            {
                MessageBox.Show("相机" + (idx + 1) + "未开始采集，请先启动采集再触发", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int nRet = m_MyCamera[idx].MV_CC_SetCommandValue_NET("TriggerSoftware");
            if (nRet != MyCamera.MV_OK)
            {
                if (nRet == MyCamera.MV_E_ACCESS_DENIED)
                    MessageBox.Show("相机" + (idx + 1) + "触发失败(0x80000203 无权限)：相机被其他程序或残留会话占用，请重启软件并确认无其他软件占用相机", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (nRet == MyCamera.MV_E_CALLORDER)
                    MessageBox.Show("相机" + (idx + 1) + "触发失败：相机未处于触发模式或未在采集中，请先开启软触发并启动采集", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    ShowErrorMsg("相机" + (idx + 1) + "触发失败", nRet);
                MsgErroeLog.WriteLog("相机" + (idx + 1) + "触发失败:" + nRet);
            }
        }

        private void bnTriggerExec_Click(object sender, EventArgs e)
        {
            TriggerCamera(0);
        }

        private void button7_Click_4(object sender, EventArgs e)
        {
            if (!comboBox7.Text.Contains(".vpp"))
                canshuIni.WriteString("camera1", "fen", " ");
            if (!comboBox9.Text.Contains(".vpp"))
                canshuIni.WriteString("camera2", "fen", " ");
            if (!comboBox10.Text.Contains(".vpp"))
                canshuIni.WriteString("camera3", "fen", " ");
            if (!comboBox11.Text.Contains(".vpp"))
                canshuIni.WriteString("camera4", "fen", " ");
            if (!comboBox12.Text.Contains(".vpp"))
                canshuIni.WriteString("camera5", "fen", " ");
            if (!comboBox13.Text.Contains(".vpp"))
                canshuIni.WriteString("camera6", "fen", " ");
            if (!comboBox14.Text.Contains(".vpp"))
                canshuIni.WriteString("camera7", "fen", " ");
            if (!comboBox15.Text.Contains(".vpp"))
                canshuIni.WriteString("camera8", "fen", " ");
            canshuIni.WriteString("time", "IOyanshi", numericUpDown5.Value.ToString());
            canshuIni.WriteString("time", "feng", numericUpDown22.Value.ToString());
            if (comboBox21.SelectedIndex == 0)
                canshuIni.WriteString("camera", "cuntu", "存图限制");
            else
                canshuIni.WriteString("camera", "cuntu", "存图释放");
            if (comboBox22.SelectedIndex == 0)
                canshuIni.WriteString("camera", "tongji", "存图限制");
            else
                canshuIni.WriteString("camera", "tongji", "存图释放");
            canshuIni.WriteString("camera1", "en", button18.Text);
            canshuIni.WriteString("camera", "qufan", button31.Text);
            canshuIni.WriteString("camera2", "en", button19.Text);
            canshuIni.WriteString("camera3", "en", button34.Text);
            canshuIni.WriteString("camera4", "en", button33.Text);
            canshuIni.WriteString("camera5", "en", button30.Text);
            canshuIni.WriteString("camera6", "en", button29.Text);
            canshuIni.WriteString("camera7", "en", button28.Text);
            canshuIni.WriteString("camera8", "en", button21.Text);
            canshuIni.WriteString("camera", "yanshi", numericUpDown1.Value.ToString());
            canshuIni.WriteString("cuntu", "zhangshu", numericUpDown4.Value.ToString());
            if (checkBox3.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "shijianEn", "true");
            else
                canshuIni.WriteString("camera1", "shijianEn", "false");
            if (checkBox13.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "shijianEn", "true");
            else
                canshuIni.WriteString("camera2", "shijianEn", "false");
            if (checkBox18.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "shijianEn", "true");
            else
                canshuIni.WriteString("camera3", "shijianEn", "false");
            if (checkBox22.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "shijianEn", "true");
            else
                canshuIni.WriteString("camera4", "shijianEn", "false");
            if (checkBox36.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "shijianEn", "true");
            else
                canshuIni.WriteString("camera5", "shijianEn", "false");
            if (checkBox42.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "shijianEn", "true");
            else
                canshuIni.WriteString("camera6", "shijianEn", "false");
            if (checkBox48.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "shijianEn", "true");
            else
                canshuIni.WriteString("camera7", "shijianEn", "false");
            if (checkBox54.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "shijianEn", "true");
            else
                canshuIni.WriteString("camera8", "shijianEn", "false");
            if (checkBox27.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "serial", "true");
            else
                canshuIni.WriteString("camera1", "serial", "false");
            if (checkBox28.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "serial", "true");
            else
                canshuIni.WriteString("camera2", "serial", "false");
            if (checkBox29.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "serial", "true");
            else
                canshuIni.WriteString("camera3", "serial", "false");
            if (checkBox30.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "serial", "true");
            else
                canshuIni.WriteString("camera4", "serial", "false");
            if (checkBox31.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "serial", "true");
            else
                canshuIni.WriteString("camera5", "serial", "false");
            if (checkBox37.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "serial", "true");
            else
                canshuIni.WriteString("camera6", "serial", "false");
            if (checkBox43.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "serial", "true");
            else
                canshuIni.WriteString("camera7", "serial", "false");
            if (checkBox49.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "serial", "true");
            else
                canshuIni.WriteString("camera8", "serial", "false");
            canshuIni.WriteString("path", "path_1", path_1);
            canshuIni.WriteString("camera1", "triggermode", comboBox1.Text);
            canshuIni.WriteString("camera1", "exposure", tbExposure1.Text);
            canshuIni.WriteString("camera1", "gain", tbGain1.Text);
            canshuIni.WriteString("camera1", "rate", tbFrameRate1.Text);
            canshuIni.WriteString("camera1", "outtime", textBox3.Text);

            if (checkBox25.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera", "datajilu", "true");
            else
                canshuIni.WriteString("camera", "datajilu", "false");
            if (checkBox70.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera", "gongjujilu", "true");
            else
                canshuIni.WriteString("camera", "gongjujilu", "false");
            canshuIni.WriteString("camera", "display_raw", displayRawImage ? "true" : "false");
            if (checkBox5.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "triggeren", "true");
            else
                canshuIni.WriteString("camera1", "triggeren", "false");

            if (checkBox24.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "modbustcp", "true");
            else
                canshuIni.WriteString("camera1", "modbustcp", "false");
            if (checkBox1.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "1", "true");
            else
                canshuIni.WriteString("camera1", "1", "false");
            if (checkBox2.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "2", "true");
            else
                canshuIni.WriteString("camera1", "2", "false");
            canshuIni.WriteString("camera2", "triggermode", comboBox4.Text);
            canshuIni.WriteString("camera2", "exposure", tbExposure2.Text);
            canshuIni.WriteString("camera2", "gain", tbGain2.Text);
            canshuIni.WriteString("camera2", "rate", tbFrameRate2.Text);

            if (checkBox9.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "triggeren", "true");
            else
                canshuIni.WriteString("camera2", "triggeren", "false");
            if (checkBox23.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "modbustcp", "true");
            else
                canshuIni.WriteString("camera2", "modbustcp", "false");
            canshuIni.WriteString("camera3", "triggermode", comboBox5.Text);
            canshuIni.WriteString("camera3", "exposure", tbExposure3.Text);
            canshuIni.WriteString("camera3", "gain", tbGain3.Text);
            canshuIni.WriteString("camera3", "rate", tbFrameRate3.Text);
            if (checkBox15.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "triggeren", "true");
            else
                canshuIni.WriteString("camera3", "triggeren", "false");

            if (checkBox21.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "modbustcp", "true");
            else
                canshuIni.WriteString("camera3", "modbustcp", "false");
            canshuIni.WriteString("camera4", "triggermode", comboBox8.Text);
            canshuIni.WriteString("camera4", "exposure", tbExposure4.Text);
            canshuIni.WriteString("camera4", "gain", tbGain4.Text);
            canshuIni.WriteString("camera4", "rate", tbFrameRate4.Text);
            if (checkBox19.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "triggeren", "true");
            else
                canshuIni.WriteString("camera4", "triggeren", "false");
            if (checkBox17.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "modbustcp", "true");
            else
                canshuIni.WriteString("camera4", "modbustcp", "false");
            if (checkBox7.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "chatu", "true");
            else
                canshuIni.WriteString("camera1", "chatu", "false");
            if (checkBox71.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera", "NG", "true");
            else
                canshuIni.WriteString("camera", "NG", "false");

            if (checkBox6.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "chatu", "true");
            else
                canshuIni.WriteString("camera2", "chatu", "false");
            if (checkBox14.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "chatu", "true");
            else
                canshuIni.WriteString("camera3", "chatu", "false");
            if (checkBox12.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "chatu", "true");
            else
                canshuIni.WriteString("camera4", "chatu", "false");
            if (checkBox35.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "chatu", "true");
            else
                canshuIni.WriteString("camera5", "chatu", "false");
            if (checkBox41.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "chatu", "true");
            else
                canshuIni.WriteString("camera6", "chatu", "false");
            if (checkBox55.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "chatu", "true");
            else
                canshuIni.WriteString("camera7", "chatu", "false");
            if (checkBox53.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "chatu", "true");
            else
                canshuIni.WriteString("camera8", "chatu", "false");

            if (checkBox11.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera1", "biaoge", "true");
            else
                canshuIni.WriteString("camera1", "biaoge", "false");
            if (checkBox8.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera2", "biaoge", "true");
            else
                canshuIni.WriteString("camera2", "biaoge", "false");
            if (checkBox56.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera3", "biaoge", "true");
            else
                canshuIni.WriteString("camera3", "biaoge", "false");
            if (checkBox57.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera4", "biaoge", "true");
            else
                canshuIni.WriteString("camera4", "biaoge", "false");
            if (checkBox58.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "biaoge", "true");
            else
                canshuIni.WriteString("camera5", "biaoge", "false");
            if (checkBox59.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "biaoge", "true");
            else
                canshuIni.WriteString("camera6", "biaoge", "false");
            if (checkBox60.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "biaoge", "true");
            else
                canshuIni.WriteString("camera7", "biaoge", "false");
            if (checkBox61.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "biaoge", "true");
            else
                canshuIni.WriteString("camera8", "biaoge", "false");

            canshuIni.WriteString("camera5", "triggermode", comboBox25.Text);
            canshuIni.WriteString("camera5", "exposure", tbExposure5.Text);
            canshuIni.WriteString("camera5", "gain", tbGain5.Text);
            canshuIni.WriteString("camera5", "rate", tbFrameRate5.Text);
            if (checkBox33.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "triggeren", "true");
            else
                canshuIni.WriteString("camera5", "triggeren", "false");
            if (checkBox32.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera5", "modbustcp", "true");
            else
                canshuIni.WriteString("camera5", "modbustcp", "false");

            canshuIni.WriteString("camera6", "triggermode", comboBox28.Text);
            canshuIni.WriteString("camera6", "exposure", tbExposure6.Text);
            canshuIni.WriteString("camera6", "gain", tbGain6.Text);
            canshuIni.WriteString("camera6", "rate", tbFrameRate6.Text);
            if (checkBox39.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "triggeren", "true");
            else
                canshuIni.WriteString("camera6", "triggeren", "false");
            if (checkBox38.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera6", "modbustcp", "true");
            else
                canshuIni.WriteString("camera6", "modbustcp", "false");


            canshuIni.WriteString("camera7", "triggermode", comboBox31.Text);
            canshuIni.WriteString("camera7", "exposure", tbExposure7.Text);
            canshuIni.WriteString("camera7", "gain", tbGain7.Text);
            canshuIni.WriteString("camera7", "rate", tbFrameRate7.Text);
            if (checkBox45.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "triggeren", "true");
            else
                canshuIni.WriteString("camera7", "triggeren", "false");
            if (checkBox44.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera7", "modbustcp", "true");
            else
                canshuIni.WriteString("camera7", "modbustcp", "false");

            canshuIni.WriteString("camera8", "triggermode", comboBox34.Text);
            canshuIni.WriteString("camera8", "exposure", tbExposure8.Text);
            canshuIni.WriteString("camera8", "gain", tbGain8.Text);
            canshuIni.WriteString("camera8", "rate", tbFrameRate8.Text);
            if (checkBox51.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "triggeren", "true");
            else
                canshuIni.WriteString("camera8", "triggeren", "false");
            if (checkBox50.CheckState == CheckState.Checked)
                canshuIni.WriteString("camera8", "modbustcp", "true");
            else
                canshuIni.WriteString("camera8", "modbustcp", "false");
            canshuIni.WriteString("zhendongpan", "path", textBox19.Text);
            int geshu = comboBox38.Items.Count;
            canshuIni.WriteString("canshu", "geshu", geshu.ToString());
            if (geshu > 0)
            {
                for (int i = 0; i < geshu; i++)
                {
                    canshuIni.WriteString("canshu", (i + 1).ToString(), comboBox38.Items[i].ToString());
                }
                canshuIni.WriteString("canshu", "xuanze", comboBox38.Text);
            }

        }

        private void bnGetLineSel_Click(object sender, EventArgs e)
        {
            get_Selector(0, cbLineSel1);
        }

        private void bnSetLineSel_Click(object sender, EventArgs e)
        {
            set_Selector(0, cbLineSel1);
        }

        private void bnGetLineMode_Click(object sender, EventArgs e)
        {
            get_Mode(0, cbLineMode1);
        }

        private void bnSetLineMode_Click(object sender, EventArgs e)
        {
            set_Mode(0, cbLineMode1);
        }

        // ch:点击保存图片按钮 | en:Click on Save Image Button
        private void bnSaveBmp_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < m_nCanOpenDeviceNum; ++i)
            {
                MyCamera.MV_SAVE_IMG_TO_FILE_PARAM stSaveParam = new MyCamera.MV_SAVE_IMG_TO_FILE_PARAM();
                lock (m_BufForSaveImageLock[i])
                {
                    //if (m_stFrameInfo[i].nFrameLen == 0)
                    //{
                    //    richTextBox.Text += String.Format("No.{0} save image failed! No data!\r\n", (i + 1).ToString());
                    //    continue;
                    //}
                    stSaveParam.enImageType = MyCamera.MV_SAVE_IAMGE_TYPE.MV_Image_Bmp;
                    stSaveParam.enPixelType = m_stFrameInfo[i].enPixelType;
                    stSaveParam.pData = m_pSaveImageBuf[i];
                    stSaveParam.nDataLen = m_stFrameInfo[i].nFrameLen;
                    stSaveParam.nHeight = m_stFrameInfo[i].nHeight;
                    stSaveParam.nWidth = m_stFrameInfo[i].nWidth;
                    stSaveParam.pImagePath = "Dev" + i.ToString() + "_Image_w" + stSaveParam.nWidth.ToString() + "_h" + stSaveParam.nHeight.ToString() + "_fn" + m_stFrameInfo[i].nFrameNum.ToString() + ".bmp";
                    //stSaveParam.nQuality = 80;//存Jpeg时有效
                    int nRet = m_MyCamera[i].MV_CC_SaveImageToFile_NET(ref stSaveParam);
                    //if (MyCamera.MV_OK != nRet)
                    //{
                    //    richTextBox.Text += String.Format("No.{0} save image failed! nRet=0x{1}\r\n", (i + 1).ToString(), nRet.ToString("X"));
                    //}
                    //else
                    //{
                    //    richTextBox.Text += String.Format("No.{0} save image Succeed!\r\n", (i + 1).ToString());
                    //}
                }
            }
        }
        private void checkBox4_CheckedChanged_1(object sender, EventArgs e)
        {
            output_inverse(0, cbLineMode1, checkBox4);

        }

        private void button24_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox10, listBox5);
        }

        private void button25_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox5.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob2.img = new Bitmap(item.filePath);
                    if (myjob2.trriger == 0)
                    {
                        myjob2.trriger = 1;
                        getrecord(myjob2);
                    }
                    _ioPulseEnabled = true;
                    trriger2_temp = 1;
                    timer8.Interval = int.Parse(textBox9.Text);
                    timer8.Enabled = true;
                }
            }
            //img = new Bitmap(item.filePath);
            // myjob1.trriger = 1;
        }

        private void button23_Click(object sender, EventArgs e)
        {
            trriger2_temp = 0;
            timer8.Enabled = false;
            StopAllIoPulses();
        }

        private void comboBox4_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob2.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox4.Text == "连续运行")
                    {
                        if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger2.Enabled = false;
                        bnTriggerExec2.Enabled = false;
                    }
                    else if (comboBox4.Text == "触发拍照" || comboBox4.Text == "通讯触发")
                    {

                        if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger2.Checked || comboBox4.Text == "通讯触发")
                        {
                            if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing2)
                            {
                                bnTriggerExec2.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger2.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换2");
                }
                myjob2.triggerMode = comboBox4.Text;
            }
        }

        private void checkBox10_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(1, cbLineMode2, checkBox10);
        }

        private void listBox5_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox5.SelectedItem;
                if (item == null) return;
                myjob2.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob2.trriger == 0)
                    {
                        myjob2.trriger = 1;
                        getrecord(myjob2);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnGetParam2_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[1] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure2.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain2.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate2.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab2.Enabled = false;
            }
        }

        private void bnSetParam2_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[1] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure2.Text);
                float.Parse(tbGain2.Text);
                float.Parse(tbFrameRate2.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                MsgErroeLog.WriteLog("相机2开始设置参数: 曝光=" + tbExposure2.Text + ", 增益=" + tbGain2.Text + ", 帧率=" + tbFrameRate2.Text);
                
                int nRet = m_MyCamera[1].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                MsgErroeLog.WriteLog("相机2关闭自动曝光返回: 0x" + ((uint)nRet).ToString("X8"));
                
                float baoguang_temp = float.Parse(tbExposure2.Text);
                if (myjob2.baoguang != 0 && myjob2.block != null && myjob2.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob2, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval2 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[1].MV_CC_GetFloatValue_NET("ExposureTime", ref fval2) == MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机2曝光范围: 最小=" + fval2.fMin + ", 最大=" + fval2.fMax + ", 当前=" + fval2.fCurValue);
                    if (baoguang_temp < fval2.fMin) baoguang_temp = fval2.fMin;
                    if (baoguang_temp > fval2.fMax) baoguang_temp = fval2.fMax;
                    MsgErroeLog.WriteLog("相机2曝光调整后值: " + baoguang_temp);
                }
                nRet = m_MyCamera[1].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                MsgErroeLog.WriteLog("相机2设置曝光返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + baoguang_temp + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机2设置曝光失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                nRet = m_MyCamera[1].MV_CC_SetEnumValue_NET("GainAuto", 0);
                MsgErroeLog.WriteLog("相机2关闭自动增益返回: 0x" + ((uint)nRet).ToString("X8"));
                
                nRet = m_MyCamera[1].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain2.Text));
                MsgErroeLog.WriteLog("相机2设置增益返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + tbGain2.Text + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机2设置增益失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[1].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate2.Text));
                MsgErroeLog.WriteLog("相机2设置帧率返回: 0x" + ((uint)nRet).ToString("X8") + " (值=" + tbFrameRate2.Text + ")");
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("相机2设置帧率失败! 错误码: 0x" + ((uint)nRet).ToString("X8"));
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
                
                // 验证设置结果
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机2验证曝光: 当前值=" + stParam.fCurValue);
                
                nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机2验证增益: 当前值=" + stParam.fCurValue);
                
                nRet = m_MyCamera[1].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (nRet == MyCamera.MV_OK)
                    MsgErroeLog.WriteLog("相机2验证帧率: 当前值=" + stParam.fCurValue);
                    
                MsgErroeLog.WriteLog("相机2参数设置完成");
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("相机2参数设置异常:" + ex.Message); }
        }

        private void bnStartGrab2_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing2) return; // ch:防重入
            if (!CanStartGrab(1)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing2 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[1].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[1].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[1].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing2 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab2.Enabled = false;
                bnStopGrab2.Enabled = true;
            }
            catch
            {
                m_bGrabbing2 = false;
                MsgErroeLog.WriteLog("相机2开始采集");
            }
        }

        private void bnStopGrab2_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing2 == true && m_MyCamera[1] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing2 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[1].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab2.Enabled = true;
                bnStopGrab2.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机2停止采集"); }
        }

        private void cbSoftTrigger2_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger2.Checked || comboBox4.Text == "通讯触发")
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing2)
                    {
                        bnTriggerExec2.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec2.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnTriggerExec2_Click(object sender, EventArgs e)
        {
            TriggerCamera(1);
        }

        private void bnGetLineMode2_Click(object sender, EventArgs e)
        {
            get_Mode(1, cbLineMode2);
        }

        private void bnSetLineMode2_Click(object sender, EventArgs e)
        {
            set_Mode(1, cbLineMode2);
        }

        private void bnGetLineSel2_Click(object sender, EventArgs e)
        {
            get_Selector(1, cbLineSel2);
        }

        private void bnSetLineSel2_Click(object sender, EventArgs e)
        {
            set_Selector(1, cbLineSel2);
        }

        private void button19_Click_1(object sender, EventArgs e)
        {

        }

        private void 配置相机2ToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob2.block);
        }

        private void listBox3_MouseDown_1(object sender, MouseEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    string[] time111 = listBox3.SelectedItem.ToString().Split(':');
                    int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                    string ttt2 = time111[3];
                    int ttt3 = int.Parse(time111[4]);
                    Process myProc = null;
                    myProc = Process.Start(myjob2.pathhead_ng + day1 + "\\" + ttt1 + ttt2 + "#" + ttt3 + ".bmp");//开启一个进程
                    try
                    {
                        myProc.Kill();//关闭一个进程
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "图片显示2"); };
            });
        }

        private void button26_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox12, listBox8);
        }

        private void button38_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox17, listBox9);
        }

        private void button27_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox8.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob3.img = new Bitmap(item.filePath);
                    if (myjob3.trriger == 0)
                    {
                        myjob3.trriger = 1;
                        getrecord(myjob3);
                    }
                    _ioPulseEnabled = true;
                    trriger3_temp = 1;
                    timer11.Interval = int.Parse(textBox11.Text);
                    timer11.Enabled = true;
                }
            }
        }

        private void button39_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox9.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob4.img = new Bitmap(item.filePath);
                    if (myjob4.trriger == 0)
                    {
                        myjob4.trriger = 1;
                        getrecord(myjob4);
                    }
                    _ioPulseEnabled = true;
                    trriger4_temp = 1;
                    timer12.Interval = int.Parse(textBox16.Text);
                    timer12.Enabled = true;
                }
            }
        }

        private void timer12_Tick(object sender, EventArgs e)
        {
            if (myjob4.trriger == 0)
            {
                if (trriger4_temp == 1)
                {
                    int count = listBox9.Items.Count;
                    int select = listBox9.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox9.SelectedIndex = select + 1;
                        }
                        else
                            listBox9.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void button22_Click(object sender, EventArgs e)
        {
            trriger3_temp = 0;
            timer11.Enabled = false;
            StopAllIoPulses();
        }

        private void button37_Click(object sender, EventArgs e)
        {
            trriger4_temp = 0;
            timer12.Enabled = false;
            StopAllIoPulses();
        }

        private void comboBox5_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob3.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox5.Text == "连续运行")
                    {
                        if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger3.Enabled = false;
                        bnTriggerExec3.Enabled = false;
                    }
                    else if (comboBox5.Text == "触发拍照" || comboBox5.Text == "通讯触发")
                    {

                        if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger3.Checked || comboBox5.Text == "通讯触发")
                        {
                            if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing3)
                            {
                                bnTriggerExec3.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger3.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换3");
                }
                myjob3.triggerMode = comboBox5.Text;
            }
        }

        private void comboBox8_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob4.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox8.Text.Contains("连续运行"))
                    {
                        if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger4.Enabled = false;
                        bnTriggerExec4.Enabled = false;
                    }
                    else if (comboBox8.Text.Contains("触发拍照") || comboBox8.Text.Contains("通讯触发"))
                    {

                        if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger4.Checked || comboBox8.Text.Contains("通讯触发"))
                        {
                            if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing4)
                            {
                                bnTriggerExec4.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger4.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换4");
                }
                myjob4.triggerMode = comboBox8.Text;
            }
        }

        private void listBox8_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox8.SelectedItem;
                if (item == null) return;
                myjob3.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob3.trriger == 0)
                    {
                        myjob3.trriger = 1;
                        getrecord(myjob3);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox9_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox9.SelectedItem;
                if (item == null) return;
                myjob4.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob4.trriger == 0)
                    {
                        myjob4.trriger = 1;
                        getrecord(myjob4);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnGetParam3_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[2] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[2].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure3.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[2].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain3.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[2].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate3.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab3.Enabled = false;
            }
        }

        private void bnGetParam4_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[3] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[3].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure4.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[3].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain4.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[3].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate4.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab4.Enabled = false;
            }
        }

        private void bnSetParam3_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[2] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure3.Text);
                float.Parse(tbGain3.Text);
                float.Parse(tbFrameRate3.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[2].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure3.Text);
                if (myjob3.baoguang != 0 && myjob3.block != null && myjob3.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob3, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval3 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[2].MV_CC_GetFloatValue_NET("ExposureTime", ref fval3) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval3.fMin) baoguang_temp = fval3.fMin;
                    if (baoguang_temp > fval3.fMax) baoguang_temp = fval3.fMax;
                }
                int nRet = m_MyCamera[2].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[2].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[2].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain3.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[2].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate3.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnSetParam4_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[3] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure4.Text);
                float.Parse(tbGain4.Text);
                float.Parse(tbFrameRate4.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[3].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure4.Text);
                if (myjob4.baoguang != 0 && myjob4.block != null && myjob4.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob4, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval4 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[3].MV_CC_GetFloatValue_NET("ExposureTime", ref fval4) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval4.fMin) baoguang_temp = fval4.fMin;
                    if (baoguang_temp > fval4.fMax) baoguang_temp = fval4.fMax;
                }
                int nRet = m_MyCamera[3].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[3].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[3].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain4.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[3].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate4.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnStartGrab3_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing3) return; // ch:防重入
            if (!CanStartGrab(2)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing3 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[2].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[2].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[2].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing3 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab3.Enabled = false;
                bnStopGrab3.Enabled = true;
            }
            catch
            {
                m_bGrabbing3 = false;
                MsgErroeLog.WriteLog("相机3开始采集");
            }
        }

        private void bnStartGrab4_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing4) return; // ch:防重入
            if (!CanStartGrab(3)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing4 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[3].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[3].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[3].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing4 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab4.Enabled = false;
                bnStopGrab4.Enabled = true;
            }
            catch
            {
                m_bGrabbing4 = false;
                MsgErroeLog.WriteLog("相机4开始采集");
            }
        }

        private void bnStopGrab3_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing3 == true && m_MyCamera[2] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing3 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[2].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab3.Enabled = true;
                bnStopGrab3.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机3停止采集"); }
        }

        private void bnStopGrab4_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing4 == true && m_MyCamera[3] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing4 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[3].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab4.Enabled = true;
                bnStopGrab4.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机4停止采集"); }
        }

        private void cbSoftTrigger3_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger3.Checked || comboBox5.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing3)
                    {
                        bnTriggerExec3.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec3.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void cbSoftTrigger4_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger4.Checked || comboBox8.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing4)
                    {
                        bnTriggerExec4.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec4.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnTriggerExec3_Click(object sender, EventArgs e)
        {
            TriggerCamera(2);
        }

        private void bnTriggerExec4_Click(object sender, EventArgs e)
        {
            TriggerCamera(3);
        }

        private void 配置相机3ToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob3.block);
        }

        private void 配置相机4ToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob4.block);
        }

        private void listBox7_MouseDown_1(object sender, MouseEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    string[] time111 = listBox1.SelectedItem.ToString().Split(':');
                    int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                    string ttt2 = time111[3];
                    int ttt3 = int.Parse(time111[4]);
                    Process myProc = null;
                    myProc = Process.Start(myjob3.pathhead_ng + day1 + "\\" + ttt1 + ttt2 + "#" + ttt3 + ".bmp");//开启一个进程
                    try
                    {
                        myProc.Kill();//关闭一个进程
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "图片显示3"); };
            });
        }

        private void listBox6_MouseDown_1(object sender, MouseEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    string[] time111 = listBox1.SelectedItem.ToString().Split(':');
                    int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                    string ttt2 = time111[3];
                    int ttt3 = int.Parse(time111[4]);
                    Process myProc = null;
                    myProc = Process.Start(myjob4.pathhead_ng + day1 + "\\" + ttt1 + ttt2 + "#" + ttt3 + ".bmp");//开启一个进程
                    try
                    {
                        myProc.Kill();//关闭一个进程
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "图片显示4"); };
            });
        }

        private void checkBox24_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox24.CheckState == CheckState.Checked)
                {
                    myjob1.modbustcp = true;
                }
                else
                {

                    myjob1.modbustcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox23_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox23.CheckState == CheckState.Checked)
                {
                    myjob2.modbustcp = true;
                }
                else
                {

                    myjob2.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox21_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox21.CheckState == CheckState.Checked)
                {
                    myjob3.modbustcp = true;
                }
                else
                {

                    myjob3.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox17_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox17.CheckState == CheckState.Checked)
                {
                    myjob4.modbustcp = true;
                }
                else
                {

                    myjob4.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void button12_Click_1(object sender, EventArgs e)
        {
            if (button12.Text == "显示")
            {
                tabControl1.Visible = true;
                button12.Text = "隐藏";
            }
            else if (button12.Text == "隐藏")
            {
                tabControl1.Visible = false;
                button12.Text = "显示";
            }
        }

        private void checkBox6_CheckedChanged_1(object sender, EventArgs e)
        {

        }

        private void 打开日志界面ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReadErrorLog f1 = new ReadErrorLog();
            f1.Show();
        }

        private void 打开参数调整界面ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (f1.Visible == false)
            {
                f1.block_1 = myjob1.block;
                if (manager1.JobCount > 1)
                {
                    f1.temp = 1;
                    f1.block_2 = myjob2.block;
                }
                if (manager1.JobCount > 2)
                    f1.block_3 = myjob3.block;
                if (manager1.JobCount > 3)
                    f1.block_4 = myjob4.block;
                if (manager1.JobCount > 4)
                    f1.block_5 = myjob5.block;
                if (manager1.JobCount > 5)
                    f1.block_6 = myjob6.block;
                if (manager1.JobCount > 6)
                    f1.block_7 = myjob7.block;
                if (manager1.JobCount > 7)
                    f1.block_8 = myjob8.block;
                f1.path = path_1;
                f1.Myjob = manager1;
                f1.Visible = true;
            }
            else
            {
                f1.temp = 0;
                f1.Visible = false;

            }
        }

        private void 通讯界面ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (frm3.Visible == false)
                frm3.Visible = true;
            else
                frm3.Visible = false;
        }

        // ch:P2 「通讯 → 版本」：显示版本信息窗口（原生 WinForms 窗体，可点开查询；版本号 = 推送仓库的 tag，含时间线）
        private void 版本ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FormVersion.ShowVersion(this);
        }

        private void tabPage5_Click(object sender, EventArgs e)
        {

        }

        private void label57_Click(object sender, EventArgs e)
        {

        }

        private void label59_Click(object sender, EventArgs e)
        {

        }

        private void numericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            try
            {
                jiankongshijian = double.Parse(numericUpDown2.Value.ToString());
                canshuIni.WriteString("time", "NG", numericUpDown2.Value.ToString());

            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        private void label58_Click(object sender, EventArgs e)
        {

        }

        private void button3_Click_2(object sender, EventArgs e)
        {
            myjob1.runcishu = 0;
            myjob2.runcishu = 0;
            myjob3.runcishu = 0;
            myjob4.runcishu = 0;
            //  label140.Text = cogRecordDisplay1.BackColor.ToString();
        }

        private void button4_Click_1(object sender, EventArgs e)
        {

        }

        private void button19_Click(object sender, EventArgs e)
        {
            if (button19.Text == "2使用中")
            {
                myjob2.en = 0;
                button34.Visible = false;
                button33.Visible = false;
                button30.Visible = false;
                button29.Visible = false;
                button28.Visible = false;
                button21.Visible = false;
                button19.Text = "2屏蔽中";
                button34.Text = "3屏蔽中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob2.en = 1;
                button19.Text = "2使用中";
                button34.Text = "3屏蔽中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button34.Visible = true;
                button33.Visible = true;
                button30.Visible = true;
                button29.Visible = true;
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button18_Click_1(object sender, EventArgs e)
        {
            if (button18.Text == "1使用中")
            {
                myjob1.en = 0;
                button18.Text = "1屏蔽中";
                button19.Visible = false;
                button34.Visible = false;
                button33.Visible = false;
                button30.Visible = false;
                button29.Visible = false;
                button28.Visible = false;
                button21.Visible = false;
                button19.Text = "2屏蔽中";
                button34.Text = "3屏蔽中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob1.en = 1;
                button18.Text = "1使用中";
                button19.Text = "2屏蔽中";
                button34.Text = "3屏蔽中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button19.Visible = true;
                button34.Visible = true;
                button33.Visible = true;
                button30.Visible = true;
                button29.Visible = true;
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button34_Click(object sender, EventArgs e)
        {
            if (button34.Text == "3使用中")
            {
                myjob3.en = 0;
                button33.Visible = false;
                button30.Visible = false;
                button29.Visible = false;
                button28.Visible = false;
                button21.Visible = false;
                button34.Text = "3屏蔽中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob3.en = 1;
                button34.Text = "3使用中";
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button33.Visible = true;
                button30.Visible = true;
                button29.Visible = true;
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button33_Click(object sender, EventArgs e)
        {
            if (button33.Text == "4使用中")
            {
                myjob4.en = 0;
                button30.Visible = false;
                button29.Visible = false;
                button28.Visible = false;
                button21.Visible = false;
                button33.Text = "4屏蔽中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob4.en = 1;
                button33.Text = "4使用中";
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button30.Visible = true;
                button29.Visible = true;
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button4_Click_2(object sender, EventArgs e)
        {
            myjob1.sum = 0;
            myjob1.oksum = 0;
            myjob1.ngsum = 0;
            myjob1.rate = 0;
            myjob2.sum = 0;
            myjob2.oksum = 0;
            myjob2.ngsum = 0;
            myjob2.rate = 0;
            myjob3.sum = 0;
            myjob3.oksum = 0;
            myjob3.ngsum = 0;
            myjob3.rate = 0;
            myjob4.sum = 0;
            myjob4.oksum = 0;
            myjob4.ngsum = 0;
            myjob4.rate = 0;
            myjob5.sum = 0;
            myjob5.oksum = 0;
            myjob5.ngsum = 0;
            myjob5.rate = 0;
            myjob6.sum = 0;
            myjob6.oksum = 0;
            myjob6.ngsum = 0;
            myjob6.rate = 0;
            myjob7.sum = 0;
            myjob7.oksum = 0;
            myjob7.ngsum = 0;
            myjob7.rate = 0;
            myjob8.sum = 0;
            myjob8.oksum = 0;
            myjob8.ngsum = 0;
            myjob8.rate = 0;
            for (int i = 0; i < 8; ++i)
            {
                m_nFrames[i] = 0;
            }

        }

        private void comboBox21_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox21.SelectedIndex == 0)
            {
                cuntu = 0;
            }
            else
            {
                cuntu = 1;
            }
        }

        private void bnGetLineSel3_Click(object sender, EventArgs e)
        {
            get_Selector(2, cbLineSel3);
        }

        private void bnGetLineSel4_Click(object sender, EventArgs e)
        {
            get_Selector(3, cbLineSel4);
        }

        private void bnSetLineSel3_Click(object sender, EventArgs e)
        {
            set_Selector(2, cbLineSel3);
        }

        private void bnSetLineSel4_Click(object sender, EventArgs e)
        {
            set_Selector(3, cbLineSel4);
        }



        private void bnGetLineMode4_Click(object sender, EventArgs e)
        {
            get_Mode(3, cbLineMode4);
        }

        private void bnSetLineMode3_Click(object sender, EventArgs e)
        {
            set_Mode(2, cbLineMode3);
        }
        private void get_Selector(int c, ComboBox b)
        {
            int nRet;
            MyCamera.MVCC_ENUMVALUE stSelValue = new MyCamera.MVCC_ENUMVALUE();
            nRet = m_MyCamera[c].MV_CC_GetEnumValue_NET("LineSelector", ref stSelValue);
            if (MyCamera.MV_OK != nRet)
            {
                ShowErrorMsg("Get Fail!", nRet);
                return;
            }

            b.Items.Clear();

            for (int i = 0; i < stSelValue.nSupportedNum; i++)
            {
                b.Items.Add("LineSelector" + stSelValue.nSupportValue[i]);
                if (stSelValue.nCurValue == stSelValue.nSupportValue[i])
                {
                    b.SelectedIndex = i;
                }
            }
        }
        private void set_Selector(int c, ComboBox b)
        {
            int nRet;

            if (b.SelectedIndex == -1)
            {
                ShowErrorMsg("Please Select Output!", 0);
                return;
            }

            String strValue = b.SelectedItem.ToString().Substring(12);
            UInt32 nValue = Convert.ToUInt32(strValue);
            nRet = m_MyCamera[c].MV_CC_SetEnumValue_NET("LineSelector", nValue);
            if (MyCamera.MV_OK != nRet)
            {
                ShowErrorMsg("Set Fail!", nRet);
                return;
            }

            ShowErrorMsg("Set Succeed!", 0);
        }
        private void get_Mode(int c, ComboBox b)
        {
            int nRet;
            MyCamera.MVCC_ENUMVALUE stModeValue = new MyCamera.MVCC_ENUMVALUE();
            nRet = m_MyCamera[c].MV_CC_GetEnumValue_NET("LineMode", ref stModeValue);
            if (MyCamera.MV_OK != nRet)
            {
                ShowErrorMsg("Get Fail!", nRet);
                return;
            }

            b.Items.Clear();

            for (int i = 0; i < stModeValue.nSupportedNum; i++)
            {
                b.Items.Add("LineMode" + stModeValue.nSupportValue[i]);
                if (stModeValue.nCurValue == stModeValue.nSupportValue[i])
                {
                    b.SelectedIndex = i;
                }
            }
        }
        private void set_Mode(int c, ComboBox b)
        {
            int nRet;
            if (!dahua)
            {
                if (b.SelectedIndex == -1)
                {
                    ShowErrorMsg("Please Select Output!", 0);
                    return;
                }

                String strValue = b.SelectedItem.ToString().Substring(8);
                UInt32 nValue = Convert.ToUInt32(strValue);
                nRet = m_MyCamera[c].MV_CC_SetEnumValue_NET("LineMode", nValue);
                if (MyCamera.MV_OK != nRet)
                {
                    ShowErrorMsg("Set Fail!", nRet);
                    return;
                }

                ShowErrorMsg("Set Succeed!", 0);
            }

        }
        private void output_inverse(int c, ComboBox b, CheckBox d)
        {
            if (d.CheckState == CheckState.Checked)
            {
                int nRet;

                if (b.SelectedIndex == -1)
                {
                    ShowErrorMsg("Please Select Output!", 0);
                    return;
                }

                //String strValue = cbLineMode.SelectedItem.ToString().Substring(8);
                UInt32 nValue = Convert.ToUInt32("1");
                //nRet = m_MyCamera.MV_CC_SetEnumValue_NET("LineInverter", nValue);
                nRet = m_MyCamera[c].MV_CC_SetBoolValue_NET("LineInverter", true);
                if (MyCamera.MV_OK != nRet)
                {
                    ShowErrorMsg("Set Fail!", nRet);
                    return;
                }

                ShowErrorMsg("Set Succeed!", 0);

            }
            else
            {
                int nRet;

                if (b.SelectedIndex == -1)
                {
                    ShowErrorMsg("Please Select Output!", 0);
                    return;
                }

                //String strValue = cbLineMode.SelectedItem.ToString().Substring(8);
                UInt32 nValue = Convert.ToUInt32("0");
                // nRet = m_MyCamera.MV_CC_SetEnumValue_NET("LineInverter", nValue);
                nRet = m_MyCamera[c].MV_CC_SetBoolValue_NET("LineInverter", false);
                if (MyCamera.MV_OK != nRet)
                {
                    ShowErrorMsg("Set Fail!", nRet);
                    return;
                }

                ShowErrorMsg("Set Succeed!", 0);
            }
        }

        private void bnGetLineMode3_Click(object sender, EventArgs e)
        {
            get_Mode(2, cbLineMode3);
        }

        private void bnSetLineMode4_Click(object sender, EventArgs e)
        {
            set_Mode(3, cbLineMode4);
        }

        private void checkBox16_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(2, cbLineMode3, checkBox16);
        }

        private void checkBox20_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(3, cbLineMode4, checkBox20);
        }

        private void numericUpDown22_ValueChanged(object sender, EventArgs e)
        {
            feng = long.Parse(numericUpDown22.Value.ToString());
        }

        private void button6_Click_3(object sender, EventArgs e)
        {
            if (button6.Text == "顶" && listBox4.Visible == true)
            {
                if (yunxing == false && listBox4.Items.Count > 0)
                {
                    listBox4.SelectedIndex = 0;
                    button6.Text = "底";
                }
            }
            else if (button6.Text == "底" && listBox4.Visible == true)
            {
                if (yunxing == false && listBox4.Items.Count > 0)
                {
                    listBox4.SelectedIndex = listBox4.Items.Count - 1;
                    button6.Text = "顶";
                }
            }
        }

        private void button14_Click_1(object sender, EventArgs e)
        {
            if (button14.Text == "顶" && listBox5.Visible == true)
            {
                if (yunxing == false && listBox5.Items.Count > 0)
                {
                    listBox5.SelectedIndex = 0;
                    button15.Text = "底";
                }
            }
            else if (button14.Text == "底" && listBox5.Visible == true)
            {
                if (yunxing == false && listBox5.Items.Count > 0)
                {
                    listBox5.SelectedIndex = listBox5.Items.Count - 1;
                    button14.Text = "顶";
                }
            }
        }

        private void button15_Click_1(object sender, EventArgs e)
        {
            if (button15.Text == "顶" && listBox8.Visible == true)
            {
                if (yunxing == false && listBox8.Items.Count > 0)
                {
                    listBox8.SelectedIndex = 0;
                    button15.Text = "底";
                }
            }
            else if (button15.Text == "底" && listBox8.Visible == true)
            {
                if (yunxing == false && listBox8.Items.Count > 0)
                {
                    listBox8.SelectedIndex = listBox8.Items.Count - 1;
                    button15.Text = "顶";
                }
            }
        }

        private void button16_Click_1(object sender, EventArgs e)
        {
            if (button16.Text == "顶" && listBox9.Visible == true)
            {
                if (yunxing == false && listBox9.Items.Count > 0)
                {
                    listBox9.SelectedIndex = 0;
                    button16.Text = "底";
                }
            }
            else if (button16.Text == "底" && listBox9.Visible == true)
            {
                if (yunxing == false && listBox9.Items.Count > 0)
                {
                    listBox9.SelectedIndex = listBox9.Items.Count - 1;
                    button16.Text = "顶";
                }
            }
        }

        private void button17_Click_1(object sender, EventArgs e)
        {

        }
        private void display()
        {
            this.Invoke(new Action(() =>
            {
                camera_sum = 0; // ch:R16 每次进入重算（原靠调用方先清零，若重复调用会累加导致排版分支走错）
                if (canshuIni.ReadString("camera1", "en", "1使用中").Contains("使用"))
                {
                    button18.Text = "1使用中";
                    myjob1.en = 1;
                    tableLayoutPanel2.Visible = true;
                }
                else
                {
                    button18.Text = "1屏蔽中";
                    myjob1.en = 0;
                    tableLayoutPanel2.Visible = false; // ch:P2-20 原缺此句（2~8 相机屏蔽分支都有）：ini 配置为屏蔽时相机1渲染面板仍显示
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera2", "en", "2使用中").Contains("使用"))
                {
                    button19.Text = "2使用中";
                    myjob2.en = 1;
                    tableLayoutPanel3.Visible = true;
                }
                else
                {
                    button19.Text = "2屏蔽中";
                    myjob2.en = 0;
                    tableLayoutPanel3.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera3", "en", "3使用中").Contains("使用"))
                {
                    button34.Text = "3使用中";
                    myjob3.en = 1;
                    tableLayoutPanel5.Visible = true;
                }
                else
                {
                    button34.Text = "3屏蔽中";
                    myjob3.en = 0;
                    tableLayoutPanel5.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera4", "en", "4使用中").Contains("使用"))
                {
                    button33.Text = "4使用中";
                    myjob4.en = 1;
                    tableLayoutPanel7.Visible = true;
                }
                else
                {
                    button33.Text = "4屏蔽中";
                    myjob4.en = 0;
                    tableLayoutPanel7.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera5", "en", "5使用中").Contains("使用"))
                {
                    button30.Text = "5使用中";
                    myjob5.en = 1;
                    tableLayoutPanel9.Visible = true;
                }
                else
                {
                    button30.Text = "5屏蔽中";
                    myjob5.en = 0;
                    tableLayoutPanel9.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera6", "en", "6使用中").Contains("使用"))
                {
                    button29.Text = "6使用中";
                    myjob6.en = 1;
                    tableLayoutPanel12.Visible = true;
                }
                else
                {
                    button29.Text = "6屏蔽中";
                    myjob6.en = 0;
                    tableLayoutPanel12.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera7", "en", "7使用中").Contains("使用"))
                {
                    button28.Text = "7使用中";
                    myjob7.en = 1;
                    tableLayoutPanel14.Visible = true;
                }
                else
                {
                    button28.Text = "7屏蔽中";
                    myjob7.en = 0;
                    tableLayoutPanel14.Visible = false;
                    camera_sum++;
                }
                if (canshuIni.ReadString("camera8", "en", "8使用中").Contains("使用"))
                {
                    button21.Text = "8使用中";
                    myjob8.en = 1;
                    tableLayoutPanel16.Visible = true;
                }
                else
                {
                    button21.Text = "8屏蔽中";
                    myjob8.en = 0;
                    tableLayoutPanel16.Visible = false;
                    camera_sum++;
                }
                if (camera_sum >= 7)
                {
                    // ch:R16 全跨改由 ApplyMonitorGrid 统一处理（旧固定跨相机1面板，仅相机1单独启用时才碰巧正确）
                    myjob1.record[0] = 1;
                    myjob1.record[1] = 1;
                    myjob1.record[2] = 1;
                }
                else if (camera_sum >= 6)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 1F));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 99F));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5F));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5F));
                    myjob1.record[0] = 49.5f;
                    myjob1.record[1] = 49.5f;
                    myjob1.record[2] = 1f;
                    myjob2.record[0] = 99f;
                    myjob2.record[1] = 0.5f;
                    myjob2.record[2] = 0.5f;
                }
                else if (camera_sum >= 4)
                {
                    // ch:R16 原硬编码「藏相机3面板、强显相机4/5面板」凑 2×2，正是4相机错位根源：槽位3显示
                    // 相机4的图、槽位4是相机5的黑面板、相机3的图无处显示；且17处以 tableLayoutPanel5.Visible
                    // 为「相机3启用」的判断(结果标签路由/NG详情芯片/双击开块编辑器)全被带偏。排版改交 ApplyMonitorGrid。
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 1F));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 1F));
                    myjob1.record[0] = 49.5f;
                    myjob1.record[1] = 49.5f;
                    myjob1.record[2] = 1f;
                    myjob2.record[0] = 49.5f;
                    myjob2.record[1] = 49.5f;
                    myjob2.record[2] = 1f;

                }
                else if (camera_sum >= 2)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 33F));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 34F));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 34F));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 49.5F));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 1F));
                    myjob1.record[0] = 33f;
                    myjob1.record[1] = 33f;
                    myjob1.record[2] = 34f;
                    myjob2.record[0] = 49.5f;
                    myjob2.record[1] = 49.5f;
                    myjob2.record[2] = 1f;
                }
                else
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 33F));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 33F));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 34F));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 33F));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 33F));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 34F));
                    myjob1.record[0] = 33f;
                    myjob1.record[1] = 33f;
                    myjob1.record[2] = 34f;
                    myjob2.record[0] = 33f;
                    myjob2.record[1] = 33f;
                    myjob2.record[2] = 34f;
                }

                ApplyMonitorGrid(); // ch:R16 按启用相机重排槽位（见下方方法注释：修4相机槽位错位/相机3图不显示）
            }));
        }

        // ch:R16 监控画面排版：tableLayoutPanel1 是 3×3 固定单元格网格（设计格位 cam1(0,0) cam2(1,0)
        // cam3(2,0) cam4(0,1) cam5(1,1) cam6(2,1) cam7(0,2) cam8(1,2)），旧 display() 只按启用数改
        // ColumnStyles/RowStyles 并硬凑可见性、从不移动单元格 —— 4相机分支藏 cam3、强显 cam5，2×2 槽位
        // 实际是 cam1/cam2/cam4/cam5（槽位3=相机4的图、槽位4=相机5黑面板、相机3的图无处显示）。
        // 现按相机号升序把第 k 个启用相机装入第 k 个槽位：槽表按启用数取行优先前 N 格，与各分支的
        // 百分比样式一一对应；面板可见性以 myjobN.en 为准（tableLayoutPanel5.Visible 被17处当作
        // 「相机3启用」标志，必须与 en 对齐）。单相机把唯一启用面板跨满 3×3；双击最大化的
        // GetCellPosition/还原快照(myjob1/2.record) 取当前格位自动跟随；cam9 备用面板不参与。
        private void ApplyMonitorGrid()
        {
            TableLayoutPanel[] panels = {
                tableLayoutPanel2, tableLayoutPanel3, tableLayoutPanel5, tableLayoutPanel7,
                tableLayoutPanel9, tableLayoutPanel12, tableLayoutPanel14, tableLayoutPanel16 };
            bool[] en = {
                myjob1.en != 0, myjob2.en != 0, myjob3.en != 0, myjob4.en != 0,
                myjob5.en != 0, myjob6.en != 0, myjob7.en != 0, myjob8.en != 0 };

            int n = 0;
            for (int i = 0; i < 8; i++)
                if (en[i]) n++;

            TableLayoutPanelCellPosition[] slots;
            if (n <= 1) slots = new TableLayoutPanelCellPosition[] { new TableLayoutPanelCellPosition(0, 0) };
            else if (n == 2) slots = new TableLayoutPanelCellPosition[] { new TableLayoutPanelCellPosition(0, 0), new TableLayoutPanelCellPosition(1, 0) };
            else if (n <= 4) slots = new TableLayoutPanelCellPosition[] {
                new TableLayoutPanelCellPosition(0, 0), new TableLayoutPanelCellPosition(1, 0), new TableLayoutPanelCellPosition(0, 1), new TableLayoutPanelCellPosition(1, 1) };
            else if (n <= 6) slots = new TableLayoutPanelCellPosition[] {
                new TableLayoutPanelCellPosition(0, 0), new TableLayoutPanelCellPosition(1, 0), new TableLayoutPanelCellPosition(2, 0),
                new TableLayoutPanelCellPosition(0, 1), new TableLayoutPanelCellPosition(1, 1), new TableLayoutPanelCellPosition(2, 1) };
            else slots = new TableLayoutPanelCellPosition[] {
                new TableLayoutPanelCellPosition(0, 0), new TableLayoutPanelCellPosition(1, 0), new TableLayoutPanelCellPosition(2, 0),
                new TableLayoutPanelCellPosition(0, 1), new TableLayoutPanelCellPosition(1, 1), new TableLayoutPanelCellPosition(2, 1),
                new TableLayoutPanelCellPosition(0, 2), new TableLayoutPanelCellPosition(1, 2), new TableLayoutPanelCellPosition(2, 2) };

            tableLayoutPanel1.SuspendLayout();
            try
            {
                for (int i = 0; i < 8; i++) // 复位：清残留全跨、收回 8 块相机面板
                {
                    tableLayoutPanel1.SetRowSpan(panels[i], 1);
                    tableLayoutPanel1.SetColumnSpan(panels[i], 1);
                    panels[i].Visible = false;
                }
                int k = 0;
                for (int i = 0; i < 8 && k < slots.Length; i++)
                {
                    if (!en[i]) continue;
                    panels[i].Visible = true;
                    if (n <= 1) // 单相机：唯一启用面板跨满 3×3
                    {
                        tableLayoutPanel1.SetCellPosition(panels[i], new TableLayoutPanelCellPosition(0, 0));
                        tableLayoutPanel1.SetRowSpan(panels[i], 3);
                        tableLayoutPanel1.SetColumnSpan(panels[i], 3);
                    }
                    else
                        tableLayoutPanel1.SetCellPosition(panels[i], slots[k]);
                    k++;
                }
            }
            finally
            {
                tableLayoutPanel1.ResumeLayout(true);
            }
        }

        private void baoguang_set()
        {
            // 创建Myjob数组便于循环处理
            Myjob[] myjobs = new Myjob[] { myjob1, myjob2, myjob3, myjob4, myjob5, myjob6, myjob7, myjob8 };
            TextBox[] tbExposures = new TextBox[] { tbExposure1, tbExposure2, tbExposure3, tbExposure4, tbExposure5, tbExposure6, tbExposure7, tbExposure8 };
            TextBox[] tbGains = new TextBox[] { tbGain1, tbGain2, tbGain3, tbGain4, tbGain5, tbGain6, tbGain7, tbGain8 };
            TextBox[] tbFrameRates = new TextBox[] { tbFrameRate1, tbFrameRate2, tbFrameRate3, tbFrameRate4, tbFrameRate5, tbFrameRate6, tbFrameRate7, tbFrameRate8 };
            
            this.Invoke(new Action(() =>
            {
                for (int i = 0; i < 8; i++)
                {
                    string camSection = "camera" + (i + 1);
                    // ch:屏蔽(en!=1)的相机不做参数处理：原实现对全部 8 路读 VPP/ini，屏蔽相机 ini 段无配置时产生噪声日志甚至"格式不正确"异常
                    if (myjobs[i].en != 1) continue;
                    bool fromVpp = false;
                    
                    // ★ 优先从VPP block读取曝光值
                    try
                    {
                        if (myjobs[i].block != null && myjobs[i].block.Inputs.Contains("baoguang"))
                        {
                            myjobs[i].baoguang = float.Parse(myjobs[i].block.Inputs["baoguang"].Value.ToString());
                            tbExposures[i].Text = myjobs[i].baoguang.ToString();
                            fromVpp = true;
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从VPP block读取曝光=" + myjobs[i].baoguang);
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 读取VPP曝光失败: " + ex.Message);
                    }
                    
                    // ★ VPP block无baoguang输入时，从code.ini读取曝光值
                    if (!fromVpp)
                    {
                        try
                        {
                            string iniExp = canshuIni.ReadString(camSection, "exposure", "1000");
                            float expVal;
                            if (!float.TryParse(iniExp, out expVal)) expVal = 1000; // ch:键存在但值为空/畸形时回退默认，不再抛"输入字符串的格式不正确"
                            myjobs[i].baoguang = expVal;
                            tbExposures[i].Text = expVal.ToString();
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取曝光=" + expVal);
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取曝光失败: " + ex.Message);
                        }
                    }
                    
                    // ★ 从VPP block读取增益值（如果block有此输入）
                    bool gainFromVpp = false;
                    try
                    {
                        if (myjobs[i].block != null && myjobs[i].block.Inputs.Contains("zengyi"))
                        {
                            float gainVal = float.Parse(myjobs[i].block.Inputs["zengyi"].Value.ToString());
                            tbGains[i].Text = gainVal.ToString();
                            gainFromVpp = true;
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从VPP block读取增益=" + gainVal);
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 读取VPP增益失败: " + ex.Message);
                    }
                    
                    // ★ VPP block无增益输入时，从code.ini读取增益值
                    if (!gainFromVpp)
                    {
                        try
                        {
                            string iniGain = canshuIni.ReadString(camSection, "gain", "1");
                            float gainVal;
                            if (!float.TryParse(iniGain, out gainVal)) gainVal = 1; // ch:空值回退默认（现场 code.ini 存在 gain 键为空的情况）
                            tbGains[i].Text = gainVal.ToString();
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取增益=" + gainVal);
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取增益失败: " + ex.Message);
                        }
                    }
                    
                    // ★ 从code.ini读取帧率（确保bnSetParam_Click不会因空值而跳过）
                    try
                    {
                        string iniRate = canshuIni.ReadString(camSection, "rate", "500").Replace("\0", "").Trim();
                        if (iniRate == "") iniRate = "500"; // ch:空值回退默认，保证 bnSetParam 不因空跳过
                        tbFrameRates[i].Text = iniRate;
                        MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取帧率=" + iniRate);
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("baoguang_set: 相机" + (i + 1) + " 从code.ini读取帧率失败: " + ex.Message);
                    }
                }
                
                // 读取changdu参数
                try
                {
                    if (myjob1.block != null && myjob1.block.Inputs.Contains("changdu")) myjob1.changdu = int.Parse(myjob1.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob2.block != null && myjob2.block.Inputs.Contains("changdu")) myjob2.changdu = int.Parse(myjob2.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob3.block != null && myjob3.block.Inputs.Contains("changdu")) myjob3.changdu = int.Parse(myjob3.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob4.block != null && myjob4.block.Inputs.Contains("changdu")) myjob4.changdu = int.Parse(myjob4.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob5.block != null && myjob5.block.Inputs.Contains("changdu")) myjob5.changdu = int.Parse(myjob5.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob6.block != null && myjob6.block.Inputs.Contains("changdu")) myjob6.changdu = int.Parse(myjob6.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob7.block != null && myjob7.block.Inputs.Contains("changdu")) myjob7.changdu = int.Parse(myjob7.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                try
                {
                    if (myjob8.block != null && myjob8.block.Inputs.Contains("changdu")) myjob8.changdu = int.Parse(myjob8.block.Inputs["changdu"].Value.ToString());
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }));
        }
        private void trriger_set()
        {
            this.Invoke(new Action(() =>
            {
                if (true) // 海康相机：触发模式初始化必须执行（原 dahua 恒 true 导致此处为死代码）
                {
                    try
                    {
                        if (myjob1.block != null && myjob1.block.Inputs.Contains("triggermode") && (myjob1.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob1.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob1.block.Inputs["triggermode"].Value.ToString() == "通讯触发"))
                        {
                            comboBox1.Text = myjob1.block.Inputs["triggermode"].Value.ToString();
                            myjob1.triggerMode = myjob1.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox1.Text = canshuIni.ReadString("camera1", "triggermode", "连续运行").Replace("\0", "");
                            myjob1.triggerMode = canshuIni.ReadString("camera1", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox1.Text = canshuIni.ReadString("camera1", "triggermode", "连续运行").Replace("\0", "");
                        myjob1.triggerMode = canshuIni.ReadString("camera1", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob2.block != null && myjob2.block.Inputs.Contains("triggermode") && (myjob2.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob2.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob2.block.Inputs["triggermode"].Value.ToString() == "通讯触发"))
                        {
                            comboBox4.Text = myjob2.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                            myjob2.triggerMode = myjob2.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox4.Text = canshuIni.ReadString("camera2", "triggermode", "连续运行").Replace("\0", "");
                            myjob2.triggerMode = canshuIni.ReadString("camera2", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox4.Text = canshuIni.ReadString("camera2", "triggermode", "连续运行").Replace("\0", "");
                        myjob2.triggerMode = canshuIni.ReadString("camera2", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob3.block != null && myjob3.block.Inputs.Contains("triggermode") && (myjob3.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob3.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob3.block.Inputs["triggermode"].Value.ToString() == "通讯触发"))
                        {
                            comboBox5.Text = myjob3.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                            myjob3.triggerMode = myjob3.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox5.Text = canshuIni.ReadString("camera3", "triggermode", "连续运行").Replace("\0", "");
                            myjob3.triggerMode = canshuIni.ReadString("camera3", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox5.Text = canshuIni.ReadString("camera3", "triggermode", "连续运行").Replace("\0", "");
                        myjob3.triggerMode = canshuIni.ReadString("camera3", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob4.block != null && myjob4.block.Inputs.Contains("triggermode") && (myjob4.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob4.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob4.block.Inputs["triggermode"].Value.ToString() == "通讯触发"))
                        {
                            comboBox8.Text = myjob4.block.Inputs["triggermode"].Value.ToString();
                            myjob4.triggerMode = myjob4.block.Inputs["triggermode"].Value.ToString();
                        }
                        else
                        {
                            comboBox8.Text = canshuIni.ReadString("camera4", "triggermode", "连续运行").Replace("\0", "");
                            myjob4.triggerMode = canshuIni.ReadString("camera4", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox8.Text = canshuIni.ReadString("camera4", "triggermode", "连续运行").Replace("\0", "");
                        myjob4.triggerMode = canshuIni.ReadString("camera4", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob5.block != null && myjob5.block.Inputs.Contains("triggermode") && ((myjob5.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob5.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob5.block.Inputs["triggermode"].Value.ToString() == "通讯触发")))
                        {
                            comboBox25.Text = myjob5.block.Inputs["triggermode"].Value.ToString();
                            myjob5.triggerMode = myjob5.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox25.Text = canshuIni.ReadString("camera5", "triggermode", "连续运行").Replace("\0", "");
                            myjob5.triggerMode = canshuIni.ReadString("camera5", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox25.Text = canshuIni.ReadString("camera5", "triggermode", "连续运行").Replace("\0", "");
                        myjob5.triggerMode = canshuIni.ReadString("camera5", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob6.block != null && myjob6.block.Inputs.Contains("triggermode") && ((myjob6.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob6.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob6.block.Inputs["triggermode"].Value.ToString() == "通讯触发")))
                        {
                            comboBox28.Text = myjob6.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                            myjob6.triggerMode = myjob6.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox28.Text = canshuIni.ReadString("camera6", "triggermode", "连续运行").Replace("\0", "");
                            myjob6.triggerMode = canshuIni.ReadString("camera6", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox28.Text = canshuIni.ReadString("camera6", "triggermode", "连续运行").Replace("\0", "");
                        myjob6.triggerMode = canshuIni.ReadString("camera6", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob7.block != null && myjob7.block.Inputs.Contains("triggermode") && ((myjob7.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob7.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob7.block.Inputs["triggermode"].Value.ToString() == "通讯触发")))
                        {
                            comboBox31.Text = myjob7.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                            myjob7.triggerMode = myjob7.block.Inputs["triggermode"].Value.ToString().Replace("\0", "");
                        }
                        else
                        {
                            comboBox31.Text = canshuIni.ReadString("camera7", "triggermode", "连续运行").Replace("\0", "");
                            myjob7.triggerMode = canshuIni.ReadString("camera7", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox31.Text = canshuIni.ReadString("camera7", "triggermode", "连续运行").Replace("\0", "");
                        myjob7.triggerMode = canshuIni.ReadString("camera7", "triggermode", "连续运行").Replace("\0", "");
                    }
                    try
                    {
                        if (myjob8.block != null && myjob8.block.Inputs.Contains("triggermode") && ((myjob8.block.Inputs["triggermode"].Value.ToString() == "触发拍照" || myjob8.block.Inputs["triggermode"].Value.ToString() == "连续运行" || myjob8.block.Inputs["triggermode"].Value.ToString() == "通讯触发")))
                        {
                            comboBox34.Text = myjob8.block.Inputs["triggermode"].Value.ToString();
                            myjob8.triggerMode = myjob8.block.Inputs["triggermode"].Value.ToString();
                        }
                        else
                        {
                            comboBox34.Text = canshuIni.ReadString("camera8", "triggermode", "连续运行").Replace("\0", "");
                            myjob8.triggerMode = canshuIni.ReadString("camera8", "triggermode", "连续运行").Replace("\0", "");
                        }
                    }
                    catch
                    {
                        comboBox34.Text = canshuIni.ReadString("camera8", "triggermode", "连续运行").Replace("\0", "");
                        myjob8.triggerMode = canshuIni.ReadString("camera8", "triggermode", "连续运行").Replace("\0", "");
                    }
                }
            }));
            // ch:P0-1 原尾段直接调用线程写 UI 控件与相机句柄：调用方含后台线程（jindu/切换 Task.Run），
            //   与 timer2 重连（持 _cameraLocks 做 Destroy/Create）并发可致句柄 UAF。封送 UI 线程 + 全程持相机锁。
            Action tail = TrrigerSetTail;
            try { if (InvokeRequired) Invoke(tail); else tail(); }
            catch (Exception ex) { MsgErroeLog.WriteLog("trriger_set 尾段封送失败:" + ex.Message); }
        }

        private void TrrigerSetTail()
        {
            if (!TryLockAllCameras(2000))
            {
                MsgErroeLog.WriteLog("trriger_set: 相机锁获取超时（打开/重连进行中），本轮触发模式下发跳过");
                return;
            }
            try
            {
            if (true) // 海康相机：触发模式下发必须执行（原 dahua 恒 true 导致此处为死代码）
            {
                try
                {
                    if (myjob1.triggerMode == "连续运行")
                    {
                        if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger1.Enabled = false;
                        bnTriggerExec1.Enabled = false;
                    }
                    else if (myjob1.triggerMode == "触发拍照" || myjob1.triggerMode == "通讯触发")
                    {
                        myjob1.trrigerEn = true;
                        if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger1.Checked || myjob1.triggerMode == "通讯触发")
                        {
                            if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing1)
                            {
                                bnTriggerExec1.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[0] != null) m_MyCamera[0].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger1.Enabled = true;

                    }
                    //uint nValue = 2;
                    // m_MyCamera[0].MV_CC_SetEnumValue_NET("LineSelector", nValue);
                    //nValue = 8;
                    // m_MyCamera[0].MV_CC_SetEnumValue_NET("LineMode", nValue);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换1" + myjob1.index);
                }
                try
                {
                    if (manager1.JobCount > 1)
                    {
                        try
                        {
                            if (myjob2.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger2.Enabled = false;
                                bnTriggerExec2.Enabled = false;
                            }
                            else if (myjob2.triggerMode == "触发拍照" || myjob2.triggerMode == "通讯触发")
                            {
                                myjob2.trrigerEn = true;
                                if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger2.Checked || myjob2.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing2)
                                    {
                                        bnTriggerExec2.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[1] != null) m_MyCamera[1].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger2.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换2" + myjob2.index);
                        }
                    }
                    if (manager1.JobCount > 2)
                    {
                        try
                        {
                            if (myjob3.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger3.Enabled = false;
                                bnTriggerExec3.Enabled = false;
                            }
                            else if (myjob3.triggerMode == "触发拍照" || myjob3.triggerMode == "通讯触发")
                            {
                                myjob3.trrigerEn = true;
                                if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger3.Checked || myjob3.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing3)
                                    {
                                        bnTriggerExec3.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[2] != null) m_MyCamera[2].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger3.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换3" + myjob3.index);
                        }
                    }
                    if (manager1.JobCount > 3)
                    {
                        try
                        {
                            if (myjob4.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger4.Enabled = false;
                                bnTriggerExec4.Enabled = false;
                            }
                            else if (myjob4.triggerMode == "触发拍照" || myjob4.triggerMode == "通讯触发")
                            {
                                myjob4.trrigerEn = true;
                                if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger4.Checked || myjob4.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing4)
                                    {
                                        bnTriggerExec4.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[3] != null) m_MyCamera[3].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger4.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换4" + myjob4.index);
                        }
                    }
                    if (manager1.JobCount > 4)
                    {
                        try
                        {
                            if (myjob5.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger5.Enabled = false;
                                bnTriggerExec5.Enabled = false;
                            }
                            else if (myjob5.triggerMode == "触发拍照" || myjob5.triggerMode == "通讯触发")
                            {
                                myjob5.trrigerEn = true;
                                if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger5.Checked || myjob5.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing5)
                                    {
                                        bnTriggerExec5.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger5.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换5" + myjob5.index);
                        }
                    }
                    if (manager1.JobCount > 5)
                    {
                        try
                        {
                            if (myjob6.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger6.Enabled = false;
                                bnTriggerExec6.Enabled = false;
                            }
                            else if (myjob6.triggerMode == "触发拍照" || myjob6.triggerMode == "通讯触发")
                            {
                                myjob6.trrigerEn = true;
                                if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger6.Checked || myjob6.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing6)
                                    {
                                        bnTriggerExec6.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger6.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换6" + myjob6.index);
                        }
                    }
                    if (manager1.JobCount > 6)
                    {
                        try
                        {
                            if (myjob7.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger7.Enabled = false;
                                bnTriggerExec7.Enabled = false;
                            }
                            else if (myjob7.triggerMode == "触发拍照" || myjob7.triggerMode == "通讯触发")
                            {
                                myjob7.trrigerEn = true;
                                if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger7.Checked || myjob7.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing7)
                                    {
                                        bnTriggerExec7.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger7.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换7" + myjob7.index);
                        }
                    }
                    if (manager1.JobCount > 7)
                    {
                        try
                        {
                            if (myjob8.triggerMode == "连续运行")
                            {
                                if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                                cbSoftTrigger8.Enabled = false;
                                bnTriggerExec8.Enabled = false;
                            }
                            else if (myjob8.triggerMode == "触发拍照" || myjob8.triggerMode == "通讯触发")
                            {
                                myjob8.trrigerEn = true;
                                if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                                // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                                //           1 - Line1;
                                //           2 - Line2;
                                //           3 - Line3;
                                //           4 - Counter;
                                //           7 - Software;
                                if (cbSoftTrigger8.Checked || myjob8.triggerMode == "通讯触发")
                                {
                                    if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                                    if (m_bGrabbing8)
                                    {
                                        bnTriggerExec8.Enabled = true;
                                    }
                                }
                                else
                                {
                                    if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                                }
                                cbSoftTrigger8.Enabled = true;

                            }
                        }
                        catch (Exception ex)
                        {
                            MsgErroeLog.WriteLog(ex.Message + "触发切换8" + myjob8.index);
                        }
                    }
                }
                catch
                {
                    MsgErroeLog.WriteLog("无流程5");
                }
            }
            // ch:P0-3 triggerZifu 读取入各 job blockLock（原实现锁外读 Inputs，与检测线程 Run 并发）；每相机独立 try
            Myjob[] zifuJobs = new Myjob[] { myjob1, myjob2, myjob3, myjob4, myjob5, myjob6, myjob7, myjob8 };
            for (int t = 0; t < 8; t++)
            {
                try
                {
                    Myjob j = zifuJobs[t];
                    if (j.triggerMode == "通讯触发")
                    {
                        lock (j.blockLock)
                        {
                            if (j.block != null && j.block.Inputs.Contains("triggerZifu"))
                                j.triggerZifu = j.block.Inputs["triggerZifu"].Value.ToString();
                        }
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
            }
            finally { UnlockAllCameras(); }
        }
        private void cuntu_fangfa(CogImageFileBMP cogbmp, int FileLength, int JobNumber, string Jobpath, string cuowuma, string temptime, ICogImage cogimage)
        {
            try
            {
                string[] time111 = temptime.Split(':');
                int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                if (FileLength < zhangshu || cuntu == 1)
                {
                    // myjob3.number = 1;
                    // ch:同一相机相邻帧的存图任务并发（Task.Run 不保序）会争用同一个 CogImageFileBMP，加锁串行化
                    lock (cogbmp)
                    {
                        cogbmp.Open(Jobpath + day1 + "\\" + cuowuma + ttt1 + "#" + JobNumber + ".bmp", CogImageFileModeConstants.Write);
                        cogbmp.Append(cogimage);
                        cogbmp.Close();
                    }
                }
                else
                {

                    double dt2 = 0;
                    double dt3 = 0;
                    string ffff = "f";
                    DateTime dt1;
                    foreach (string f in Directory.GetFileSystemEntries(Jobpath + day1))
                    {
                        if (File.Exists(f))
                        {
                            dt1 = Directory.GetCreationTime(f);
                            dt2 = DateTime.Now.Subtract(dt1).TotalMinutes;
                            if (dt2 > dt3)
                            {
                                dt3 = dt2;
                                ffff = f;
                            }
                        }
                    }
                    //如果有子文件删除文件
                    if (ffff != "f")
                        File.Delete(ffff);
                    // ch:同一相机相邻帧的存图任务并发（Task.Run 不保序）会争用同一个 CogImageFileBMP，加锁串行化
                    lock (cogbmp)
                    {
                        cogbmp.Open(Jobpath + day1 + "\\" + cuowuma + ttt1 + "#" + JobNumber + ".bmp", CogImageFileModeConstants.Write);
                        cogbmp.Append(cogimage);
                        cogbmp.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog("存图方法" + ex.Message);
            }
        }
        private void chatu_fangfa(int jobhao, Myjob myjob, string list_time)
        {
            Task.Run(() =>
            {
                try
                {
                    string[] time111 = list_time.Split(':');
                    int ttt1 = int.Parse(time111[0] + time111[1] + time111[2]);
                    string ttt2 = time111[3];
                    int ttt3 = int.Parse(time111[4]);
                    Process myProc = null;
                    myProc = Process.Start(myjob.pathhead_ng + day1 + "\\" + ttt2 + ttt1 + "#" + ttt3 + ".bmp");//开启一个进程
                    try
                    {
                        myProc.Kill();//关闭一个进程
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog(ex.Message + "图片显示" + jobhao); };
            });
        }
        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {

        }
        private void jiankong_Huamian(Myjob myjob)
        {
            foreach (Control item in this.panel1.Controls)
            {
                if (item is Form)
                {
                    ((Form)item).Close();
                }
            }
            Form8 frm8 = new Form8(myjob.block);
            frm8.TopLevel = false;
            frm8.FormBorderStyle = FormBorderStyle.None;
            frm8.Parent = this.panel1;
            frm8.Dock = DockStyle.Fill;
            frm8.Show();
            this.Invoke(new Action(() =>
            {
                groupBox1.Visible = false;
                groupBox5.Visible = false;
                groupBox7.Visible = false;
                groupBox8.Visible = false;
                groupBox18.Visible = false;
                groupBox19.Visible = false;
                groupBox21.Visible = false;
                groupBox22.Visible = false;
            }));
        }
        private void daoqi_jiankong()
        {
            int dayt = 0;
            int dayz = 0;
            string zhongjian = "22";

            string code = canshuIni.ReadString("code1", "code2", "");
            if (code == "")
            {
                Thread.Sleep(20);
                code = canshuIni.ReadString("code1", "code2", "");
                if (code == "")
                {
                    MsgErroeLog.WriteLog("未读到");
                    MessageBox.Show("未读到");
                }
            }
            try
            {
                duini.ReadINIFile("C:\\Program Files\\test.ini");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + ":1488");
            }
            string code1 = duini.ReadString("1", "2", "");
            string code2 = duini.ReadString("1", "3", "");
            string code3 = duini.ReadString("1", "4", "");
            Thread.Sleep(5);
            try
            {
                if (int.Parse(code1) - int.Parse(code3) <= 1)
                {
                    daoqi = "软件剩余时间1天，请联系厂家!";
                }
                else
                    daoqi = "";
            }
            catch (Exception ex)
            { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            if (code != "")
            {
                try
                {
                    try
                    {
                        dayt = int.Parse(code.Substring(int.Parse(code.Substring(code.Length - 2)), 5)) - ((int)DateTime.Now.ToOADate());
                    }
                    catch
                    {
                        try
                        {
                            code = canshuIni.ReadString("code1", "code2", "");
                            dayt = int.Parse(code.Substring(int.Parse(code.Substring(code.Length - 2)), 5)) - ((int)DateTime.Now.ToOADate());
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message + ":1501:" + code);
                        }
                    }
                    if (dayt >= 0)
                    {
                        zhongjian = code.Substring(int.Parse(code.Substring(code.Length - 2)), 5);
                    }
                    else
                        zhongjian = DateTime.Now.ToOADate().ToString();
                    if (int.Parse(code1) > ((int)DateTime.Now.ToOADate()) && int.Parse(code2) <= ((int)DateTime.Now.ToOADate()) && int.Parse(code3) <= ((int)DateTime.Now.ToOADate()))
                    {
                        dayz = 1;
                    }
                    else
                        dayz = 0;
                    if (int.Parse(code3) <= ((int)DateTime.Now.ToOADate()))
                        duini.WriteString("1", "4", ((int)DateTime.Now.ToOADate()).ToString());
                }

                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + ":10056:" + code1);
                }
            }
            try
            {
                day2 = GetCPUSerialnumber(zhongjian);
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + ":对比");
            };
            if (dayz == 0)
            {
                // ch:授权到期界面更新（timer2 的 Task 线程调用，转 UI 线程执行）
                Action uiAction = () =>
                {
                    checkedListBox1.SetItemChecked(0, false);
                    checkedListBox1.SetItemChecked(1, false);
                    checkedListBox1.SetItemChecked(2, false);
                    checkedListBox1.SetItemChecked(3, false);
                    button2.Enabled = false;
                    button1.Enabled = false;
                    label12.Text = "加密中";
                    button5.Visible = true;
                    textBox4.Visible = true;
                    pictureBox1.Visible = true;
                    tableLayoutPanel1.Visible = false;
                    getCode();
                };
                if (this.InvokeRequired)
                    this.BeginInvoke(uiAction);
                else
                    uiAction();
            }
        }
        private void 相机1ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob1);
        }

        private void 相机2ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob2);
        }

        private void 相机3ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob3);
        }

        private void 相机4ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob4);
        }

        private void label74_Click(object sender, EventArgs e)
        {

        }

        private void 关闭监控ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (Control item in this.panel1.Controls)
            {
                if (item is Form)
                {
                    ((Form)item).Close();
                }
            }
            this.Invoke(new Action(() =>
            {
                groupBox1.Visible = true;
                groupBox5.Visible = true;
                groupBox7.Visible = true;
                groupBox8.Visible = true;
                groupBox18.Visible = true;
                groupBox19.Visible = true;
                groupBox21.Visible = true;
                groupBox22.Visible = true;
            }));
        }

        private void listBox1_MouseDown_1(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(1, myjob1, listBox1.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox3_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(2, myjob2, listBox3.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox7_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(3, myjob3, listBox7.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox6_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(4, myjob4, listBox6.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void numericUpDown4_ValueChanged(object sender, EventArgs e)
        {
            zhangshu = numericUpDown4.Value;
        }

        private void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox3.CheckState == CheckState.Checked)
                myjob1.shijianEn = true;
            else
                myjob1.shijianEn = false;
        }

        private void comboBox22_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox22.SelectedIndex == 0)
            {
                tongji = 0;
            }
            else
            {
                tongji = 1;
            }
        }

        private void button20_Click(object sender, EventArgs e)
        {
            if (button20.Text == "顶" && listBox10.Visible == true)
            {
                if (yunxing == false && listBox10.Items.Count > 0)
                {
                    listBox10.SelectedIndex = 0;
                    button20.Text = "底";
                }
            }
            else if (button20.Text == "底" && listBox10.Visible == true)
            {
                if (yunxing == false && listBox10.Items.Count > 0)
                {
                    listBox10.SelectedIndex = listBox10.Items.Count - 1;
                    button20.Text = "顶";
                }
            }
        }

        private void button44_Click(object sender, EventArgs e)
        {
            if (button44.Text == "顶" && listBox11.Visible == true)
            {
                if (yunxing == false && listBox11.Items.Count > 0)
                {
                    listBox11.SelectedIndex = 0;
                    button44.Text = "底";
                }
            }
            else if (button44.Text == "底" && listBox11.Visible == true)
            {
                if (yunxing == false && listBox11.Items.Count > 0)
                {
                    listBox11.SelectedIndex = listBox11.Items.Count - 1;
                    button44.Text = "顶";
                }
            }
        }

        private void button57_Click(object sender, EventArgs e)
        {
            if (button57.Text == "顶" && listBox12.Visible == true)
            {
                if (yunxing == false && listBox12.Items.Count > 0)
                {
                    listBox12.SelectedIndex = 0;
                    button57.Text = "底";
                }
            }
            else if (button57.Text == "底" && listBox12.Visible == true)
            {
                if (yunxing == false && listBox12.Items.Count > 0)
                {
                    listBox12.SelectedIndex = listBox12.Items.Count - 1;
                    button57.Text = "顶";
                }
            }
        }

        private void button70_Click(object sender, EventArgs e)
        {
            if (button70.Text == "顶" && listBox13.Visible == true)
            {
                if (yunxing == false && listBox13.Items.Count > 0)
                {
                    listBox13.SelectedIndex = 0;
                    button70.Text = "底";
                }
            }
            else if (button70.Text == "底" && listBox13.Visible == true)
            {
                if (yunxing == false && listBox13.Items.Count > 0)
                {
                    listBox13.SelectedIndex = listBox13.Items.Count - 1;
                    button70.Text = "顶";
                }
            }
        }

        private void button40_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox22, listBox10);
        }

        private void button53_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox28, listBox11);
        }

        private void button66_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox34, listBox12);
        }

        private void button79_Click(object sender, EventArgs e)
        {
            ShowPictureList(textBox40, listBox13);
        }

        private void timer13_Tick(object sender, EventArgs e)
        {
            if (myjob5.trriger == 0)
            {
                if (trriger5_temp == 1)
                {
                    int count = listBox10.Items.Count;
                    int select = listBox10.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox10.SelectedIndex = select + 1;
                        }
                        else
                            listBox10.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void timer14_Tick(object sender, EventArgs e)
        {
            if (myjob6.trriger == 0)
            {
                if (trriger6_temp == 1)
                {
                    int count = listBox11.Items.Count;
                    int select = listBox11.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox11.SelectedIndex = select + 1;
                        }
                        else
                            listBox11.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void timer15_Tick(object sender, EventArgs e)
        {
            if (myjob7.trriger == 0)
            {
                if (trriger7_temp == 1)
                {
                    int count = listBox12.Items.Count;
                    int select = listBox12.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox12.SelectedIndex = select + 1;
                        }
                        else
                            listBox12.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void timer16_Tick(object sender, EventArgs e)
        {
            if (myjob8.trriger == 0)
            {
                if (trriger8_temp == 1)
                {
                    int count = listBox13.Items.Count;
                    int select = listBox13.SelectedIndex;
                    this.Invoke(new Action(() =>
                    {
                        if (select < count - 1)
                        {
                            listBox13.SelectedIndex = select + 1;
                        }
                        else
                            listBox13.SelectedIndex = 0;
                    }));
                }

            }
        }

        private void button41_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox10.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob5.img = new Bitmap(item.filePath);
                    if (myjob5.trriger == 0)
                    {
                        myjob5.trriger = 1;
                        getrecord(myjob5);
                    }
                    _ioPulseEnabled = true;
                    trriger5_temp = 1;
                    timer13.Interval = int.Parse(textBox21.Text);
                    timer13.Enabled = true;
                }
            }
        }

        private void button54_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox11.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob6.img = new Bitmap(item.filePath);
                    if (myjob6.trriger == 0)
                    {
                        myjob6.trriger = 1;
                        getrecord(myjob6);
                    }
                    _ioPulseEnabled = true;
                    trriger6_temp = 1;
                    timer14.Interval = int.Parse(textBox27.Text);
                    timer14.Enabled = true;
                }
            }
        }

        private void button67_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox12.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob7.img = new Bitmap(item.filePath);
                    if (myjob7.trriger == 0)
                    {
                        myjob7.trriger = 1;
                        getrecord(myjob7);
                    }
                    _ioPulseEnabled = true;
                    trriger7_temp = 1;
                    timer15.Interval = int.Parse(textBox33.Text);
                    timer15.Enabled = true;
                }
            }
        }

        private void button80_Click(object sender, EventArgs e)
        {
            if (yunxing == false)
            {
                PictureListItem item = (PictureListItem)listBox13.SelectedItem;
                if (item == null)
                {
                    MessageBox.Show("请选择图片");
                    return;
                }
                else
                {
                    myjob8.img = new Bitmap(item.filePath);
                    if (myjob8.trriger == 0)
                    {
                        myjob8.trriger = 1;
                        getrecord(myjob8);
                    }
                    _ioPulseEnabled = true;
                    trriger8_temp = 1;
                    timer16.Interval = int.Parse(textBox39.Text);
                    timer16.Enabled = true;
                }
            }
        }

        private void button36_Click(object sender, EventArgs e)
        {
            trriger5_temp = 0;
            timer13.Enabled = false;
            StopAllIoPulses();
        }

        private void button52_Click(object sender, EventArgs e)
        {
            trriger6_temp = 0;
            timer14.Enabled = false;
            StopAllIoPulses();
        }

        private void button65_Click(object sender, EventArgs e)
        {
            trriger7_temp = 0;
            timer15.Enabled = false;
            StopAllIoPulses();
        }

        private void button78_Click(object sender, EventArgs e)
        {
            trriger8_temp = 0;
            timer16.Enabled = false;
            StopAllIoPulses();
        }

        private void comboBox25_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob5.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox25.Text.Contains("连续运行"))
                    {
                        if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger5.Enabled = false;
                        bnTriggerExec5.Enabled = false;
                    }
                    else if (comboBox25.Text.Contains("触发拍照") || comboBox25.Text.Contains("通讯触发"))
                    {

                        if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger5.Checked || comboBox25.Text.Contains("通讯触发"))
                        {
                            if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing5)
                            {
                                bnTriggerExec5.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger5.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换5");
                }
                myjob5.triggerMode = comboBox25.Text;
            }
        }

        private void comboBox28_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob6.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox28.Text.Contains("连续运行"))
                    {
                        if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger6.Enabled = false;
                        bnTriggerExec6.Enabled = false;
                    }
                    else if (comboBox28.Text.Contains("触发拍照") || comboBox28.Text.Contains("通讯触发"))
                    {

                        if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger6.Checked || comboBox28.Text.Contains("通讯触发"))
                        {
                            if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing6)
                            {
                                bnTriggerExec6.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger6.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换6");
                }
                myjob6.triggerMode = comboBox28.Text;
            }
        }

        private void comboBox31_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob7.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox31.Text.Contains("连续运行"))
                    {
                        if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger7.Enabled = false;
                        bnTriggerExec7.Enabled = false;
                    }
                    else if (comboBox31.Text.Contains("触发拍照") || comboBox31.Text.Contains("通讯触发"))
                    {

                        if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger7.Checked || comboBox31.Text.Contains("通讯触发"))
                        {
                            if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing7)
                            {
                                bnTriggerExec7.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger7.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换7");
                }
                myjob7.triggerMode = comboBox31.Text;
            }
        }

        private void comboBox34_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (frm5.mark == 1 || myjob8.state.Contains("相") || (sender == null && e == null)) // ch:P1-8 重连恢复（sender==null）绕过登录门
            {
                try
                {
                    if (comboBox34.Text.Contains("连续运行"))
                    {
                        if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                        cbSoftTrigger8.Enabled = false;
                        bnTriggerExec8.Enabled = false;
                    }
                    else if (comboBox34.Text.Contains("触发拍照") || comboBox34.Text.Contains("通讯触发"))
                    {

                        if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                        // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                        //           1 - Line1;
                        //           2 - Line2;
                        //           3 - Line3;
                        //           4 - Counter;
                        //           7 - Software;
                        if (cbSoftTrigger8.Checked || comboBox34.Text.Contains("通讯触发"))
                        {
                            if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                            if (m_bGrabbing8)
                            {
                                bnTriggerExec8.Enabled = true;
                            }
                        }
                        else
                        {
                            if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                        }
                        cbSoftTrigger8.Enabled = true;

                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message + "触发切换8");
                }
                myjob8.triggerMode = comboBox34.Text;
            }
        }

        private void listBox10_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox10.SelectedItem;
                if (item == null) return;
                myjob5.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    _ioPulseEnabled = true;
                    myjob5.trriger = 1;
                    getrecord(myjob5);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox11_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox11.SelectedItem;
                if (item == null) return;
                myjob6.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob6.trriger == 0)
                    {
                        myjob6.trriger = 1;
                        getrecord(myjob6);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox12_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox12.SelectedItem;
                if (item == null) return;
                myjob7.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob7.trriger == 0)
                    {
                        myjob7.trriger = 1;
                        getrecord(myjob7);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox13_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                PictureListItem item = (PictureListItem)listBox13.SelectedItem;
                if (item == null) return;
                myjob8.img = new Bitmap(item.filePath);
                if (yunxing == false)
                {
                    if (myjob8.trriger == 0)
                    {
                        myjob8.trriger = 1;
                        getrecord(myjob8);
                    }
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox32_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox32.CheckState == CheckState.Checked)
                {
                    myjob5.modbustcp = true;
                }
                else
                {

                    myjob5.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox38_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox38.CheckState == CheckState.Checked)
                {
                    myjob6.modbustcp = true;
                }
                else
                {

                    myjob6.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox44_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox44.CheckState == CheckState.Checked)
                {
                    myjob7.modbustcp = true;
                }
                else
                {

                    myjob7.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox50_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox50.CheckState == CheckState.Checked)
                {
                    myjob8.modbustcp = true;
                }
                else
                {

                    myjob8.modbustcp = false;
                }

            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnGetLineSel5_Click(object sender, EventArgs e)
        {
            get_Selector(4, cbLineSel5);
        }

        private void bnGetLineSel6_Click(object sender, EventArgs e)
        {
            get_Selector(5, cbLineSel6);
        }

        private void bnGetLineSel7_Click(object sender, EventArgs e)
        {
            get_Selector(6, cbLineSel7);
        }

        private void bnGetLineSel8_Click(object sender, EventArgs e)
        {
            get_Selector(7, cbLineSel8);
        }

        private void bnSetLineSel5_Click(object sender, EventArgs e)
        {
            set_Selector(4, cbLineSel5);
        }

        private void bnSetLineSel6_Click(object sender, EventArgs e)
        {
            set_Selector(5, cbLineSel6);
        }

        private void bnSetLineSel7_Click(object sender, EventArgs e)
        {
            set_Selector(6, cbLineSel7);
        }

        private void bnSetLineSel8_Click(object sender, EventArgs e)
        {
            set_Selector(7, cbLineSel8);
        }

        private void bnGetLineMode5_Click(object sender, EventArgs e)
        {
            get_Mode(4, cbLineMode5);
        }

        private void bnGetLineMode6_Click(object sender, EventArgs e)
        {
            get_Mode(5, cbLineMode6);
        }

        private void bnGetLineMode7_Click(object sender, EventArgs e)
        {
            get_Mode(6, cbLineMode7);
        }

        private void bnGetLineMode8_Click(object sender, EventArgs e)
        {
            get_Mode(7, cbLineMode8);
        }

        private void bnSetLineMode5_Click(object sender, EventArgs e)
        {
            set_Mode(4, cbLineMode5);
        }

        private void bnSetLineMode6_Click(object sender, EventArgs e)
        {
            set_Mode(5, cbLineMode6);
        }

        private void bnSetLineMode7_Click(object sender, EventArgs e)
        {
            set_Mode(6, cbLineMode7);
        }

        private void bnSetLineMode8_Click(object sender, EventArgs e)
        {
            set_Mode(7, cbLineMode8);
        }

        private void checkBox34_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(4, cbLineMode5, checkBox34);
        }

        private void checkBox40_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(5, cbLineMode6, checkBox40);
        }

        private void checkBox46_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(6, cbLineMode7, checkBox46);
        }

        private void checkBox52_CheckedChanged(object sender, EventArgs e)
        {
            output_inverse(7, cbLineMode8, checkBox52);
        }

        private void bnGetParam5_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[4] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[4].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure5.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[4].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain5.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[4].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate5.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab5.Enabled = false;
            }
        }

        private void bnGetParam6_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[5] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[5].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure6.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[5].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain6.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[5].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate6.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab6.Enabled = false;
            }
        }

        private void bnGetParam7_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[6] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[6].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure7.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[6].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain7.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[6].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate7.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab7.Enabled = false;
            }
        }

        private void bnGetParam8_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[7] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = m_MyCamera[7].MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbExposure8.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[7].MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbGain8.Text = stParam.fCurValue.ToString("F1");
                }

                nRet = m_MyCamera[7].MV_CC_GetFloatValue_NET("ResultingFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    tbFrameRate8.Text = stParam.fCurValue.ToString("F1");
                }
            }
            catch
            {
                bnStartGrab4.Enabled = false;
            }
        }

        private void bnSetParam5_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[4] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure5.Text);
                float.Parse(tbGain5.Text);
                float.Parse(tbFrameRate5.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[4].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure5.Text);
                if (myjob5.baoguang != 0 && myjob5.block != null && myjob5.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob5, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval5 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[4].MV_CC_GetFloatValue_NET("ExposureTime", ref fval5) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval5.fMin) baoguang_temp = fval5.fMin;
                    if (baoguang_temp > fval5.fMax) baoguang_temp = fval5.fMax;
                }
                int nRet = m_MyCamera[4].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[4].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[4].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain5.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[4].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate5.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnSetParam6_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[5] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure6.Text);
                float.Parse(tbGain6.Text);
                float.Parse(tbFrameRate6.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[5].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure6.Text);
                if (myjob6.baoguang != 0 && myjob6.block != null && myjob6.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob6, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval6 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[5].MV_CC_GetFloatValue_NET("ExposureTime", ref fval6) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval6.fMin) baoguang_temp = fval6.fMin;
                    if (baoguang_temp > fval6.fMax) baoguang_temp = fval6.fMax;
                }
                int nRet = m_MyCamera[5].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[5].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[5].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain6.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[5].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate6.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnSetParam7_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[6] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure7.Text);
                float.Parse(tbGain7.Text);
                float.Parse(tbFrameRate7.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[6].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure7.Text);
                if (myjob7.baoguang != 0 && myjob7.block != null && myjob7.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob7, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval7 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[6].MV_CC_GetFloatValue_NET("ExposureTime", ref fval7) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval7.fMin) baoguang_temp = fval7.fMin;
                    if (baoguang_temp > fval7.fMax) baoguang_temp = fval7.fMax;
                }
                int nRet = m_MyCamera[6].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[6].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[6].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain7.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[6].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate7.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnSetParam8_Click(object sender, EventArgs e)
        {
            if (m_MyCamera[7] == null) return; // ch:相机未打开时跳过参数读写
            try
            {
                float.Parse(tbExposure8.Text);
                float.Parse(tbGain8.Text);
                float.Parse(tbFrameRate8.Text);
            }
            catch
            {
                //ShowErrorMsg("Please enter correct type!", 0);
                return;
            }
            try
            {
                m_MyCamera[7].MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                float baoguang_temp = float.Parse(tbExposure8.Text);
                if (myjob8.baoguang != 0 && myjob8.block != null && myjob8.block.Inputs.Contains("baoguang"))
                {
                    SetBlockInputSafe(myjob8, "baoguang", baoguang_temp);
                }
                // ch:读取相机曝光范围并限制，避免配置值超出相机允许范围导致 Set 失败（0x80000102）
                MyCamera.MVCC_FLOATVALUE fval8 = new MyCamera.MVCC_FLOATVALUE();
                if (m_MyCamera[7].MV_CC_GetFloatValue_NET("ExposureTime", ref fval8) == MyCamera.MV_OK)
                {
                    if (baoguang_temp < fval8.fMin) baoguang_temp = fval8.fMin;
                    if (baoguang_temp > fval8.fMax) baoguang_temp = fval8.fMax;
                }
                int nRet = m_MyCamera[7].MV_CC_SetFloatValue_NET("ExposureTime", baoguang_temp);
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Exposure Time Fail!" + nRet);
                    //ShowErrorMsg("Set Exposure Time Fail!", nRet);
                }

                m_MyCamera[7].MV_CC_SetEnumValue_NET("GainAuto", 0);
                nRet = m_MyCamera[7].MV_CC_SetFloatValue_NET("Gain", float.Parse(tbGain8.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Gain Fail!" + nRet);
                    // ShowErrorMsg("Set Gain Fail!", nRet);
                }

                nRet = m_MyCamera[7].MV_CC_SetFloatValue_NET("AcquisitionFrameRate", float.Parse(tbFrameRate8.Text));
                if (nRet != MyCamera.MV_OK)
                {
                    MsgErroeLog.WriteLog("Set Frame Rate Fail!" + nRet);
                    // ShowErrorMsg("Set Frame Rate Fail!", nRet);
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnStartGrab5_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing5) return; // ch:防重入
            if (!CanStartGrab(4)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing5 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[4].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[4].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[4].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing5 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab5.Enabled = false;
                bnStopGrab5.Enabled = true;
            }
            catch
            {
                m_bGrabbing5 = false;
                MsgErroeLog.WriteLog("相机5开始采集");
            }
        }

        private void bnStartGrab6_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing6) return; // ch:防重入
            if (!CanStartGrab(5)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing6 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[5].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[5].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[5].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing6 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab6.Enabled = false;
                bnStopGrab6.Enabled = true;
            }
            catch
            {
                m_bGrabbing6 = false;
                MsgErroeLog.WriteLog("相机6开始采集");
            }
        }

        private void bnStartGrab7_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing7) return; // ch:防重入
            if (!CanStartGrab(6)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing7 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[6].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[6].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[6].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing7 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab7.Enabled = false;
                bnStopGrab7.Enabled = true;
            }
            catch
            {
                m_bGrabbing7 = false;
                MsgErroeLog.WriteLog("相机7开始采集");
            }
        }

        private void bnStartGrab8_Click(object sender, EventArgs e)
        {
            if (m_bGrabbing8) return; // ch:防重入
            if (!CanStartGrab(7)) return; // ch:取流前检查（参考正常版本）
            try
            {
                m_bGrabbing8 = true;
                //if (myjob1.yun == 0)
                //{
                //    m_hReceiveThread = new Thread(ReceiveThreadProcess);
                //    m_hReceiveThread.Start();
                //}
                m_stFrameInfo[7].nFrameLen = 0;//取流之前先清除帧长度
                m_stFrameInfo[7].enPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Undefined;
                // ch:开始采集 | en:Start Grabbing
                int nRet = m_MyCamera[7].MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    m_bGrabbing8 = false;
                    //  m_hReceiveThread.Join();
                    MsgErroeLog.WriteLog("Start Grabbing Fail!" + nRet);
                    return;
                }
                bnStartGrab8.Enabled = false;
                bnStopGrab8.Enabled = true;
            }
            catch
            {
                m_bGrabbing8 = false;
                MsgErroeLog.WriteLog("相机8开始采集");
            }
        }

        private void bnStopGrab5_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing5 == true && m_MyCamera[4] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing5 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[4].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab5.Enabled = true;
                bnStopGrab5.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机5停止采集"); }
        }

        private void bnStopGrab6_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing6 == true && m_MyCamera[5] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing6 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[5].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab6.Enabled = true;
                bnStopGrab6.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机6停止采集"); }
        }

        private void bnStopGrab7_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing7 == true && m_MyCamera[6] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing7 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[6].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab7.Enabled = true;
                bnStopGrab7.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机7停止采集"); }
        }

        private void bnStopGrab8_Click(object sender, EventArgs e)
        {
            try
            {
                if (m_bGrabbing8 == true && m_MyCamera[7] != null)
                {
                    // ch:标志位设为false | en:Set flag bit false
                    m_bGrabbing8 = false;
                    // ch:停止采集 | en:Stop Grabbing
                    int nRet = m_MyCamera[7].MV_CC_StopGrabbing_NET();
                    if (nRet != MyCamera.MV_OK)
                    {
                        MsgErroeLog.WriteLog("停止采集失败:" + nRet);
                    }
                }
                bnStartGrab8.Enabled = true;
                bnStopGrab8.Enabled = false;
            }
            catch
            { MsgErroeLog.WriteLog("相机8停止采集"); }
        }

        private void cbSoftTrigger5_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger5.Checked || comboBox25.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing5)
                    {
                        bnTriggerExec5.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[4] != null) m_MyCamera[4].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec5.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void cbSoftTrigger6_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger6.Checked || comboBox28.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing6)
                    {
                        bnTriggerExec6.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[5] != null) m_MyCamera[5].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec6.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void cbSoftTrigger7_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger7.Checked || comboBox31.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing7)
                    {
                        bnTriggerExec7.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[6] != null) m_MyCamera[6].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec7.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void cbSoftTrigger8_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbSoftTrigger8.Checked || comboBox34.Text.Contains("通讯触发"))
                {
                    // ch:触发源设为软触发 | en:Set trigger source as Software
                    if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (m_bGrabbing8)
                    {
                        bnTriggerExec8.Enabled = true;
                    }
                }
                else
                {
                    if (m_MyCamera[7] != null) m_MyCamera[7].MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    bnTriggerExec8.Enabled = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void bnTriggerExec5_Click(object sender, EventArgs e)
        {
            TriggerCamera(4);
        }

        private void bnTriggerExec6_Click(object sender, EventArgs e)
        {
            TriggerCamera(5);
        }

        private void bnTriggerExec7_Click(object sender, EventArgs e)
        {
            TriggerCamera(6);
        }

        private void bnTriggerExec8_Click(object sender, EventArgs e)
        {
            TriggerCamera(7);
        }

        private void button30_Click(object sender, EventArgs e)
        {
            if (button30.Text == "5使用中")
            {
                myjob5.en = 0;
                button29.Visible = false;
                button28.Visible = false;
                button21.Visible = false;
                button30.Text = "5屏蔽中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob5.en = 1;
                button30.Text = "5使用中";
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button29.Visible = true;
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button29_Click(object sender, EventArgs e)
        {
            if (button29.Text == "6使用中")
            {
                myjob6.en = 0;
                button28.Visible = false;
                button21.Visible = false;
                button29.Text = "6屏蔽中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob6.en = 1;
                button29.Text = "6使用中";
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
                button28.Visible = true;
                button21.Visible = true;
            }
        }

        private void button28_Click(object sender, EventArgs e)
        {
            if (button28.Text == "7使用中")
            {
                myjob7.en = 0;
                button21.Visible = false;
                button28.Text = "7屏蔽中";
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob7.en = 1;
                button28.Text = "7使用中";
                button21.Text = "8屏蔽中";
                button21.Visible = true;
            }
        }

        private void button21_Click(object sender, EventArgs e)
        {
            if (button21.Text == "8使用中")
            {
                myjob8.en = 0;
                button21.Text = "8屏蔽中";
            }
            else
            {
                myjob8.en = 1;
                button21.Text = "8使用中";
            }
        }

        private void 相机5ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob5);
        }

        private void 相机6ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob6);
        }

        private void 相机7ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob7);
        }

        private void 相机8ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            jiankong_Huamian(myjob8);
        }

        private void cogRecordDisplay9_Enter(object sender, EventArgs e)
        {

        }

        private void listBox14_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(5, myjob5, listBox14.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox15_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(6, myjob6, listBox15.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox18_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(7, myjob7, listBox18.SelectedItem.ToString());
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void listBox17_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                chatu_fangfa(8, myjob8, listBox17.SelectedItem.ToString());
            }
            catch (Exception ex)
            { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void button31_Click(object sender, EventArgs e)
        {
            if (button31.Text == "输出取反")
            {
                button31.Text = "输出取正";
                shuchuqufan = "Reject";
            }
            else
            {
                button31.Text = "输出取反";
                shuchuqufan = "Accept";
            }
        }

        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void textBox3_TextChanged(object sender, EventArgs e)
        {
            int temp_time = 0;
            temp_time = int.Parse(textBox3.Text.ToString().Trim());
            myjob1.timespace = temp_time;
            myjob2.timespace = temp_time;
            myjob3.timespace = temp_time;
            myjob4.timespace = temp_time;
            myjob5.timespace = temp_time;
            myjob6.timespace = temp_time;
            myjob7.timespace = temp_time;
            myjob8.timespace = temp_time;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox1.CheckState == CheckState.Checked)
            {
                myjob1.cunok = true;
                myjob2.cunok = true;
                myjob3.cunok = true;
                myjob4.cunok = true;
                myjob5.cunok = true;
                myjob6.cunok = true;
                myjob7.cunok = true;
                myjob8.cunok = true;
            }
            else
            {
                myjob1.cunok = false;
                myjob2.cunok = false;
                myjob3.cunok = false;
                myjob4.cunok = false;
                myjob5.cunok = false;
                myjob6.cunok = false;
                myjob7.cunok = false;
                myjob8.cunok = false;

            }
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox2.CheckState == CheckState.Checked)
            {
                myjob1.cunng = true;
                myjob2.cunng = true;
                myjob3.cunng = true;
                myjob4.cunng = true;
                myjob5.cunng = true;
                myjob6.cunng = true;
                myjob7.cunng = true;
                myjob8.cunng = true;
            }
            else
            {
                myjob1.cunng = false;
                myjob2.cunng = false;
                myjob3.cunng = false;
                myjob4.cunng = false;
                myjob5.cunng = false;
                myjob6.cunng = false;
                myjob7.cunng = false;
                myjob8.cunng = false;

            }
        }

        private void checkBox31_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox31.CheckState == CheckState.Checked)
                {
                    myjob5.serial = true;
                }
                else
                {

                    myjob5.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox27_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox27.CheckState == CheckState.Checked)
                {
                    myjob1.serial = true;
                }
                else
                {

                    myjob1.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox28_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox28.CheckState == CheckState.Checked)
                {
                    myjob2.serial = true;
                }
                else
                {

                    myjob2.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox29_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox29.CheckState == CheckState.Checked)
                {
                    myjob3.serial = true;
                }
                else
                {

                    myjob3.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox30_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox30.CheckState == CheckState.Checked)
                {
                    myjob4.serial = true;
                }
                else
                {

                    myjob4.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox37_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox37.CheckState == CheckState.Checked)
                {
                    myjob6.serial = true;
                }
                else
                {

                    myjob6.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox43_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox43.CheckState == CheckState.Checked)
                {
                    myjob7.serial = true;
                }
                else
                {

                    myjob7.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox49_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox49.CheckState == CheckState.Checked)
                {
                    myjob8.serial = true;
                }
                else
                {

                    myjob8.serial = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox5_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox5.CheckState == CheckState.Checked)
                {
                    myjob1.tcp = true;
                }
                else
                {

                    myjob1.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox9_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox9.CheckState == CheckState.Checked)
                {
                    myjob2.tcp = true;
                }
                else
                {

                    myjob2.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox15_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox15.CheckState == CheckState.Checked)
                {
                    myjob3.tcp = true;
                }
                else
                {

                    myjob3.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox19_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox19.CheckState == CheckState.Checked)
                {
                    myjob4.tcp = true;
                }
                else
                {

                    myjob4.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox33_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox33.CheckState == CheckState.Checked)
                {
                    myjob5.tcp = true;
                }
                else
                {

                    myjob5.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox39_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox39.CheckState == CheckState.Checked)
                {
                    myjob6.tcp = true;
                }
                else
                {

                    myjob6.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox45_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox45.CheckState == CheckState.Checked)
                {
                    myjob7.tcp = true;
                }
                else
                {

                    myjob7.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void checkBox51_CheckedChanged(object sender, EventArgs e)
        {
            try
            {

                if (checkBox51.CheckState == CheckState.Checked)
                {
                    myjob8.tcp = true;
                }
                else
                {

                    myjob8.tcp = false;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox2.Text.Contains("使能"))
            {
                myjob1.IO = true;
            }
            else
            {
                myjob1.IO = false;
            }
        }

        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox3.Text.Contains("使能"))
            {
                myjob1.xuanran = true;
            }
            else
            {
                myjob1.xuanran = false;
            }
        }

        private void comboBox6_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox6.Text.Contains("使能"))
            {
                myjob1.cuntu = true;
            }
            else
            {
                myjob1.cuntu = false;
            }
        }

        private void button32_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(textBox19.Text);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void textBox19_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "应用 File|*.application;*.exe*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox19.Text = openFileDialog.FileName;
            }
        }

        private void textBox19_TextChanged(object sender, EventArgs e)
        {

        }

        private void checkBox25_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox25.CheckState == CheckState.Checked)
            {
                datajilu = 1;

            }
            else
            {
                datajilu = 0;

            }
        }

        private void numericUpDown5_ValueChanged(object sender, EventArgs e)
        {
            myjob1.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob2.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob3.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob4.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob5.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob6.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob7.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
            myjob8.IOyanshi = int.Parse(numericUpDown5.Value.ToString());
        }

        private void dataGridView2_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }

        private void checkBox13_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox13.CheckState == CheckState.Checked)
                myjob2.shijianEn = true;
            else
                myjob2.shijianEn = false;
        }

        private void checkBox18_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox18.CheckState == CheckState.Checked)
                myjob3.shijianEn = true;
            else
                myjob3.shijianEn = false;
        }

        private void checkBox22_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox22.CheckState == CheckState.Checked)
                myjob4.shijianEn = true;
            else
                myjob4.shijianEn = false;
        }

        private void checkBox36_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox36.CheckState == CheckState.Checked)
                myjob5.shijianEn = true;
            else
                myjob5.shijianEn = false;
        }

        private void checkBox42_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox42.CheckState == CheckState.Checked)
                myjob6.shijianEn = true;
            else
                myjob6.shijianEn = false;
        }

        private void checkBox48_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox48.CheckState == CheckState.Checked)
                myjob7.shijianEn = true;
            else
                myjob7.shijianEn = false;
        }

        private void checkBox54_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox54.CheckState == CheckState.Checked)
                myjob8.shijianEn = true;
            else
                myjob8.shijianEn = false;
        }

        private void mesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (frm7.Visible == false)
                frm7.Visible = true;
            else
                frm7.Visible = false;
        }

        private void finsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (omron.Visible == false)
                omron.Visible = true;
            else
                omron.Visible = false;
        }

        private void 三菱Fx编程口ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (fx.Visible == false)
                fx.Visible = true;
            else
                fx.Visible = false;
        }

        private void comboBox7_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob1.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\1\\" + comboBox7.SelectedItem.ToString());
                    canshuIni.WriteString("camera1", "fen", comboBox7.SelectedItem.ToString());
                    MessageBox.Show("切换流程1:" + comboBox7.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob1);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程1:" + comboBox7.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox7_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox7, 1);
        }

        private void comboBox9_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox9, 2);
        }

        private void comboBox10_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox10, 3);
        }

        private void comboBox11_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox11, 4);
        }

        private void comboBox12_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox12, 5);
        }

        private void comboBox13_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox13, 6);
        }

        private void comboBox14_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox14, 7);
        }

        private void comboBox15_DropDown(object sender, EventArgs e)
        {
            Showjob(comboBox15, 8);
        }

        private void comboBox9_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob2.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\2\\" + comboBox9.SelectedItem.ToString());
                    canshuIni.WriteString("camera2", "fen", comboBox9.SelectedItem.ToString());
                    MessageBox.Show("切换流程2:" + comboBox9.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob2);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程2:" + comboBox9.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox10_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob3.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\3\\" + comboBox10.SelectedItem.ToString());
                    canshuIni.WriteString("camera3", "fen", comboBox10.SelectedItem.ToString());
                    MessageBox.Show("切换流程3:" + comboBox10.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob3);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程3:" + comboBox10.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox11_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob4.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\4\\" + comboBox11.SelectedItem.ToString());
                    canshuIni.WriteString("camera4", "fen", comboBox11.SelectedItem.ToString());
                    MessageBox.Show("切换流程4:" + comboBox11.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob4);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程4:" + comboBox11.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox12_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob5.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\5\\" + comboBox12.SelectedItem.ToString());
                    canshuIni.WriteString("camera5", "fen", comboBox12.SelectedItem.ToString());
                    MessageBox.Show("切换流程5:" + comboBox12.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob5);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程5:" + comboBox12.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox13_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob6.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\6\\" + comboBox13.SelectedItem.ToString());
                    canshuIni.WriteString("camera6", "fen", comboBox13.SelectedItem.ToString());
                    MessageBox.Show("切换流程6:" + comboBox13.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob6);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程6:" + comboBox13.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox14_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob7.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\7\\" + comboBox14.SelectedItem.ToString());
                    canshuIni.WriteString("camera7", "fen", comboBox14.SelectedItem.ToString());
                    MessageBox.Show("切换流程7:" + comboBox14.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob7);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程7:" + comboBox14.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void comboBox15_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!yunxing && Frm2.start == 1)
            {
                try
                {
                    myjob8.block = (CogToolBlock)CogSerializer.LoadObjectFromFile(wenjianjia + "\\8\\" + comboBox15.SelectedItem.ToString());
                    canshuIni.WriteString("camera8", "fen", comboBox15.SelectedItem.ToString());
                    MessageBox.Show("切换流程8:" + comboBox15.SelectedItem.ToString() + "成功");
                    sync_job_meta(myjob8);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                    MessageBox.Show("切换流程8:" + comboBox15.SelectedItem.ToString() + "失败");
                }
            }
        }

        private void tabPage10_Click(object sender, EventArgs e)
        {

        }

        private void comboBox16_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 0)
                {
                    textBox13.Text = myjob1.block.Inputs[comboBox16.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox16_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 0)
                {
                    comboBox16.Items.Clear();
                    for (int i = 0; i < myjob1.block.Inputs.Count; i++)
                    {
                        comboBox16.Items.Add(myjob1.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button35_Click(object sender, EventArgs e)
        {
            try {
                if (manager1.JobCount > 0)
                {
                    SetBlockInputSafe(myjob1, comboBox16.Text, textBox13.Text); // ch:P2 手参写入改走 blockLock 保护路径，避免与检测线程 Run 并发
                }
                MessageBox.Show("参数:" + comboBox16.Text + "写入数值" + textBox13.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox26_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 7)
                {
                    textBox35.Text = myjob8.block.Inputs[comboBox26.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox26_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 7)
                {
                    comboBox26.Items.Clear();
                    for (int i = 0; i < myjob8.block.Inputs.Count; i++)
                    {
                        comboBox26.Items.Add(myjob8.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button49_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 7)
                {
                    SetBlockInputSafe(myjob8, comboBox26.Text, textBox35.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox26.Text + "写入数值" + textBox35.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox17_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 1)
                {
                    textBox23.Text = myjob2.block.Inputs[comboBox17.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button42_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 1)
                {
                    SetBlockInputSafe(myjob2, comboBox17.Text, textBox23.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox17.Text + "写入数值" + textBox23.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox17_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 1)
                {
                    comboBox17.Items.Clear();
                    for (int i = 0; i < myjob2.block.Inputs.Count; i++)
                    {
                        comboBox17.Items.Add(myjob2.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox18_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 2)
                {
                    comboBox18.Items.Clear();
                    for (int i = 0; i < myjob3.block.Inputs.Count; i++)
                    {
                        comboBox18.Items.Add(myjob3.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox19_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 3)
                {
                    comboBox19.Items.Clear();
                    for (int i = 0; i < myjob4.block.Inputs.Count; i++)
                    {
                        comboBox19.Items.Add(myjob4.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox20_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 4)
                {
                    comboBox20.Items.Clear();
                    for (int i = 0; i < myjob5.block.Inputs.Count; i++)
                    {
                        comboBox20.Items.Add(myjob5.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox23_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 5)
                {
                    comboBox23.Items.Clear();
                    for (int i = 0; i < myjob6.block.Inputs.Count; i++)
                    {
                        comboBox23.Items.Add(myjob6.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox24_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 6)
                {
                    comboBox24.Items.Clear();
                    for (int i = 0; i < myjob7.block.Inputs.Count; i++)
                    {
                        comboBox24.Items.Add(myjob7.block.Inputs[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button48_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 6)
                {
                    SetBlockInputSafe(myjob7, comboBox24.Text, textBox31.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox24.Text + "写入数值" + textBox31.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button47_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 5)
                {
                    SetBlockInputSafe(myjob6, comboBox23.Text, textBox30.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox23.Text + "写入数值" + textBox30.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button46_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 4)
                {
                    SetBlockInputSafe(myjob5, comboBox20.Text, textBox29.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox20.Text + "写入数值" + textBox29.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button45_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 3)
                {
                    SetBlockInputSafe(myjob4, comboBox19.Text, textBox25.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox19.Text + "写入数值" + textBox25.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button43_Click(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 2)
                {
                    SetBlockInputSafe(myjob3, comboBox18.Text, textBox24.Text); // ch:P2 手参写入改走 blockLock 保护路径
                }
                MessageBox.Show("参数:" + comboBox18.Text + "写入数值" + textBox24.Text + "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox18_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 2)
                {
                    textBox24.Text = myjob3.block.Inputs[comboBox18.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox19_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 3)
                {
                    textBox25.Text = myjob4.block.Inputs[comboBox19.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox20_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 4)
                {
                    textBox29.Text = myjob5.block.Inputs[comboBox20.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox23_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 5)
                {
                    textBox30.Text = myjob6.block.Inputs[comboBox23.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox24_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (manager1.JobCount > 6)
                {
                    textBox31.Text = myjob7.block.Inputs[comboBox24.Text].Value.ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void comboBox27_DropDownClosed(object sender, EventArgs e)
        {

        }
        Dictionary<string, string> toolName1 = new Dictionary<string, string>();
        Dictionary<string, ICogTool> tools1 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools2 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools3 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools4 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools5 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools6 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools7 = new Dictionary<string, ICogTool>();
        Dictionary<string, ICogTool> tools8 = new Dictionary<string, ICogTool>();
        private void calibdrop(CogToolBlock blk, out CogCalibNPointToNPointTool tol)
        {
            CogCalibNPointToNPointTool tooltemp = null;
            foreach (ICogTool tool in blk.Tools)
            {
                if (!(tool is CogToolBlock) && (tool is CogCalibNPointToNPointTool))
                {
                    tooltemp = (CogCalibNPointToNPointTool)tool;
                }
                if (tool is CogToolBlock)
                {
                    block_11 = tool as CogToolBlock;
                    foreach (ICogTool tool1 in block_11.Tools)
                    {
                        if (!(tool is CogToolBlock) && (tool1 is CogCalibNPointToNPointTool))
                        {
                            tooltemp = (CogCalibNPointToNPointTool)tool1;
                        }
                        if (tool1 is CogToolBlock)
                        {
                            block_11 = tool as CogToolBlock;
                            foreach (ICogTool tool2 in block_11.Tools)
                            {
                                if (!(tool2 is CogToolBlock) && (tool2 is CogCalibNPointToNPointTool))
                                {
                                    tooltemp = (CogCalibNPointToNPointTool)tool2;
                                }
                                if (tool2 is CogToolBlock)
                                {
                                    block_11 = tool as CogToolBlock;
                                    foreach (ICogTool tool12 in block_11.Tools)
                                    {
                                        if (tool12 is CogCalibNPointToNPointTool)
                                        {
                                            tooltemp = (CogCalibNPointToNPointTool)tool12;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            tol = tooltemp;
        }
        void gongjukuai(string camere, string jieguo)
        {
            CogToolBlock block_temp;
            int youwu = 0;
            this.BeginInvoke(new Action(() =>
            {
                // ch:P0-3 ToolBlock 非线程安全：本方法遍历 Tools/读 Outputs，须与该相机检测线程的 Run 互斥（blockLock）；
                //   仅命中一个 case，锁对应 job 即可。
                Myjob gjob = camere == "1" ? myjob1 : camere == "2" ? myjob2 : camere == "3" ? myjob3 : camere == "4" ? myjob4 :
                             camere == "5" ? myjob5 : camere == "6" ? myjob6 : camere == "7" ? myjob7 : myjob8;
                if (gjob == null || gjob.block == null) return;
                if (Volatile.Read(ref qiehuanzhong) == 1) return; // ch:P0-3 切换/加载期间加载器锁外重写 block，本渲染路径让路
                lock (gjob.blockLock)
                {
                switch (camere)
                {
                    case "1":
                        if (jieguo == "Accept")
                        {
                            c11.Text = "ok";
                            c11.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c11.Text = "ng";
                            c11.BackColor = Color.Red;
                        }

                        myjob1.list_block.Clear();
                        c12.Visible = false;
                        c13.Visible = false;
                        c14.Visible = false;
                        c15.Visible = false;
                        foreach (ICogTool tool in myjob1.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob1.list_block.Count < 4)
                                        {
                                            switch (myjob1.list_block.Count)
                                            {
                                                case 0:
                                                    c12.Visible = true;
                                                    break;
                                                case 1:
                                                    c13.Visible = true;
                                                    break;
                                                case 2:
                                                    c14.Visible = true;
                                                    break;
                                                case 3:
                                                    c15.Visible = true;
                                                    break;
                                            }
                                            myjob1.list_block[myjob1.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            // for (int j= 0; j < block_temp.Outputs.Count; j++)
                                            // {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob1.list_block.Count < 4)
                                                    {
                                                        switch (myjob1.list_block.Count)
                                                        {
                                                            case 0:
                                                                c12.Visible = true;
                                                                break;
                                                            case 1:
                                                                c13.Visible = true;
                                                                break;
                                                            case 2:
                                                                c14.Visible = true;
                                                                break;
                                                            case 3:
                                                                c15.Visible = true;
                                                                break;
                                                        }
                                                        myjob1.list_block[myjob1.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        // for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        //{
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob1.list_block.Count < 4)
                                                                {
                                                                    switch (myjob1.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c12.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c13.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c14.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c15.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob1.list_block[myjob1.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //  }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                //}
                            }
                        }
                        break;
                    case "2":
                        if (jieguo == "Accept")
                        {
                            c21.Text = "ok";
                            c21.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c21.Text = "ng";
                            c21.BackColor = Color.Red;
                        }
                        myjob2.list_block.Clear();
                        c22.Visible = false;
                        c23.Visible = false;
                        c24.Visible = false;
                        c25.Visible = false;
                        foreach (ICogTool tool in myjob2.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                //for (int i = 0; i < block_temp.Outputs.Count; i++)
                                //  {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob2.list_block.Count < 4)
                                        {
                                            switch (myjob2.list_block.Count)
                                            {
                                                case 0:
                                                    c22.Visible = true;
                                                    break;
                                                case 1:
                                                    c23.Visible = true;
                                                    break;
                                                case 2:
                                                    c24.Visible = true;
                                                    break;
                                                case 3:
                                                    c25.Visible = true;
                                                    break;
                                            }
                                            myjob2.list_block[myjob2.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            // for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            // {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob2.list_block.Count < 4)
                                                    {
                                                        switch (myjob2.list_block.Count)
                                                        {
                                                            case 0:
                                                                c22.Visible = true;
                                                                break;
                                                            case 1:
                                                                c23.Visible = true;
                                                                break;
                                                            case 2:
                                                                c24.Visible = true;
                                                                break;
                                                            case 3:
                                                                c25.Visible = true;
                                                                break;
                                                        }
                                                        myjob2.list_block[myjob2.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        //for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        // {
                                                        youwu = 0;
                                                        for (int j = 0; j < block_temp.Outputs.Count; j++)
                                                        {
                                                            if (block_temp.Outputs[j].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob2.list_block.Count < 4)
                                                                {
                                                                    switch (myjob2.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c22.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c23.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c24.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c25.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob2.list_block[myjob2.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //   }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }
                                //  }
                            }
                        }
                        break;
                    case "3":

                        myjob3.list_block.Clear();
                        if (tableLayoutPanel5.Visible == true)
                        {
                            if (jieguo == "Accept")
                            {
                                c31.Text = "ok";
                                c31.BackColor = Color.LightGreen;
                            }
                            else
                            {
                                c31.Text = "ng";
                                c31.BackColor = Color.Red;
                            }
                            c32.Visible = false;
                            c33.Visible = false;
                            c34.Visible = false;
                            c35.Visible = false;
                        }
                        else
                        {
                            if (jieguo == "Accept")
                            {
                                c41.Text = "ok";
                                c41.BackColor = Color.LightGreen;
                            }
                            else
                            {
                                c41.Text = "ng";
                                c41.BackColor = Color.Red;
                            }
                            c42.Visible = false;
                            c43.Visible = false;
                            c44.Visible = false;
                            c45.Visible = false;
                        }
                        foreach (ICogTool tool in myjob3.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                //for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob3.list_block.Count < 4)
                                        {
                                            if (tableLayoutPanel5.Visible == true)
                                            {
                                                switch (myjob3.list_block.Count)
                                                {
                                                    case 0:
                                                        c32.Visible = true;
                                                        break;
                                                    case 1:
                                                        c33.Visible = true;
                                                        break;
                                                    case 2:
                                                        c34.Visible = true;
                                                        break;
                                                    case 3:
                                                        c35.Visible = true;
                                                        break;
                                                }
                                            }
                                            else
                                            {
                                                switch (myjob3.list_block.Count)
                                                {
                                                    case 0:
                                                        c42.Visible = true;
                                                        break;
                                                    case 1:
                                                        c43.Visible = true;
                                                        break;
                                                    case 2:
                                                        c44.Visible = true;
                                                        break;
                                                    case 3:
                                                        c45.Visible = true;
                                                        break;
                                                }
                                            }
                                            myjob3.list_block[myjob3.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            // for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            //  {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob3.list_block.Count < 4)
                                                    {
                                                        if (tableLayoutPanel5.Visible == true)
                                                        {
                                                            switch (myjob3.list_block.Count)
                                                            {
                                                                case 0:
                                                                    c32.Visible = true;
                                                                    break;
                                                                case 1:
                                                                    c33.Visible = true;
                                                                    break;
                                                                case 2:
                                                                    c34.Visible = true;
                                                                    break;
                                                                case 3:
                                                                    c35.Visible = true;
                                                                    break;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            switch (myjob3.list_block.Count)
                                                            {
                                                                case 0:
                                                                    c42.Visible = true;
                                                                    break;
                                                                case 1:
                                                                    c43.Visible = true;
                                                                    break;
                                                                case 2:
                                                                    c44.Visible = true;
                                                                    break;
                                                                case 3:
                                                                    c45.Visible = true;
                                                                    break;
                                                            }
                                                        }
                                                        myjob3.list_block[myjob3.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        //    for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        //  {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob3.list_block.Count < 4)
                                                                {
                                                                    switch (myjob3.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c32.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c33.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c34.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c35.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob3.list_block[myjob3.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //  }
                                                    }
                                                }

                                            }

                                            // }
                                        }
                                    }

                                }

                                // }
                            }
                        }
                        break;
                    case "4":

                        myjob4.list_block.Clear();
                        if (tableLayoutPanel5.Visible == true)
                        {
                            if (jieguo == "Accept")
                            {
                                c41.Text = "ok";
                                c41.BackColor = Color.LightGreen;
                            }
                            else
                            {
                                c41.Text = "ng";
                                c41.BackColor = Color.Red;
                            }
                            c42.Visible = false;
                            c43.Visible = false;
                            c44.Visible = false;
                            c45.Visible = false;
                        }
                        else
                        {
                            if (jieguo == "Accept")
                            {
                                c51.Text = "ok";
                                c51.BackColor = Color.LightGreen;
                            }
                            else
                            {
                                c51.Text = "ng";
                                c51.BackColor = Color.Red;
                            }
                            c52.Visible = false;
                            c53.Visible = false;
                            c54.Visible = false;
                            c55.Visible = false;
                        }
                        foreach (ICogTool tool in myjob4.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                // for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob4.list_block.Count < 4)
                                        {
                                            if (tableLayoutPanel5.Visible == true)
                                            {
                                                switch (myjob4.list_block.Count)
                                                {
                                                    case 0:
                                                        c42.Visible = true;
                                                        break;
                                                    case 1:
                                                        c43.Visible = true;
                                                        break;
                                                    case 2:
                                                        c44.Visible = true;
                                                        break;
                                                    case 3:
                                                        c45.Visible = true;
                                                        break;
                                                }
                                            }
                                            else
                                            {
                                                switch (myjob4.list_block.Count)
                                                {
                                                    case 0:
                                                        c52.Visible = true;
                                                        break;
                                                    case 1:
                                                        c53.Visible = true;
                                                        break;
                                                    case 2:
                                                        c54.Visible = true;
                                                        break;
                                                    case 3:
                                                        c55.Visible = true;
                                                        break;
                                                }
                                            }
                                            myjob4.list_block[myjob4.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            //  for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            // {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob4.list_block.Count < 4)
                                                    {
                                                        if (tableLayoutPanel5.Visible == true)
                                                        {
                                                            switch (myjob4.list_block.Count)
                                                            {
                                                                case 0:
                                                                    c42.Visible = true;
                                                                    break;
                                                                case 1:
                                                                    c43.Visible = true;
                                                                    break;
                                                                case 2:
                                                                    c44.Visible = true;
                                                                    break;
                                                                case 3:
                                                                    c45.Visible = true;
                                                                    break;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            switch (myjob4.list_block.Count)
                                                            {
                                                                case 0:
                                                                    c52.Visible = true;
                                                                    break;
                                                                case 1:
                                                                    c53.Visible = true;
                                                                    break;
                                                                case 2:
                                                                    c54.Visible = true;
                                                                    break;
                                                                case 3:
                                                                    c55.Visible = true;
                                                                    break;
                                                            }
                                                        }
                                                        myjob4.list_block[myjob4.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        //  for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        //  {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob4.list_block.Count < 4)
                                                                {
                                                                    if (tableLayoutPanel5.Visible == true)
                                                                    {
                                                                        switch (myjob4.list_block.Count)
                                                                        {
                                                                            case 0:
                                                                                c42.Visible = true;
                                                                                break;
                                                                            case 1:
                                                                                c43.Visible = true;
                                                                                break;
                                                                            case 2:
                                                                                c44.Visible = true;
                                                                                break;
                                                                            case 3:
                                                                                c45.Visible = true;
                                                                                break;
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        switch (myjob4.list_block.Count)
                                                                        {
                                                                            case 0:
                                                                                c52.Visible = true;
                                                                                break;
                                                                            case 1:
                                                                                c53.Visible = true;
                                                                                break;
                                                                            case 2:
                                                                                c54.Visible = true;
                                                                                break;
                                                                            case 3:
                                                                                c55.Visible = true;
                                                                                break;
                                                                        }
                                                                    }
                                                                    myjob4.list_block[myjob4.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //  }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                //  }
                            }
                        }
                        break;
                    case "5":
                        if (jieguo == "Accept")
                        {
                            c51.Text = "ok";
                            c51.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c51.Text = "ng";
                            c51.BackColor = Color.Red;
                        }
                        myjob5.list_block.Clear();
                        c52.Visible = false;
                        c53.Visible = false;
                        c54.Visible = false;
                        c55.Visible = false;
                        foreach (ICogTool tool in myjob5.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                // for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob5.list_block.Count < 4)
                                        {
                                            switch (myjob5.list_block.Count)
                                            {
                                                case 0:
                                                    c52.Visible = true;
                                                    break;
                                                case 1:
                                                    c53.Visible = true;
                                                    break;
                                                case 2:
                                                    c54.Visible = true;
                                                    break;
                                                case 3:
                                                    c55.Visible = true;
                                                    break;
                                            }
                                            myjob5.list_block[myjob5.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            // for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            //  {
                                            //  for (int k = 0; k < block_temp.Outputs.Count; k++)
                                            //  {
                                            youwu = 0;
                                            for (int k = 0; k < block_temp.Outputs.Count; k++)
                                            {
                                                if (block_temp.Outputs[k].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob5.list_block.Count < 4)
                                                    {
                                                        switch (myjob5.list_block.Count)
                                                        {
                                                            case 0:
                                                                c52.Visible = true;
                                                                break;
                                                            case 1:
                                                                c53.Visible = true;
                                                                break;
                                                            case 2:
                                                                c54.Visible = true;
                                                                break;
                                                            case 3:
                                                                c55.Visible = true;
                                                                break;
                                                        }
                                                        myjob5.list_block[myjob5.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        // for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        // {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob5.list_block.Count < 4)
                                                                {
                                                                    switch (myjob5.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c52.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c53.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c54.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c55.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob5.list_block[myjob5.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //  }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                // }
                            }
                        }
                        break;
                    case "6":
                        if (jieguo == "Accept")
                        {
                            c61.Text = "ok";
                            c61.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c61.Text = "ng";
                            c61.BackColor = Color.Red;
                        }
                        myjob6.list_block.Clear();
                        c62.Visible = false;
                        c63.Visible = false;
                        c64.Visible = false;
                        c65.Visible = false;
                        foreach (ICogTool tool in myjob6.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                //  for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob6.list_block.Count < 4)
                                        {
                                            switch (myjob6.list_block.Count)
                                            {
                                                case 0:
                                                    c62.Visible = true;
                                                    break;
                                                case 1:
                                                    c63.Visible = true;
                                                    break;
                                                case 2:
                                                    c64.Visible = true;
                                                    break;
                                                case 3:
                                                    c65.Visible = true;
                                                    break;
                                            }
                                            myjob6.list_block[myjob6.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            //  for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            //  {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob6.list_block.Count < 4)
                                                    {
                                                        switch (myjob6.list_block.Count)
                                                        {
                                                            case 0:
                                                                c62.Visible = true;
                                                                break;
                                                            case 1:
                                                                c63.Visible = true;
                                                                break;
                                                            case 2:
                                                                c64.Visible = true;
                                                                break;
                                                            case 3:
                                                                c65.Visible = true;
                                                                break;
                                                        }
                                                        myjob6.list_block[myjob6.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        // for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        // {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob6.list_block.Count < 4)
                                                                {
                                                                    switch (myjob6.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c62.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c63.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c64.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c65.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob6.list_block[myjob6.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        // }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                //  }
                            }
                        }
                        break;
                    case "7":
                        if (jieguo == "Accept")
                        {
                            c71.Text = "ok";
                            c71.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c71.Text = "ng";
                            c71.BackColor = Color.Red;
                        }
                        myjob7.list_block.Clear();
                        c72.Visible = false;
                        c73.Visible = false;
                        c74.Visible = false;
                        c75.Visible = false;
                        foreach (ICogTool tool in myjob7.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                // for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob7.list_block.Count < 4)
                                        {
                                            switch (myjob7.list_block.Count)
                                            {
                                                case 0:
                                                    c72.Visible = true;
                                                    break;
                                                case 1:
                                                    c73.Visible = true;
                                                    break;
                                                case 2:
                                                    c74.Visible = true;
                                                    break;
                                                case 3:
                                                    c75.Visible = true;
                                                    break;
                                            }
                                            myjob7.list_block[myjob7.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            // for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            // {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob7.list_block.Count < 4)
                                                    {
                                                        switch (myjob7.list_block.Count)
                                                        {
                                                            case 0:
                                                                c72.Visible = true;
                                                                break;
                                                            case 1:
                                                                c73.Visible = true;
                                                                break;
                                                            case 2:
                                                                c74.Visible = true;
                                                                break;
                                                            case 3:
                                                                c75.Visible = true;
                                                                break;
                                                        }
                                                        myjob7.list_block[myjob7.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        // for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        // {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob7.list_block.Count < 4)
                                                                {
                                                                    switch (myjob7.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c72.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c73.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c74.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c75.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob7.list_block[myjob7.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //  }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                // }
                            }
                        }
                        break;
                    case "8":
                        if (jieguo == "Accept")
                        {
                            c81.Text = "ok";
                            c81.BackColor = Color.LightGreen;
                        }
                        else
                        {
                            c81.Text = "ng";
                            c81.BackColor = Color.Red;
                        }
                        myjob8.list_block.Clear();
                        c82.Visible = false;
                        c83.Visible = false;
                        c84.Visible = false;
                        c85.Visible = false;
                        foreach (ICogTool tool in myjob8.block.Tools)
                        {
                            if (tool is CogToolBlock)
                            {
                                block_temp = tool as CogToolBlock;
                                // for (int i = 0; i < block_temp.Outputs.Count; i++)
                                // {
                                youwu = 0;
                                for (int i = 0; i < block_temp.Outputs.Count; i++)
                                {
                                    if (block_temp.Outputs[i].Name == "RunStatus")
                                    {
                                        youwu = 1;
                                    }
                                }
                                if (youwu == 1)
                                {
                                    if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                    {
                                        if (myjob8.list_block.Count < 4)
                                        {
                                            switch (myjob8.list_block.Count)
                                            {
                                                case 0:
                                                    c82.Visible = true;
                                                    break;
                                                case 1:
                                                    c83.Visible = true;
                                                    break;
                                                case 2:
                                                    c84.Visible = true;
                                                    break;
                                                case 3:
                                                    c85.Visible = true;
                                                    break;
                                            }
                                            myjob8.list_block[myjob8.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (ICogTool tool1 in block_temp.Tools)
                                    {
                                        if (tool1 is CogToolBlock)
                                        {
                                            block_temp = tool1 as CogToolBlock;
                                            //for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            // {
                                            youwu = 0;
                                            for (int j = 0; j < block_temp.Outputs.Count; j++)
                                            {
                                                if (block_temp.Outputs[j].Name == "RunStatus")
                                                {
                                                    youwu = 1;
                                                }
                                            }
                                            if (youwu == 1)
                                            {
                                                if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                {
                                                    if (myjob8.list_block.Count < 4)
                                                    {
                                                        switch (myjob8.list_block.Count)
                                                        {
                                                            case 0:
                                                                c82.Visible = true;
                                                                break;
                                                            case 1:
                                                                c83.Visible = true;
                                                                break;
                                                            case 2:
                                                                c84.Visible = true;
                                                                break;
                                                            case 3:
                                                                c85.Visible = true;
                                                                break;
                                                        }
                                                        myjob8.list_block[myjob8.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                foreach (ICogTool tool2 in block_temp.Tools)
                                                {
                                                    if (tool2 is CogToolBlock)
                                                    {
                                                        block_temp = tool2 as CogToolBlock;
                                                        // for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        // {
                                                        youwu = 0;
                                                        for (int k = 0; k < block_temp.Outputs.Count; k++)
                                                        {
                                                            if (block_temp.Outputs[k].Name == "RunStatus")
                                                            {
                                                                youwu = 1;
                                                            }
                                                        }
                                                        if (youwu == 1)
                                                        {
                                                            if (block_temp.Outputs["RunStatus"].Value.ToString() == "0")
                                                            {
                                                                if (myjob8.list_block.Count < 4)
                                                                {
                                                                    switch (myjob8.list_block.Count)
                                                                    {
                                                                        case 0:
                                                                            c82.Visible = true;
                                                                            break;
                                                                        case 1:
                                                                            c83.Visible = true;
                                                                            break;
                                                                        case 2:
                                                                            c84.Visible = true;
                                                                            break;
                                                                        case 3:
                                                                            c85.Visible = true;
                                                                            break;
                                                                    }
                                                                    myjob8.list_block[myjob8.list_block.Count + 1] = block_temp; // ch:P2 ConcurrentDictionary 的 Add 是显式接口实现，改索引器赋值（重复键覆盖而非抛异常）
                                                                }
                                                            }
                                                        }
                                                        //   }
                                                    }
                                                }

                                            }

                                            //  }
                                        }
                                    }

                                }

                                // }
                            }
                        }
                        break;
                }
                } // ch:P0-3 blockLock 段结束
            }));
        }
        private void combdrop(ComboBox combox, CogToolBlock blk, Dictionary<string, ICogTool> dic)
        {
            combox.Items.Clear();
            toolName1.Clear();
            dic.Clear();
            block_11 = null;
            string name_temp = "";
            int i = 0;
            int j = 0;
            int k = 0;
            foreach (ICogTool tool in blk.Tools)
            {
                if (!(tool is CogToolBlock) && (tool is CogPMAlignTool || tool is CogBlobTool || tool is CogPMAlignMultiTool))
                {
                    dic.Add(tool.Name, tool);
                    combox.Items.Add(dic.Keys.Last());
                }
                if (tool is CogToolBlock)
                {
                    i++;
                    block_11 = tool as CogToolBlock;
                    name_temp = block_11.Name + ".";
                    foreach (ICogTool tool1 in block_11.Tools)
                    {
                        if (!(tool1 is CogToolBlock) && (tool1 is CogPMAlignTool || tool1 is CogBlobTool || tool1 is CogPMAlignMultiTool))
                        {
                            dic.Add(name_temp + tool1.Name, tool1);
                            combox.Items.Add(dic.Keys.Last());
                        }
                        if (tool1 is CogToolBlock)
                        {
                            j++;
                            block_11 = tool as CogToolBlock;
                            name_temp = block_11.Name + ".";
                            foreach (ICogTool tool2 in block_11.Tools)
                            {
                                if (!(tool2 is CogToolBlock) && (tool2 is CogPMAlignTool || tool2 is CogBlobTool || tool2 is CogPMAlignMultiTool))
                                {
                                    dic.Add(name_temp + tool2.Name, tool2);
                                    combox.Items.Add(dic.Keys.Last());
                                }
                                if (tool2 is CogToolBlock)
                                {
                                    k++;
                                    block_11 = tool as CogToolBlock;
                                    name_temp = block_11.Name + ".";
                                    foreach (ICogTool tool12 in block_11.Tools)
                                    {
                                        dic.Add(name_temp + tool12.Name, tool2);
                                        combox.Items.Add(dic.Keys.Last());
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void comboBox27_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox27, myjob1.block, tools1);
        }

        private void checkBox26_CheckedChanged(object sender, EventArgs e)
        {
            myjob1.roi = checkBox26.Checked;
            roiset2(myjob1.block, tools1[comboBox27.Text], myjob1.roi, PrepareRoiDisplay(pictureBoxCam1, myjob1.block, myjob1.roi));
        }

        private void comboBox29_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox29, myjob2.block, tools2);
        }

        private void comboBox30_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox30, myjob3.block, tools3);
        }

        private void comboBox32_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox32, myjob4.block, tools4);
        }

        private void comboBox33_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox33, myjob5.block, tools5);
        }

        private void comboBox35_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox35, myjob6.block, tools6);
        }

        private void comboBox36_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox36, myjob7.block, tools7);
        }

        private void comboBox37_DropDown(object sender, EventArgs e)
        {
            combdrop(comboBox37, myjob8.block, tools8);
        }

        private void checkBox68_CheckedChanged(object sender, EventArgs e)
        {
            myjob8.roi = checkBox68.Checked;
            roiset2(myjob8.block, tools8[comboBox37.Text], checkBox68.Checked, PrepareRoiDisplay(pictureBoxCam8, myjob8.block, checkBox68.Checked));
        }

        private void checkBox67_CheckedChanged(object sender, EventArgs e)
        {
            myjob7.roi = checkBox67.Checked;
            roiset2(myjob7.block, tools7[comboBox36.Text], checkBox67.Checked, PrepareRoiDisplay(pictureBoxCam7, myjob7.block, checkBox67.Checked));
        }

        private void checkBox66_CheckedChanged(object sender, EventArgs e)
        {
            myjob6.roi = checkBox66.Checked;
            roiset2(myjob6.block, tools6[comboBox35.Text], checkBox66.Checked, PrepareRoiDisplay(pictureBoxCam6, myjob6.block, checkBox66.Checked));
        }

        private void checkBox65_CheckedChanged(object sender, EventArgs e)
        {
            myjob5.roi = checkBox65.Checked;
            roiset2(myjob5.block, tools5[comboBox33.Text], checkBox65.Checked, PrepareRoiDisplay(pictureBoxCam5, myjob5.block, checkBox65.Checked));
        }

        private void checkBox64_CheckedChanged(object sender, EventArgs e)
        {
            myjob4.roi = checkBox64.Checked;
            roiset2(myjob4.block, tools4[comboBox32.Text], checkBox64.Checked, PrepareRoiDisplay(pictureBoxCam4, myjob4.block, checkBox64.Checked));
        }

        private void checkBox63_CheckedChanged(object sender, EventArgs e)
        {
            myjob3.roi = checkBox63.Checked;
            roiset2(myjob3.block, tools3[comboBox30.Text], checkBox63.Checked, PrepareRoiDisplay(pictureBoxCam3, myjob3.block, checkBox63.Checked));
        }

        private void checkBox62_CheckedChanged(object sender, EventArgs e)
        {
            myjob2.roi = checkBox62.Checked;
            roiset2(myjob2.block, tools2[comboBox29.Text], checkBox62.Checked, PrepareRoiDisplay(pictureBoxCam2, myjob2.block, checkBox62.Checked));
        }

        private void checkBox11_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox11.Checked)
            {
                numericUpDown7.Visible = false;
                button50.Visible = false;
                dataGridView1.ReadOnly = true;
                dataGridView1.DataSource = myjob1.myTable;
            }
            else
            {
                dataGridView1.ReadOnly = false;
                numericUpDown7.Visible = true;
                button50.Visible = true;
                dataGridView1.DataSource = myjob1.myTable1;
            }
        }

        private void checkBox8_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox8.Checked)
            {
                numericUpDown8.Visible = false;
                dataGridView2.ReadOnly = true;
                button51.Visible = false;
                dataGridView2.DataSource = myjob2.myTable;
            }
            else
            {
                numericUpDown8.Visible = true;
                button51.Visible = true;
                dataGridView2.ReadOnly = false;
                dataGridView2.DataSource = myjob2.myTable1;
            }
        }

        private void dataGridView2_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (Frm2.start == 1)
            {
                try
                {
                    if (dataGridView2.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "像素X")
                    {
                        myjob2.calib.Calibration.SetUncalibratedPointX(e.RowIndex, double.Parse(dataGridView2[0, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView2.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "像素Y")
                    {
                        myjob2.calib.Calibration.SetUncalibratedPointY(e.RowIndex, double.Parse(dataGridView2[1, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView2.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "实际X")
                    {
                        myjob2.calib.Calibration.SetRawCalibratedPointX(e.RowIndex, double.Parse(dataGridView2[2, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView2.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "实际Y")
                    {
                        myjob2.calib.Calibration.SetRawCalibratedPointY(e.RowIndex, double.Parse(dataGridView2[3, e.RowIndex].Value.ToString()));
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                }
            }
        }

        private void dataGridView1_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (Frm2.start == 1)
            {
                try
                {
                    if (dataGridView1.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "像素X")
                    {
                        myjob1.calib.Calibration.SetUncalibratedPointX(e.RowIndex, double.Parse(dataGridView1[0, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView1.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "像素Y")
                    {
                        myjob1.calib.Calibration.SetUncalibratedPointY(e.RowIndex, double.Parse(dataGridView1[1, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView1.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "实际X")
                    {
                        myjob1.calib.Calibration.SetRawCalibratedPointX(e.RowIndex, double.Parse(dataGridView1[2, e.RowIndex].Value.ToString()));
                    }
                    else if (dataGridView1.Columns[e.ColumnIndex].HeaderCell.Value.ToString() == "实际Y")
                    {
                        myjob1.calib.Calibration.SetRawCalibratedPointY(e.RowIndex, double.Parse(dataGridView1[3, e.RowIndex].Value.ToString()));
                    }
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog(ex.Message);
                }

            }
        }

        private void button51_Click(object sender, EventArgs e)
        {
            if (myjob2.calib != null)
            {
                try
                {
                    myjob2.calib.Calibration.Calibrate();
                    MessageBox.Show("相机2标定成功");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void button50_Click(object sender, EventArgs e)
        {
            if (myjob1.calib != null)
            {
                try
                {
                    myjob1.calib.Calibration.Calibrate();
                    MessageBox.Show("相机1标定成功");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void comboBox27_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void 相机1ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[0] = new Form9(myjob1.block);
            f9[0].Show();
            f9[0].label2.Text = "相机一";
        }

        private void 相机2ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[1] = new Form9(myjob2.block);
            f9[1].Show();
            f9[1].label2.Text = "相机二";
        }

        private void 相机3ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[2] = new Form9(myjob3.block);
            f9[2].Show();
            f9[2].label2.Text = "相机三";
        }

        private void 相机4ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[3] = new Form9(myjob4.block);
            f9[3].Show();
            f9[3].label2.Text = "相机四";
        }

        private void 相机5ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[4] = new Form9(myjob5.block);
            f9[4].Show();
            f9[4].label2.Text = "相机五";
        }

        private void 相机6ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[5] = new Form9(myjob6.block);
            f9[5].Show();
            f9[5].label2.Text = "相机六";
        }

        private void 相机7ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[6] = new Form9(myjob7.block);
            f9[6].Show();
            f9[6].label2.Text = "相机七";
        }

        private void 相机8ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            f9[7] = new Form9(myjob8.block);
            f9[7].Show();
            f9[7].label2.Text = "相机八";
        }

        private void cogRecordDisplay1_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel2);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob1.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob1.records == 0)
                myjob1.records = 1;
            else
                myjob1.records = 0;
        }

        private void record_bian(int b1, int b2, float c2, float c3, float c4, int c5, float c6, float c7, float c8)
        {
            if (c5 == 0)
            {
                if (b1 == 0 && b2 == 0)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 1 && b2 == 0)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 2 && b2 == 0)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 0 && b2 == 1)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 1 && b2 == 1)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 2 && b2 == 1)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 0.5f));
                }
                else if (b1 == 0 && b2 == 2)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 99f));
                }
                else if (b1 == 1 && b2 == 2)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 99f));
                }
                else if (b1 == 2 && b2 == 2)
                {
                    this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, 99f));
                    this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, 0.5f));
                    this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, 99f));
                }

            }
            else
            {

                this.tableLayoutPanel1.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, c2));
                this.tableLayoutPanel1.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, c3));
                this.tableLayoutPanel1.ColumnStyles[2] = (new ColumnStyle(SizeType.Percent, c4));
                this.tableLayoutPanel1.RowStyles[0] = (new RowStyle(SizeType.Percent, c6));
                this.tableLayoutPanel1.RowStyles[1] = (new RowStyle(SizeType.Percent, c7));
                this.tableLayoutPanel1.RowStyles[2] = (new RowStyle(SizeType.Percent, c8));

            }
        }

        private void cogRecordDisplay2_DoubleClick(object sender, EventArgs e)
        {

            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel3);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob2.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob2.records == 0)
                myjob2.records = 1;
            else
                myjob2.records = 0;
        }

        private void tableLayoutPanel1_DoubleClick(object sender, EventArgs e)
        {

        }

        private void cogRecordDisplay3_Enter(object sender, EventArgs e)
        {

        }

        private void cogRecordDisplay3_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel5);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob3.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob3.records == 0)
                myjob3.records = 1;
            else
                myjob3.records = 0;
        }

        private void cogRecordDisplay4_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel7);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob4.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob4.records == 0)
                myjob4.records = 1;
            else
                myjob4.records = 0;
        }

        private void cogRecordDisplay5_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel9);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob5.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob5.records == 0)
                myjob5.records = 1;
            else
                myjob5.records = 0;
        }

        private void cogRecordDisplay6_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel12);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob6.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob6.records == 0)
                myjob6.records = 1;
            else
                myjob6.records = 0;
        }

        private void cogRecordDisplay7_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel14);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob7.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob7.records == 0)
                myjob7.records = 1;
            else
                myjob7.records = 0;
        }

        private void cogRecordDisplay8_DoubleClick(object sender, EventArgs e)
        {
            TableLayoutPanelCellPosition p = new TableLayoutPanelCellPosition();
            p = tableLayoutPanel1.GetCellPosition(tableLayoutPanel16);
            record_bian(p.Column, p.Row, myjob1.record[0], myjob1.record[1], myjob1.record[2], myjob8.records, myjob2.record[0], myjob2.record[1], myjob2.record[2]);
            if (myjob8.records == 0)
                myjob8.records = 1;
            else
                myjob8.records = 0;
        }

        private void cogRecordDisplay9_DoubleClick(object sender, EventArgs e)
        {
        }

        private void label153_Click(object sender, EventArgs e)
        {

        }

        private void timer17_Tick(object sender, EventArgs e)
        {
            ExpireIoPulses();
        }

        private void checkBox69_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox69.CheckState == CheckState.Checked)
            {
                timer17.Enabled = true;
            }
            else
            {
                timer17.Enabled = false;
                myjob1.outputok2 = -1;
                myjob1.outputok = 0;
                myjob1.outputng2 = -1;
                myjob1.outputng = 0;
            }
        }

        private void tableLayoutPanel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void c12_Click(object sender, EventArgs e)
        {
            ccdshow(myjob1.list_block[1]);
        }
        private void ccdshow(CogToolBlock block_temp)
        {
            Form10 frm10 = new Form10(block_temp);
            frm10.Show();
        }

        private void c13_Click(object sender, EventArgs e)
        {
            ccdshow(myjob1.list_block[2]);
        }

        private void c14_Click(object sender, EventArgs e)
        {
            ccdshow(myjob1.list_block[3]);
        }

        private void c15_Click(object sender, EventArgs e)
        {
            ccdshow(myjob1.list_block[4]);
        }

        private void ForceGongjuJiluOff()
        {
            // ch:已是未勾选时 CheckedChanged 不会触发，必须显式套用隐藏布局并把 gongjujilu 置 0
            if (checkBox70.CheckState != CheckState.Unchecked)
                checkBox70.CheckState = CheckState.Unchecked;
            checkBox70_CheckedChanged(null, null);
        }

        private void checkBox70_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox70.CheckState == CheckState.Checked)
            {
                gongjujilu = 1;
                this.tableLayoutPanel11.Visible = true;
                this.tableLayoutPanel4.Visible = true;
                this.tableLayoutPanel6.Visible = true;
                this.tableLayoutPanel8.Visible = true;
                this.tableLayoutPanel10.Visible = true;
                this.tableLayoutPanel13.Visible = true;
                this.tableLayoutPanel15.Visible = true;
                this.tableLayoutPanel17.Visible = true;
                this.tableLayoutPanel2.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel2.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel3.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel3.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel5.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel5.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel7.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel7.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel9.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel9.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel12.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel12.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel14.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel14.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
                this.tableLayoutPanel16.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 90f));
                this.tableLayoutPanel16.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 10f));
            }
            else
            {
                gongjujilu = 0;
                this.tableLayoutPanel11.Visible = false;
                this.tableLayoutPanel4.Visible = false;
                this.tableLayoutPanel6.Visible = false;
                this.tableLayoutPanel8.Visible = false;
                this.tableLayoutPanel10.Visible = false;
                this.tableLayoutPanel13.Visible = false;
                this.tableLayoutPanel15.Visible = false;
                this.tableLayoutPanel17.Visible = false;
                this.tableLayoutPanel2.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel2.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel3.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel3.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel5.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel5.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel7.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel7.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel9.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel9.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel12.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel12.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel14.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel14.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
                this.tableLayoutPanel16.ColumnStyles[0] = (new ColumnStyle(SizeType.Percent, 99f));
                this.tableLayoutPanel16.ColumnStyles[1] = (new ColumnStyle(SizeType.Percent, 1f));
            }
        }

        private void c22_Click(object sender, EventArgs e)
        {
            ccdshow(myjob2.list_block[1]);
        }

        private void c23_Click(object sender, EventArgs e)
        {
            ccdshow(myjob2.list_block[2]);
        }

        private void c24_Click(object sender, EventArgs e)
        {
            ccdshow(myjob2.list_block[3]);
        }

        private void c25_Click(object sender, EventArgs e)
        {
            ccdshow(myjob2.list_block[4]);
        }

        private void c32_Click(object sender, EventArgs e)
        {
            ccdshow(myjob3.list_block[1]);
        }

        private void c33_Click(object sender, EventArgs e)
        {
            ccdshow(myjob3.list_block[2]);
        }

        private void c34_Click(object sender, EventArgs e)
        {
            ccdshow(myjob3.list_block[3]);
        }

        private void c35_Click(object sender, EventArgs e)
        {
            ccdshow(myjob3.list_block[4]);
        }

        private void c42_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob3.list_block[1]);
            }
            else
            {
                ccdshow(myjob4.list_block[1]);
            }
        }

        private void c43_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob3.list_block[2]);
            }
            else
            {
                ccdshow(myjob4.list_block[2]);
            }
        }

        private void c44_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob3.list_block[3]);
            }
            else
            {
                ccdshow(myjob4.list_block[3]);
            }
        }

        private void c45_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob3.list_block[4]);
            }
            else
            {
                ccdshow(myjob4.list_block[4]);
            }
        }

        private void c52_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob4.list_block[1]);
            }
            else
            {
                ccdshow(myjob5.list_block[1]);
            }
        }

        private void c53_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob4.list_block[2]);
            }
            else
            {
                ccdshow(myjob5.list_block[2]);
            }
        }

        private void c54_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob4.list_block[3]);
            }
            else
            {
                ccdshow(myjob5.list_block[3]);
            }
        }

        private void c55_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                ccdshow(myjob4.list_block[4]);
            }
            else
            {
                ccdshow(myjob5.list_block[4]);
            }
        }

        private void c62_Click(object sender, EventArgs e)
        {

            ccdshow(myjob6.list_block[1]);
        }

        private void c63_Click(object sender, EventArgs e)
        {
            ccdshow(myjob6.list_block[2]);
        }

        private void c64_Click(object sender, EventArgs e)
        {
            ccdshow(myjob6.list_block[3]);
        }

        private void c65_Click(object sender, EventArgs e)
        {
            ccdshow(myjob6.list_block[4]);
        }

        private void c72_Click(object sender, EventArgs e)
        {
            ccdshow(myjob7.list_block[1]);
        }

        private void c73_Click(object sender, EventArgs e)
        {
            ccdshow(myjob7.list_block[2]);
        }

        private void c74_Click(object sender, EventArgs e)
        {
            ccdshow(myjob7.list_block[3]);
        }

        private void c75_Click(object sender, EventArgs e)
        {
            ccdshow(myjob7.list_block[4]);
        }

        private void c82_Click(object sender, EventArgs e)
        {
            ccdshow(myjob8.list_block[1]);
        }

        private void c83_Click(object sender, EventArgs e)
        {
            ccdshow(myjob8.list_block[2]);
        }

        private void c84_Click(object sender, EventArgs e)
        {
            ccdshow(myjob8.list_block[3]);
        }

        private void c85_Click(object sender, EventArgs e)
        {
            ccdshow(myjob8.list_block[4]);
        }
        List<Form6> frm6 = new List<Form6>();
        // ch:打开工具块编辑窗口（Form6/CogToolBlockEditV2）。皮肤引擎已移除（IrisSkin4 钩子对该控件崩溃），
        // ch:此处统一管理编辑窗生命周期：关闭后从列表移除并 Dispose，防止反复打开累积引用/句柄
        private void OpenToolBlockEditor(CogToolBlock block)
        {
            Form6 f6 = null;
            try
            {
                f6 = new Form6(block);
                frm6.Add(f6);
                // ch:窗口关闭后从列表移除并 Dispose，防止反复打开累积引用/句柄
                f6.FormClosed += (s2, e2) =>
                {
                    frm6.Remove(f6);
                    try { f6.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                };
                f6.Show();
            }
            catch
            {
                // ch:创建失败时从列表移除并释放，避免残留半初始化窗口
                if (f6 != null)
                {
                    frm6.Remove(f6);
                    try { f6.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                throw;
            }
        }
        private void c11_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob1.block);
        }

        private void c21_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob2.block);
        }

        private void c31_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob3.block);
        }

        private void c41_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                OpenToolBlockEditor(myjob3.block);
            }
            else
            {
                OpenToolBlockEditor(myjob4.block);
            }
        }

        private void c51_Click(object sender, EventArgs e)
        {
            if (tableLayoutPanel5.Visible == false)
            {
                OpenToolBlockEditor(myjob4.block);
            }
            else
            {
                OpenToolBlockEditor(myjob5.block);
            }
        }

        private void c61_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob6.block);
        }

        private void c71_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob7.block);
        }

        private void c81_Click(object sender, EventArgs e)
        {
            OpenToolBlockEditor(myjob8.block);
        }

        private void button76_Click(object sender, EventArgs e)
        {
            string canshu = textBox5.Text.ToString().Trim();
            int cc = 0;
            if (canshu.Length > 0)
            {
                if (comboBox38.Items.Count == 0)
                {
                    comboBox38.Items.Add(canshu);
                    MessageBox.Show("参数添加成功");
                }
                else
                {
                    for (int i = 0; i < comboBox38.Items.Count; i++)
                    {
                        if (comboBox38.Items[i].ToString() == canshu)
                        {
                            MessageBox.Show("参数名重复");
                            cc = 1;
                            return;
                        }
                    }
                    if (cc == 0)
                    {
                        comboBox38.Items.Add(canshu);
                        MessageBox.Show("参数添加成功");
                    }
                }


            }
            else
            {
                MessageBox.Show("请输入参数名");
            }

        }

        private void comboBox38_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void comboBox38_TextChanged(object sender, EventArgs e)
        {
            if (Volatile.Read(ref qiehuanzhong) == 1) return; // ch:P0-3 切换/加载期间（后台 Task 重写 block）让路，避免与锁外加载器并发
            string canshu = comboBox38.Text.Trim();
            if (canshu.Length > 0)
            {
                for (int i = 0; i < manager1.JobCount; i++)
                {
                    switch (i)
                    {
                        case 0:
                            lock (myjob1.blockLock) // ch:P0-3 Inputs 遍历入锁+判空（SetBlockInputSafe 同锁可重入）
                            {
                                if (myjob1.block != null)
                                    for (int j = 0; j < myjob1.block.Inputs.Count; j++)
                                    {
                                        if (myjob1.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob1, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 1:
                            lock (myjob2.blockLock) // ch:P0-3
                            {
                                if (myjob2.block != null)
                                    for (int j = 0; j < myjob2.block.Inputs.Count; j++)
                                    {
                                        if (myjob2.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob2, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 2:
                            lock (myjob3.blockLock) // ch:P0-3
                            {
                                if (myjob3.block != null)
                                    for (int j = 0; j < myjob3.block.Inputs.Count; j++)
                                    {
                                        if (myjob3.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob3, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 3:
                            lock (myjob4.blockLock) // ch:P0-3
                            {
                                if (myjob4.block != null)
                                    for (int j = 0; j < myjob4.block.Inputs.Count; j++)
                                    {
                                        if (myjob4.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob4, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 4:
                            lock (myjob5.blockLock) // ch:P0-3 Inputs 遍历入锁+判空（SetBlockInputSafe 同锁可重入）
                            {
                                if (myjob5.block != null)
                                    for (int j = 0; j < myjob5.block.Inputs.Count; j++)
                                    {
                                        if (myjob5.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob5, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 5:
                            lock (myjob6.blockLock) // ch:P0-3 Inputs 遍历入锁+判空（SetBlockInputSafe 同锁可重入）
                            {
                                if (myjob6.block != null)
                                    for (int j = 0; j < myjob6.block.Inputs.Count; j++)
                                    {
                                        if (myjob6.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob6, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 6:
                            lock (myjob7.blockLock) // ch:P0-3 Inputs 遍历入锁+判空（SetBlockInputSafe 同锁可重入）
                            {
                                if (myjob7.block != null)
                                    for (int j = 0; j < myjob7.block.Inputs.Count; j++)
                                    {
                                        if (myjob7.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob7, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                        case 7:
                            lock (myjob8.blockLock) // ch:P0-3 Inputs 遍历入锁+判空（SetBlockInputSafe 同锁可重入）
                            {
                                if (myjob8.block != null)
                                    for (int j = 0; j < myjob8.block.Inputs.Count; j++)
                                    {
                                        if (myjob8.block.Inputs[j].Name.Contains("canshu"))
                                        {
                                            SetBlockInputSafe(myjob8, "canshu", canshu);
                                        }
                                    }
                            }
                            break;
                    }
                }
            }
        }

        private void button77_Click(object sender, EventArgs e)
        {
            comboBox38.Items.Clear();
            comboBox38.Text = "";
        }

        private void checkBox71_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox71.CheckState == CheckState.Checked)
                NG = true;
            else
                NG = false;
        }

        private void label15_Click(object sender, EventArgs e)
        {

        }

        private void comboBox39_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (comboBox39.Text)
            {
                case "zh-CN":
                    this.Invoke(new Action(() =>
                    {
                        Thread.CurrentThread.CurrentUICulture = new CultureInfo(comboBox39.Text);
                        Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(comboBox39.Text);
                        保存ToolStripMenuItem.Text = Properties.ZH_cn.保存;
                        设置ToolStripMenuItem.Text = Properties.ZH_cn.菜单;
                        打开ToolStripMenuItem.Text = Properties.ZH_cn.打开;
                        另存为ToolStripMenuItem.Text = Properties.ZH_cn.另存为;
                        用户登录ToolStripMenuItem.Text = Properties.ZH_cn.登录;
                        注销ToolStripMenuItem.Text = Properties.ZH_cn.注销;
                        查找ToolStripMenuItem.Text = Properties.ZH_cn.通讯;
                    }));
                    break;
                case "En":
                    this.Invoke(new Action(() =>
                    {
                        Thread.CurrentThread.CurrentUICulture = new CultureInfo(comboBox39.Text);
                        Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(comboBox39.Text);
                        保存ToolStripMenuItem.Text = Properties.En.保存;
                        设置ToolStripMenuItem.Text = Properties.En.菜单;
                        打开ToolStripMenuItem.Text = Properties.En.打开;
                        另存为ToolStripMenuItem.Text = Properties.En.另存为;
                        用户登录ToolStripMenuItem.Text = Properties.En.登录;
                        注销ToolStripMenuItem.Text = Properties.En.注销;
                        查找ToolStripMenuItem.Text = Properties.En.通讯;
                    }));
                    break;
                case "Fr":
                    this.Invoke(new Action(() =>
                    {
                        Thread.CurrentThread.CurrentUICulture = new CultureInfo(comboBox39.Text);
                        Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(comboBox39.Text);
                        保存ToolStripMenuItem.Text = Properties.Fr.保存;
                        设置ToolStripMenuItem.Text = Properties.Fr.菜单;
                        打开ToolStripMenuItem.Text = Properties.Fr.打开;
                        另存为ToolStripMenuItem.Text = Properties.Fr.另存为;
                        用户登录ToolStripMenuItem.Text = Properties.Fr.登录;
                        注销ToolStripMenuItem.Text = Properties.Fr.注销;
                        查找ToolStripMenuItem.Text = Properties.Fr.通讯;
                    }));
                    break;
                case "Ja":
                    this.Invoke(new Action(() =>
                    {
                        Thread.CurrentThread.CurrentUICulture = new CultureInfo(comboBox39.Text);
                        Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(comboBox39.Text);
                        保存ToolStripMenuItem.Text = Properties.Ja.保存;
                        设置ToolStripMenuItem.Text = Properties.Ja.菜单;
                        打开ToolStripMenuItem.Text = Properties.Ja.打开;
                        另存为ToolStripMenuItem.Text = Properties.Ja.另存为;
                        用户登录ToolStripMenuItem.Text = Properties.Ja.登录;
                        注销ToolStripMenuItem.Text = Properties.Ja.注销;
                        查找ToolStripMenuItem.Text = Properties.Ja.通讯;
                    }));
                    break;
            }
        }

        private void toolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (modbustcp.Visible == false)
                modbustcp.Visible = true;
            else
                modbustcp.Visible = false;
        }

        private void modbusrtuToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (modbusrtu.Visible == false)
                modbusrtu.Visible = true;
            else
                modbusrtu.Visible = false;
        }
    }
}
