using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Globalization;
using demo;

namespace WindowsFormsApplication1
{
    public partial class Form3 : Form
    {

        public Form1 f1;
        public string aa;
        public string monitor;
        public delegate void GetSeletionData(object Sender, SelectionChangedEventArgs e);
        public event GetSeletionData getData;
        public string modbus_style="float";
        public Form3(Form1 f1)
        {
            wdini.ReadINIFile(AppDomain.CurrentDomain.BaseDirectory + "//test.ini");
            this.f1 = f1;
            InitializeComponent();
        }
        private ClassIni wdini = new ClassIni();
        private Thread getRecevice;
        private Thread tcpclientmo;
        protected bool stop = false;
        protected bool constant = false;
        private StreamReader sRead;
        public string gongnengma="06";
        public string serial_xieyi = "无协议";
        public string qiehuan_fangshi = "";
        volatile bool bAccpet = false; // ch:v1.3.54 P2-1 跨线程可见性：接收线程 fun 循环与停止/关闭路径读改
       // SerialPortmdcan.port = new SerialPort();
       public  Modbus mdcan = new Modbus();
        // ch:v1.3.54 串口无协议帧累积器抽到 Core\SerialFrameAccumulator（纯字节、可单测）：
        //   分包/GBK/前缀串误切三类问题统一在此处理，AcceptData 固定在 UI 线程执行（fun 循环 Invoke），累积器 UI 线程独占无需加锁。
        private readonly byte[] _serialReadBuffer = new byte[4096];
        private readonly SerialFrameAccumulator _serialRxAccumulator = new SerialFrameAccumulator();
        public int modbus_qufan = 0;
        public static string strportName = "";
        public static string strbaudRate = "";
        public static string strDataBits = "";
        public static string strStopBits = "";
        public static string strjiaoyan = "";
        Socket socketClient;
        int oks;
        int ngs;
        int oksmonitor;
        int ngsmonitor;
        int jinzhi;
        public string zifu;
        public string lujing;
        ErrorLog MsgErroeLog = new ErrorLog();
        public string xie = "0";
        public string du = "0";
        private void textBox3_TextChanged(object sender, EventArgs e)
        {

        }

