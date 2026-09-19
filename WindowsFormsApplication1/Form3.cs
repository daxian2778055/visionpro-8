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
using HslCommunication.ModBus;
using HslCommunication;
using System.Diagnostics;

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
        Stopwatch timelunxun = new Stopwatch();
        Stopwatch timelunxun1 = new Stopwatch();
        public string gongnengma="06";
        public string serial_xieyi = "无协议";
        public string qiehuan_fangshi = "";
        private int lunxun = 0;
        private int xiangji = 1;
        string strReceive;
        bool bAccpet = false;
       // SerialPortmdcan.port = new SerialPort();
       public  Modbus mdcan = new Modbus();
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
        string out0ok;
        string out1ok;
        string out2ok;
        string out3ok;
        string out0ng;
        string out1ng;
        string out2ng;
        string out3ng;
        int jinzhi;
        public string zifu;
        public string lujing;
        public int jobsum=0;
        /// <summary>
        /// 初始化类
        /// </summary>
        public ModbusTcpNet busTcpClient = null;
        /// <summary>
        /// 监听状态
        /// </summary>
        public bool IsEnable = false;
        ErrorLog MsgErroeLog = new ErrorLog();
        public string xie = "0";
        public string du = "0";
        private void textBox3_TextChanged(object sender, EventArgs e)
        {

        }

        private void Form3_Load(object sender, EventArgs e)
        {
            try
            {
                mdcan.port= new SerialPort();
                textBox18.Text = wdini.ReadString("change", "1", "");
                textBox19.Text = wdini.ReadString("change", "2", "");
                textBox21.Text = wdini.ReadString("change", "3", "");
                textBox20.Text = wdini.ReadString("change", "4", "");
                textBox23.Text = wdini.ReadString("change", "5", "");
                textBox22.Text = wdini.ReadString("change", "6", "");
                textBox25.Text = wdini.ReadString("change", "7", "");
                textBox24.Text = wdini.ReadString("change", "8", "");
                textBox10.Text = wdini.ReadString("path", "1", "");
                textBox11.Text = wdini.ReadString("path", "2", "");
                textBox12.Text = wdini.ReadString("path", "3", "");
                textBox13.Text = wdini.ReadString("path", "4", "");
                textBox14.Text = wdini.ReadString("path", "5", "");
                textBox15.Text = wdini.ReadString("path", "6", "");
                textBox16.Text = wdini.ReadString("path", "7", "");
                textBox17.Text = wdini.ReadString("path", "8", "");
                textBox17.Text = wdini.ReadString("path", "8", "");
                comboBox2.Text = wdini.ReadString("change", "all", "");
                numericUpDown2.Value =decimal.Parse( wdini.ReadString("modbustcp", "lunxun","0"));
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
            decimal.TryParse(wdini.ReadString("modbustcp", "xie", "0"), out devalue);
            numericUpDown18.Value = devalue;
            decimal.TryParse(wdini.ReadString("modbustcp", "du", "0"), out devalue);
            numericUpDown1.Value = devalue;
            txtIp.Text = wdini.ReadString("modbustcp", "ip", txtIp.Text);
            txtPort.Text = wdini.ReadString("modbustcp", "port", txtPort.Text);
            textBox10.Text = wdini.ReadString("out0", "ok", textBox10.Text);
            textBox11.Text = wdini.ReadString("out1", "ok", textBox11.Text);
            textBox12.Text = wdini.ReadString("out2", "ok", textBox12.Text);
            textBox13.Text = wdini.ReadString("out3", "ok", textBox13.Text);
            textBox14.Text = wdini.ReadString("out0", "ng", textBox14.Text);
            textBox15.Text = wdini.ReadString("out1", "ng", textBox15.Text);
            textBox16.Text = wdini.ReadString("out2", "ng", textBox16.Text);
            textBox17.Text = wdini.ReadString("out3", "ng", textBox17.Text);
            out0ok = textBox10.Text;
            out1ok = textBox11.Text;
            out2ok = textBox12.Text;
            out3ok = textBox13.Text;
            out0ng = textBox14.Text;
            out1ng = textBox15.Text;
            out2ng = textBox16.Text;
            out3ng = textBox17.Text;
            comboBox3.Text = wdini.ReadString("modbustcp", "style","float").Replace("\0", "");
            comboBox4.Text = wdini.ReadString("modbustcp", "gongnengma", "06").Replace("\0", "");
            comboBox5.Text = wdini.ReadString("modbustcp", "serial", "无协议").Replace("\0", "");
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
            if (wdini.ReadString("modbustcp", "en", "false") == "true")
            {
                checkBox5.CheckState = CheckState.Checked;
            }
            else
                checkBox5.CheckState = CheckState.Unchecked;
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
                        if (wdini.ReadString("modbustcp", "serial", "无协议").Replace("\0", "") == "无协议")
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
                    catch { monitor = "Tcpclient连接失败"; }
                }
            }
            if(checkBox5.CheckState == CheckState.Checked)
            {
                if (string.IsNullOrWhiteSpace(txtIp.Text) || string.IsNullOrWhiteSpace(txtPort.Text))
                {
                    // ch:未配置 ModbusTCP 服务器地址时不自动连接，避免无谓的重试与 Socket 释放竞争
                    MsgErroeLog.WriteLog("ModbusTCP 未配置 IP/端口，跳过自动连接");
                }
                else
                {
                    button21_Click(null, null);
                    for (int i = 0; i < 5; i++)
                    {
                        if (IsEnable == false)
                        {
                            Thread.Sleep(5000);
                            if (IsEnable == false)

                            {
                                button21_Click(null, null);
                            }
                        }
                    }
                    td1 = new Thread(new ThreadStart(lunxun_monitor));
                    td1.IsBackground = true;
                    td1.Start();
                }
            }
        }
        bool tongxunzhong = false;
        bool tongxunzhong1 = false;
        public void modbus_duxie(string address,ref object value,int duxie)
        {
            
            //lock (locker_2)
           // {
               tongxunzhong = true;       
                if (duxie == 1)
                {
                    float f1, f2 = 0;
                    int i1, i2 = 0;
                    short s1, s2 = 0;
                    timelunxun1.Restart();
                    if (value is float)
                    {
                        f1 = (float)value;
                        busTcpClient.Write(address, f1);
                    }
                    else if (value is int)
                    {
                        i1 = (int)value;
                        busTcpClient.Write(address, i1);
                    }
                    else if (value is short)
                    {
                    s1 = (short)value;
                    busTcpClient.Write(address, s1);
                }
                if (timelunxun1.ElapsedMilliseconds > 400)
                {
                    MsgErroeLog.WriteLog("modbustcp轮询写监控超时");
                }
            }
                else
                {
                    float f1, f2 = 0;
                    int i1, i2 = 0;
                    short s1, s2 = 0;
                    for (int i = 0; i < jobsum; i++)
                    {
                        if (value is float)
                        {
                            f1 = Getfloat(busTcpClient.ReadFloat((int.Parse(du)+(i*2)).ToString()).Content);
                            value = f1;
                            if (xiangji == i + 1)
                            {
                                f2 = f1;
                            }
                        }
                        else if (value is int)
                        {
                            byte[] tempdata;
                            i1 = Getint(busTcpClient.ReadInt32((int.Parse(du) + (i * 2)).ToString()).Content, out tempdata);
                            value = i1;
                            if (xiangji == i + 1)
                            {
                                i2 = i1;
                            }
                        }
                        else if (value is short)
                        {
                            s1 = busTcpClient.ReadInt16((int.Parse(du) + i).ToString()).Content;
                            value = s1;
                            if (xiangji == i + 1)
                            {
                                s2 = s1;
                            }
                        }
                        
                        if (qiehuan_fangshi == "modbustcp" && (value.ToString() != "0"))
                        {
                            if (value is float)
                            {
                                f1 = 0;
                                busTcpClient.Write((int.Parse(du) + (i * 2)).ToString(), f1);
                            }
                            else if (value is int)
                            {
                                i1 = 0;
                                busTcpClient.Write((int.Parse(du) + (i * 2)).ToString(), i1);
                            }
                            else if (value is short)
                            {
                                s1 = 0;
                                busTcpClient.Write((int.Parse(du) +i).ToString(), s1);
                            }
                            if (qiehuan(value.ToString()) == 0)
                            {
                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(value.ToString());
                                getData(this, E);
                            }
                        }
                    }
                    if (value is float)
                    {
                        value = f2;
                    }
                    else if (value is int)
                    {
                        value = i2;
                    }
                    else if (value is short)
                    {
                        value = s2;
                    }
                }
                tongxunzhong = false;
           // }
           
        }
        private void lunxun_monitor()
        {
            while(true)
            {
                if (IsEnable && lunxun != 0&& tongxunzhong == false)
                {
                    Thread.Sleep(lunxun);
                    timelunxun.Restart();
                    button9_Click(null, null);
                   if( timelunxun.ElapsedMilliseconds>400)
                    {
                        MsgErroeLog.WriteLog("modbustcp轮询读监控超时"+ timelunxun.ElapsedMilliseconds);
                        if(timelunxun.ElapsedMilliseconds>1000)
                        Thread.Sleep(2000); // ch:P1-④ 退避封顶 2s，避免通讯抖动后节拍恢复要等 10 秒
                    }
                }
                else
                    Thread.Sleep(20);
            }
        }
        void DataChange(string data)
        {
            textBox9.Text = data;
        }
        void clientmonitor()
        {
            while (true)
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
                    catch
                    {
                        monitor = "Tcpclient连接失败";
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
                    catch { monitor = "串口连接失败"; }
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
            try
            {
                while (true)
                {
                    try
                    {
                        int r = sock.Receive(buffer);
                        if (r == 0)
                        { break; }
                        string sss = DecodeProtoBytes(buffer, r); // ch:P2 显式 GBK 解码（原 Encoding.Default 依赖系统代码页）
                        if (jinzhi == 1)
                            sss = sss.Replace('\0', '0');
                        ShowMsgClient(sock.RemoteEndPoint + ":" + sss + "\r\n");
                        if (qiehuan_fangshi == "Tcp_client")
                        {
                            if (qiehuan(sss) == 0)
                            {
                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(sss);
                                getData(this, E);
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
            try { if (mdcan != null && mdcan.port != null && mdcan.port.IsOpen) mdcan.port.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            try { if (socketClient != null) { socketClient.Close(); socketClient = null; } } catch (Exception exInner) { new ErrorLog().WriteLog(exInner.ToString()); }
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
                socketClient.Send(newBuffer);
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
                    MsgErroeLog.WriteLog("异常:" + ex.Message);
                    // ch:监听套接字被关闭（重新监听/退出）时结束监听线程，避免异常忙循环空转
                    if (ex is ObjectDisposedException || ex is SocketException)
                        break;
                }
            }
        }
        string rcv1;
        private void Receive(object o)
        {
            // ch:P1-08 每连接独立缓冲/长度：原 bufferr/rr 是窗体共享字段，多客户端并发 Receive 时互相覆盖未解码数据
            byte[] bufferr = new byte[1024 * 1024 * 5];
            int rr;
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
                        //发送的文字消息
                        string str = DecodeProtoBytes(bufferr, rr); // ch:P2 显式 GBK 解码（原 Encoding.Default 依赖系统代码页）
                        if (jinzhi == 1)
                            str = str.Replace('\0', '0');
                        ShowMsg(current.RemoteEndPoint + ";" + str + "\r\n");
                        rcv1 = str;
                        if (qiehuan_fangshi == "Tcp_server")
                        {
                            if (qiehuan(str) == 0)
                            {
                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(str);
                                getData(this, E);
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
                // ch:客户端断开后关闭 socket 并从字典移除，
                // ch:否则后续 serverSocket[ip].Send 对已释放对象抛 ObjectDisposedException
                try { if (current != null) current.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                // ch:P1-⑥ 断开后从字典移除（ConcurrentDictionary.TryRemove 线程安全）
                try { if (remoteKey != "") serverSocket.TryRemove(remoteKey, out _); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            }
        }
        private int qiehuan(string aa)
        {
            if (textBox18.Text == aa)
            {
                zifu = aa;
                lujing = textBox10.Text;
                return 1;
            }
          else  if (textBox19.Text == aa)
            {
                zifu = aa;
                lujing = textBox11.Text;
                return 1;
            }
            else if (textBox21.Text == aa)
            {
                zifu = aa;
                lujing = textBox12.Text;
                return 1;
            }
            else if (textBox20.Text == aa)
            {
                zifu = aa;
                lujing = textBox13.Text;
                return 1;
            }
            else if (textBox23.Text == aa)
            {
                zifu = aa;
                lujing = textBox14.Text;
                return 1;
            }
            else if (textBox22.Text == aa)
            {
                zifu = aa;
                lujing = textBox15.Text;
                return 1;
            }
            else if (textBox25.Text == aa)
            {
                zifu = aa;
                lujing = textBox16.Text;
                return 1;
            }
            else if (textBox24.Text == aa)
            {
                zifu = aa;
                lujing = textBox17.Text;
                return 1;
            }
            else
            {
                return 0;
            }
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
                        target.Send(buffer);
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
           mdcan.port.Write(data, 0, data.Length);
          //  this.label9.Text = "数据发送中······";
        }

        private void timer2_Tick(object sender, EventArgs e)
        {
            string str =mdcan.port.ReadExisting();
            string str2 = str.Replace("\r", "\r\n");
            txtReceive.AppendText(str2);
            txtReceive.ScrollToCaret();
        }

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
                timer2.Enabled = false;
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
                        mdcan.port.Write(tempdata, 0, tempdata.Length);
                    }
                    else
                    {
                        send = texSend.Text;
                       mdcan.port.Write(send);
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
                try
                {
                    if (getRecevice != null && getRecevice.IsAlive)
                    {
                        // 只等待，不强制终止；线程在退出前可能正阻塞在 Begin/Invoke 上，超时后由 UI 线程恢复即自行结束
                        if (!getRecevice.Join(1000))
                            MsgErroeLog.WriteLog("接收线程未在 1s 内退出，将继续后台自行结束");
                    }
                }
                catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
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
            }
        }
        void AcceptData()
        {
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
                    strReceive =mdcan.port.ReadExisting();
                    txtReceive.AppendText(strReceive);
                    if (qiehuan_fangshi == "Serial")
                    {
                        if (qiehuan(strReceive) == 0)
                        {
                            SelectionChangedEventArgs E = new SelectionChangedEventArgs(strReceive);
                            getData(this, E);
                        }
                    }
                }
                catch (Exception ex) { }
            }
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
            wdini.WriteString("modbustcp", "lunxun", lunxun.ToString());
            wdini.WriteString("modbustcp", "style", comboBox3.Text);
            wdini.WriteString("modbustcp", "gongnengma", comboBox4.Text);
            wdini.WriteString("modbustcp", "serial", comboBox5.Text);
            wdini.WriteString("change", "1", textBox18.Text);
            wdini.WriteString("change", "2", textBox19.Text);
            wdini.WriteString("change", "3", textBox21.Text);
            wdini.WriteString("change", "4", textBox20.Text);
            wdini.WriteString("change", "5", textBox23.Text);
            wdini.WriteString("change", "6", textBox22.Text);
            wdini.WriteString("change", "7", textBox25.Text);
            wdini.WriteString("change", "8", textBox24.Text);
            wdini.WriteString("change", "all", comboBox2.Text);
            wdini.WriteString("path", "1", textBox10.Text);
            wdini.WriteString("path", "2", textBox11.Text);
            wdini.WriteString("path", "3", textBox12.Text);
            wdini.WriteString("path", "4", textBox13.Text);
            wdini.WriteString("path", "5", textBox14.Text);
            wdini.WriteString("path", "6", textBox15.Text);
            wdini.WriteString("path", "7", textBox16.Text);
            wdini.WriteString("path", "8", textBox17.Text);
            wdini.WriteString("modbustcp","xie", numericUpDown18.Value.ToString());
            wdini.WriteString("modbustcp", "du", numericUpDown18.Value.ToString());
            wdini.WriteString("serial", "strportName", strportName);
            wdini.WriteString("serial", "strbaudRate", strbaudRate);
            wdini.WriteString("serial", "strStopBits", strStopBits);
            wdini.WriteString("serial", "strDataBits", strDataBits);
            wdini.WriteString("serial","strjiaoyan",strjiaoyan);
            // WritePrivateProfileString("Test", "id", "xym", "d://vc//Ex1//ex1.ini");
            wdini.WriteString("tcpserver", "port1", textBox3.Text);
            wdini.WriteString("tcpserver", "ip1", textBox4.Text);
            wdini.WriteString("modbustcp", "port", txtPort.Text);
            wdini.WriteString("modbustcp", "ip", txtIp.Text);
            wdini.WriteString("tcpsend", "send1", textBox2.Text);
            wdini.WriteString("tcpclient", "port2", textBox5.Text);
            wdini.WriteString("tcpclient", "ip2", textBox6.Text);
            wdini.WriteString("out0", "ok", textBox10.Text);
            wdini.WriteString("out1", "ok", textBox11.Text);
            wdini.WriteString("out2", "ok", textBox12.Text);
            wdini.WriteString("out3", "ok", textBox13.Text);
            wdini.WriteString("out0", "ng", textBox14.Text);
            wdini.WriteString("out1", "ng", textBox15.Text);
            wdini.WriteString("out2", "ng", textBox16.Text);
            wdini.WriteString("out3", "ng", textBox17.Text);
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
            if (checkBox5.CheckState == CheckState.Checked)
                wdini.WriteString("modbustcp", "en", "true");
            else
                wdini.WriteString("modbustcp", "en", "false");

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
        private object locker_2 = new object();
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

                        Task.Run(() =>
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
                                socketClient.Send(newBuffer);
                            }
                            catch
                            {

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

                        Task.Run(() =>
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
                                        target.Send(buffer);
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
                                mdcan.port.Write(send);
                            }
                            else
                            {
                                int indata;
                                byte[] tempdata;
                                int.TryParse(aa, out indata);
                                Getint(indata, out tempdata);
                                mdcan.port.Write(tempdata, 0, tempdata.Length);
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
                   
                    Task.Run(() =>
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
                                socketClient.Send(newBuffer);
                            }
                            catch
                            {
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
        

                    Task.Run(() =>
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
                                        target.Send(buffer);
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
                       mdcan.port.Write(send);
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
            catch { monitor = "Tcpclient连接失败"; }
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

        private void button21_Click(object sender, EventArgs e)
        {
            try
            {
                if (IsEnable)
                {
                    MessageBox.Show("请勿重复建立连接!");
                    return;
                }
                string ip = txtIp.Text.Trim();
                int port = Convert.ToInt32(txtPort.Text);
                if (ip == null || ip == "")
                {
                    MessageBox.Show("ip不能为空!");
                    return;
                }
                busTcpClient = new ModbusTcpNet(ip, port, 0x01);
                OperateResult res = busTcpClient.ConnectServer();
                if (res.IsSuccess == true) //接收状态返回值
                {
                    IsEnable = true;
                    MsgErroeLog.WriteLog("开启连接成功");
                }
                else
                {
                    MsgErroeLog.WriteLog("开启连接失败");
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog("开启连接失败!" + "---" + ex.Message.ToString());
            }
        }

        private void button20_Click(object sender, EventArgs e)
        {
            try
            {
                if (!IsEnable)
                {
                    MsgErroeLog.WriteLog("尚未建立连接!");
                    return;
                }
                busTcpClient.ConnectClose();
                IsEnable = false;
                MsgErroeLog.WriteLog("关闭连接成功！");
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog("关闭连接失败!" + "---" + ex.Message.ToString());
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

        private void button8_Click(object sender, EventArgs e)
        {
            if (IsEnable)
            {
                if (modbus_style == "float")
                {
                    float indata;
                    float.TryParse(textBox27.Text, out indata);
                    //  busTcpClient.Write(xie, Getfloat(indata));
                    object temp_xie= Getfloat(indata);
                    modbus_duxie(xie,ref temp_xie,1);
                }
                else if(modbus_style=="int")
                {
                    int indata;
                    short s_indata;
                    byte[] tempdata;
                    int.TryParse(textBox27.Text, out indata);
                    short.TryParse(textBox27.Text, out s_indata);
                    if (gongnengma == "10")
                    {
                       // busTcpClient.Write(xie, Getint(indata, out tempdata));
                        object temp_xie = Getint(indata, out tempdata);
                        modbus_duxie(xie, ref temp_xie, 1);
                    }
                    else if (gongnengma == "06")
                    {
                        //busTcpClient.Write(xie, s_indata);
                        object temp_xie = s_indata;
                        modbus_duxie(xie, ref temp_xie, 1);
                    }
                }
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
        public float Getfloat(float values)
        {
            float[] value = new float[] { values };
            var result = new byte[value.Length * sizeof(float)];
            Buffer.BlockCopy(value, 0, result, 0, result.Length);
            byte[] data;
            if (modbus_qufan == 1)
            {
                data = new byte[4] { result[2], result[3], result[0], result[1] };
            }
            else
            {
                data = new byte[4] { result[0], result[1], result[2], result[3] };
            }
            value[0] = BitConverter.ToSingle(data, 0);
            return value[0];
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
        // ch:轮询线程写接收值显示，跨线程时转 UI 线程
        private void SetText26(string s)
        {
            if (this.InvokeRequired)
                this.BeginInvoke(new Action(() => textBox26.Text = s));
            else
                textBox26.Text = s;
        }
        private void button9_Click(object sender, EventArgs e)
        {
            if (IsEnable)
            {
                //float Pdata;
                // Pdata = Diaohuan(Pdata);
                if (modbus_style == "float")
                {
                    object temp_du = (float)2.2;
                    modbus_duxie(du, ref temp_du, 0);
                    SetText26(temp_du.ToString());
                    //textBox26.Text = Getfloat(busTcpClient.ReadFloat(du).Content).ToString();
                }
                else if(modbus_style=="int")
                {
                    if (gongnengma == "10")
                    {
                        //byte[] tempdata;
                        //  textBox26.Text = Getint(busTcpClient.ReadInt32(du).Content, out tempdata).ToString();
                        object temp_du = (int)2;
                        modbus_duxie(du, ref temp_du, 0);
                        SetText26(temp_du.ToString());
                    }
                    else if(gongnengma=="06")
                    {
                        object temp_du = (short)2;
                        modbus_duxie(du, ref temp_du, 0);
                        SetText26(temp_du.ToString());
                        //textBox26.Text = busTcpClient.ReadInt16(du).Content.ToString();
                    }
                }
            }
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
            if (comboBox5.Text == "无协议")
            {
                serial_xieyi = "无协议";
            }
            else if (comboBox5.Text == "modbus_RTU")
            {
                serial_xieyi = "modbus_RTU";
            }
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
            else if (comboBox2.Text == "modbustcp")
            {
                qiehuan_fangshi = "modbustcp";
            }
        }
        private bool front = false;
        private Thread td1;

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

        private void numericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            lunxun =(int)numericUpDown2.Value;
        }

        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            xiangji =(int) numericUpDown3.Value;
        }
    }
}
