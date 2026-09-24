using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using HslCommunication.Profinet;
using HslCommunication;
using HslCommunication.ModBus;
using System.Threading;
using System.IO.Ports;
using demo;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;

namespace WindowsFormsApplication1
{
    public partial class FormModbusRtu : Form
    {
        public Label labelFallbackHint = new Label();

        public FormModbusRtu( )
        {
            wdini.ReadINIFile(AppDomain.CurrentDomain.BaseDirectory + "//test.ini");
            InitializeComponent( );

            // 在 tabPage2 (数据绑定页面) label39 "1拖多需重启软件" 下方添加提示 label（默认隐藏）
            labelFallbackHint.AutoSize = true;
            labelFallbackHint.TextAlign = ContentAlignment.MiddleLeft;
            labelFallbackHint.ForeColor = Color.Red;
            labelFallbackHint.Text = "写操作已降级为逐地址写入(FC06)";
            labelFallbackHint.Visible = false;
            labelFallbackHint.Location = new Point(label39.Left, label39.Bottom + 8);
            tabPage2.Controls.Add(labelFallbackHint);
        }
        private ClassIni wdini = new ClassIni();
        public delegate void GetSeletionData(object Sender, SelectionChangedEventArgs e);
        public event GetSeletionData getData;
        private int x = 999;
        private int y = 999;
        Dictionary<string, string[]> fins_dic = new Dictionary<string, string[]>();
        public Dictionary<int, string[]> camera_dic = new Dictionary<int, string[]>();
        Dictionary<int, int[]> fins_name = new Dictionary<int, int[]>();
        Dictionary<int, int[]> fins_data = new Dictionary<int, int[]>();
        Dictionary<int, byte[]> fins_value = new Dictionary<int, byte[]>();
        Dictionary<string, string> fins_zuhe = new Dictionary<string, string>();
        private decimal address_qishi = 0;
        private decimal address_length = 0;
        private decimal fins_qishi = 0;
        private decimal fins_length = 0;
        private string ABCD = "触发";
        private string fins_style = "int";
        private string fins_mingcheng = "";
        public string zifu;
        public string lujing;
        // ch:P2 本窗体切换回执闩：轮询线程设置、Form1 的 cam10 分支复位（跨线程），改 volatile 保证可见性
        public volatile int qiehuanzhong = 0;
        ErrorLog MsgErroeLog = new ErrorLog();
        bool fins_lunxunen = false;
        public bool fins_en = false;
        decimal lunxun_time = 0;
        public bool chushihua = false;
        public class SelectionChangedEventArgs : EventArgs
        {

            private string m_selection;

            private string m_camere;
            private string m_all;

            //本属性用于传递事件数据

            public string Selection
            {

                get { return m_selection; }

            }
            public string Camera
            {

                get { return m_camere; }

            }
            public string all
            {

                get { return m_all; }

            }
            public SelectionChangedEventArgs(string selection, string camera, string all)
            {

                m_selection = selection;
                m_camere = camera;
                m_all = all;
            }
        }

        private ModbusRtu busRtuClient = null;
        // ch:RTU 连接参数缓存（供后台线程重连复用，避免跨线程读取 UI 控件）
        private byte _rtuStation;
        private bool _rtuAddrStartZero;
        private bool _rtuIsStringReverse;
        private string _rtuPortName;
        private int _rtuBaudRate;
        private int _rtuDataBits;
        private System.IO.Ports.StopBits _rtuStopBits;
        private System.IO.Ports.Parity _rtuParity;
        // ch:RTU 断线自动重连控制（节流防重连风暴；仅在自动监控状态且为意外掉线时触发）
        private readonly object _rtuReconnectLock = new object();
        private long _rtuLastReconnectTick = 0;
        private volatile bool _rtuAutoReconnect = false;
        // 结果登记与 xie_wu 消费必须串行；单一 RTU 写入模式下，避免同一相机下一帧覆盖上一帧 pending。
        private readonly object _rtuIoLock = new object();
        private int _rtuDataFormatIndex = 0; // ch:P2-⑤ 缓存 DataFormat 选择，避免后台重连线程跨线程读 comboBox2.SelectedIndex


        private void FormSiemens_Load( object sender, EventArgs e )
        {
            panel2.Enabled = false;
            comboBox1.SelectedIndex = 0;



            comboBox2.SelectedIndex = 0;
            _rtuDataFormatIndex = 0; // ch:P2-⑤ 缓存初始 DataFormat 索引
            comboBox2.SelectedIndexChanged += ComboBox2_SelectedIndexChanged;
            checkBox3.CheckedChanged += CheckBox3_CheckedChanged;

            comboBox3.DataSource = SerialPort.GetPortNames( );
            try
            {
                comboBox3.SelectedIndex = 0;
            }
            catch
            {
                comboBox3.Text = "COM3";
            }

            Language( Program.Language );


            fins_duxie = new Thread(new ThreadStart(Fins_duxie));
            fins_duxie.IsBackground = true;
            fins_duxie.Start();
            decimal xuanzhong_temp = 0;
            for (int i = 0; i < 10; i++)
            {
                dataGridView1.Rows.Add();
            }
            for (int i = 0; i < 50; i++)
            {
                fins_data.Add(i, new int[] { i % 10, i / 10 * 2 + 1 });
                fins_name.Add(i, new int[] { i % 10, i / 10 * 2 });
                fins_value.Add(i, new byte[] { 0x00, 0x00 });
            }

            // ch:R11-2 ini 使能/地址/通道数读取族统一 TryParse+回退默认+夹取 NUD 范围：
            //   原裸 bool.Parse/decimal.Parse 任一值被现场改坏即抛异常 → Load 的 catch 只记一行日志，
            //   跳过其后串口/通道配置与自动连接，通讯窗体静默不工作。
            // ch:R12 补：①解析失败/取整记日志（原静默回退，现场改了 ini 不生效无从排查）；
            //   ②统一取整——小数会存成 "10.5"，轮询线程 int.Parse(par.Value[1]/[2])、int.Parse(address_qishi.ToString())
            //     抛 FormatException → 整轮 foreach 中断、其后通道全不读；lunxun_time 保留 (0,1) 会被 (int) 截成 0 → Sleep(0) 忙等
            decimal IniDec(string s, decimal dft)
            {
                decimal v;
                if (!decimal.TryParse(s, out v))
                {
                    MsgErroeLog.WriteLog("modbusrtu ini 解析失败，回退默认值 原值=[" + (s ?? "null") + "] 默认=" + dft);
                    v = dft;
                }
                decimal t = Math.Truncate(v);
                if (t != v)
                    MsgErroeLog.WriteLog("modbusrtu ini 小数值已取整 [" + v + "]→" + t);
                return t;
            }
            decimal ClampNud(NumericUpDown nud, decimal v) { return Math.Max(nud.Minimum, Math.Min(nud.Maximum, v)); }
            bool bl_ini;
            // ch:R12 TryParse 失败时 out 被置 false，语义与原写法一致；补日志，避免 "1"/"是" 等历史写法静默变 false
            if (!bool.TryParse(wdini.ReadString("modbusrtu", "modbusrtu_lunxunen", "false"), out bl_ini))
                MsgErroeLog.WriteLog("modbusrtu ini modbusrtu_lunxunen 非布尔值，按 false 处理");
            fins_lunxunen = bl_ini;
            if (!bool.TryParse(wdini.ReadString("modbusrtu", "modbusrtu_en", "false"), out bl_ini))
                MsgErroeLog.WriteLog("modbusrtu ini modbusrtu_en 非布尔值，按 false 处理");
            fins_en = bl_ini;
            address_qishi = ClampNud(numericUpDown1, IniDec(wdini.ReadString("modbusrtu", "qishi", "0"), 0));
            address_length = ClampNud(numericUpDown2, IniDec(wdini.ReadString("modbusrtu", "zongchang", "1"), 1));
            lunxun_time = ClampNud(numericUpDown3, IniDec(wdini.ReadString("modbusrtu", "lunxun_time", "20"), 20));
            numericUpDown1.Value = address_qishi;
            numericUpDown2.Value = address_length;
            numericUpDown3.Value = lunxun_time;

            comboBox1.Text = wdini.ReadString("modbusrtu", "Parity", "无").Replace("\0", "");
            comboBox3.Text = wdini.ReadString("modbusrtu", "PortName", "COM3").Replace("\0", "");
            comboBox2.Text = wdini.ReadString("modbusrtu", "abcd", "CDAB").Replace("\0", "");
            textBox16.Text = wdini.ReadString("modbusrtu", "dataBits", "8").Replace("\0", "");
            textBox2.Text = wdini.ReadString("modbusrtu", "baudRate", "9600").Replace("\0", "");
            textBox17.Text = wdini.ReadString("modbusrtu", "stopBits", "1").Replace("\0", "");
            textBox15.Text = wdini.ReadString("modbusrtu", "station", "1").Replace("\0", "");
            if (fins_lunxunen)
            {
                c1.CheckState = CheckState.Checked;
            }
            if (fins_en)
            {
                c2.CheckState = CheckState.Checked;
                button1_Click(null, null);
            }
            geshu = (int)IniDec(wdini.ReadString("modbusrtu", "geshu", "0"), 0); // ch:R11-2 TryParse+夹取，循环上界防手改天文数字卡死 Load
            geshu = Math.Max(0, Math.Min(64, geshu));
            if (geshu > 0)
            {
                for (int i = 0; i < geshu; i++)
                {
                    fins_mingcheng = wdini.ReadString((i + 1).ToString()+ "modbusrtu", "name", "").Replace("\0", "").Trim();
                    // ch:R12 name 缺省 ""，多通道未填名 → Dictionary 重复键 ArgumentException → Load 后续初始化全跳过
                    if (fins_mingcheng.Length == 0)
                    {
                        fins_mingcheng = "通道" + (i + 1);
                        MsgErroeLog.WriteLog("modbusrtu 第" + (i + 1) + "段 name 为空，改用默认名[" + fins_mingcheng + "]");
                    }
                    else if (fins_dic.ContainsKey(fins_mingcheng))
                    {
                        MsgErroeLog.WriteLog("modbusrtu 第" + (i + 1) + "段 name 与既有通道重名[" + fins_mingcheng + "]，降级为唯一名");
                        fins_mingcheng = fins_mingcheng + "_" + (i + 1);
                    }
                    fins_qishi = IniDec(wdini.ReadString((i + 1).ToString() + "modbusrtu", "qishi", "0"), 0);
                    fins_length = Math.Max(0, Math.Min(1000, IniDec(wdini.ReadString((i + 1).ToString() + "modbusrtu", "changdu", "0"), 0))); // ch:R11-2 内层循环上界，夹 [0,1000]（IniDec 已取整）
                    ABCD = wdini.ReadString((i + 1).ToString() + "modbusrtu", "gaodiwei", "触发").Replace("\0", "");
                    fins_style = wdini.ReadString((i + 1).ToString() + "modbusrtu", "geshi", "int").Replace("\0", "");
                    fins_dic.Add(fins_mingcheng, new string[] { fins_mingcheng, fins_qishi.ToString(), fins_length.ToString(), ABCD, fins_style });
                    for (int j = 0; j < fins_length; j++)
                    {
                        xuanzhong_temp = fins_qishi - address_qishi + j;
                        int t = (int)xuanzhong_temp; // ch:R11-2 原 int.Parse(decimal.ToString()) 遇小数抛异常；并补越界守卫(原 fins_name[...] 无守卫会 KeyNotFound)
                        // ch:R12 起始条件用原始 decimal——(int)(-0.5)==0 会误判通过，把上色打到 0 号格
                        if (xuanzhong_temp < 0 || xuanzhong_temp >= 50 || t < 0 || t >= 50 || !fins_name.ContainsKey(t) || !fins_data.ContainsKey(t))
                            continue;
                        dataGridView1[fins_name[t][0], fins_name[t][1]].Style.BackColor = Color.Green;
                        dataGridView1[fins_data[t][0], fins_data[t][1]].Style.BackColor = Color.Green;
                        dataGridView1[fins_name[t][0], fins_name[t][1]].Value = fins_mingcheng;
                    }
                }
            }
            for (int i = 0; i < 10; i++)
            {
                string temp_jian = wdini.ReadString("c" + (i + 1).ToString() + "modbusrtu", "chufa", " ").Replace("\0", "");
                camera_dic.Add(i + 1, new string[] { temp_jian, wdini.ReadString("c" + (i + 1).ToString() + "modbusrtu", "fanhuizhi", "0").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString() + "modbusrtu", "fanhuien", "false").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString() + "modbusrtu", "fankui", "0").Replace("\0", ""), "无", "无" });

