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
        public int qiehuanzhong = 0;
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


        private void FormSiemens_Load( object sender, EventArgs e )
        {
            panel2.Enabled = false;
            comboBox1.SelectedIndex = 0;



            comboBox2.SelectedIndex = 0;
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

            fins_lunxunen = bool.Parse(wdini.ReadString("modbusrtu", "modbusrtu_lunxunen", "false"));
            fins_en = bool.Parse(wdini.ReadString("modbusrtu", "modbusrtu_en", "false"));
            address_qishi = decimal.Parse(wdini.ReadString("modbusrtu", "qishi", "0"));
            address_length = decimal.Parse(wdini.ReadString("modbusrtu", "zongchang", "1"));
            lunxun_time = decimal.Parse(wdini.ReadString("modbusrtu", "lunxun_time", "20"));
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
            geshu = int.Parse(wdini.ReadString("modbusrtu", "geshu", "0"));
            if (geshu > 0)
            {
                for (int i = 0; i < geshu; i++)
                {
                    fins_mingcheng = wdini.ReadString((i + 1).ToString()+ "modbusrtu", "name", "").Replace("\0", "");
                    fins_qishi = decimal.Parse(wdini.ReadString((i + 1).ToString() + "modbusrtu", "qishi", "0"));
                    fins_length = decimal.Parse(wdini.ReadString((i + 1).ToString() + "modbusrtu", "changdu", "0"));
                    ABCD = wdini.ReadString((i + 1).ToString() + "modbusrtu", "gaodiwei", "触发").Replace("\0", "");
                    fins_style = wdini.ReadString((i + 1).ToString() + "modbusrtu", "geshi", "int").Replace("\0", "");
                    fins_dic.Add(fins_mingcheng, new string[] { fins_mingcheng, fins_qishi.ToString(), fins_length.ToString(), ABCD, fins_style });
                    for (int j = 0; j < fins_length; j++)
                    {
                        xuanzhong_temp = fins_qishi - address_qishi + j;
                        dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                        dataGridView1[fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                        if (xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                        dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Value = fins_mingcheng;
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
                button2.Enabled = true;
                button1.Enabled = false;
                panel2.Enabled = true;

                userControlCurve1.ReadWriteNet = busRtuClient;
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog( ex.Message );
            }
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
                    busRtuClient = nc; // 原子替换引用，轮询线程下一轮即使用新连接
                    userControlCurve1.ReadWriteNet = nc;
                    MsgErroeLog.WriteLog( "RTU PLC 自动重连成功" );
                    try { old?.Close( ); } catch { }
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
                address_qishi = numericUpDown1.Value;
                wdini.WriteString("modbusrtu", "qishi", numericUpDown1.Value.ToString());
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
                lunxun_time = numericUpDown3.Value;
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

        private void timer2_Tick(object sender, EventArgs e)
        {
            try
            {
                if (camera_dic.ContainsKey(9) && camera_dic[9][2] == "true")
                {
                    camera_dic[9][4] = camera_dic[9][1];
                    camera_dic[9][5] = camera_dic[9][0];
                    fins_xie[8] = true;
                }
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
                                    if (chongdie)
                                    {
                                        MessageBox.Show("数据有重叠");
                                    }
                                    else
                                    {
                                        fins_dic.Add(t1.Text, new string[] { t1.Text, numericUpDown5.Value.ToString(), numericUpDown4.Value.ToString(), cb2.Text, cb3.Text });
                                        for (int i = 0; i < numericUpDown4.Value; i++)
                                        {
                                            xuanzhong_temp = numericUpDown5.Value - numericUpDown1.Value + i;
                                            dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                                            dataGridView1[fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                                            if (xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                            dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Value = t1.Text;
                                        }
                                        geshu = fins_dic.Count;
                                        wdini.WriteString("modbusrtu", "geshu", fins_dic.Count.ToString());
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "name", t1.Text);
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "qishi", numericUpDown5.Value.ToString());
                                        wdini.WriteString(geshu.ToString()+ "modbusrtu", "changdu", numericUpDown4.Value.ToString());
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

        private void cb13_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][3] = cb13.Text;
                wdini.WriteString("c1"+ "modbusrtu", "fankui", cb13.Text);
            }
        }

        private void cb14_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][3] = cb14.Text;
                wdini.WriteString("c2"+ "modbusrtu", "fankui", cb14.Text);
            }
        }

        private void cb15_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][3] = cb15.Text;
                wdini.WriteString("c3"+ "modbusrtu", "fankui", cb15.Text);
            }
        }

        private void cb16_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][3] = cb16.Text;
                wdini.WriteString("c4"+ "modbusrtu", "fankui", cb16.Text);
            }
        }

        private void cb17_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][3] = cb17.Text;
                wdini.WriteString("c5"+ "modbusrtu", "fankui", cb17.Text);
            }
        }

        private void cb18_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][3] = cb18.Text;
                wdini.WriteString("c6"+ "modbusrtu", "fankui", cb18.Text);
            }
        }

        private void cb19_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][3] = cb19.Text;
                wdini.WriteString("c7"+ "modbusrtu", "fankui", cb19.Text);
            }
        }

        private void cb20_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][3] = cb20.Text;
                wdini.WriteString("c8"+ "modbusrtu", "fankui", cb20.Text);
            }
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
                dataGridView1[col, row].Value = value;
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        void Fins_duxie()
        {
            while (true)
            {
                if (lunxun_time > 0)
                {
                    Thread.Sleep(int.Parse(lunxun_time.ToString()));
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
                        foreach (var par in fins_dic)
                        {
                            if (pat.Value[5] == par.Value[0])
                            {
                                int addr_start = int.Parse(par.Value[1]);
                                string fmt = par.Value[4];

                                if (fmt == "int")
                                {
                                    // 逗号分隔 → short数组, FC16批量写
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    short[] vals = new short[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    DemoUtils.WriteResultRender1(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp);
                                    for (int j = 0; j < vals.Length; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (gridUi)
                                            SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "long")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int[] vals = new int[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = (int)Math.Round(double.Parse(parts[i].Trim()));
                                    DemoUtils.WriteResultRender1(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp);
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (gridUi)
                                            SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "float")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    float[] vals = new float[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = float.Parse(parts[i].Trim());
                                    DemoUtils.WriteResultRender1(() => busRtuClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp);
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (gridUi)
                                            SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "string")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    for (int j = 0; j < parts.Length; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        DemoUtils.WriteResultRender1(() => busRtuClient.Write((addr_start + j).ToString(), parts[j].Trim()), (addr_start + j).ToString(), out fins_temp);
                                        if (gridUi)
                                            SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                break;
                            }
                        }
                        pat.Value[5] = "无";
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
                            foreach (var par in fins_dic)
                            {
                                if (pat.Value[5] == par.Value[0])
                                {
                                    int addr_start = int.Parse(par.Value[1]);
                                    string fmt = par.Value[4];

                                if (fmt == "int")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    short[] vals = new short[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
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
                                    int[] vals = new int[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = (int)Math.Round(double.Parse(parts[i].Trim()));
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
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
                                    float[] vals = new float[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = float.Parse(parts[i].Trim());
                                    OperateResult wr = busRtuClient.Write(addr_start.ToString(), vals); // ch:R7 捕获写结果
                                    bool writeOk = wr != null && wr.IsSuccess; // ch:R7 仅成功才显示"已发送"，失败显示"发送失败"
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
                                    bool writeOk = true; // ch:R7 聚合逐寄存器写结果，任一失败即整体失败
                                    for (int j = 0; j < parts.Length; j++)
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
                                                    for (int j = 0; j < parts.Length; j++)
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
                        pat.Value[5] = "无";
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