        /// <summary>
        /// ch:R46 串口 Modbus 参数读取：优先 [serial] 新键，缺失回退旧 [modbustcp] 键。
        ///   legacyKey 为 null 表示没有旧键可回退；空值/纯空白按缺失处理，返回 defaultValue。
        /// </summary>
        private string ReadSerialSetting(string key, string legacyKey, string defaultValue)
        {
            string value = wdini.ReadString("serial", key, "");
            if (string.IsNullOrEmpty(value) && legacyKey != null)
                value = wdini.ReadString("modbustcp", legacyKey, "");
            value = value.Replace("\0", "");
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        private void Form3_Load(object sender, EventArgs e)
        {
            try
            {
                mdcan.port= new SerialPort();
                // ch:R12 方案表：按 count 键（缺省=老 8 行）装载 N 行进 store，前 8 行由面板同步到文本框。
                // ch:R45 本通道（无协议/串口）改用独立段 "change_serial"/"path_serial"，与 Omron 的
                //   "change"/"path" 分开；新段还没有 count 键时回退读旧段，现场升级无需手改 ini。
                string schemeChangeSection, schemePathSection, schemeCount;
                SerialSchemeSection.Resolve(
                    (section, key, def) => wdini.ReadString(section, key, def),
                    out schemeChangeSection, out schemePathSection, out schemeCount);
                _schemes.LoadFrom(i => wdini.ReadString(schemeChangeSection, i.ToString(), ""),
                                  i => wdini.ReadString(schemePathSection, i.ToString(), ""),
                                  schemeCount);
                InitSchemeUi();
                // "all" 是协议选择键（Serial/Tcp_client/Tcp_server），随方案表迁到新段；
                //   新段还没有该键时回退读旧段，避免升级后协议选择丢失。
                string protocolMode = wdini.ReadString(schemeChangeSection, "all", "");
                if (string.IsNullOrEmpty(protocolMode) && schemeChangeSection == SerialSchemeSection.Change)
                    protocolMode = wdini.ReadString(SerialSchemeSection.LegacyChange, "all", "");
                // ch:R46 旧配置 all=modbustcp：Form3 的 ModbusTCP 已删除（统一由 FormModbus 负责），
                //   清空选择并留日志，避免升级后界面仍显示一个已失效的协议项。
                if (string.Equals(protocolMode, "modbustcp", StringComparison.OrdinalIgnoreCase))
                {
                    MsgErroeLog.WriteLog("Form3 协议选择 modbustcp 已停用：ModbusTCP 统一由 FormModbus 配置，本次已清空该选择");
                    protocolMode = "";
                }
                comboBox2.Text = protocolMode;
                // ch:R46 装载时只赋 Text 不会触发 SelectedIndexChanged（ComboBox 默认 DropDown 样式），
                //   显式同步一次运行态字段；否则重启后 qiehuan_fangshi 为空，串口/网口按 PLC 报文自动切方案的三个判断全部失效。
                comboBox2_SelectedIndexChanged(null, EventArgs.Empty);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            try
            {
                strportName = wdini.ReadString("serial", "strportName", "Com1");
                strbaudRate = wdini.ReadString("serial", "strbaudRate", "9600");
                strStopBits = wdini.ReadString("serial", "strStopBits", "1");
                strDataBits = wdini.ReadString("serial", "strDataBits", "8");
                strjiaoyan= wdini.ReadString("serial", "strjiaoyan", "0");
                mdcan.port.PortName = strportName;
               mdcan.port.BaudRate = int.Parse(strbaudRate);
               mdcan.port.StopBits = (StopBits)int.Parse(strStopBits);
               mdcan.port.DataBits = int.Parse(strDataBits);
               if(strjiaoyan=="0")
                {
                    mdcan.port.Parity = Parity.None;
                }
               else if(strjiaoyan == "1")
                {
                    mdcan.port.Parity = Parity.Odd;
                }
                else if (strjiaoyan == "2")
                {
                    mdcan.port.Parity = Parity.Even;
                }

                mdcan.port.ReadTimeout = 500;
            }
            catch (Exception ex)
            {
                monitor = "错误：" + ex.Message;

            };
            tcpclientmo = new Thread(new ThreadStart(clientmonitor));
            tcpclientmo.IsBackground = true; // ch:防止主窗体关闭后进程驻留
            tcpclientmo.Start();
            monitor = "";
            textBox3.Text = wdini.ReadString("tcpserver", "port1", textBox3.Text);
            textBox4.Text = wdini.ReadString("tcpserver", "ip1", textBox4.Text);
            textBox5.Text = wdini.ReadString("tcpclient", "port2", textBox5.Text);
            textBox6.Text = wdini.ReadString("tcpclient", "ip2", textBox6.Text);
            textBox2.Text = wdini.ReadString("tcpsend", "send1", textBox2.Text);
            decimal devalue;
            // ch:R46 Form3 的 Modbus TCP 已删除，串口 Modbus RTU 参数改存 [serial]：
            //   style/gongnengma/xie/du/xieyi 先读新键，缺失再回退旧 [modbustcp] 键（现场升级零迁移）；
            //   保存只写 [serial]，不再碰 FormModbus 使用的 [modbustcp] ip/port 等键。
            decimal.TryParse(ReadSerialSetting("xie", "xie", "0"), out devalue);
            numericUpDown18.Value = Math.Max(numericUpDown18.Minimum, Math.Min(numericUpDown18.Maximum, devalue)); // ch:R43 P2-4 夹取范围，防 ini 越界值让整个 Load 中断
            decimal.TryParse(ReadSerialSetting("du", "du", "0"), out devalue);
            numericUpDown1.Value = Math.Max(numericUpDown1.Minimum, Math.Min(numericUpDown1.Maximum, devalue));
            // ch:R13-3 删除 out0..out3 的 ok/ng 装载与字段：textBox10..17 的真实语义是方案表 path/1..8，
            //   原装载在其后追加执行会用历史 out* 段值覆盖方案路径（现场 ini 若残留 OK/NG 串→切方案必 File.Exists 失败）。
            comboBox3.Text = ReadSerialSetting("style", "style", "float");
            comboBox4.Text = ReadSerialSetting("gongnengma", "gongnengma", "06");
            // 旧处理器写的是 "modbus_RTU"（下划线），设计器选项是 "modbus-RTU"（连字符）；
            //   升级时统一归一化，避免旧值被当成"无协议"而误开文本接收。
            string serialProtocol = ReadSerialSetting("xieyi", "serial", "无协议");
            comboBox5.Text = serialProtocol.Replace("_", "-").Equals("modbus-RTU", StringComparison.OrdinalIgnoreCase)
                ? "modbus-RTU" : "无协议";
            serial_xieyi = comboBox5.Text;
            if (wdini.ReadString("jinzhi", "16en", "true") == "true")
            {
                jinzhi = 1;
                checkBox1.CheckState = CheckState.Checked;

            }
            else
                checkBox1.CheckState = CheckState.Unchecked;
            if (wdini.ReadString("modbus", "qufan", "true") == "true")
            {
                modbus_qufan = 1;
                checkBox6.CheckState = CheckState.Checked;

            }
            else
            {
                modbus_qufan = 0;
                checkBox6.CheckState = CheckState.Unchecked;
            }
            if (wdini.ReadString("tcpclient", "en", "false") == "true")
            {
                checkBox3.CheckState = CheckState.Checked;
            }
            else
                checkBox3.CheckState = CheckState.Unchecked;
            if (wdini.ReadString("tcpserver", "en", "false") == "true")
            {
                checkBox2.CheckState = CheckState.Checked;
            }
            else
                checkBox2.CheckState = CheckState.Unchecked;
            if (wdini.ReadString("serial", "en", "false") == "true")
            {
                checkBox4.CheckState = CheckState.Checked;
            }
            else
                checkBox4.CheckState = CheckState.Unchecked;
            oks = 0;
            ngs = 0;
            oksmonitor = 0;
            ngsmonitor = 0;
            // Form1 frm1 = (Form1)this.Owner;
            //  frm1.changedata_event+= new Form1.changedata(DataChange);
            groupBox1.Enabled = false;
            groupBox2.Enabled = false;
            this.label5.Text = "端口号：端口未打开|";
            this.label6.Text = "波特率：端口未打开|";
            this.label7.Text = "数据位：端口未打开|";
            this.label8.Text = "停止位：端口未打开|";
            this.label9.Text = "校验: 端口未打开";
            ValidateLoadTriggerUniqueness(false, _schemes.GetRows()); // ch:R43 装载期只记日志，不弹窗阻塞启动；ch:R12 按方案表全量校验
            Task.Run(() =>
            {
                chushihua();
            });
        }
        void chushihua()
        {
            Thread.Sleep(50);
            if (checkBox4.CheckState == CheckState.Checked)
            {
                if (strDataBits != "" && strportName != "" && strbaudRate != "" && strStopBits != "")
                {
                    try
                    {
                        Thread.Sleep(300);
                       mdcan.port.Open();
                        Thread.Sleep(300);
                        if (!mdcan.port.IsOpen)
                           mdcan.port.Open();
                        button6.Text = "关闭串口";
                        groupBox1.Enabled = true;
                        groupBox2.Enabled = true;
                        this.label5.Text = "端口号：" +mdcan.port.PortName + "|";
                        this.label6.Text = "波特率：" +mdcan.port.BaudRate + "|";
                        this.label7.Text = "数据位：" +mdcan.port.DataBits + "|";
                        this.label8.Text = "停止位：" +mdcan.port.StopBits + "|";
                        this.label9.Text = "校验:" + mdcan.port.Parity;
                        if (serial_xieyi == "无协议")
                            btnReceive_Click(null,null);
                    }
                    catch (Exception ex)
                    {
                        monitor = "错误：" + ex.Message;

                    };
                }
                else
                    monitor = "请先设置串口!" + "通讯";
            }
            if (checkBox2.CheckState == CheckState.Checked)
            {
                int listenPort = 0;
                if (!int.TryParse(textBox3.Text.Trim(), out listenPort) || listenPort <= 0)
                {
                    // ch:未配置有效监听端口时不自动启动 TCP 服务器，避免无谓绑定/报错
                    MsgErroeLog.WriteLog("TCP服务器未配置有效端口，跳过自动监听");
                }
                else
                {
                    try
                    {
                        IPAddress ip = IPAddress.Any;
                        //IPAddress ip = IPAddress.Parse("192.168.88.1");
                        // ch:重新监听前先关闭上一次的 watch socket，避免句柄泄漏
                        try { if (socketWatch != null) { socketWatch.Close(); socketWatch = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                        socketWatch = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        IPEndPoint point = new IPEndPoint(ip, listenPort);
                        socketWatch.Bind(point);
                        //监听
                        ShowMsg("监听成功");
                        socketWatch.Listen(10);
                        Thread td = new Thread(Listen);
                        td.IsBackground = true;
                        td.Start(socketWatch);
                        //等待客户端连接
                    }
                    catch (Exception ex)
                    {
                        // ch:绑定/监听失败时关闭 watch socket，避免句柄泄漏
                        try { if (socketWatch != null) { socketWatch.Close(); socketWatch = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                        MsgErroeLog.WriteLog("异常:" + ex.Message);
                    }
                }
            }
            if (checkBox3.CheckState == CheckState.Checked)
            {
                IPAddress ipaddr;
                if (!IPAddress.TryParse(textBox6.Text.Trim(), out ipaddr))
                {
                    // ch:未配置有效服务器 IP 时不自动连接，避免反复连接失败
                    MsgErroeLog.WriteLog("TCP客户端未配置有效服务器IP，跳过自动连接");
                }
                else
                {
                    try
                    {
                        socketClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        IPEndPoint point = new IPEndPoint(ipaddr, Convert.ToInt32(textBox5.Text));
                        //获得要连接的远程IP和端口号
                        socketClient.Connect(point);
                        ShowMsgClient(socketClient.RemoteEndPoint + "连接成功,我是客户机");
                        //开启一个线程，不断的接收服务端发来的消息
                        Thread th = new Thread(receiveClient);
                        th.IsBackground = true;
                        th.Start();
                    }
                    catch (Exception ex) { monitor = "Tcpclient连接失败"; LogThrottled(ref _lastCliConnErrLogMs, "TCP客户端连接失败:" + ex.Message); } // ch:R10-7 补限流日志
                }
            }
        }
        // ch:R14 重连线程退出标志：clientmonitor 原为 while(true)，
        //   进程退出阶段(Form1 已 closing=true、正在关 socket/冲刷日志)它们仍在发请求/自动重连，
        //   会与清理竞争、刷出退出期异常。由 Form1_FormClosing 经 RequestExit() 置位后停手。
        //   注意只在真正退出时调用：窗体被「隐藏但不销毁」(本窗体 FormClosing 里 e.Cancel=true)不置位，
        //   否则用户下次再打开配置窗时重连线程已经停了、功能失效。
        private volatile bool _exiting = false;

        // ch:R14 进程退出时停掉本窗体的重连线程(不 Join —— 它们是后台线程，最多再睡完当前一觉)
        public void RequestExit()
        {
            _exiting = true;
        }

        void DataChange(string data)
        {
            textBox9.Text = data;
        }
        void clientmonitor()
        {
            while (!_exiting) // ch:R14 原 while(true)，退出期仍会按 checkBox3 自动重连 socket
            {
                Thread.Sleep(500);
                if (checkBox3.CheckState == CheckState.Checked)
                {
                    try
                    {
                        if (monitor.Contains("Tcp"))
                        {
                            Socket sock = CreateAndConnectClientSocket();
                            ShowMsgClient(sock.RemoteEndPoint + "连接成功,我是客户机");
                            //开启一个线程，不断的接收服务端发来的消息
                            Thread th = new Thread(receiveClient);
                            th.IsBackground = true;
                            th.Start();
                            monitor = "";
                        }
                    }
                    catch (Exception ex)
                    {
                        monitor = "Tcpclient连接失败";
                        LogThrottled(ref _lastCliConnErrLogMs, "TCP客户端连接失败:" + ex.Message); // ch:R10-7 补限流日志
                        // ch:释放未连接成功的 socket，避免句柄泄漏
                        try { if (socketClient != null) { socketClient.Close(); socketClient = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                    }
                }
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    try
                    {
                        if (strDataBits != "" && strportName != "" && strbaudRate != "" && strStopBits != "")
                        {
                            try
                            {
                                if (!mdcan.port.IsOpen) // ch:P1-③ 避免 500ms 循环重复 Open 已打开串口而靠异常吞噬维持
                                    mdcan.port.Open();
                                this.BeginInvoke(new Action(() => // ch:P1-③ 跨线程写控件必须封送
                                {
                                    button6.Text = "关闭串口";
                                    groupBox1.Enabled = true;
                                    groupBox2.Enabled = true;
                                    this.label5.Text = "端口号：" + mdcan.port.PortName + "|";
                                    this.label6.Text = "波特率：" + mdcan.port.BaudRate + "|";
                                    this.label7.Text = "数据位：" + mdcan.port.DataBits + "|";
                                    this.label8.Text = "停止位：" + mdcan.port.StopBits + "|";
                                    this.label9.Text = "校验:" + mdcan.port.Parity;
                                }));
                                monitor = "";
                            }
                            catch (Exception ex)
                            {
                                monitor = "错误：" + ex.Message;

                            }
                        }
                        else
                            monitor = "请先设置串口!" + "RS232串口通讯";
                    }
                    catch (Exception ex) { monitor = "串口连接失败"; LogThrottled(ref _lastSerialConnErrLogMs, "串口连接失败:" + ex.Message); } // ch:R10-7 补限流日志
                }
            }
        }
        private void button2_Click(object sender, EventArgs e)
        {
            try
            {
                Socket sock = CreateAndConnectClientSocket();
                ShowMsgClient(sock.RemoteEndPoint + "连接成功,我是客户机");
                //开启一个线程，不断的接收服务端发来的消息
                Thread th = new Thread(receiveClient);
                th.IsBackground = true;
                th.Start();
            }
            catch {
                monitor = "Tcpclient连接失败";
            }
        }
        // ch:P1-⑦ 客户端 socket 生命周期互斥：重连/退出/接收线程并发时避免误关新连接
        private readonly object _clientSocketLock = new object();
        // ch:R40 H4 服务端发送短临界区：Accept/NG 帧的内层发送任务与手动发送（三处 target.Send）可能并发到同一 socket
        private readonly object _serverSendLock = new object();

        // ch:R10-7 限流日志：同一失败点 5 秒内只记一条，避免重连/轮询循环刷屏
        private int _lastCliConnErrLogMs = 0;
        private int _lastSerialConnErrLogMs = 0;
        private void LogThrottled(ref int lastMs, string msg)
        {
            int nowMs = Environment.TickCount;
            if (unchecked(nowMs - lastMs) >= 5000)
            {
                lastMs = nowMs;
                MsgErroeLog.WriteLog(msg);
            }
        }

        // ch:P1-⑦ 统一创建+连接客户端 socket，并原子替换旧 socket（旧连接随后关闭）。
        //   连接失败时释放本次新建的 socket，避免句柄泄漏；旧 socket 直接 Close，避免覆盖导致泄漏。
        private Socket CreateAndConnectClientSocket()
        {
            Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IPAddress ip = IPAddress.Parse(textBox6.Text.Trim());
                IPEndPoint point = new IPEndPoint(ip, Convert.ToInt32(textBox5.Text));
                sock.Connect(point);
                sock.SendTimeout = 3000; // ch:R11-3 发送路径持 _clientSocketLock，无超时时长塞满的发送缓冲会长期卡锁(重连/接收线程收尾一起堵)
                EnableTcpKeepAlive(sock); // ch:P2 半开检测：30s 探活，PLC 掉电可及时触发重连
            }
            catch
            {
                try { sock.Close(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                throw;
            }
            Socket old;
            lock (_clientSocketLock)
            {
                old = socketClient;
                socketClient = sock;
            }
            if (old != null)
            {
                try { old.Close(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
            }
            return sock;
        }

        // ch:P2 显式 GBK 解码：原用 Encoding.Default 依赖系统 ANSI 代码页（中文机=936，英文机=1252 → 中文乱码）。
        //   注意：TCP 无组帧定界，若一条报文的 GBK 多字节被拆到两个 TCP 段，本段解码会出现 U+FFFD；
        //   该告警只提示一次，便于现场判断是否需要按分隔符组帧（需先确认现场报文分隔约定）。
        private static readonly System.Text.Encoding _protoEncoding = System.Text.Encoding.GetEncoding("GB2312");
        private static bool _decodeWarned = false;

        // ch:P2 半开检测：开启 TCP KeepAlive 并把探活压到 30s（系统默认 2 小时）。
        //   现场"PLC 掉电/网线拔掉"时对端不发明文 RST，Receive 会一直阻塞且不报错（半开），
        //   上位机要等 2 小时才走重连；30s 探活后内核判定对端不可达 → Receive 抛错 → 触发重连。
        //   对健康连接零影响（探活包由对端内核应答）。
        private static void EnableTcpKeepAlive(Socket sock)
        {
            if (sock == null) return;
            try
            {
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // SIO_KEEPALIVE_VALS(0x98000004)：开关=1、空闲 30000ms、间隔 5000ms
                byte[] inVals = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(inVals, 0);
                BitConverter.GetBytes(30000u).CopyTo(inVals, 4);
                BitConverter.GetBytes(5000u).CopyTo(inVals, 8);
                sock.IOControl(unchecked((int)0x98000004), inVals, null);
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        // ch:P2 粘包/拆包可观测性：现场协议是"N 条精确匹配的触发串"（ch:R13-2 起含方案表全部载图串），无分隔符/无长度前缀，
        //   因此报文被 TCP 合并（如 "A1B1"）或拆开时，qiehuan 与后续 DataChange 都匹配不上 → 静默无动作，
        //   这是最难排查的失败模式。这里只在"段内包含某条已配置触发串、但整段不等于它"时记一条限流日志
        //   （正常报文不触发），用于现场判断是否需要与对端约定组帧。
        private int _lastFrameAnomalyLogMs = 0;
        private void NoteProtoFrameAnomaly(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            string[] cfg = _schemes.KeysArray(); // ch:R13-2 改用方案表快照（R12 扩容后 9..100 行也纳入诊断；不再读控件）
            string hit = null;
            foreach (string t in cfg)
            {
                if (!string.IsNullOrEmpty(t) && s.Length > t.Length && s.Contains(t)) { hit = t; break; }
            }
            if (hit == null) return;
            int now = Environment.TickCount;
            if (unchecked(now - _lastFrameAnomalyLogMs) < 5000) return;
            _lastFrameAnomalyLogMs = now;
            MsgErroeLog.WriteLog("无协议报文疑似粘包/拆包：整段含已配置触发串[" + hit + "]但不等于它，整段=["
                + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0") + "] 长度=" + s.Length
                + "；若频繁出现请与对端确认是否分段/合并发送或需要约定组帧");
        }
        // ch:R42 收包组帧用触发串集合：方案表全部载图串（ch:R13-2 由 8 条扩到 ≤100 条，取 SchemeStore 快照，
        //   不再读控件） + Form1 经 framePatternsProvider 提供的各相机方案 triggerZifu（vpp 输入，Form3 拿不到）。
        //   载图串部分按快照数组引用缓存字节编码：仅改表时重建，100 行时不再每次 Receive 重编 100 个 GBK 串。
        //   ch:R13-4 缓存打包成单一不可变对象后一次发布：客户端/服务端多个接收线程并发重建时，
        //   读侧要么看到完整的旧缓存、要么看到完整的新缓存，不会出现"新 keys+旧 bytes"或 null 缓存。
        public Func<string[]> framePatternsProvider;
        private sealed class FrameCache
        {
            public readonly string[] Keys;
            public readonly List<byte[]> Bytes;
            public readonly HashSet<string> Seen;
            public FrameCache(string[] keys, List<byte[]> bytes, HashSet<string> seen)
            {
                Keys = keys; Bytes = bytes; Seen = seen;
            }
        }
        private volatile FrameCache _frameCache;
        private int _frameOverflowLoggedMs = 0;
        private List<byte[]> BuildFramePatterns()
        {
            string[] own = _schemes.KeysArray();
            FrameCache cache = _frameCache;
            if (cache == null || !ReferenceEquals(own, cache.Keys))
            {
                List<byte[]> built = new List<byte[]>();
                HashSet<string> seenB = new HashSet<string>();
                for (int i = 0; i < own.Length; i++) AddFramePattern(built, seenB, own[i]);
                cache = new FrameCache(own, built, seenB);
                _frameCache = cache; // ch:R13-4 单次引用发布
            }
            List<byte[]> list = new List<byte[]>(cache.Bytes);
            HashSet<string> seen = new HashSet<string>(cache.Seen);
            try
            {
                Func<string[]> ext = framePatternsProvider;
                if (ext != null)
                {
                    string[] more = ext();
                    if (more != null)
                        foreach (string t in more) AddFramePattern(list, seen, t);
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            return list;
        }
        private void AddFramePattern(List<byte[]> list, HashSet<string> seen, string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            // ch:R13-4 先去重再判上限：满额时重复串不能再误报"超上限"（原顺序会记录假告警）
            if (seen.Contains(t)) return;
            if (list.Count >= ProtoFrameAssembler.MaxPatterns)
            {
                // ch:R13-2 原静默丢弃：108 条(100 方案+8 相机)内不会触发，一旦配置异常超限要留线索而不是无声少挂一条
                int now = Environment.TickCount;
                if (unchecked(now - _frameOverflowLoggedMs) >= 5000)
                {
                    _frameOverflowLoggedMs = now;
                    MsgErroeLog.WriteLog("组帧触发串集合已达上限 " + ProtoFrameAssembler.MaxPatterns + "，多余串未加入组帧（配置异常，请检查方案表/触发串数量）");
                }
                return;
            }
            seen.Add(t);
            list.Add(_protoEncoding.GetBytes(t));
        }
        private string DecodeProtoBytes(byte[] buf, int len)
        {
            string s = _protoEncoding.GetString(buf, 0, len);
            if (!_decodeWarned && s.IndexOf('\uFFFD') >= 0)
            {
                _decodeWarned = true;
                MsgErroeLog.WriteLog("无协议报文解码出现替换字符(U+FFFD)：GBK 多字节可能被 TCP 分段拆开，或对端非 GBK 编码（仅提示一次）");
            }
            return s;
        }

        private void receiveClient()
        {
            Socket sock = socketClient; // ch:P1-⑦ 捕获本次会话 socket，避免重连覆盖字段后 Receive 到新连接
            if (sock == null)
                return;
            byte[] buffer = new byte[1024 * 1024 * 3]; // ch:P1-② 提到循环外复用，避免高频消息下大对象堆(LOH)反复分配
            ProtoFrameAssembler asm = new ProtoFrameAssembler(); // ch:R42 本连接独立组帧缓冲
            try
            {
                while (true)
                {
                    try
                    {
                        int r = sock.Receive(buffer);
                        if (r == 0)
                        { break; }
                        // ch:R42 TCP 触发串组帧：按已知触发串集合（Form3 载图串 ∪ Form1 方案 triggerZifu）做字节级组帧，
                        //   被拆开/合并的触发串还原为完整帧后再走既有 qiehuan/getData 路径；非触发字节仍按残留段原样处理
                        List<byte[]> frames = asm.Append(buffer, r, BuildFramePatterns());
                        foreach (byte[] fb in frames)
                        {
                            string sss = DecodeProtoBytes(fb, fb.Length); // ch:P2 显式 GBK 解码（原 Encoding.Default 依赖系统代码页）
                            if (jinzhi == 1)
                                sss = sss.Replace('\0', '0');
                            ShowMsgClient(sock.RemoteEndPoint + ":" + sss + "\r\n");
                            if (qiehuan_fangshi == "Tcp_client")
                            {
                                if (qiehuan(sss) == 0)
                                {
                                    NoteProtoFrameAnomaly(sss); // ch:P2 未匹配时诊断是否被 TCP 粘包/拆包（限流，正常报文不记）
                                    SelectionChangedEventArgs E = new SelectionChangedEventArgs(sss);
                                    getData(this, E);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("TCP客户端接收断开:" + ex.Message);
                        monitor = "Tcpclient连接失败"; // ch:置位触发 clientmonitor 自动重连
                        break;
                    }
                }
            }
            finally
            {
                asm.Reset(); // ch:R42 连接结束丢弃半截组帧缓冲
                // ch:P1-⑦ 线程退出时只关闭本次会话 socket；字段仍指向自己时才置空，避免误关重连后的新连接
                try { sock.Close(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                lock (_clientSocketLock)
                {
                    if (ReferenceEquals(socketClient, sock))
                        socketClient = null;
                }
            }
        }
        // ch:主程序退出时调用：关闭串口/TCP 连接与接收线程，保证对 PLC/上位机优雅断开
        public void CloseResources()
        {
            try { bAccpet = false; } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            _serialRxAccumulator.Reset(); // ch:v1.3.53 关闭时丢弃半截累积帧
            try { if (mdcan != null && mdcan.port != null && mdcan.port.IsOpen) mdcan.port.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            // ch:R11-3 关闭/置空与 Send/重连同入 _clientSocketLock：原不加锁会在 Send 持锁进行中并发置空，违背本轮"串行化"不变量
            try { lock (_clientSocketLock) { if (socketClient != null) { socketClient.Close(); socketClient = null; } } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
            try { if (socketWatch != null) { socketWatch.Close(); socketWatch = null; } } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            // ch:清理 TCP 服务器已接入的客户端 socket（serverSocket 字典），避免残留句柄
            try
            {
                // ch:P1-⑥ 用 ToArray() 快照枚举，避免并发 Add/Remove 时枚举抛 "集合已修改" 异常
                foreach (Socket s in serverSocket.Values.ToArray())
                {
                    try { if (s != null) s.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
                serverSocket.Clear();
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }
        private void ShowMsgClient(string str)
        {
            // ch:TCP 客户端接收线程调用，跨线程时转 UI 线程
            if (this.InvokeRequired)
                this.BeginInvoke(new Action(() => textBox7.AppendText(str + "\r\n")));
            else
                textBox7.AppendText(str + "\r\n");
        }

        private void button4_Click(object sender, EventArgs e)
        {
            try
            {
                string str = textBox8.Text;
                byte[] buffer;
                buffer = null;
                if (jinzhi==1)
                {
                    //string sHex = textBox8.Text.Replace(" ", "");
                    //if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex.Length / 2];
                    //    for (int i = 0; i < sHex.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    buffer = vbyte;
                    //}
                    int indata;
                    byte[] tempdata;
                    int.TryParse(textBox8.Text, out indata);
                    Getint(indata, out tempdata);
                    buffer = tempdata;
                }
                else
                    buffer = System.Text.Encoding.Default.GetBytes(str);
                List<byte> list = new List<byte>();
                //list.Add(0);
                list.AddRange(buffer);
                //将泛型集合转换为数组
                byte[] newBuffer = list.ToArray();
                // ch:R10-6 Socket.Send 非线程安全：判空+发送同入 _clientSocketLock，避免与重连置空/并发发送交叉
                lock (_clientSocketLock)
                {
                    if (socketClient == null)
                        throw new Exception("TCP 客户端未连接，发送取消");
                    socketClient.Send(newBuffer);
                }
            }
            catch (Exception ex)
            { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }
        Socket socketWatch;
        Socket socketServer;
        // ch:P1-⑥ 原 Dictionary 在监听线程(Add/Remove)、UI 发送线程(读/Remove)、CloseResources(Clear) 并发下会重复键异常/内部结构损坏。
        //   改用 ConcurrentDictionary，所有单步操作线程安全。
        ConcurrentDictionary<string, Socket> serverSocket = new ConcurrentDictionary<string, Socket>();
        private void button1_Click(object sender, EventArgs e)
        {
            try
            {
                IPAddress ip = IPAddress.Any;
                //IPAddress ip = IPAddress.Parse("192.168.88.1");
                // ch:重新监听前先关闭上一次的 watch socket，避免句柄泄漏
                try { if (socketWatch != null) { socketWatch.Close(); socketWatch = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                socketWatch = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IPEndPoint point = new IPEndPoint(ip, Convert.ToInt32(textBox3.Text));
                socketWatch.Bind(point);
                //监听
                ShowMsg("监听成功");
                socketWatch.Listen(10);
                Thread td = new Thread(Listen);
                td.IsBackground = true;
                td.Start(socketWatch);
                //等待客户端连接
            }
            catch (Exception ex)
            {
                // ch:绑定/监听失败时关闭 watch socket，避免句柄泄漏
                try { if (socketWatch != null) { socketWatch.Close(); socketWatch = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                MsgErroeLog.WriteLog("异常:" + ex.Message);
            }
        }
        private void ShowMsg(string str)
        {
            // ch:TCP 服务器接收线程调用，跨线程时转 UI 线程
            if (this.InvokeRequired)
                this.BeginInvoke(new Action(() => textBox1.AppendText(str + "\r\n")));
            else
                textBox1.AppendText(str + "\r\n");
        }
        private void Listen(object o)
        {
            Socket socketWatch = o as Socket;
            while (true)
            {
                try
                {
                    if (socketWatch == null)
                        break; // ch:监听套接字为空（已被关闭/释放）时结束监听线程，避免空引用忙循环
                    socketServer = socketWatch.Accept();
                    EnableTcpKeepAlive(socketServer); // ch:P2 半开检测：接入的连接同样 30s 探活
                    // ch:R12 服务端接入的 socket 同样设发送超时：原只有客户端 sock 设了 SendTimeout，
                    //   服务端 target.Send(Form3.cs:1049/1837/2003) 在对端不读时会无限阻塞发送线程
                    try { socketServer.SendTimeout = 3000; } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                    string remoteTemp = socketServer.RemoteEndPoint.ToString();
                    // ch:P1-⑥ 同客户端重连：先关旧连接，再用索引器原子替换（ConcurrentDictionary 索引器 = 新增或覆盖，无重复键异常）
                    Socket old = null;
                    if (serverSocket.TryGetValue(remoteTemp, out old))
                    {
                        try { if (old != null) old.Close(); } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
                    }
                    serverSocket[remoteTemp] = socketServer;
                    //将远程连接的IP地址和端口号填入下拉菜单
                    if (this.InvokeRequired)
                        this.BeginInvoke(new Action(() =>
                        {
                            comboBox1.Items.Add(remoteTemp);
                            comboBox1.Text = remoteTemp;
                        }));
                    else
                    {
                        comboBox1.Items.Add(remoteTemp);
                        comboBox1.Text = remoteTemp;
                    }
                    ShowMsg(remoteTemp + "连接成功，我是服务器");
                    Thread th1 = new Thread(Receive);
                    th1.IsBackground = true;
                    th1.Start(socketServer);
                }
                catch (Exception ex)
                {
                    MsgErroeLog.WriteLog("TCP监听异常:" + ex.Message);
                    // ch:R43 P1-8 仅在监听套接字已被替换/关闭时退出；瞬时 SocketException 记录后退避继续监听，
                    //   原实现对任意 SocketException 直接 break，一次网络抖动后服务器永久不再接受连接
                    if (ex is ObjectDisposedException) break;
                    if (socketWatch == null || socketWatch != this.socketWatch) break;
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }
        string rcv1;
        private void Receive(object o)
        {
            // ch:P1-08 每连接独立缓冲/长度：原 bufferr/rr 是窗体共享字段，多客户端并发 Receive 时互相覆盖未解码数据
            byte[] bufferr = new byte[1024 * 1024 * 5];
            int rr;
            ProtoFrameAssembler asm = new ProtoFrameAssembler(); // ch:R42 本连接独立组帧缓冲
            Socket current = o as Socket;
            socketServer = current;
            string remoteKey = "";
            try { if (current != null) remoteKey = current.RemoteEndPoint.ToString(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            try
            {
                while (true)
                {
                    try
                    {
                        //连接成功后，接受客户端发过来的消息
                        //实际接收到的有效字节数
                        rr = current.Receive(bufferr);
                        if (rr == 0)
                        { break; }
                        // ch:R42 TCP 触发串组帧：同客户端路径（字节级组帧后再走既有 qiehuan/getData）
                        List<byte[]> frames = asm.Append(bufferr, rr, BuildFramePatterns());
                        foreach (byte[] fb in frames)
                        {
                            string str = DecodeProtoBytes(fb, fb.Length); // ch:P2 显式 GBK 解码（原 Encoding.Default 依赖系统代码页）
                            if (jinzhi == 1)
                                str = str.Replace('\0', '0');
                            ShowMsg(current.RemoteEndPoint + ";" + str + "\r\n");
                            rcv1 = str;
                            if (qiehuan_fangshi == "Tcp_server")
                            {
                                if (qiehuan(str) == 0)
                                {
                                    NoteProtoFrameAnomaly(str); // ch:P2 未匹配时诊断是否被 TCP 粘包/拆包（限流，正常报文不记）
                                    SelectionChangedEventArgs E = new SelectionChangedEventArgs(str);
                                    getData(this, E);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog("TCP服务器接收异常:" + ex.Message);
                        Thread.Sleep(200);
                        break; // ch:连接断开退出循环，避免忙转
                    }
                }
            }
            finally
            {
                asm.Reset(); // ch:R42 连接结束丢弃半截组帧缓冲
                // ch:客户端断开后关闭 socket 并从字典移除，
                // ch:否则后续 serverSocket[ip].Send 对已释放对象抛 ObjectDisposedException
                try { if (current != null) current.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                // ch:P1-⑥ 断开后从字典移除（ConcurrentDictionary.TryRemove 线程安全）
                try { if (remoteKey != "") serverSocket.TryRemove(remoteKey, out _); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
        }
        private int qiehuan(string aa)
        {
            // ch:R43 载图串冲突收口：同一触发串命中多路时拒绝切换（原实现总是第一路胜出，静默切错方案）
            // ch:R12 匹配走 SchemeStore 快照（≤100 行）：接收线程不再逐控件跨线程取 Text；空串不参与、多命中冲突拒绝。
            // ch:R12 匹配走 SchemeStore（≤100 行）；ch:R13-1 改 TryResolve 一次取配对快照，接收线程不再逐控件跨线程取 Text
            int hits;
            string schemePath;
            if (_schemes.TryResolve(aa, out schemePath, out hits))
            {
                zifu = aa;
                lujing = schemePath;
                return 1;
            }
            if (hits > 1)
                LogTriggerConflictThrottled("载图串[" + aa + "]命中" + hits + "路，已拒绝切换（请检查配置重复）");
            return 0;
        }
        private int _lastTriggerConflictLogMs = 0;
        private void LogTriggerConflictThrottled(string msg)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastTriggerConflictLogMs) < 5000)
                return;
            _lastTriggerConflictLogMs = now;
            MsgErroeLog.WriteLog(msg);
        }
        // ch:R12 方案表数据层（≤100 行，qiehuan 读其快照）。Form3 无 TabControl：面板放模态弹窗，
        //   入口按钮在代码里挂到"保存设置"按钮下方，不动 Designer/resx。
        private SchemeStore _schemes = new SchemeStore();
        private SchemeTablePanel _schemePanel;
        private Button _btnSchemeTable;
        private void InitSchemeUi()
        {
            if (_schemePanel == null)
            {
                TextBox[] zifu = { textBox18, textBox19, textBox21, textBox20, textBox23, textBox22, textBox25, textBox24 };
                TextBox[] path = { textBox10, textBox11, textBox12, textBox13, textBox14, textBox15, textBox16, textBox17 };
                _schemePanel = new SchemeTablePanel(_schemes, zifu, path);
                // ch:R14 旧载图串/路径两列并入方案表后隐藏（面板仍以其为镜像，逻辑零改动）
                textBox18.Visible = textBox19.Visible = textBox21.Visible = textBox20.Visible = false;
                textBox23.Visible = textBox22.Visible = textBox25.Visible = textBox24.Visible = false;
                textBox10.Visible = textBox11.Visible = textBox12.Visible = textBox13.Visible = false;
                textBox14.Visible = textBox15.Visible = textBox16.Visible = textBox17.Visible = false;
                label15.Visible = label16.Visible = label14.Visible = false;
                _schemePanel.SaveRequested += delegate { button5_Click(this, EventArgs.Empty); }; // ch:R14-2 就地保存=原"保存设置"逻辑
                _btnSchemeTable = new Button();
                _btnSchemeTable.Text = "方案表(≤100)";
                _btnSchemeTable.Width = 100;
                _btnSchemeTable.Height = 24;
                _btnSchemeTable.Location = new Point(button5.Location.X, button5.Location.Y + button5.Height + 6);
                _btnSchemeTable.Anchor = button5.Anchor;
                _btnSchemeTable.Click += BtnSchemeTable_Click;
                button5.Parent.Controls.Add(_btnSchemeTable);
            }
            else
            {
                _schemePanel.SyncAllFromStore();
            }
        }
        private void BtnSchemeTable_Click(object sender, EventArgs e)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "方案表（最多 100 行；前 8 行与界面文本框双向同步）";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(680, 460);
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                _schemePanel.Dock = DockStyle.Fill;
                dlg.Controls.Add(_schemePanel);
                dlg.FormClosed += delegate { if (_schemePanel != null) _schemePanel.Parent = null; };
                dlg.ShowDialog(this);
                _schemePanel.SyncAllFromStore();
            }
        }
        // ch:R43 载图串唯一性校验：重复时无法确定切哪个方案，保存直接拒绝。
        //   另检查与各相机方案 triggerZifu 的交叉冲突（可能是有意的“切方案+采图”，仅提示）。
        // ch:R12 校验对象从固定 8 文本框改为传入的方案表全量行（≤100），重复检测用 SchemeStore.FindDuplicates。
        private bool ValidateLoadTriggerUniqueness(bool showUi, List<string[]> rows)
        {
            string dup = SchemeStore.FindDuplicates(rows);
            if (dup != null)
            {
                string msg = "载图触发串重复，无法确定切换目标：" + dup;
                MsgErroeLog.WriteLog(msg);
                if (showUi)
                    MessageBox.Show(msg + "\r\n\r\n请修改重复的载图串后再保存。", "载图串冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            // ch:v1.3.54 P1 前缀冲突：无分隔符协议下 "1"/"12"、"相机1"/"相机10" 这类前缀关系在分包时会误切，
            //   运行期靠 SerialFrameAccumulator settle 等待兜底，但配置层尽早拒绝更稳。
            string[] extraTriggers = null;
            try
            {
                Func<string[]> ext = framePatternsProvider;
                if (ext != null) extraTriggers = ext();
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            string prefix = SchemeStore.FindPrefixConflicts(rows, extraTriggers);
            if (prefix != null)
            {
                string msg = "载图串存在前缀关系，分包时会误切方案：" + prefix;
                MsgErroeLog.WriteLog(msg);
                if (showUi)
                    MessageBox.Show(msg + "\r\n\r\n无分隔符协议下前缀串无法区分（如 \"1\" 与 \"12\"），请改用互不为前缀的载图串后再保存。", "载图串前缀冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            // ch:v1.3.55 P3 载图串字节长度上限：SerialFrameAccumulator 累积上限 MaxFrame=1024 字节，
            //   超过该长度的载图串在运行期永不可能凑齐 → 静默不触发（现场最难查的一类"配了没反应"）。保存层尽早拒绝。
            for (int i = 0; i < rows.Count; i++)
            {
                string k = rows[i] == null ? null : rows[i][0];
                if (string.IsNullOrEmpty(k)) continue;
                if (_protoEncoding.GetByteCount(k) > SerialFrameAccumulator.MaxFrame)
                {
                    string msg = "载图串过长（第" + (i + 1) + "行 [" + k.Substring(0, Math.Min(20, k.Length)) + "…] GB2312 字节数超过串口帧上限 "
                        + SerialFrameAccumulator.MaxFrame + "），运行期永远凑不齐、不会触发。";
                    MsgErroeLog.WriteLog(msg);
                    if (showUi)
                        MessageBox.Show(msg + "\r\n\r\n请缩短该载图串后再保存。", "载图串超长", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            for (int i = 0; i < rows.Count; i++)
            {
                string k = rows[i] == null ? null : rows[i][0];
                if (!string.IsNullOrEmpty(k) && !seen.ContainsKey(k))
                    seen[k] = true;
            }
            List<string> cross = new List<string>();
            try
            {
                string[] more = extraTriggers;
                if (more != null)
                {
                    foreach (string t in more)
                    {
                        if (!string.IsNullOrEmpty(t) && seen.ContainsKey(t))
                        {
                            string m = "载图串[" + t + "]与相机方案 triggerZifu 相同：同一帧只会切方案并跳过 getData，相机不会触发";
                            cross.Add(m);
                            LogTriggerConflictThrottled(m);
                        }
                    }
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            if (cross.Count > 0)
            {
                string msg = "载图串与相机触发串冲突：" + string.Join("；", cross.ToArray());
                MsgErroeLog.WriteLog(msg);
                if (showUi)
                    MessageBox.Show(msg + "\r\n\r\n请修改冲突的载图串或相机触发串后再保存。", "触发串冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }
        private void button3_Click(object sender, EventArgs e)
        {
            try
            {
                string str = textBox2.Text;
                byte[] buffer;
                buffer = null;
                if (jinzhi==1)
                {
                    //string sHex = textBox2.Text.Replace(" ", "");
                    //if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex.Length / 2];
                    //    for (int i = 0; i < sHex.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    buffer = vbyte;
                    //}
                    int indata;
                    byte[] tempdata;
                    int.TryParse(textBox2.Text, out indata);
                    Getint(indata, out tempdata);
                    buffer = tempdata;
                }
                else
                {
                    // byte[] buffer=Convert.ToByte(StringToHexOrDec(str));
                    buffer = System.Text.Encoding.Default.GetBytes(str);
                }
                string ip = comboBox1.SelectedItem.ToString();
                // ch:发送前校验目标连接，断开时清理字典，避免向已释放 socket 发送抛 ObjectDisposedException
                // ch:P1-⑥ 用 TryGetValue 取连接（ConcurrentDictionary 索引器在键缺失时会抛异常），失败或断开则关闭并移除
                Socket target;
                if (serverSocket.TryGetValue(ip, out target))
                {
                    if (target != null && target.Connected)
                    {
                        lock (_serverSendLock) { target.Send(buffer); } // ch:R40 H4 短临界区（服务端 socket 并发发送串行化）
                    }
                    else
                    {
                        try { if (target != null) target.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                        serverSocket.TryRemove(ip, out _);
                        MsgErroeLog.WriteLog("TCP服务器发送目标已断开并移除:" + ip);
                    }
                }
                //  soketSend.Send(buffer);
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void btnSetSp_Click(object sender, EventArgs e)
        {
            try
            {
                timer1.Enabled = false;
               mdcan.port.Close();
                Form4 frm4 = new Form4();
                if (frm4.ShowDialog() == DialogResult.OK)
                {
                   mdcan.port.PortName = strportName;
                   mdcan.port.BaudRate = int.Parse(strbaudRate);
                   mdcan.port.StopBits = (StopBits)int.Parse(strStopBits);
                   mdcan.port.DataBits = int.Parse(strDataBits);
                    if (strjiaoyan == "0")
                    {
                        mdcan.port.Parity = Parity.None;
                    }
                    else if (strjiaoyan == "1")
                    {
                        mdcan.port.Parity = Parity.Odd;
                    }
                    else if (strjiaoyan == "2")
                    {
                        mdcan.port.Parity = Parity.Even;
                    }
                    mdcan.port.ReadTimeout = 500;
                }
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            string str1;
            str1 = sRead.ReadLine();
            if (str1 != null)
            {
                timer1.Stop();
                sRead.Close();
                MessageBox.Show("发送文件成功！", "c#串口通讯");
              //this.label9.Text = "";
                return;
            }
            byte[] data = Encoding.Default.GetBytes(str1);
           lock (mdcan.PortLock) { mdcan.port.Write(data, 0, data.Length); } // ch:R40 H4 串口短临界区（与检测线程数据路写同锁串行）
          //  this.label9.Text = "数据发送中······";
        }

        // ch:v1.3.53 P3-4 删除死代码 timer2_Tick：原无 PortLock/判空的 mdcan.port.ReadExisting() 会与 AcceptData 抢字节；
        //   timer2 从未被启用（仅一处 Enabled=false），保留只会诱导后人启用后绕过 PortLock。串口接收统一走 AcceptData。

        private void btnclear_Click(object sender, EventArgs e)
        {
            try
            {
                string path = Directory.GetCurrentDirectory() + @"\output.txt";
                string content = this.txtReceive.Text;
                FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
                StreamWriter write = new StreamWriter(fs);
                write.Write(content);
                write.Flush();
                write.Close();
                fs.Close();
                MessageBox.Show("接收数据在：" + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            if (button6.Text == "打开串口")
            {
                if (strDataBits != "" && strportName != "" && strbaudRate != "" && strStopBits != "")
                {
                    try
                    {
                        if (mdcan.port.IsOpen)
                        {
                           mdcan.port.Close();
                           mdcan.port.Open();
                        }
                        else
                           mdcan.port.Open();
                        button6.Text = "关闭串口";
                        groupBox1.Enabled = true;
                        groupBox2.Enabled = true;
                        this.label5.Text = "端口号：" +mdcan.port.PortName + "|";
                        this.label6.Text = "波特率：" +mdcan.port.BaudRate + "|";
                        this.label7.Text = "数据位：" +mdcan.port.DataBits + "|";
                        this.label8.Text = "停止位：" +mdcan.port.StopBits + "|";
                        this.label9.Text = "校验:" + mdcan.port.Parity;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("错误：" + ex.Message, "c#串口通讯");

                    }
                }
                else
                    MessageBox.Show("请先设置串口!", "RS232串口通讯");
            }
            else
            {
                timer1.Enabled = false;
                button6.Text = "打开串口";
                if (mdcan.port.IsOpen)
                   mdcan.port.Close();
                groupBox1.Enabled = false;
                groupBox2.Enabled = false;
                this.label5.Text = "端口号：端口未打开|";
                this.label6.Text = "波特率：端口未打开|";
                this.label7.Text = "数据位：端口未打开|";
                this.label8.Text = "停止位：端口未打开|";
                this.label9.Text = "校验: 端口未打开";
            }
        }

        private void btnSendData_Click(object sender, EventArgs e)
        {
            if (mdcan.port.IsOpen)
            {
                try
                {
                    string send = "";
                   // byte[] vbyte = null;
                   mdcan.port.Encoding = System.Text.Encoding.GetEncoding("GB2312");
                    if (jinzhi==1)
                    {
                        //string sHex = texSend.Text.Replace(" ", "");
                        //if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                        //{
                        //    vbyte = new byte[sHex.Length / 2];
                        //    for (int i = 0; i < sHex.Length; i = i + 2)
                        //    {
                        //        if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                        //            vbyte[i / 2] = 0;
                        //    }
                        //    send = ASCIIEncoding.Default.GetString(vbyte);
                        //   mdcan.port.Write(vbyte, 0, 8);

                        //}
                        int indata;
                        byte[] tempdata;
                        int.TryParse(texSend.Text, out indata);
                        Getint(indata, out tempdata);
                        lock (mdcan.PortLock) { mdcan.port.Write(tempdata, 0, tempdata.Length); } // ch:R40 H4 串口短临界区
                    }
                    else
                    {
                        send = texSend.Text;
                       lock (mdcan.PortLock) { mdcan.port.Write(send); } // ch:R40 H4 串口短临界区
                    }

                }
                catch (Exception ex)
                {
                    MessageBox.Show("错误", ex.Message);
                }
            }
            else
            {
                MessageBox.Show("请先打开串口！");
            }
        }

        private void btnOpenFile_Click(object sender, EventArgs e)
        {
            OpenFileDialog oFD = new OpenFileDialog();
            oFD.InitialDirectory = "C\\";
            oFD.RestoreDirectory = true;
            oFD.FilterIndex = 1;
            oFD.Filter = "txt文件(*.txt)|*.txt";
            if (oFD.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    if (oFD.OpenFile() != null)
                        txtFileName.Text = oFD.FileName;
                }
                catch (Exception err1)
                {
                    MessageBox.Show("文件打开错误！" + err1.Message, "提示信息", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void btnSendFile_Click(object sender, EventArgs e)
        {
            //string filename = txtFileName.Text.Trim();
            //if (filename == "")
            //{
            //    MessageBox.Show("请选择要发送的文件！", "Error");
            //    return;
            //}
            //else
            //{
            //    sRead = new StreamReader(filename, Encoding.Default);
            //}
            //timer1.Start();
        }

        private void btnReceive_Click(object sender, EventArgs e)
        {
            if (btnReceive.Text == "接收数据")
            {
               mdcan.port.Encoding = Encoding.GetEncoding("GB2312");
                if (mdcan.port.IsOpen)
                {
                    // ch:上一次的接收线程尚未退出时不允许重开，避免新旧两个接收线程同时跑（去 Abort 后的保护）
                    if (getRecevice != null && getRecevice.IsAlive)
                    {
                        MessageBox.Show("接收线程正在停止，请稍候再试");
                        return;
                    }
                    bAccpet = true;
                    _serialRxAccumulator.Reset(); // ch:v1.3.53 新接收会话从空累积开始
                    getRecevice = new Thread(new ThreadStart(testdelegate));
                    getRecevice.IsBackground = true; // ch:防止主程序退出后进程残留
                    getRecevice.Start();
                    btnReceive.Text = "停止接收";
                }
                else
                    MessageBox.Show("请打开串口!");
            }
            else
            {
                // ch:置位退出标志，接收线程本轮结束后自然退出；不再使用已废弃的 Thread.Abort（易致句柄未释放/状态不一致）
                bAccpet = false;
                _serialRxAccumulator.Reset(); // ch:v1.3.54 P2-1 停止接收清累积；排队中的 AcceptData 由入口 !bAccpet 守卫直接退出，不再追加
                // ch:v1.3.55 P2-2 不在 UI 线程 Join(1000)：接收线程此刻可能正阻塞在 this.Invoke(AcceptData) 上，
                //   UI 线程一旦占住消息泵去 Join，那个 Invoke 就永远等不到服务 → 直到 Join 超时(1s)界面才恢复（可感知卡顿）。
                //   置位后立即返回让消息泵继续跑，排队的 Invoke/入口 !bAccpet 早退守卫会让线程尽快自然退出；
                //   重开路径(见上文 IsAlive 守卫)已禁止旧线程未退出时新建，故无需同步等待，仅后台兜底记录"迟迟未退出"。
                Thread receiver = getRecevice;
                if (receiver != null && receiver.IsAlive)
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            if (!receiver.Join(3000))
                                MsgErroeLog.WriteLog("接收线程未在 3s 内退出（UI 已恢复，不影响停止），将继续后台自行结束");
                        }
                        catch (Exception ex) { MsgErroeLog.WriteLog("接收线程 Join 异常:" + ex.Message); }
                    });
                }
                btnReceive.Text = "接收数据";
            }
        }
        private void testdelegate()
        {
            reaction r = new reaction(fun);
            r();
        }
        delegate void DelegateAcceptData();
        delegate void reaction();
        void fun()
        {
            while (bAccpet)
            {
                AcceptData();
                try { Thread.Sleep(10); } catch { } // ch:R43 P2-8 原无间隔忙循环会与 UI 互相驱动打满 CPU
            }
        }
        private int _lastSerialErrLogMs = 0; // ch:R10-7 串口接收异常限流日志（5s 一条），原空 catch 使故障完全不可见
        void AcceptData()
        {
            // ch:v1.3.54 P2-1 入口先判 bAccpet：停止/关闭时若本委托已在 UI 队列排队，Join 返回后仍会执行——
            //   原实现此时会重新 Read 追加到刚清空的 pending 并派发多一帧。这里在停止后直接清空退出。
            if (!bAccpet)
            {
                _serialRxAccumulator.Reset();
                return;
            }
            if (txtReceive.InvokeRequired)
            {
                try
                {
                    DelegateAcceptData ddd = new DelegateAcceptData(AcceptData);
                    this.Invoke(ddd, new object[] { });
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
            else
            {
                try
                {
                    int readCount = 0;
                    // ch:R43 P1-8 串口读与 Modbus 事务共用 PortLock：原无锁读会偷走 PLC 响应字节
                    lock (mdcan.PortLock)
                    {
                        if (mdcan.port != null && mdcan.port.IsOpen)
                        {
                            int available = mdcan.port.BytesToRead;
                            if (available > 0)
                                readCount = mdcan.port.Read(_serialReadBuffer, 0, Math.Min(available, _serialReadBuffer.Length));
                        }
                    }
                    ProcessSerialBytes(readCount);
                }
                catch (Exception ex)
                {
                    int nowMs = Environment.TickCount;
                    if (unchecked(nowMs - _lastSerialErrLogMs) >= 5000)
                    {
                        _lastSerialErrLogMs = nowMs;
                        MsgErroeLog.WriteLog("串口接收异常:" + ex.Message);
                    }
                }
            }
        }

        // ch:v1.3.54 帧累积逻辑抽到 SerialFrameAccumulator；此处仅取模式表(方案串∪相机触发串)并派发产出的完整帧。
        //   readCount=0 且 pending 空时直接返回，避免空闲每 10ms 无谓重建模式表（P3 优化）。
        private void ProcessSerialBytes(int readCount)
        {
            if (readCount <= 0 && _serialRxAccumulator.PendingCount == 0)
                return;
            List<byte[]> patterns = BuildFramePatterns(); // 方案载图串 ∪ 各相机 triggerZifu（GB2312 字节）
            List<byte[]> frames = _serialRxAccumulator.Append(readCount, _serialReadBuffer, patterns);
            for (int f = 0; f < frames.Count; f++)
                DispatchSerialFrame(frames[f]);
        }

        private void DispatchSerialFrame(byte[] frame)
        {
            string strReceive = DecodeProtoBytes(frame, frame.Length);
            if (jinzhi == 1)
                strReceive = strReceive.Replace('\0', '0');
            txtReceive.AppendText(strReceive);
            // ch:v1.3.54 P3 纯 CR/LF/空白/空帧只作显示，不派发 getData：否则设备以 CR/LF 作帧尾时，
            //   触发串被消费后残留的 "\r\n" 会覆盖 8 路 block 的 jieshou 输入（Form1 DataChange 写 jieshou）。
            if (qiehuan_fangshi == "Serial" && !IsWhitespaceOnly(strReceive))
            {
                if (qiehuan(strReceive) == 0)
                {
                    SelectionChangedEventArgs E = new SelectionChangedEventArgs(strReceive);
                    getData(this, E);
                }
            }
        }

        private static bool IsWhitespaceOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\r' && c != '\n' && c != '\0' && c != ' ' && c != '\t') return false;
            }
            return true;
        }

        private void btn_Ok_Click(object sender, EventArgs e)
        {
            if (getData != null)
            {
                SelectionChangedEventArgs E = new SelectionChangedEventArgs(textBox9.Text);
                getData(this, E);
            }
        }
        public class SelectionChangedEventArgs : EventArgs
        {

            private string m_selection;



            //本属性用于传递事件数据

            public string Selection
            {

                get { return m_selection; }

            }
            public SelectionChangedEventArgs(string selection)
            {

                m_selection = selection;

            }
        }
        private void textBox2_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (checkBox1.CheckState == CheckState.Checked)
            {
                //e.Handled = e.KeyChar < '0' || e.KeyChar > '9';  //允许输入数字
                e.Handled = !((e.KeyChar >= '0' && e.KeyChar <= '9') || (e.KeyChar >= 'a' && e.KeyChar <= 'f') || (e.KeyChar >= 'A' && e.KeyChar <= 'F') || (e.KeyChar == ' '));
                if (e.KeyChar == (char)8)  //允许输入回退键
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'x')  //允许输入‘x’
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'X')  //允许输入'X'
                {
                    e.Handled = false;
                }
            }
        }

        private void textBox8_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (checkBox1.CheckState == CheckState.Checked)
            {
                //e.Handled = e.KeyChar < '0' || e.KeyChar > '9';  //允许输入数字
                e.Handled = !((e.KeyChar >= '0' && e.KeyChar <= '9') || (e.KeyChar >= 'a' && e.KeyChar <= 'f') || (e.KeyChar >= 'A' && e.KeyChar <= 'F') || (e.KeyChar == ' '));
                if (e.KeyChar == (char)8)  //允许输入回退键
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'x')  //允许输入‘x’
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'X')  //允许输入'X'
                {
                    e.Handled = false;
                }
            }
        }
        public static int StringToHexOrDec(string strData)
        {
            int dData = -1;
            try
            {
                if ((strData.Length > 2))
                {
                    if ((strData.Substring(0, 2).Equals("0x")) || (strData.Substring(0, 2).Equals("0X")))
                    {
                        string str_sub = strData.Substring(2, strData.Length - 2);
                        dData = int.Parse(str_sub, System.Globalization.NumberStyles.HexNumber);
                    }
                    else
                    {
                        dData = int.Parse(strData, System.Globalization.NumberStyles.Integer);
                    }
                }
                else
                {
                    dData = int.Parse(strData, System.Globalization.NumberStyles.Integer);
                }
            }
            catch (Exception)
            {
                //MessageBox.Show("输入错误: " + strData, "错误");
            }
            return dData;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
                if (checkBox1.CheckState == CheckState.Checked)
                {
                    jinzhi = 1;
                    textBox2.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox2.Text)).Replace("-", " ");
                    textBox8.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox8.Text)).Replace("-", " ");
                    texSend.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(texSend.Text)).Replace("-", " ");
                    //textBox10.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox10.Text)).Replace("-", " ");
                    //textBox11.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox11.Text)).Replace("-", " ");
                    //textBox12.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox12.Text)).Replace("-", " ");
                    //textBox13.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox13.Text)).Replace("-", " ");
                    //textBox14.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox14.Text)).Replace("-", " ");
                    //textBox15.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox15.Text)).Replace("-", " ");
                    //textBox16.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox16.Text)).Replace("-", " ");
                    //textBox17.Text = BitConverter.ToString(ASCIIEncoding.Default.GetBytes(textBox17.Text)).Replace("-", " ");
                }
                else
                {
                    jinzhi = 0;
                    string sHex = textBox2.Text.Replace(" ", "");
                    if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                    {
                        byte[] vbyte = new byte[sHex.Length / 2];
                        for (int i = 0; i < sHex.Length; i = i + 2)
                        {
                            if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                                vbyte[i / 2] = 0;
                        }
                        textBox2.Text = ASCIIEncoding.Default.GetString(vbyte);
                    }
                    string sHex1 = textBox8.Text.Replace(" ", "");
                    if (sHex1.Length > 0 && (sHex1.Length % 2 == 0))
                    {
                        byte[] vbyte = new byte[sHex1.Length / 2];
                        for (int i = 0; i < sHex1.Length; i = i + 2)
                        {
                            if (!byte.TryParse(sHex1.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                                vbyte[i / 2] = 0;
                        }
                        textBox8.Text = ASCIIEncoding.Default.GetString(vbyte);
                    }
                    string sHex2 = texSend.Text.Replace(" ", "");
                    if (sHex2.Length > 0 && (sHex2.Length % 2 == 0))
                    {
                        byte[] vbyte = new byte[sHex2.Length / 2];
                        for (int i = 0; i < sHex2.Length; i = i + 2)
                        {
                            if (!byte.TryParse(sHex2.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                                vbyte[i / 2] = 0;
                        }
                        texSend.Text = ASCIIEncoding.Default.GetString(vbyte);
                    }
                    //string sHex3 = textBox10.Text.Replace(" ", "");
                    //if (sHex3.Length > 0 && (sHex3.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex3.Length / 2];
                    //    for (int i = 0; i < sHex3.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex3.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox10.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex4 = textBox11.Text.Replace(" ", "");
                    //if (sHex4.Length > 0 && (sHex4.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex4.Length / 2];
                    //    for (int i = 0; i < sHex4.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex4.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox11.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex5 = textBox12.Text.Replace(" ", "");
                    //if (sHex5.Length > 0 && (sHex5.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex5.Length / 2];
                    //    for (int i = 0; i < sHex5.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex5.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox12.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex6 = textBox13.Text.Replace(" ", "");
                    //if (sHex6.Length > 0 && (sHex6.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex6.Length / 2];
                    //    for (int i = 0; i < sHex6.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex6.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox13.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex7 = textBox14.Text.Replace(" ", "");
                    //if (sHex7.Length > 0 && (sHex7.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex7.Length / 2];
                    //    for (int i = 0; i < sHex7.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex7.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox14.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex8 = textBox15.Text.Replace(" ", "");
                    //if (sHex8.Length > 0 && (sHex8.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex8.Length / 2];
                    //    for (int i = 0; i < sHex8.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex8.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox15.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex9 = textBox16.Text.Replace(" ", "");
                    //if (sHex9.Length > 0 && (sHex9.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex9.Length / 2];
                    //    for (int i = 0; i < sHex9.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex9.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox16.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                    //string sHex10 = textBox17.Text.Replace(" ", "");
                    //if (sHex10.Length > 0 && (sHex10.Length % 2 == 0))
                    //{
                    //    byte[] vbyte = new byte[sHex10.Length / 2];
                    //    for (int i = 0; i < sHex10.Length; i = i + 2)
                    //    {
                    //        if (!byte.TryParse(sHex10.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                    //            vbyte[i / 2] = 0;
                    //    }
                    //    textBox17.Text = ASCIIEncoding.Default.GetString(vbyte);
                    //}
                }
        }

        private void texSend_TextChanged(object sender, EventArgs e)
        {

        }

        private void texSend_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (checkBox1.CheckState == CheckState.Checked)
            {
                //e.Handled = e.KeyChar < '0' || e.KeyChar > '9';  //允许输入数字
                e.Handled = !((e.KeyChar >= '0' && e.KeyChar <= '9') || (e.KeyChar >= 'a' && e.KeyChar <= 'f') || (e.KeyChar >= 'A' && e.KeyChar <= 'F') || (e.KeyChar == ' '));
                if (e.KeyChar == (char)8)  //允许输入回退键
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'x')  //允许输入‘x’
                {
                    e.Handled = false;
                }
                if (e.KeyChar == 'X')  //允许输入'X'
                {
                    e.Handled = false;
                }
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {
            // ch:v1.3.49 P2-9 整体异常保护：ClassIni.WriteString 失败会抛 ApplicationException，
            //   原实现保存循环半途抛出 → 前几键已写、后面没写，重启后方案表回退旧状态且只弹裸异常框。
            try
            {
                SaveAllSettings();
            }
            catch (Exception ex)
            {
                // ch:v1.3.51 ⑥ 决策注释：不回滚 _schemes——store 与界面本就"编辑即时生效"（面板 TextChanged 已实时同步），
                //   回滚反而造成"界面显示 A、内存表 B"的三方不一致；ini 半写以"重新保存"收敛（下方提示已引导）。
                MsgErroeLog.WriteLog("串口/方案配置保存失败:" + ex);
                MessageBox.Show("配置未完整保存：" + ex.Message + "\r\n\r\n部分键可能已写入，请确认 test.ini 未被占用/只读后重新保存。",
                    "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveAllSettings()
        {
            // ch:R12 保存前先取方案表合并行（前 8 行=文本框，9..N=表格），重复/交叉冲突拒绝保存；
            //   先写 1..N 键，再写 count，避免半程崩溃时读到超出实际写入行的旧值。
            List<string[]> schemeRows = _schemePanel != null ? _schemePanel.GetNormalizedRows() : _schemes.GetRows();
            schemeRows = SchemeStore.TrimTrailingEmpty(schemeRows);
            if (!ValidateLoadTriggerUniqueness(true, schemeRows)) // ch:R43 重复载图串拒绝保存，避免运行期静默切错方案
                return;
            _schemes.SetRows(schemeRows);
            // ch:R46 Form3 已删除 ModbusTCP；串口 Modbus 参数统一写 [serial]（不再写 [modbustcp]，
            //   避免覆盖 FormModbus 的 ip/port/en 等配置）。旧 [modbustcp] 键保留不动，仅作升级回退来源。
            wdini.WriteString("serial", "style", comboBox3.Text);
            wdini.WriteString("serial", "gongnengma", comboBox4.Text);
            wdini.WriteString("serial", "xie", numericUpDown18.Value.ToString());
            wdini.WriteString("serial", "du", numericUpDown1.Value.ToString()); // ch:R43 P1-8 原误写成写地址 NUD18，重启后读地址被覆盖
            // 协议项只有「无协议 / modbus-RTU」两种，保存前归一化，避免手输的下划线/大小写写进 ini
            serial_xieyi = comboBox5.Text == "modbus-RTU" ? "modbus-RTU" : "无协议";
            wdini.WriteString("serial", "xieyi", serial_xieyi);
            for (int i = 0; i < schemeRows.Count; i++)
            {
                wdini.WriteString(SerialSchemeSection.Change, (i + 1).ToString(), schemeRows[i][0]);
                wdini.WriteString(SerialSchemeSection.Path, (i + 1).ToString(), schemeRows[i][1]);
            }
            wdini.WriteString(SerialSchemeSection.Change, "count", schemeRows.Count.ToString());
            wdini.WriteString(SerialSchemeSection.Change, "all", comboBox2.Text);
            wdini.WriteString("serial", "strportName", strportName);
            wdini.WriteString("serial", "strbaudRate", strbaudRate);
            wdini.WriteString("serial", "strStopBits", strStopBits);
            wdini.WriteString("serial", "strDataBits", strDataBits);
            wdini.WriteString("serial","strjiaoyan",strjiaoyan);
            // WritePrivateProfileString("Test", "id", "xym", "d://vc//Ex1//ex1.ini");
            wdini.WriteString("tcpserver", "port1", textBox3.Text);
            wdini.WriteString("tcpserver", "ip1", textBox4.Text);
            wdini.WriteString("tcpsend", "send1", textBox2.Text);
            wdini.WriteString("tcpclient", "port2", textBox5.Text);
            wdini.WriteString("tcpclient", "ip2", textBox6.Text);
            // ch:R13-3 原此处的 out0..out3 ok/ng 写回已删除（键无任何消费者，留着只会把方案路径抄进死键）
            if(checkBox1.CheckState==CheckState.Checked)
            wdini.WriteString("jinzhi", "16en", "true");
            else
            wdini.WriteString("jinzhi", "16en", "false");
            if (checkBox6.CheckState == CheckState.Checked)
                wdini.WriteString("modbus", "qufan", "true");
            else
                wdini.WriteString("modbus", "qufan", "false");
            if (checkBox3.CheckState == CheckState.Checked)
                wdini.WriteString("tcpclient", "en", "true");
            else
                wdini.WriteString("tcpclient", "en", "false");    
            if (checkBox2.CheckState == CheckState.Checked)
                wdini.WriteString("tcpserver", "en", "true");
            else
                wdini.WriteString("tcpserver", "en", "false");
            if (checkBox4.CheckState == CheckState.Checked)
                wdini.WriteString("serial", "en", "true");
            else
                wdini.WriteString("serial", "en", "false");

        }

     
      

        private void Form3_FormClosing_1(object sender, FormClosingEventArgs e)
        {
            if (MessageBox.Show("将要关闭检测，是否继续？", "询问", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                // Application.Exit();
                e.Cancel = true;
                this.Visible = false;
            }
            else
            {

                e.Cancel = true;

            }
        }
        private delegate void lttext1(string aa);
        private void settext1(string aa)
        {
            if (textBox9.InvokeRequired)
            {
                lttext1 l1 = settext1;
                textBox9.Invoke(l1, aa);
            }
            else
            {
                textBox9.Text = aa;
            }
        }
        private object locker_1 = new object();
        public void changeok(string aa)
        {
            lock (locker_1)
            {
                if (checkBox3.CheckState == CheckState.Checked)
                {
                    try
                    {

                        textBox8.Text = aa;
                        oks++;

                        Form1.RunAsyncTailStatic(() =>
                        {
                            try
                            {
                                string str = aa; // ch:R3 用不可变参数 aa，避免后台读回被后续提交覆盖的 textBox8（跨帧串值）
                                byte[] buffer;
                                buffer = null;
                                if (jinzhi == 1)
                                {
                                    int indata;
                                    byte[] tempdata;
                                    int.TryParse(aa, out indata);
                                    Getint(indata, out tempdata);
                                    buffer = tempdata;
                                }
                                else
                                    buffer = System.Text.Encoding.Default.GetBytes(str);
                                List<byte> list = new List<byte>();
                            //list.Add(0);
                            list.AddRange(buffer);
                            //将泛型集合转换为数组
                            byte[] newBuffer = list.ToArray();
                                // ch:R10-6 判空+发送同入 _clientSocketLock，与重连置空串行化
                                lock (_clientSocketLock)
                                {
                                    if (socketClient != null)
                                        socketClient.Send(newBuffer);
                                    else
                                        monitor = "Tcpclient,未连接发送合格取消"; // ch:R10-7 原 NRE 被静默吞掉，现显式置状态
                                }
                            }
                            catch(Exception ex)
                            {
                                // ch:R10-7 补日志，发送失败不再只剩状态串无线索
                                MsgErroeLog.WriteLog("TCP发送合格失败:" + ex.Message);
                                monitor = "Tcpclient,发送合格失败";


                            }
                        });
                    }
                    catch (Exception ex)
                    { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                try
                {
                    //settext1(aa);
                    textBox9.Text = "1";
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                if (checkBox2.CheckState == CheckState.Checked)
                {
                    try
                    {

                        textBox2.Text = aa;
                        oks++;

                        Form1.RunAsyncTailStatic(() =>
                        {
                            try
                            {
                                string str = aa; // ch:R3 用不可变参数 aa，避免后台读回被后续提交覆盖的 textBox2（跨帧串值）
                                byte[] buffer;
                                buffer = null;
                                if (checkBox1.CheckState == CheckState.Checked)
                                {
                                    int indata;
                                    byte[] tempdata;
                                    int.TryParse(aa, out indata);
                                    Getint(indata, out tempdata);
                                    buffer = tempdata;
                                }
                                else
                                {
                                // byte[] buffer=Convert.ToByte(StringToHexOrDec(str));
                                buffer = System.Text.Encoding.Default.GetBytes(str);
                                }

                                string ip = comboBox1.SelectedItem.ToString();
                                // ch:发送前校验目标连接，断开时清理字典，避免向已释放 socket 发送抛 ObjectDisposedException
                                if (serverSocket.ContainsKey(ip))
                                {
                                    Socket target = serverSocket[ip];
                                    if (target != null && target.Connected)
                                    {
                                        lock (_serverSendLock) { target.Send(buffer); } // ch:R40 H4 短临界区
                                    }
                                    else
                                    {
                                        try { if (target != null) target.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                        serverSocket.TryRemove(ip, out _); // ch:P0 ConcurrentDictionary 无 Remove，改 TryRemove
                                        MsgErroeLog.WriteLog("TCP服务器发送目标已断开并移除:" + ip);
                                    }
                                }

                            //  soketSend.Send(buffer);
                        }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                        });
                    }
                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                }
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    if (mdcan.port.IsOpen)
                    {
                        try
                        {
                            if (jinzhi==0)
                            {
                                string send = "";
                                byte[] vbyte = null;
                                mdcan.port.Encoding = System.Text.Encoding.GetEncoding("GB2312");
                                send = aa;
                                lock (mdcan.PortLock) { mdcan.port.Write(send); } // ch:R40 H4 串口短临界区
                            }
                            else
                            {
                                int indata;
                                byte[] tempdata;
                                int.TryParse(aa, out indata);
                                Getint(indata, out tempdata);
                                lock (mdcan.PortLock) { mdcan.port.Write(tempdata, 0, tempdata.Length); } // ch:R40 H4 串口短临界区
                            }

                        }
                        catch (Exception ex)
                        {
                            monitor = "串口,发送合格失败";
                        }
                    }
                    else
                    {
                        monitor = "请先打开串口！";
                    }
                }
            }
        }
        public void changeng(string aa)
        {
            // settext1(aa);
            if (checkBox3.CheckState == CheckState.Checked)
            {
                try
                {

                        textBox8.Text =aa;
                    oks++;
                   
                    Form1.RunAsyncTailStatic(() =>
                    {
                        if (monitor == "")
                        {
                            try
                            {
                                string str = textBox8.Text;
                                byte[] buffer;
                                buffer = null;
                                if (checkBox1.CheckState == CheckState.Checked)
                                {
                                    string sHex = textBox8.Text.Replace(" ", "");
                                    if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                                    {
                                        byte[] vbyte = new byte[sHex.Length / 2];
                                        for (int i = 0; i < sHex.Length; i = i + 2)
                                        {
                                            if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                                                vbyte[i / 2] = 0;
                                        }
                                        buffer = vbyte;
                                    }
                                }
                                else
                                    buffer = System.Text.Encoding.Default.GetBytes(str);
                                List<byte> list = new List<byte>();
                                //list.Add(0);
                                list.AddRange(buffer);
                                //将泛型集合转换为数组
                                byte[] newBuffer = list.ToArray();
                                // ch:R10-6 判空+发送同入 _clientSocketLock，与重连置空串行化
                                lock (_clientSocketLock)
                                {
                                    if (socketClient != null)
                                        socketClient.Send(newBuffer);
                                    else
                                        monitor = "Tcpclient,未连接发送不合格取消"; // ch:R10-7 原 NRE 被静默吞掉，现显式置状态
                                }
                            }
                            catch(Exception ex)
                            {
                                // ch:R10-7 补日志，发送失败不再只剩状态串无线索
                                MsgErroeLog.WriteLog("TCP发送不合格失败:" + ex.Message);
                                monitor = "Tcpclient,发送不合格失败";


                            }
                        }
                    });
                }
                catch (Exception ex)
                { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
            try
            {
                textBox9.Text = "0";
                // ngsmonitor++;
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            if (checkBox2.CheckState == CheckState.Checked)
            {
                try
                {

                        textBox2.Text = aa;
                    ngs++;
        

                    Form1.RunAsyncTailStatic(() =>
                    {
                        if (monitor == "")
                        {
                            try
                            {
                                string str = textBox2.Text;
                                byte[] buffer;
                                buffer = null;
                                if (checkBox1.CheckState == CheckState.Checked)
                                {
                                    string sHex = textBox2.Text.Replace(" ", "");
                                    if (sHex.Length > 0 && (sHex.Length % 2 == 0))
                                    {
                                        byte[] vbyte = new byte[sHex.Length / 2];
                                        for (int i = 0; i < sHex.Length; i = i + 2)
                                        {
                                            if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
                                                vbyte[i / 2] = 0;
                                        }
                                        buffer = vbyte;
                                    }
                                }
                                else
                                {
                                    // byte[] buffer=Convert.ToByte(StringToHexOrDec(str));
                                    buffer = System.Text.Encoding.Default.GetBytes(str);
                                }

                                string ip = comboBox1.SelectedItem.ToString();
                                // ch:发送前校验目标连接，断开时清理字典，避免向已释放 socket 发送抛 ObjectDisposedException
                                if (serverSocket.ContainsKey(ip))
                                {
                                    Socket target = serverSocket[ip];
                                    if (target != null && target.Connected)
                                    {
                                        lock (_serverSendLock) { target.Send(buffer); } // ch:R40 H4 短临界区
                                    }
                                    else
                                    {
                                        try { if (target != null) target.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                        serverSocket.TryRemove(ip, out _); // ch:P0 ConcurrentDictionary 无 Remove，改 TryRemove
                                        MsgErroeLog.WriteLog("TCP服务器发送目标已断开并移除:" + ip);
                                    }
                                }

                                //  soketSend.Send(buffer);
                            }
                            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                        }
                    });
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
            }
            if (checkBox4.CheckState == CheckState.Checked)
            {
                if (mdcan.port.IsOpen)
                {
                    try
                    {
                        string send = "";
                        byte[] vbyte = null;
                       mdcan.port.Encoding = System.Text.Encoding.GetEncoding("GB2312");
                        send = aa;
                       lock (mdcan.PortLock) { mdcan.port.Write(send); } // ch:R40 H4 串口短临界区
                    }
                    catch (Exception ex)
                    {
                        monitor = "串口,发送NG失败";
                    }
                }
                else
                {
                    monitor = "请先打开串口！";
                }
            }
        }
        private void textBox9_TextChanged_1(object sender, EventArgs e)
        {
      
            //try
            //{
                
            //    if (textBox9.Text == "0")
            //    {
            //        textBox2.Text = "A5 01 00 00 5A";
            //        ngs++;
            //        label10.Text = ngs.ToString();
            //    }
            //    if (textBox9.Text == "1")
            //    {
            //        textBox2.Text = "A5 01 00 01 5A";
            //        oks++;
            //        label11.Text = oks.ToString();
            //    }
            //      Task.Run(() =>
            //                           {
            //    try
            //    {
            //        string str = textBox2.Text;
            //        byte[] buffer;
            //        buffer = null;
            //        if (checkBox1.CheckState == CheckState.Checked)
            //        {
            //            string sHex = textBox2.Text.Replace(" ", "");
            //            if (sHex.Length > 0 && (sHex.Length % 2 == 0))
            //            {
            //                byte[] vbyte = new byte[sHex.Length / 2];
            //                for (int i = 0; i < sHex.Length; i = i + 2)
            //                {
            //                    if (!byte.TryParse(sHex.Substring(i, 2), NumberStyles.HexNumber, null, out vbyte[i / 2]))
            //                        vbyte[i / 2] = 0;
            //                }
            //                buffer = vbyte;
            //            }
            //        }
            //        else
            //        {
            //            // byte[] buffer=Convert.ToByte(StringToHexOrDec(str));
            //            buffer = System.Text.Encoding.Default.GetBytes(str);
            //        }
                   
            //        string ip = comboBox1.SelectedItem.ToString();
            //        serverSocket[ip].Send(buffer);
                                      
            //        //  soketSend.Send(buffer);
            //    }
            //    catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            //                           });
            //}
            //catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                                  
        }

        private void button7_Click(object sender, EventArgs e)
        {
            monitor = "";
        }

        private void timer3_Tick(object sender, EventArgs e)
        {
            try
            {
                if (monitor != "" && checkBox3.CheckState == CheckState.Checked)
                {
                    Socket sock = CreateAndConnectClientSocket();
                    ShowMsgClient(sock.RemoteEndPoint + "连接成功,我是客户机");
                    //开启一个线程，不断的接收服务端发来的消息
                    Thread th = new Thread(receiveClient);
                    th.IsBackground = true;
                    th.Start();
                    monitor = "";
                }
            }
            catch (Exception ex) { monitor = "Tcpclient连接失败"; LogThrottled(ref _lastCliConnErrLogMs, "TCP客户端连接失败(重连):" + ex.Message); } // ch:R10-7 补限流日志
        }

        private void textBox10_TextChanged(object sender, EventArgs e)
        {

        }

        private void textBox10_KeyPress(object sender, KeyPressEventArgs e)
        {
           
        }

        private void textBox11_KeyPress(object sender, KeyPressEventArgs e)
        {
            
        }

        private void textBox12_KeyPress(object sender, KeyPressEventArgs e)
        {
           
        }

        private void textBox13_KeyPress(object sender, KeyPressEventArgs e)
        {
            
        }

        private void textBox14_KeyPress(object sender, KeyPressEventArgs e)
        {
           
        }

        private void textBox15_KeyPress(object sender, KeyPressEventArgs e)
        {
            
        }

        private void textBox16_KeyPress(object sender, KeyPressEventArgs e)
        {
            
        }

        private void textBox17_KeyPress(object sender, KeyPressEventArgs e)
        {
           
        }

        private void textBox10_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox10.Text = openFileDialog.FileName;
            }
        }

        private void textBox11_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox11.Text = openFileDialog.FileName;
            }
        }

        private void textBox12_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox12.Text = openFileDialog.FileName;
            }
        }

        private void textBox13_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox13.Text = openFileDialog.FileName;
            }
        }

        private void textBox14_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox14.Text = openFileDialog.FileName;
            }
        }

        private void textBox15_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox15.Text = openFileDialog.FileName;
            }
        }

        private void textBox16_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox16.Text = openFileDialog.FileName;
            }
        }

        private void textBox17_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox17.Text = openFileDialog.FileName;
            }
        }

        private void numericUpDown18_ValueChanged(object sender, EventArgs e)
        {
            xie = numericUpDown18.Value.ToString();
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            du= numericUpDown1.Value.ToString();
        }

        public int Getint(int values,out byte [] data1)
        {
            int[] value = new int[] { values };
            var result = new byte[value.Length * sizeof(int)];
            Buffer.BlockCopy(value, 0, result, 0, result.Length);
            byte[] data;
            if (modbus_qufan==1)
            {
                data1 = new byte[4] { result[1], result[0], result[3], result[2] };
                data = new byte[4] { result[2], result[3], result[0], result[1] };
            }
            else
            {
                data1 = new byte[4] { result[3], result[2], result[1], result[0] };
                data = new byte[4] { result[0], result[1], result[2], result[3] };
            }
            value[0] = BitConverter.ToInt32(data, 0);
            return value[0];
        }
        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {
            if(comboBox3.Text=="float")
            {
                modbus_style = "float";
            }
            else if(comboBox3.Text=="int")
            {
                modbus_style = "int";
            }
        }

        private void button11_Click(object sender, EventArgs e)
        {
                   int indata;
                   ushort add_temp;
                   ushort.TryParse(xie,out add_temp);
                    byte[] tempdata;                   
                  string a="";                
                  int.TryParse(textBox28.Text, out indata);
                 Getint(indata, out tempdata);
                  if(gongnengma=="06")
                  mdcan.WriteRegister(1, add_temp, indata, out a);
                  else if(gongnengma=="10")
                   {
                mdcan.WriteRegisters(1, add_temp, tempdata, out a);
                   }
                  texSend.Text = a;
        }

        private void button10_Click(object sender, EventArgs e)
        {
            int indata = 4;
            if (gongnengma=="10")
            indata=4;
            else if(gongnengma=="06")
            {
                indata = 2;
            }
            ushort add_temp;
            string a,b;
            ushort.TryParse(du, out add_temp);
            byte[] temp_buff = new byte[5 + indata];
            textBox29.Text = mdcan.ReadRegister(1, add_temp, indata/2, out a,out b,temp_buff).ToString();
            texSend.Text = a;
            txtReceive.Text = b;
        }

        private void checkBox6_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox6.CheckState == CheckState.Checked)
            {
                modbus_qufan = 1;
            }
            else
                modbus_qufan = 0;
        }

        private void comboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox4.Text == "06")
            {
                gongnengma = "06";
            }
            else if (comboBox4.Text == "10")
            {
                gongnengma = "10";
            }
        }

        private void comboBox5_SelectedIndexChanged(object sender, EventArgs e)
        {
            // ch:R46 统一使用设计器里的 "modbus-RTU"（连字符）；旧配置的 "modbus_RTU" 在装载时已归一化。
            serial_xieyi = comboBox5.Text == "modbus-RTU" ? "modbus-RTU" : "无协议";
        }

        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox2.Text == "Serial")
            {
                qiehuan_fangshi = "Serial";
            }
            else if (comboBox2.Text == "Tcp_client")
            {
                qiehuan_fangshi = "Tcp_client";
            }
            else if (comboBox2.Text == "Tcp_server")
            {
                qiehuan_fangshi = "Tcp_server";
            }
            else
            {
                // ch:R46 "modbustcp" 已从 Form3 移除（统一由 FormModbus 负责），旧配置按空选择处理
                qiehuan_fangshi = "";
            }
        }
        private bool front = false;

        private void timer4_Tick(object sender, EventArgs e)
        {
            if (this.Visible == true && front == false)
            {
                front = true;
                this.BringToFront();
                this.TopMost = true;
            }
            if (this.Visible == false)
            {
                front = false;
            }
        }
    }
}