                if (!fins_zuhe.ContainsKey(temp_jian))
                {
                    fins_zuhe.Add(temp_jian, (i + 1).ToString());
                }
                else
                    fins_zuhe[temp_jian] += (i + 1).ToString();

            }
            // ch:R22 载入期存量重复绑定扫描：ini 里两相机绑同一反馈通道=同地址互相覆盖，仅记日志不弹窗、不改配置（移植 modbustcp R21）
            Dictionary<string, int> fankuiSeen = new Dictionary<string, int>();
            for (int ci = 1; ci <= camera_dic.Count; ci++)
            {
                string fk = camera_dic[ci][3];
                if (IsUnboundFankui(fk))
                    continue;
                int prevCam;
                if (fankuiSeen.TryGetValue(fk, out prevCam))
                    MsgErroeLog.WriteLog("反馈通道存量重复绑定: 通道[" + fk + "] 同时绑定相机" + prevCam + "与相机" + ci + "，请在配置窗改绑");
                else
                    fankuiSeen[fk] = ci;
            }
            int cccc = 0;
            foreach (var f in fins_zuhe.Keys)
            {
                if (cccc == 0)
                {
                    label43.Text = fins_zuhe[f].ToString() + "_" + f.ToString();
                }
                if (cccc == 1)
                {
                    label42.Text = fins_zuhe[f].ToString() + "_" + f.ToString();
                }
                if (cccc == 2)
                {
                    label41.Text = fins_zuhe[f].ToString() + "_" + f.ToString();
                }
                if (cccc == 3)
                {
                    label40.Text = fins_zuhe[f].ToString() + "_" + f.ToString();
                };
                cccc++;
            }
            cb4.Items.Add(camera_dic[1][0]);
            cb4.Text = camera_dic[1][0];
            t2.Text = camera_dic[1][1];
            if (camera_dic[1][2] == "true")
                c3.CheckState = CheckState.Checked;
            cb13.Items.Add(camera_dic[1][3]);
            cb13.Text = camera_dic[1][3];

            cb5.Items.Add(camera_dic[2][0]);
            cb5.Text = camera_dic[2][0];
            t3.Text = camera_dic[2][1];
            if (camera_dic[2][2] == "true")
                c4.CheckState = CheckState.Checked;
            cb14.Items.Add(camera_dic[2][3]);
            cb14.Text = camera_dic[2][3];

            cb6.Items.Add(camera_dic[3][0]);
            cb6.Text = camera_dic[3][0];
            t4.Text = camera_dic[3][1];
            if (camera_dic[3][2] == "true")
                c5.CheckState = CheckState.Checked;
            cb15.Items.Add(camera_dic[3][3]);
            cb15.Text = camera_dic[3][3];

            cb7.Items.Add(camera_dic[4][0]);
            cb7.Text = camera_dic[4][0];
            t5.Text = camera_dic[4][1];
            if (camera_dic[4][2] == "true")
                c6.CheckState = CheckState.Checked;
            cb16.Items.Add(camera_dic[4][3]);
            cb16.Text = camera_dic[4][3];

            cb8.Items.Add(camera_dic[5][0]);
            cb8.Text = camera_dic[5][0];
            t6.Text = camera_dic[5][1];
            if (camera_dic[5][2] == "true")
                c7.CheckState = CheckState.Checked;
            cb17.Items.Add(camera_dic[5][3]);
            cb17.Text = camera_dic[5][3];

            cb9.Items.Add(camera_dic[6][0]);
            cb9.Text = camera_dic[6][0];
            t7.Text = camera_dic[6][1];
            if (camera_dic[6][2] == "true")
                c8.CheckState = CheckState.Checked;
            cb18.Items.Add(camera_dic[6][3]);
            cb18.Text = camera_dic[6][3];

            cb10.Items.Add(camera_dic[7][0]);
            cb10.Text = camera_dic[7][0];
            t8.Text = camera_dic[7][1];
            if (camera_dic[7][2] == "true")
                c9.CheckState = CheckState.Checked;
            cb19.Items.Add(camera_dic[7][3]);
            cb19.Text = camera_dic[7][3];

            cb11.Items.Add(camera_dic[8][0]);
            cb11.Text = camera_dic[8][0];
            t9.Text = camera_dic[8][1];
            if (camera_dic[8][2] == "true")
                c10.CheckState = CheckState.Checked;
            cb20.Items.Add(camera_dic[8][3]);
            cb20.Text = camera_dic[8][3];

            cb12.Items.Add(camera_dic[9][0]);
            cb12.Text = camera_dic[9][0];
            t10.Text = camera_dic[9][1];
            if (camera_dic[9][2] == "true")
                c11.CheckState = CheckState.Checked;

            cb21.Items.Add(camera_dic[10][0]);
            cb21.Text = camera_dic[10][0];
            t11.Text = camera_dic[10][1];
            if (camera_dic[10][2] == "true")
                c12.CheckState = CheckState.Checked;

            t12.Text = wdini.ReadString("change" + "modbusrtu", "1", "");
            t13.Text = wdini.ReadString("change" + "modbusrtu", "2", "");
            t14.Text = wdini.ReadString("change" + "modbusrtu", "3", "");
            t15.Text = wdini.ReadString("change" + "modbusrtu", "4", "");
            t16.Text = wdini.ReadString("change" + "modbusrtu", "5", "");
            t17.Text = wdini.ReadString("change" + "modbusrtu", "6", "");
            t18.Text = wdini.ReadString("change" + "modbusrtu", "7", "");
            t19.Text = wdini.ReadString("change" + "modbusrtu", "8", "");
            t20.Text = wdini.ReadString("path" + "modbusrtu", "1", "");
            t21.Text = wdini.ReadString("path" + "modbusrtu", "2", "");
            t22.Text = wdini.ReadString("path" + "modbusrtu", "3", "");
            t23.Text = wdini.ReadString("path" + "modbusrtu", "4", "");
            t24.Text = wdini.ReadString("path" + "modbusrtu", "5", "");
            t25.Text = wdini.ReadString("path" + "modbusrtu", "6", "");
            t26.Text = wdini.ReadString("path" + "modbusrtu", "7", "");
            t27.Text = wdini.ReadString("path" + "modbusrtu", "8", "");

            Task.Run(() =>
            {

                Thread.Sleep(1000);
                if (fins_en)
                {
                    button1_Click(null, null);
                }
                chushihua = true;
            });
        }


        private void Language( int language )
        {
            if (language == 2)
            {
                Text = "Modbus Rtu Read Demo";

                label1.Text = "Com:";
                label3.Text = "baudRate:";
                label22.Text = "DataBit";
                label23.Text = "StopBit";
                label24.Text = "parity";
                label21.Text = "station";
                checkBox1.Text = "address from 0";
                checkBox3.Text = "string reverse";
                button1.Text = "Connect";
                button2.Text = "Disconnect";

                label6.Text = "address:";
                label7.Text = "result:";

                button_read_bool.Text = "r-coil";
                button4.Text = "r-discrete";
                button_read_short.Text = "r-short";
                button_read_ushort.Text = "r-ushort";
                button_read_int.Text = "r-int";
                button_read_uint.Text = "r-uint";
                button_read_long.Text = "r-long";
                button_read_ulong.Text = "r-ulong";
                button_read_float.Text = "r-float";
                button_read_double.Text = "r-double";
                button_read_string.Text = "r-string";
                label8.Text = "length:";
                label11.Text = "Address:";
                label12.Text = "length:";
                button25.Text = "Bulk Read";
                label13.Text = "Results:";
                label16.Text = "Message:";
                label14.Text = "Results:";
                button26.Text = "Read";

                label10.Text = "Address:";
                label9.Text = "Value:";
                label19.Text = "Note: The value of the string needs to be converted";
                button24.Text = "Write Bit";
                button22.Text = "w-short";
                button21.Text = "w-ushort";
                button20.Text = "w-int";
                button19.Text = "w-uint";
                button18.Text = "w-long";
                button17.Text = "w-ulong";
                button16.Text = "w-float";
                button15.Text = "w-double";
                button14.Text = "w-string";

                groupBox1.Text = "Single Data Read test";
                groupBox2.Text = "Single Data Write test";
                groupBox3.Text = "Bulk Read test";
                groupBox4.Text = "Message reading test, hex string needs to be filled in,without crc";
                

                comboBox1.DataSource = new string[] { "None", "Odd", "Even" };
            }
        }

        private void CheckBox3_CheckedChanged( object sender, EventArgs e )
        {
            if (busRtuClient != null)
            {
                busRtuClient.IsStringReverse = checkBox3.Checked;
            }
        }

        private void ComboBox2_SelectedIndexChanged( object sender, EventArgs e )
        {
            if (busRtuClient != null)
            {
                switch (comboBox2.SelectedIndex)
                {
                    case 0: busRtuClient.DataFormat = HslCommunication.Core.DataFormat.ABCD; break;
                    case 1: busRtuClient.DataFormat = HslCommunication.Core.DataFormat.BADC; break;
                    case 2: busRtuClient.DataFormat = HslCommunication.Core.DataFormat.CDAB; break;
                    case 3: busRtuClient.DataFormat = HslCommunication.Core.DataFormat.DCBA; break;
                    default: break;
                }
            }
            _rtuDataFormatIndex = comboBox2.SelectedIndex; // ch:P2-⑤ UI 线程缓存，供后台重连线程使用
        }


        // ch:P1-⑦ 自动重连创建新 ModbusRtu 后，新连接 DataFormat 会退回 HSL 默认(ABCD)，
        //   必须按当前 comboBox2 选择重新设回，否则重连后 float/long 静默写错。
        private void ApplyRtuDataFormat( ModbusRtu client )
        {
            if ( client == null ) return;
            switch ( _rtuDataFormatIndex ) // ch:P2-⑤ 用 UI 线程缓存的索引，避免后台线程跨线程读 comboBox2
            {
                case 0: client.DataFormat = HslCommunication.Core.DataFormat.ABCD; break;
                case 1: client.DataFormat = HslCommunication.Core.DataFormat.BADC; break;
                case 2: client.DataFormat = HslCommunication.Core.DataFormat.CDAB; break;
                case 3: client.DataFormat = HslCommunication.Core.DataFormat.DCBA; break;
            }
        }

        private void FormSiemens_FormClosing( object sender, FormClosingEventArgs e )
        {
            e.Cancel = true;
            this.Visible = false;
        }
        

        #region Connect And Close



        private void button1_Click( object sender, EventArgs e )
        {
            if(!int.TryParse(textBox2.Text,out int baudRate ))
            {
                MsgErroeLog.WriteLog( DemoUtils.BaudRateInputWrong );
                return;
            }

            if (!int.TryParse( textBox16.Text, out int dataBits ))
            {
                MsgErroeLog.WriteLog( DemoUtils.DataBitsInputWrong );
                return;
            }

            if (!int.TryParse( textBox17.Text, out int stopBits ))
            {
                MsgErroeLog.WriteLog( DemoUtils.StopBitInputWrong );
                return;
            }


            if (!byte.TryParse(textBox15.Text,out byte station))
            {
                MsgErroeLog.WriteLog( "Station input wrong！" );
                return;
            }

            busRtuClient?.Close( );
            busRtuClient = new ModbusRtu( station );
            busRtuClient.AddressStartWithZero = checkBox1.Checked;
            // ch:缓存连接参数，供后台线程断线自动重连复用（避免跨线程读取 UI 控件）
            _rtuStation = station;
            _rtuAddrStartZero = checkBox1.Checked;
            _rtuIsStringReverse = checkBox3.Checked;
            _rtuPortName = comboBox3.Text;
            _rtuBaudRate = baudRate;
            _rtuDataBits = dataBits;
            _rtuStopBits = stopBits == 0 ? System.IO.Ports.StopBits.None : (stopBits == 1 ? System.IO.Ports.StopBits.One : System.IO.Ports.StopBits.Two);
            _rtuParity = comboBox1.SelectedIndex == 0 ? System.IO.Ports.Parity.None : (comboBox1.SelectedIndex == 1 ? System.IO.Ports.Parity.Odd : System.IO.Ports.Parity.Even);


            ComboBox2_SelectedIndexChanged( null, new EventArgs( ) );
            busRtuClient.IsStringReverse = checkBox3.Checked;


            try
            {
                busRtuClient.SerialPortInni( sp =>
                 {
                     sp.PortName = comboBox3.Text;
                     sp.BaudRate = baudRate;
                     sp.DataBits = dataBits;
                     sp.StopBits = stopBits == 0 ? System.IO.Ports.StopBits.None : (stopBits == 1 ? System.IO.Ports.StopBits.One : System.IO.Ports.StopBits.Two);
                     sp.Parity = comboBox1.SelectedIndex == 0 ? System.IO.Ports.Parity.None : (comboBox1.SelectedIndex == 1 ? System.IO.Ports.Parity.Odd : System.IO.Ports.Parity.Even);
                 } );
                busRtuClient.Open( );

                _rtuAutoReconnect = true; // ch:连接成功后才允许断线自动重连
                SyncConnectedUiState(); // ch:R22 Load 的 Task.Run 在池线程调 button1_Click，UI 状态更新封送到 UI 线程
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog( ex.Message );
            }
        }

        // ch:R22 连接成功后的 UI 状态更新：Load 的 Task.Run 在池线程调 button1_Click，
        // 直写 button/panel/userControlCurve 属跨线程 UI 写，InvokeRequired 时封送到 UI 线程
        private void SyncConnectedUiState()
        {
            if (IsDisposed || !IsHandleCreated) return; // ch:R22 窗口未创建/已销毁不入队
            if (InvokeRequired) { BeginInvoke(new Action(SyncConnectedUiState)); return; }
            button2.Enabled = true;
            button1.Enabled = false;
            panel2.Enabled = true;

            userControlCurve1.ReadWriteNet = busRtuClient;
        }

        private void button2_Click( object sender, EventArgs e )
        {
            // 断开连接
            _rtuAutoReconnect = false; // ch:手动断开后不再自动重连，尊重操作者意图
            busRtuClient.Close( );
            button2.Enabled = false;
            button1.Enabled = true;
            panel2.Enabled = false;
        }

        // ch:RTU 断线自动重连（节流 5s，防止重连风暴；用缓存的连接参数重建串口并重新打开）
        private void TryAutoReconnectRtu( )
        {
            if ( !_rtuAutoReconnect ) return;
            long now = Environment.TickCount;
            if ( _rtuLastReconnectTick != 0 && now - _rtuLastReconnectTick < 5000 ) return;
            lock ( _rtuReconnectLock )
            {
                if ( _rtuLastReconnectTick != 0 && Environment.TickCount - _rtuLastReconnectTick < 5000 ) return;
                _rtuLastReconnectTick = Environment.TickCount;
                try
                {
                    var old = busRtuClient;
                    var nc = new ModbusRtu( _rtuStation );
                    nc.AddressStartWithZero = _rtuAddrStartZero;
                    nc.IsStringReverse = _rtuIsStringReverse;
                    nc.SerialPortInni( sp =>
                    {
                        sp.PortName = _rtuPortName;
                        sp.BaudRate = _rtuBaudRate;
                        sp.DataBits = _rtuDataBits;
                        sp.StopBits = _rtuStopBits;
                        sp.Parity = _rtuParity;
                    } );
                    nc.Open( );
                    ApplyRtuDataFormat( nc ); // ch:P1-⑦ 新连接补设字节序，避免重连后 float/long 写错
                    busRtuClient = nc; // 原子替换引用，轮询线程下一轮即使用新连接
                    userControlCurve1.ReadWriteNet = nc;
                    MsgErroeLog.WriteLog( "RTU PLC 自动重连成功" );
                    try { lock ( _rtuIoLock ) { old?.Close( ); } } catch { } // ch:P2-⑤ 关闭旧连接前先与写线程(xie_wu)串行，避免写线程正用旧实例时被关
                }
                catch ( Exception ex )
                {
                    MsgErroeLog.WriteLog( "RTU PLC 自动重连失败:" + ex.Message );
                }
            }
        }
        
        #endregion

        #region 单数据读取测试


        private void button_read_bool_Click( object sender, EventArgs e )
        {
            // 读取bool变量
            DemoUtils.ReadResultRender( busRtuClient.ReadCoil( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button4_Click_1( object sender, EventArgs e )
        {
            // 离散输入读取
            DemoUtils.ReadResultRender( busRtuClient.ReadDiscrete( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_short_Click( object sender, EventArgs e )
        {
            // 读取short变量
            DemoUtils.ReadResultRender( busRtuClient.ReadInt16( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_ushort_Click( object sender, EventArgs e )
        {
            // 读取ushort变量
            DemoUtils.ReadResultRender( busRtuClient.ReadUInt16( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_int_Click( object sender, EventArgs e )
        {
            // 读取int变量
            DemoUtils.ReadResultRender( busRtuClient.ReadInt32(  textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_uint_Click( object sender, EventArgs e )
        {
            // 读取uint变量
            DemoUtils.ReadResultRender( busRtuClient.ReadUInt32( textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_long_Click( object sender, EventArgs e )
        {
            // 读取long变量
            DemoUtils.ReadResultRender( busRtuClient.ReadInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_ulong_Click( object sender, EventArgs e )
        {
            // 读取ulong变量
            DemoUtils.ReadResultRender( busRtuClient.ReadUInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_float_Click( object sender, EventArgs e )
        {
            // 读取float变量
            DemoUtils.ReadResultRender( busRtuClient.ReadFloat( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_double_Click( object sender, EventArgs e )
        {
            // 读取double变量
            DemoUtils.ReadResultRender( busRtuClient.ReadDouble( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_string_Click( object sender, EventArgs e )
        {
            // 读取字符串
            DemoUtils.ReadResultRender( busRtuClient.ReadString( textBox3.Text , ushort.Parse( textBox5.Text ) ), textBox3.Text, textBox4 );
        }


        #endregion

        #region 单数据写入测试


        private void button24_Click( object sender, EventArgs e )
        {
            // bool写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.WriteCoil( textBox8.Text, bool.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button22_Click( object sender, EventArgs e )
        {
            // short写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , short.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button21_Click( object sender, EventArgs e )
        {
            // ushort写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , ushort.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }


        private void button20_Click( object sender, EventArgs e )
        {
            // int写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , int.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button19_Click( object sender, EventArgs e )
        {
            // uint写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , uint.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button18_Click( object sender, EventArgs e )
        {
            // long写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , long.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button17_Click( object sender, EventArgs e )
        {
            // ulong写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , ulong.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button16_Click( object sender, EventArgs e )
        {
            // float写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , float.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        private void button15_Click( object sender, EventArgs e )
        {
            // double写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , double.Parse( textBox7.Text ) ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }


        private void button14_Click( object sender, EventArgs e )
        {
            // string写入
            try
            {
                DemoUtils.WriteResultRender( busRtuClient.Write( textBox8.Text , textBox7.Text ), textBox8.Text );
            }
            catch (Exception ex)
            {
                MessageBox.Show( ex.Message );
            }
        }

        
        #endregion

        #region 批量读取测试

        private void button25_Click( object sender, EventArgs e )
        {
            DemoUtils.BulkReadRenderResult( busRtuClient, textBox6, textBox9, textBox10 );
        }



        #endregion

        #region 报文读取测试


        private void button26_Click( object sender, EventArgs e )
        {
            OperateResult<byte[]> read = busRtuClient.ReadBase( HslCommunication.Serial.SoftCRC16.CRC16( HslCommunication.BasicFramework.SoftBasic.HexStringToBytes( textBox13.Text ) ) );
            if (read.IsSuccess)
            {
                textBox11.Text = "Result：" + HslCommunication.BasicFramework.SoftBasic.ByteToHexString( read.Content );
            }
            else
            {
                MessageBox.Show( "Read Failed：" + read.ToMessageShowString( ) );
            }
        }


        #endregion
        
        #region Test Function


        private void Test1()
        {
            OperateResult<bool[]> read = busRtuClient.ReadCoil( "100", 10 );
            if(read.IsSuccess)
            {
                bool coil_100 = read.Content[0];
                // and so on 
                bool coil_109 = read.Content[9];
            }
            else
            {
                // failed
                string err = read.Message;
            }
        }


        private void Test2()
        {
            bool[] values = new bool[] { true, false, false, false, true, true, false, true, false, false };
            OperateResult write = busRtuClient.WriteCoil( "100", values );
            if (write.IsSuccess)
            {
                // success
            }
            else
            {
                // failed
                string err = write.Message;
            }

            HslCommunication.Core.IByteTransform ByteTransform = new HslCommunication.Core.ReverseWordTransform( );
        }



        #endregion

        private void b5_Click(object sender, EventArgs e)
        {
            if (b5.Text == "显示")
            {
                tabControl1.Visible = true;
                b5.Text = "隐藏";
            }
            else
            {
                tabControl1.Visible = false;
                b5.Text = "显示";
            }
        }

        private void dataGridView1_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            dataGridView1.ClearSelection();
            x = 999;
            y = 999;
        }

        private void dataGridView1_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            bool xuanzhong_temp = false;

            if (sender is DataGridView)
            {
                DataGridView dgv = (DataGridView)sender;
                if (e.RowIndex % 2 == 1 && e.ColumnIndex >= 0)//如果该行为表头
                {


                    x = e.ColumnIndex;
                    y = e.RowIndex;
                    foreach (var pair in fins_data)
                    {
                        if (pair.Value[0] == x && pair.Value[1] == y)
                        {
                            foreach (var p_temp in fins_dic)
                            {
                                if ((pair.Key + address_qishi) >= int.Parse(p_temp.Value[1]) && (pair.Key + address_qishi) < int.Parse(p_temp.Value[1]) + int.Parse(p_temp.Value[2]))
                                {
                                    xuanzhong_temp = true;
                                    numericUpDown5.Value = int.Parse(p_temp.Value[1]);
                                    x = fins_data[int.Parse(p_temp.Value[1]) - int.Parse(p_temp.Value[1])][0];
                                    y = fins_data[int.Parse(p_temp.Value[1]) - int.Parse(p_temp.Value[1])][1];
                                    numericUpDown4.Value = int.Parse(p_temp.Value[2]);
                                    t1.Text = p_temp.Value[0];
                                    cb2.Text = p_temp.Value[3];
                                    cb3.Text = p_temp.Value[4];
                                }

                            }
                            if (xuanzhong_temp == false)
                            {
                                numericUpDown5.Value = pair.Key + address_qishi;
                            }
                        }
                    }
                    dataGridView1.CurrentCell = dataGridView1[e.ColumnIndex, e.RowIndex];
                }
                else
                {
                    dataGridView1.ClearSelection();
                    x = 999;
                    y = 999;
                }
            }
        }
        int geshu = 0;

        // ch:起始地址变化后刷新表格：清空数据/名称区，按当前起始地址重绘变量名称位置
        private void RefreshFinsTable()
        {
            try
            {
                for (int r = 0; r < dataGridView1.Rows.Count; r++)
                {
                    for (int c = 0; c < dataGridView1.Columns.Count; c++)
                    {
                        dataGridView1[c, r].Style.BackColor = (r % 2 == 0) ? Color.White : Color.LightBlue;
                        dataGridView1[c, r].Value = "";
                    }
                }
                foreach (var kv in fins_dic)
                {
                    decimal qishi = decimal.Parse(kv.Value[1]);
                    decimal len = decimal.Parse(kv.Value[2]);
                    for (int j = 0; j < len; j++)
                    {
                        int t = int.Parse((qishi - address_qishi + j).ToString());
                        if (t >= 0 && t < 50 && fins_name.ContainsKey(t) && fins_data.ContainsKey(t))
                        {
                            dataGridView1[fins_name[t][0], fins_name[t][1]].Style.BackColor = Color.Green;
                            dataGridView1[fins_data[t][0], fins_data[t][1]].Style.BackColor = Color.Green;
                            dataGridView1[fins_name[t][0], fins_name[t][1]].Value = kv.Key;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog el = new ErrorLog();
                el.WriteLog("刷新表格失败:" + ex.Message);
            }
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                address_qishi = Math.Truncate(numericUpDown1.Value); // ch:R12 取整，轮询线程 int.Parse(address_qishi.ToString()) 需整数串
                wdini.WriteString("modbusrtu", "qishi", address_qishi.ToString());
                RefreshFinsTable(); // ch:起始地址变化后立即刷新表格（清空并按新起点重绘）
            }
        }

        private void numericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                address_length = numericUpDown2.Value;
                wdini.WriteString("modbusrtu", "zongchang", numericUpDown2.Value.ToString());
            }
        }

        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                lunxun_time = Math.Truncate(numericUpDown3.Value); // ch:R12 取整，(0,1) 小数会让 Sleep((int)v)=Sleep(0) 忙等
                wdini.WriteString("modbusrtu", "lunxun_time", lunxun_time.ToString());

            }
        }
        private bool front = false;
        private void timer1_Tick(object sender, EventArgs e)
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

        // ch:P2 相机9 周期写回：① 与 xie_wu/xie 消费共用 _rtuIoLock，保证 [4](值)/[5](通道) 原子配对；
        //   ② 通道取"反馈通道"[3]（原实现误取触发通道[0] → 每秒把返回值写进触发寄存器，且破坏配对可写错地址段）
        public void SetCamera9PeriodicPending()
        {
            lock (_rtuIoLock)
            {
                if (camera_dic.ContainsKey(9) && camera_dic[9].Length > 5 && camera_dic[9][2] == "true")
                {
                    camera_dic[9][4] = camera_dic[9][1];
                    camera_dic[9][5] = camera_dic[9][3];
                    if (fins_xie.Length > 8) fins_xie[8] = true;
                }
            }
        }

        private void timer2_Tick(object sender, EventArgs e)
        {
            try
            {
                SetCamera9PeriodicPending();
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void c1_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c1.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
                else
                {
                    fins_lunxunen = false;
                }
                wdini.WriteString("modbusrtu", "modbusrtu_lunxunen", fins_lunxunen.ToString());
            }
        }

        private void c2_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c2.CheckState == CheckState.Checked)
                {
                    fins_en = true;
                }
                else
                {
                    fins_en = false;
                }
                wdini.WriteString("modbusrtu", "modbusrtu_en", fins_en.ToString());

            }
        }

        private void b1_Click(object sender, EventArgs e)
        {
            clearing = true; // ch:清除期间暂停轮询与输出线程，防止清除后旧数据写回
            try
            {
            dataGridView1.Visible = false;
            wdini.WriteString("modbusrtu", "geshu", "0");
            fins_dic.Clear();
            // ch:清掉相机反馈映射，使检测结果输出彻底停止
            for (int i = 1; i <= 8; i++)
            {
                if (camera_dic.ContainsKey(i))
                {
                    camera_dic[i][4] = "无";
                    camera_dic[i][5] = "无";
                }
            }

            //for (int i = 0; i < 50; i++)
            //{
            //    dataGridView1[fins_name[i][0], fins_name[i][1]].Value = "";
            //}
            for (int i = 0; i < 10; i++)
            {
                if ((i + 1) % 2 == 0)
                    for (int j = 0; j < 10; j++)
                    {
                        dataGridView1[j, i].Style.BackColor = Color.LightBlue;
                        dataGridView1[j, i].Value = "";
                    }
                else
                    for (int j = 0; j < 10; j++)
                    {
                        dataGridView1[j, i].Style.BackColor = Color.White;
                        dataGridView1[j, i].Value = "";
                    }
            }
            dataGridView1.Visible = true;
            b4_Click(null, null);
            }
            finally
            {
                clearing = false;
            }
        }
        public bool[] fins_xie = new bool[] { false, false, false, false, false, false, false, false, false, false };
        private Thread fins_duxie;
        private volatile bool clearing = false; // ch:清除配置期间暂停轮询/输出，防止清除后被旧数据写回

        private void b2_Click(object sender, EventArgs e)
        {
            try
            {
                fins_lunxunen = false;
                decimal xuanzhong_temp = 0;
                bool chongdie = false;
                if (cb3.Text.Length > 1 && cb2.Text.Length > 1 && t1.Text.Length > 0)
                {
                    if ((cb3.Text == "string" || cb3.Text == "int") || (numericUpDown5.Value % 2 == 0))
                    {
                        if (numericUpDown5.Value >= numericUpDown1.Value && numericUpDown1.Value + numericUpDown2.Value >= numericUpDown4.Value + numericUpDown5.Value)
                        {
                            if (t1.Text.Length > 0)
                            {
                                if (fins_dic.Count < 10)
                                {
                                    if (fins_dic.Count > 0)
                                    {
                                        foreach (var pat in fins_dic)
                                        {
                                            if (decimal.Parse(pat.Value[1]) >= numericUpDown5.Value + numericUpDown4.Value || decimal.Parse(pat.Value[1]) + decimal.Parse(pat.Value[2]) <= numericUpDown5.Value)
                                            {

                                            }
                                            else
                                            {
                                                chongdie = true;
                                            }
                                        }
                                    }
                                    // ch:R12 手动新增通道与 Load 路径同款加固：①重名去重；②地址/长度取整入 fins_dic 与 ini；
                                    //   ③上色索引改 (int) 强转 + 越界守卫（原 int.Parse(decimal.ToString()) 遇小数抛、无守卫会 KeyNotFound）
                                    string nmAdd = t1.Text.Trim();
                                    if (nmAdd.Length == 0) nmAdd = "通道" + (fins_dic.Count + 1);
                                    decimal qAdd = Math.Truncate(numericUpDown5.Value);
                                    decimal lenAdd = Math.Truncate(numericUpDown4.Value);
                                    bool chongming = fins_dic.ContainsKey(nmAdd);
                                    if (chongdie || chongming)
                                    {
                                        MessageBox.Show(chongdie ? "数据有重叠" : "通道名已存在：" + nmAdd);
                                    }
                                    else
                                    {
                                        fins_dic.Add(nmAdd, new string[] { nmAdd, qAdd.ToString(), lenAdd.ToString(), cb2.Text, cb3.Text });
                                        for (int i = 0; i < lenAdd; i++)
                                        {
                                            xuanzhong_temp = qAdd - Math.Truncate(numericUpDown1.Value) + i;
                                            int tAdd = (int)xuanzhong_temp;
                                            if (xuanzhong_temp < 0 || xuanzhong_temp >= 50 || !fins_name.ContainsKey(tAdd) || !fins_data.ContainsKey(tAdd))
                                                continue;
                                            dataGridView1[fins_name[tAdd][0], fins_name[tAdd][1]].Style.BackColor = Color.Green;
                                            dataGridView1[fins_data[tAdd][0], fins_data[tAdd][1]].Style.BackColor = Color.Green;
                                            dataGridView1[fins_name[tAdd][0], fins_name[tAdd][1]].Value = nmAdd;
                                        }
                                        geshu = fins_dic.Count;
                                        wdini.WriteString("modbusrtu", "geshu", fins_dic.Count.ToString());
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "name", nmAdd);
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "qishi", qAdd.ToString());
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "changdu", lenAdd.ToString());
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "gaodiwei", cb2.Text);
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "geshi", cb3.Text);
                                    }
                                }
                                else
                                {
                                    MessageBox.Show("数据组数不能超过10组");
                                }
                            }
                            else
                            {
                                MessageBox.Show("请设置此段数据的名称");
                            }
                        }
                        else
                        {
                            MessageBox.Show("数据地址超出限定范围");
                        }
                    }
                    else
                    {
                        MessageBox.Show("数据长度设置错误");
                    }
                }
                else
                {
                    MessageBox.Show("数据不完整");
                }
                if (c1.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                if (c1.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
        }

        private void cb4_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb4.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb4.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb5_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb5.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb5.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb6_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb6.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb6.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb7_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][0] = cb7.Text;
                wdini.WriteString("c4"+ "modbusrtu", "chufa", cb7.Text);
            }
        }

        private void cb8_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][0] = cb8.Text;
                wdini.WriteString("c5"+ "modbusrtu", "chufa", cb8.Text);
            }
        }

        private void cb8_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb8.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb8.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb9_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb9.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb9.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb10_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb10.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb10.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb11_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb11.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb11.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb12_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb12.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("心跳"))
                        {
                            cb12.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb13_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb13.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb13.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb14_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb14.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb14.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb15_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb15.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb15.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb16_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb16.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb16.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb17_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb17.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb17.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb18_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb18.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb18.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb19_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb19.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb19.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb20_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb20.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            cb20.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb21_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb21.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb21.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb4_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][0] = cb4.Text;
                wdini.WriteString("c1"+ "modbusrtu", "chufa", cb4.Text);
            }
        }

        private void cb5_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][0] = cb5.Text;
                wdini.WriteString("c2"+ "modbusrtu", "chufa", cb5.Text);
            }
        }

        private void cb6_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][0] = cb6.Text;
                wdini.WriteString("c3"+ "modbusrtu", "chufa", cb6.Text);
            }
        }

        private void cb7_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                cb7.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            cb7.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void cb9_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][0] = cb9.Text;
                wdini.WriteString("c6"+ "modbusrtu", "chufa", cb9.Text);
            }
        }

        private void cb10_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][0] = cb10.Text;
                wdini.WriteString("c7"+ "modbusrtu", "chufa", cb10.Text);
            }
        }

        private void cb11_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][0] = cb11.Text;
                wdini.WriteString("c8"+ "modbusrtu", "chufa", cb11.Text);
            }
        }

        private void cb12_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][0] = cb12.Text;
                wdini.WriteString("c9"+ "modbusrtu", "chufa", cb12.Text);
            }
        }

        // ch:R22 反馈通道绑定唯一性：多相机绑同一反馈通道 = 同地址互相覆盖（移植 modbustcp R21）
        private bool _fankuiSyncing; // ch:R22 回退赋值时防重入

        private static bool IsUnboundFankui(string v)
        {
            return string.IsNullOrEmpty(v) || v == "0" || v == "无"; // ch:R22 未绑定态（ini 默认 "0"）不参与查重
        }

        private void BindFankui(int cam, ComboBox cb, string iniSection)
        {
            if (!chushihua || _fankuiSyncing)
                return;
            string newText = cb.Text;
            string oldText = camera_dic[cam][3];
            if (newText != oldText && !IsUnboundFankui(newText))
            {
                foreach (var kv in camera_dic)
                {
                    if (kv.Key != cam && kv.Value[3] == newText)
                    {
                        MsgErroeLog.WriteLog("反馈通道重复绑定被拒: 通道[" + newText + "] 已被相机" + kv.Key + "占用，相机" + cam + " 回退为[" + oldText + "]"); // ch:R22 只记日志不写入
                        _fankuiSyncing = true;
                        try
                        {
                            // ch:R22 DropDownList 下 Text 可能不在 Items（如未绑定 "0"），用 SelectedIndex 回退（-1=清空）
                            cb.SelectedIndex = cb.Items.IndexOf(oldText);
                        }
                        finally { _fankuiSyncing = false; }
                        MessageBox.Show(this, "反馈通道「" + newText + "」已绑定到相机" + kv.Key + "，两相机绑同一通道会互相覆盖数据，请选择其它通道。", "绑定冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }
            camera_dic[cam][3] = newText;
            wdini.WriteString(iniSection, "fankui", newText);
        }

        private void cb13_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(1, cb13, "c1modbusrtu");
        }

        private void cb14_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(2, cb14, "c2modbusrtu");
        }

        private void cb15_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(3, cb15, "c3modbusrtu");
        }

        private void cb16_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(4, cb16, "c4modbusrtu");
        }

        private void cb17_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(5, cb17, "c5modbusrtu");
        }

        private void cb18_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(6, cb18, "c6modbusrtu");
        }

        private void cb19_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(7, cb19, "c7modbusrtu");
        }

        private void cb20_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(8, cb20, "c8modbusrtu");
        }

        private void cb21_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[10][0] = cb21.Text;
                wdini.WriteString("c10"+ "modbusrtu", "chufa", cb21.Text);
            }
        }

        private void t2_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][1] = t2.Text;
                wdini.WriteString("c1"+ "modbusrtu", "fanhuizhi", t2.Text);
            }
        }

        private void t3_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][1] = t3.Text;
                wdini.WriteString("c2"+ "modbusrtu", "fanhuizhi", t3.Text);
            }
        }

        private void t4_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][1] = t4.Text;
                wdini.WriteString("c3"+ "modbusrtu", "fanhuizhi", t4.Text);
            }
        }

        private void t5_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][1] = t5.Text;
                wdini.WriteString("c4"+ "modbusrtu", "fanhuizhi", t5.Text);
            }
        }

        private void t6_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][1] = t6.Text;
                wdini.WriteString("c5"+ "modbusrtu", "fanhuizhi", t6.Text);
            }
        }

        private void t7_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][1] = t7.Text;
                wdini.WriteString("c6"+ "modbusrtu", "fanhuizhi", t7.Text);
            }
        }

        private void t8_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][1] = t8.Text;
                wdini.WriteString("c7"+ "modbusrtu", "fanhuizhi", t8.Text);
            }
        }

        private void t9_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][1] = t9.Text;
                wdini.WriteString("c8"+ "modbusrtu", "fanhuizhi", t9.Text);
            }
        }

        private void t10_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][1] = t10.Text;
                wdini.WriteString("c9"+ "modbusrtu", "fanhuizhi", t10.Text);
            }
        }

        private void t11_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[10][1] = t11.Text;
                wdini.WriteString("c10"+ "modbusrtu", "fanhuizhi", t11.Text);
            }
        }

        private void t20_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t20.Text = openFileDialog.FileName;
            }
        }

        private void t21_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t21.Text = openFileDialog.FileName;
            }
        }

        private void t22_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t22.Text = openFileDialog.FileName;
            }
        }

        private void t23_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t23.Text = openFileDialog.FileName;
            }
        }

        private void t24_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t24.Text = openFileDialog.FileName;
            }
        }

        private void t25_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t25.Text = openFileDialog.FileName;
            }
        }

        private void t26_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t26.Text = openFileDialog.FileName;
            }
        }

        private void t27_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                t27.Text = openFileDialog.FileName;
            }
        }

        private void c3_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c3.CheckState == CheckState.Checked)
                {
                    camera_dic[1][2] = "true";
                    wdini.WriteString("c1"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[1][2] = "false";
                    wdini.WriteString("c1"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c4_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c4.CheckState == CheckState.Checked)
                {
                    camera_dic[2][2] = "true";
                    wdini.WriteString("c2"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[2][2] = "false";
                    wdini.WriteString("c2"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c5_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c5.CheckState == CheckState.Checked)
                {
                    camera_dic[3][2] = "true";
                    wdini.WriteString("c3"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[3][2] = "false";
                    wdini.WriteString("c3"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c6_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c6.CheckState == CheckState.Checked)
                {
                    camera_dic[4][2] = "true";
                    wdini.WriteString("c4"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[4][2] = "false";
                    wdini.WriteString("c4"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c7_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c7.CheckState == CheckState.Checked)
                {
                    camera_dic[5][2] = "true";
                    wdini.WriteString("c5"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[5][2] = "false";
                    wdini.WriteString("c5"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c8_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c8.CheckState == CheckState.Checked)
                {
                    camera_dic[6][2] = "true";
                    wdini.WriteString("c6"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[6][2] = "false";
                    wdini.WriteString("c6"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c9_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c9.CheckState == CheckState.Checked)
                {
                    camera_dic[7][2] = "true";
                    wdini.WriteString("c7"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[7][2] = "false";
                    wdini.WriteString("c7"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c10_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c10.CheckState == CheckState.Checked)
                {
                    camera_dic[8][2] = "true";
                    wdini.WriteString("c8"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[8][2] = "false";
                    wdini.WriteString("c8"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c11_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c11.CheckState == CheckState.Checked)
                {
                    camera_dic[9][2] = "true";
                    wdini.WriteString("c9"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[9][2] = "false";
                    wdini.WriteString("c9"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void c12_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (c12.CheckState == CheckState.Checked)
                {
                    camera_dic[10][2] = "true";
                    wdini.WriteString("c10"+ "modbusrtu", "fanhuien", "true");
                }
                else
                {
                    camera_dic[10][2] = "false";
                    wdini.WriteString("c10"+ "modbusrtu", "fanhuien", "false");
                }
            }
        }

        private void b4_Click(object sender, EventArgs e)
        {
            cb12.Text = "";
            cb4.Text = "";
            cb13.Text = "";
            cb14.Text = "";
            cb5.Text = "";
            cb15.Text = "";
            cb6.Text = "";
            cb16.Text = "";
            cb7.Text = "";
            cb17.Text = "";
            cb8.Text = "";
            cb18.Text = "";
            cb9.Text = "";
            cb19.Text = "";
            cb10.Text = "";
            cb20.Text = "";
            cb11.Text = "";
            cb21.Text = "";
            t2.Text = "";
            t3.Text = "";
            t4.Text = "";
            t5.Text = "";
            t6.Text = "";
            t7.Text = "";
            t8.Text = "";
            t9.Text = "";
            t10.Text = "";
            t11.Text = "";
            c3.CheckState = CheckState.Unchecked;
            c4.CheckState = CheckState.Unchecked;
            c5.CheckState = CheckState.Unchecked;
            c6.CheckState = CheckState.Unchecked;
            c10.CheckState = CheckState.Unchecked;
            c9.CheckState = CheckState.Unchecked;
            c8.CheckState = CheckState.Unchecked;
            c7.CheckState = CheckState.Unchecked;
            c11.CheckState = CheckState.Unchecked;
            c12.CheckState = CheckState.Unchecked;
            cb4_SelectedIndexChanged(null, null);
            cb5_SelectedIndexChanged(null, null);
            cb6_SelectedIndexChanged(null, null);
            cb7_SelectedIndexChanged(null, null);
            cb8_SelectedIndexChanged(null, null);
            cb9_SelectedIndexChanged(null, null);
            cb10_SelectedIndexChanged(null, null);
            cb11_SelectedIndexChanged(null, null);
            cb12_SelectedIndexChanged(null, null);
            cb13_SelectedIndexChanged(null, null);
            cb14_SelectedIndexChanged(null, null);
            cb15_SelectedIndexChanged(null, null);
            cb16_SelectedIndexChanged(null, null);
            cb17_SelectedIndexChanged(null, null);
            cb18_SelectedIndexChanged(null, null);
            cb19_SelectedIndexChanged(null, null);
            cb20_SelectedIndexChanged(null, null);
            cb21_SelectedIndexChanged(null, null);
        }

        private void b3_Click(object sender, EventArgs e)
        {
            wdini.WriteString("change"+ "modbusrtu", "1", t12.Text);
            wdini.WriteString("change"+ "modbusrtu", "2", t13.Text);
            wdini.WriteString("change"+ "modbusrtu", "3", t14.Text);
            wdini.WriteString("change"+ "modbusrtu", "4", t15.Text);
            wdini.WriteString("change"+ "modbusrtu", "5", t16.Text);
            wdini.WriteString("change"+ "modbusrtu", "6", t17.Text);
            wdini.WriteString("change"+ "modbusrtu", "7", t18.Text);
            wdini.WriteString("change"+ "modbusrtu", "8", t19.Text);

            wdini.WriteString("path"+ "modbusrtu", "1", t20.Text);
            wdini.WriteString("path"+ "modbusrtu", "2", t21.Text);
            wdini.WriteString("path"+ "modbusrtu", "3", t22.Text);
            wdini.WriteString("path"+ "modbusrtu", "4", t23.Text);
            wdini.WriteString("path"+ "modbusrtu", "5", t24.Text);
            wdini.WriteString("path"+ "modbusrtu", "6", t25.Text);
            wdini.WriteString("path"+ "modbusrtu", "7", t26.Text);
            wdini.WriteString("path"+ "modbusrtu", "8", t27.Text);
        }
        public string GetMiddleValue(string str, string sta, string end)
        {
            Regex rg = new Regex("(?<=(" + sta + "))[.\\s\\S]*?(?=(" + end + "))", RegexOptions.Multiline | RegexOptions.Singleline);
            return rg.Match(str).Value;
        }
        private int _lastGridUiTick;

        // ch:N1 与 FormModbus 一致的 UI 刷新节流（250ms）：轮询线程不得高频裸写 dataGridView1
        private bool AllowModbusGridRefresh()
        {
            if (!Visible || IsDisposed || !IsHandleCreated)
                return false;
            int t = Environment.TickCount;
            if (unchecked(t - _lastGridUiTick) < 250)
                return false;
            _lastGridUiTick = t;
            return true;
        }

        private void SetModbusGridValue(int col, int row, object value)
        {
            if (col < 0 || row < 0)
                return;
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return; // ch:R22 窗口未创建/已销毁不入队
                if (InvokeRequired)
                {
                    // ch:R22 移植 FormModbus(R21) 头号嫌疑修复：轮询/写回线程直写 dataGridView1 单元格被
                    // CheckForIllegalCrossThreadCalls=false 掩盖为控件状态累积损坏(配置窗越开越慢)，
                    // 改为 BeginInvoke 封送到 UI 线程；调用方已有 Visible+250ms 门控，入队量有界
                    BeginInvoke(new Action<int, int, object>(SetModbusGridValue), new object[] { col, row, value });
                    return;
                }
                dataGridView1[col, row].Value = value;
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        void Fins_duxie()
        {
            while (true)
            {
                // ch:R10-3 原 int.Parse(decimal.ToString()) 在区域小数点/ini 带小数时抛 FormatException，且在 try 外直接杀死轮询线程；
                // 另：lunxun_time<=0 时补 50ms 小睡，避免 while(true) 全速空转吃满一核
                // ch:R12 判据由 >0 改为 >=1：原 0<lunxun_time<1 时 (int) 截成 0 → Sleep(0) 忙等且 if 仍为真 → 打满单核（NUD3 最小值 10，ini 手改 0.5 即绕过）
                bool pollOn = lunxun_time >= 1;
                Thread.Sleep(pollOn ? (int)lunxun_time : 50);
                if (pollOn)
                {
                    try
                    {
                        if (chushihua)
                        {
                            if (fins_en && fins_lunxunen && !clearing)
                            {
                                if (busRtuClient == null) continue;
                                bool gridUi = AllowModbusGridRefresh(); // ch:N1 每轮取一次（勿每次写入都调用，否则 250ms 只放行一次）
                                int xuanzhong_temp = 0;
                                string fins_temp = "";
                                string shuju_temp = "";
                                if (fins_dic.Count > 0)
                                {
                                    foreach (var par in fins_dic)
                                    {
                                        shuju_temp = "";
                                        bool commFailed = false; // ch:记录本轮是否有读取失败，用于触发断线自动重连
                                        for (int j = 0; j < int.Parse(par.Value[2]); j++)
                                        {
                                            if (par.Value[4] == "int")
                                            {

                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取short变量
                                                var readRtu = busRtuClient.ReadInt16((int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readRtu.IsSuccess;
                                                DemoUtils.ReadResultRender1(readRtu, (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "string")
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readRtu = busRtuClient.ReadString((int.Parse(par.Value[1]) + j).ToString(), 1);
                                                commFailed |= !readRtu.IsSuccess;
                                                DemoUtils.ReadResultRender1(readRtu, (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "long" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readRtu = busRtuClient.ReadInt32((int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readRtu.IsSuccess;
                                                DemoUtils.ReadResultRender1(readRtu, (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "float" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readRtu = busRtuClient.ReadFloat((int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readRtu.IsSuccess;
                                                DemoUtils.ReadResultRender1(readRtu, (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                        }
                                        if (commFailed) TryAutoReconnectRtu(); // ch:检测到读取失败则触发（方法内部节流）断线自动重连
                                        if (par.Value[3] == "触发")
                                        {
                                            foreach (var pap in camera_dic)
                                            {
                                                if (pap.Value[0] == par.Value[0])
                                                {
                                                    if (pap.Key == 10 && qiehuanzhong == 0)
                                                    {

                                                        if (qiehuan(shuju_temp.Replace("\0", "")) == 1)
                                                        {
                                                            if (File.Exists(lujing.Replace("\0", "")))
                                                            {
                                                                qiehuanzhong = 1;
                                                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(shuju_temp, pap.Key.ToString(), "0");
                                                                getData(this, E);
                                                            }
                                                            else
                                                            {
                                                                MsgErroeLog.WriteLog("方案路径:" + lujing + ":不存在!");
                                                            }
                                                        }
                                                    }
                                                    else if (pap.Key != 10)
                                                    {
                                                        if (camera_dic[pap.Key][2] == "true")
                                                        {
                                                            if (shuju_temp != camera_dic[pap.Key][1])
                                                            {
                                                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(shuju_temp, pap.Key.ToString(), fins_zuhe[camera_dic[pap.Key][0]]);
                                                                getData(this, E);
                                                                camera_dic[pap.Key][1] = shuju_temp; // ch:P2-⑥ 记录已处理触发值，防止 PLC 触发字保持期间重复触发采图
                                                            }
                                                        }

                                                    }
                                                }
                                            }
                                        }
                                    }

                                }
                                if (fins_temp.Contains(":"))
                                {
                                    MsgErroeLog.WriteLog(fins_temp + "_modbusrtu");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog(ex.Message + "modbusrtu");
                    }
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
        }
        public void xie(string value)
        {
            try
            {
                if (!chushihua || !fins_en || fins_dic.Count == 0) return;

                bool gridUi = AllowModbusGridRefresh(); // ch:N1残留 写回回显节流，跨线程写 GridView 前统一封送
                string fins_temp = "";
                foreach (var pat in camera_dic)
                {
                    if (pat.Value[5] != "无")
                    {
                        try // ch:P2 每相机独立 try：单个相机的坏值/越界不再中断整表写回（原实现一个异常饿死其余相机）
                        {
                        fins_temp = ""; // ch:P2-② 每轮重置写回渲染结果，避免上一相机失败串入本相机判定
                        bool anyWriteFailed = false; // ch:P2-④ 聚合本相机所有写回的真实结果（多寄存器循环写逐次与）
                        if (busRtuClient == null) // ch:P2 客户端为空也计入失败：否则 pending 永不超限、每帧 NRE 刷日志
                        {
                            MsgErroeLog.WriteLog("modbusrtu 写回失败: 客户端为空 cam=" + pat.Key);
                            if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无";
                            continue;
                        }
                        foreach (var par in fins_dic)
                        {
                            if (pat.Value[5] == par.Value[0])
                            {
                                int addr_start;
                                if (!int.TryParse(par.Value[1], out addr_start)) { anyWriteFailed = true; MsgErroeLog.WriteLog("写回地址解析失败(保留pending):" + par.Value[1]); break; } // ch:P2 裸 Parse 改 TryParse
                                string fmt = par.Value[4];
                                int regLen; // ch:R22 写回长度截断：移植 modbustcp(R21)，值数按通道登记长度 Math.Min 截断，防溢入相邻通道(多相机数据互串)
                                if (!int.TryParse(par.Value[2], out regLen)) { anyWriteFailed = true; MsgErroeLog.WriteLog("写回通道长度解析失败(保留pending):" + par.Value[2]); break; } // ch:R22 与地址解析同款处理

                                if (fmt == "int")
                                {
                                    // 逗号分隔 → short数组, FC16批量写
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R22 写回截断：值数按通道登记长度截断，与 modbustcp(R21) 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    short[] vals = new short[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    if (!DemoUtils.WriteResultRenderOk(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    for (int j = 0; j < vals.Length; j++)
                                    {
                                        if (!gridUi) break; // ch:R22 网格回写守卫，移植 modbustcp(R21)
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R22 网格越界守卫：PLC已写成功，仅跳过UI回写，防 KeyNotFound 被 catch 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "long")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R22 long 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R22 写回截断，与 modbustcp(R21) 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    int[] vals = new int[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (int)Math.Round(double.Parse(parts[i].Trim()));
                                    if (!DemoUtils.WriteResultRenderOk(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        if (!gridUi) break; // ch:R22 网格回写守卫，移植 modbustcp(R21)
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R22 网格越界守卫，防 KeyNotFound 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "float")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R22 float 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R22 写回截断，与 modbustcp(R21) 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    float[] vals = new float[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = float.Parse(parts[i].Trim());
                                    if (!DemoUtils.WriteResultRenderOk(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        if (!gridUi) break; // ch:R22 网格回写守卫，移植 modbustcp(R21)
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R22 网格越界守卫，防 KeyNotFound 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "string")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R22 string 每值占1寄存器，截断与 modbustcp(R21) 语义对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    for (int j = 0; j < writeCount; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (!DemoUtils.WriteResultRenderOk(() => busRtuClient.Write((addr_start + j).ToString(), parts[j].Trim()), (addr_start + j).ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④ 逐寄存器聚合，避免末次成功掩盖前次失败
                                        if (gridUi && xuanzhong_temp >= 0 && fins_data.ContainsKey(xuanzhong_temp)) // ch:R22 网格越界守卫，防 KeyNotFound 误判写回失败
                                            SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                break;
                            }
                        }
                        // ch:P2-④ 以逐次写入的真实 bool 结果聚合判定（替代文本判定），避免多寄存器循环写"末次成功掩盖前次失败"
                        if (!anyWriteFailed)
                        {
                            pat.Value[5] = "无";
                            NotePendingWriteOk(pat.Key); // ch:P2 写成功清除失败计数
                        }
                        else
                        {
                            MsgErroeLog.WriteLog("普通写回失败(保留 pending) cam=" + pat.Key + " ch=" + pat.Value[5] + " val=[" + pat.Value[4] + "] msg=" + fins_temp);
                            if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无"; // ch:P2 超限/超时丢弃，防断线恢复后补发旧结果
                        }
                        }
                        catch (Exception exPer)
                        {
                            MsgErroeLog.WriteLog("写回单相机异常 cam=" + pat.Key + ":" + exPer.Message);
                            // ch:P2 异常同样计入 TTL 失败：坏 pending（值畸形/越界等）否则会无限重试 + 每帧刷日志
                            if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "modbusrtu");
            }
        }
        // 由检测线程按相机提交不可变结果；登记和消费放在同一事务内。
        public void WriteCameraResult(int camIndex, string value)
        {
            lock (_rtuIoLock)
            {
                if (clearing || !chushihua || !fins_en || !camera_dic.ContainsKey(camIndex))
                    return;
                camera_dic[camIndex][4] = value;
                camera_dic[camIndex][5] = camera_dic[camIndex][3];
                if (camIndex >= 1 && camIndex <= fins_xie.Length)
                    fins_xie[camIndex - 1] = true;
                xie_wu(value);
            }
        }
        // ch:P2 pending TTL：写失败保留 pending 后若长期写不出去（PLC 断线/掉线），恢复瞬间会把旧结果发给 PLC（错误工位数据）。
        //   记录"首次失败时刻 + 连续失败次数"，连续 3 次或超过 10 秒即丢弃并告警，避免无限重试。
        private const int PendingMaxFailTimes = 3;
        private const int PendingMaxAgeMs = 10000;
        private readonly Dictionary<int, long> _pendingFirstFailMs = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _pendingFailCount = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _pendingFailId = new Dictionary<int, string>(); // ch:P2 失败计数绑定的"结果身份"([5]+[4])
        private readonly object _pendingTtlLock = new object();

        // ch:P2 写回失败登记：返回 true 表示该结果已超限，调用方应丢弃 pending。以 [5]+[4] 作为"结果身份"，
        //   身份变化（新帧登记覆盖了 pending）即重新计数，避免新结果继承旧失败计数/旧起始时刻被误丢。
        private bool NotePendingWriteFailed(int camIndex)
        {
            lock (_pendingTtlLock)
            {
                long now = Environment.TickCount;
                string id = "";
                string[] row;
                if (camera_dic.TryGetValue(camIndex, out row) && row != null && row.Length > 5)
                    id = (row[5] ?? "") + "\u0001" + (row[4] ?? "");
                string lastId;
                if (!_pendingFailId.TryGetValue(camIndex, out lastId) || lastId != id)
                {
                    _pendingFailId[camIndex] = id;
                    _pendingFirstFailMs[camIndex] = now;
                    _pendingFailCount[camIndex] = 1;
                    return false; // 新结果首次失败：保留待重试
                }
                int n;
                _pendingFailCount.TryGetValue(camIndex, out n);
                n++;
                _pendingFailCount[camIndex] = n;
                long first;
                if (!_pendingFirstFailMs.TryGetValue(camIndex, out first)) first = now;
                if (n >= PendingMaxFailTimes || unchecked((int)now - (int)first) > PendingMaxAgeMs) // ch:P2 用 int 差值：long 相减在 TickCount 回绕瞬间会变巨大负数使时间支路失效
                {
                    _pendingFailId.Remove(camIndex);
                    _pendingFirstFailMs.Remove(camIndex);
                    _pendingFailCount.Remove(camIndex);
                    MsgErroeLog.WriteLog("写回 pending 超限已丢弃(防断线恢复后补发旧结果) cam=" + camIndex + " 连续失败=" + n);
                    return true;
                }
                return false;
            }
        }

        private void NotePendingWriteOk(int camIndex)
        {
            lock (_pendingTtlLock)
            {
                _pendingFailId.Remove(camIndex);
                _pendingFirstFailMs.Remove(camIndex);
                _pendingFailCount.Remove(camIndex);
            }
        }

        // ch:P0 方案切换写回暂存：与 xie_wu 消费共用 _rtuIoLock，保证 [4](值)/[5](通道) 原子配对，消除跨线程撕裂
        public void SetSwitchPending(int camIndex)
        {
            lock (_rtuIoLock)
            {
                if (camera_dic.ContainsKey(camIndex) && !camera_dic[camIndex][1].Contains("无"))
                {
                    camera_dic[camIndex][4] = camera_dic[camIndex][1];
                    camera_dic[camIndex][5] = camera_dic[camIndex][0];
                    if (camIndex >= 1 && camIndex <= fins_xie.Length) fins_xie[camIndex - 1] = true;
                }
            }
        }

        public void xie_wu(string value)
        {
            lock (_rtuIoLock)
            {
                try
                {
                    if (!chushihua || !fins_en || fins_dic.Count == 0) return;
                    if (clearing) return;

                    foreach (var pat in camera_dic)
                    {
                        if (pat.Value[5] != "无")
                        {
                            try // ch:P2 每相机独立 try：单个相机异常不再中断其余相机的极速写
                            {
                            bool patWrote = false; // ch:P1-⑧ 是否发起过写
                            bool patWriteOk = true; // ch:P1-⑧ 写是否全部成功
                            string fmt = ""; // ch:P1-⑧ 供失败日志使用
                            int addr_start;
                            if (busRtuClient == null) // ch:P2 客户端为空也计入失败：否则 pending 永不超限、每帧 NRE 刷日志
                            {
                                MsgErroeLog.WriteLog("modbusrtu 极速写失败: 客户端为空 cam=" + pat.Key);
                                if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无";
                                continue;
                            }
                            foreach (var par in fins_dic)
                            {
                                if (pat.Value[5] == par.Value[0])
                                {
                                    if (!int.TryParse(par.Value[1], out addr_start)) { patWriteOk = false; MsgErroeLog.WriteLog("极速写地址解析失败(保留pending):" + par.Value[1]); break; } // ch:P2 裸 Parse 改 TryParse
                                    fmt = par.Value[4];
                                    int regLen; // ch:R22 极速写长度截断：与 modbustcp XieWuWriteOne 对齐，值数按通道登记长度截断，防溢入相邻通道(多相机数据互串)
                                    if (!int.TryParse(par.Value[2], out regLen)) { patWriteOk = false; MsgErroeLog.WriteLog("极速写通道长度解析失败(保留pending):" + par.Value[2]); break; } // ch:R22 与地址解析同款处理

                                if (fmt == "int")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R22 极速写截断：值数按通道登记长度截断，与 modbustcp XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("极速写超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    short[] vals = new short[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
                                    patWrote = true; if (!writeOk) patWriteOk = false; // ch:P1-⑧ 跟踪写结果
                                    try
                                    {
                                        if (this.IsHandleCreated)
                                            this.BeginInvoke(new Action(() =>
                                            {
                                                try
                                                {
                                                    for (int j = 0; j < vals.Length; j++)
                                                    {
                                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                                        if (fins_data.ContainsKey(xuanzhong_temp))
                                                            dataGridView1[fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1]].Value = writeOk ? "已发送" : "发送失败";
                                                    }
                                                }
                                                catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                            }));
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                }
                                else if (fmt == "long")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R22 long 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R22 极速写截断，与 modbustcp XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("极速写超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    int[] vals = new int[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (int)Math.Round(double.Parse(parts[i].Trim()));
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
                                    patWrote = true; if (!writeOk) patWriteOk = false; // ch:P1-⑧ 跟踪写结果
                                    try
                                    {
                                        if (this.IsHandleCreated)
                                            this.BeginInvoke(new Action(() =>
                                            {
                                                try
                                                {
                                                    for (int j = 0; j < vals.Length * 2; j++)
                                                    {
                                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                                        if (fins_data.ContainsKey(xuanzhong_temp))
                                                            dataGridView1[fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1]].Value = writeOk ? "已发送" : "发送失败";
                                                    }
                                                }
                                                catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                            }));
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                }
                                else if (fmt == "float")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R22 float 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R22 极速写截断，与 modbustcp XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("极速写超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    float[] vals = new float[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = float.Parse(parts[i].Trim());
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
                                    patWrote = true; if (!writeOk) patWriteOk = false; // ch:P1-⑧ 跟踪写结果
                                    try
                                    {
                                        if (this.IsHandleCreated)
                                            this.BeginInvoke(new Action(() =>
                                            {
                                                try
                                                {
                                                    for (int j = 0; j < vals.Length * 2; j++)
                                                    {
                                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                                        if (fins_data.ContainsKey(xuanzhong_temp))
                                                            dataGridView1[fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1]].Value = writeOk ? "已发送" : "发送失败";
                                                    }
                                                }
                                                catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                            }));
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                }
                                else if (fmt == "string")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R22 string 每值占1寄存器，截断与 modbustcp XieWuWriteOne 语义对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("极速写超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R22 仅截断时记一条
                                    bool writeOk = true; // ch:R7 聚合逐寄存器写结果，任一失败即整体失败
                                    patWrote = true;
                                    for (int j = 0; j < writeCount; j++)
                                    {
                                        OperateResult wr = busRtuClient.Write((addr_start + j).ToString(), parts[j].Trim());
                                        if (wr == null || !wr.IsSuccess)
                                        {
                                            writeOk = false;
                                            break;
                                        }
                                    }
                                    try
                                    {
                                        if (this.IsHandleCreated)
                                            this.BeginInvoke(new Action(() =>
                                            {
                                                try
                                                {
                                                    for (int j = 0; j < writeCount; j++) // ch:R22 与写入同长回显，截断后不显示未写寄存器
                                                    {
                                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                                        if (fins_data.ContainsKey(xuanzhong_temp))
                                                            dataGridView1[fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1]].Value = writeOk ? "已发送" : "发送失败";
                                                    }
                                                }
                                                catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                            }));
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
                                }
                                break;
                            }
                        }
                        if (patWrote && patWriteOk)
                        {
                            pat.Value[5] = "无"; // ch:P1-⑧ 仅写成功才清 pending，避免写失败永久丢失结果
                            NotePendingWriteOk(pat.Key); // ch:P2 写成功清除失败计数
                        }
                        else if (!patWriteOk)
                        {
                            MsgErroeLog.WriteLog("modbusrtu 极速写失败 cam=" + pat.Key + " fmt=" + fmt + " value=[" + pat.Value[4] + "]");
                            if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无"; // ch:P2 超限/超时丢弃，防断线恢复后补发旧结果
                        }
                            }
                            catch (Exception exPer)
                            {
                                MsgErroeLog.WriteLog("极速写单相机异常 cam=" + pat.Key + ":" + exPer.Message);
                                // ch:P2 异常同样计入 TTL 失败：坏 pending（值畸形/越界等）否则会无限重试 + 每帧刷日志
                                if (NotePendingWriteFailed(pat.Key)) pat.Value[5] = "无";
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "modbusrtu_xie_wu");
            }
        }
        }
        private int qiehuan(string aa)
        {
            if (t12.Text == aa)
            {
                zifu = aa;
                lujing = t20.Text;
                return 1;
            }
            else if (t13.Text == aa)
            {
                zifu = aa;
                lujing = t21.Text;
                return 1;
            }
            else if (t14.Text == aa)
            {
                zifu = aa;
                lujing = t22.Text;
                return 1;
            }
            else if (t15.Text == aa)
            {
                zifu = aa;
                lujing = t23.Text;
                return 1;
            }
            else if (t16.Text == aa)
            {
                zifu = aa;
                lujing = t24.Text;
                return 1;
            }
            else if (t17.Text == aa)
            {
                zifu = aa;
                lujing = t25.Text;
                return 1;
            }
            else if (t18.Text == aa)
            {
                zifu = aa;
                lujing = t26.Text;
                return 1;
            }
            else if (t19.Text == aa)
            {
                zifu = aa;
                lujing = t27.Text;
                return 1;
            }
            else
            {
                return 0;
            }
        }

        private void comboBox2_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            _rtuDataFormatIndex = comboBox2.SelectedIndex; // ch:P2-⑤ UI 线程缓存，供后台重连线程使用
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "abcd", comboBox2.Text);
            }
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "baudRate", textBox2.Text);
            }
        }

        private void textBox16_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "dataBits", textBox16.Text);
            }
        }

        private void textBox17_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "stopBits", textBox17.Text);
            }
        }

        private void textBox15_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "station", textBox15.Text);
            }
        }

        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "PortName", comboBox3.Text);
            }
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbusrtu", "Parity", comboBox1.Text);
            }
        }

        private void dataGridView1_RowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            if (sender is DataGridView)
            {
                DataGridView dgv = (DataGridView)sender;
                if ((e.RowIndex + 1) % 2 == 0)//如果该行为2的倍数 则上色
                {
                    dgv.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.LightBlue;
                }

            }
        }
    }
}
