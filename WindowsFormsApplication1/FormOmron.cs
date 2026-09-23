using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using HslCommunication.Profinet;
using System.Threading;
using HslCommunication;
using HslCommunication.Profinet.Omron;
using demo;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;

namespace WindowsFormsApplication1
{
    public partial class FormOmron : Form
    {
        public Label labelFallbackHint = new Label();

        public FormOmron( )
        {
            wdini.ReadINIFile(AppDomain.CurrentDomain.BaseDirectory + "//test.ini");
            InitializeComponent( );
            omronFinsNet = new OmronFinsNet( );
            omronFinsNet.ConnectTimeOut = 2000;

            // 在 tabPage2 (数据绑定页面) 底部添加提示 label（默认隐藏）
            labelFallbackHint.AutoSize = true;
            labelFallbackHint.TextAlign = ContentAlignment.MiddleLeft;
            labelFallbackHint.ForeColor = Color.Red;
            labelFallbackHint.Text = "写操作已降级为逐地址写入";
            labelFallbackHint.Visible = false;
            labelFallbackHint.Location = new Point(211, 380);
            tabPage2.Controls.Add(labelFallbackHint);
        }
        private ClassIni wdini = new ClassIni();
        private OmronFinsNet omronFinsNet = null;
        // ch:OMRON 断线自动重连控制（节流防重连风暴；仅在自动监控状态且为意外掉线时触发）
        private readonly object _omronReconnectLock = new object();
        private long _omronLastReconnectTick = 0;
        private volatile bool _omronAutoReconnect = false;
        private readonly object _omronIoLock = new object(); // ch:R4 串行化 pending 登记与消费
        public delegate void GetSeletionData(object Sender, SelectionChangedEventArgs e);
        public event GetSeletionData getData;
        private int x=999;
        private int y=999;
        Dictionary<string, string []> fins_dic = new Dictionary<string,string[]>();
        public  Dictionary<int, string[]> camera_dic = new Dictionary<int, string[]>();
        Dictionary<int, int[]> fins_name = new Dictionary<int, int[]>();
        Dictionary<int, int[]> fins_data = new Dictionary<int, int[]>();
        Dictionary<int, byte[]> fins_value = new Dictionary<int, byte[]>();
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

        public class SelectionChangedEventArgs : EventArgs
        {

            private string m_selection;

            private string m_camere;

            //本属性用于传递事件数据

            public string Selection
            {

                get { return m_selection; }

            }
            public string Camera
            {

                get { return m_camere; }

            }
            public SelectionChangedEventArgs(string selection,string camera)
            {

                m_selection = selection;
                m_camere = camera;
            }
        }

        private void FormSiemens_Load( object sender, EventArgs e )
        {
            fins_duxie = new Thread(new ThreadStart(Fins_duxie));
            fins_duxie.IsBackground = true;
            fins_duxie.Start();
            decimal xuanzhong_temp = 0;
            comboBox1.DataSource = HslCommunication.BasicFramework.SoftBasic.GetEnumValues<HslCommunication.Core.DataFormat>( );
            comboBox1.SelectedItem = HslCommunication.Core.DataFormat.CDAB;
            panel2.Enabled = false;
            Program.Language = Settings1.Default.language;
            Language( Program.Language );
            for (int i = 0; i < 10; i++)
            {
                dataGridView1.Rows.Add();
            }
            for (int i = 0; i < 50; i++)
            {            
                fins_data.Add(i, new int[] {i%10, i / 10*2 +1});
                fins_name.Add(i, new int[] { i % 10, i / 10*2 });
                fins_value.Add(i, new byte[] { 0x00,0x00 });
            }
          fins_lunxunen=bool.Parse( wdini.ReadString("fins", "fins_lunxunen", "false"));
          fins_en = bool.Parse(wdini.ReadString("fins", "fins_en", "false"));
            address_qishi = decimal.Parse(wdini.ReadString("fins", "qishi", "0"));
            address_length = decimal.Parse(wdini.ReadString("fins", "zongchang", "1"));
            // ch:R10-3 ini 手改成非法值/小数时 TryParse 回退默认，并夹取到控件范围，避免 Load 中断或 NUD 越界抛异常
            decimal lt_ini;
            if (!decimal.TryParse(wdini.ReadString("fins", "lunxun_time", "20"), out lt_ini)) lt_ini = 20;
            lt_ini = Math.Max(numericUpDown3.Minimum, Math.Min(numericUpDown3.Maximum, lt_ini));
            lunxun_time = lt_ini;
            numericUpDown1.Value = address_qishi;
            numericUpDown2.Value = address_length;
            numericUpDown3.Value = lunxun_time;
           
            comboBox1.Text = wdini.ReadString("fins", "abcd", "CDAB").Replace("\0", "");
            textBox1.Text = wdini.ReadString("fins", "ip", "127.0.0.1").Replace("\0", "");
            textBox2.Text = wdini.ReadString("fins", "port", "9600").Replace("\0", "");
            textBox16.Text = wdini.ReadString("fins", "cell", "0").Replace("\0", "");
            textBox15.Text = wdini.ReadString("fins", "local", "192").Replace("\0", "");
            if (fins_lunxunen)
            {
                checkBox1.CheckState = CheckState.Checked;
            }
            if (fins_en)
            {
                checkBox2.CheckState = CheckState.Checked;
                button1_Click(null, null);
            }
            geshu = int.Parse(wdini.ReadString("fins", "geshu", "0"));
            if (geshu > 0)
            {
                for (int i = 0; i < geshu; i++)
                {
                    fins_mingcheng= wdini.ReadString((i+1).ToString(), "name", "").Replace("\0", "");
                    fins_qishi = decimal.Parse(wdini.ReadString((i + 1).ToString(), "qishi", "0"));
                    fins_length= decimal.Parse(wdini.ReadString((i + 1).ToString(), "changdu", "0"));
                    ABCD= wdini.ReadString((i + 1).ToString(), "gaodiwei", "触发").Replace("\0", "");
                    fins_style = wdini.ReadString((i + 1).ToString(), "geshi", "int").Replace("\0", "");
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
            for(int i=0;i<10;i++)
            {
                camera_dic.Add(i+1,new string[] { wdini.ReadString("c" + (i + 1).ToString(), "chufa", " ").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString(), "fanhuizhi", "0").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString(), "fanhuien", "false").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString(), "fankui", "0").Replace("\0", ""),"无","无" });
            }
            comboBox5.Items.Add(camera_dic[1][0]);
            comboBox5.Text = camera_dic[1][0];
            textBox14.Text = camera_dic[1][1];
            if(camera_dic[1][2]=="true")
            checkBox3.CheckState = CheckState.Checked;
            comboBox6.Items.Add(camera_dic[1][3]);
            comboBox6.Text = camera_dic[1][3];

            comboBox8.Items.Add(camera_dic[2][0]);
            comboBox8.Text = camera_dic[2][0];
            textBox17.Text = camera_dic[2][1];
            if (camera_dic[2][2] == "true")
                checkBox4.CheckState = CheckState.Checked;
            comboBox7.Items.Add(camera_dic[2][3]);
            comboBox7.Text = camera_dic[2][3];

            comboBox10.Items.Add(camera_dic[3][0]);
            comboBox10.Text = camera_dic[3][0];
            textBox19.Text = camera_dic[3][1];
            if (camera_dic[3][2] == "true")
                checkBox5.CheckState = CheckState.Checked;
            comboBox9.Items.Add(camera_dic[3][3]);
            comboBox9.Text = camera_dic[3][3];

            comboBox12.Items.Add(camera_dic[4][0]);
            comboBox12.Text = camera_dic[4][0];
            textBox18.Text = camera_dic[4][1];
            if (camera_dic[4][2] == "true")
                checkBox6.CheckState = CheckState.Checked;
            comboBox11.Items.Add(camera_dic[4][3]);
            comboBox11.Text = camera_dic[4][3];

            comboBox14.Items.Add(camera_dic[5][0]);
            comboBox14.Text = camera_dic[5][0];
            textBox23.Text = camera_dic[5][1];
            if (camera_dic[5][2] == "true")
                checkBox10.CheckState = CheckState.Checked;
            comboBox13.Items.Add(camera_dic[5][3]);
            comboBox13.Text = camera_dic[5][3];

            comboBox16.Items.Add(camera_dic[6][0]);
            comboBox16.Text = camera_dic[6][0];
            textBox22.Text = camera_dic[6][1];
            if (camera_dic[6][2] == "true")
                checkBox9.CheckState = CheckState.Checked;
            comboBox15.Items.Add(camera_dic[6][3]);
            comboBox15.Text = camera_dic[6][3];

            comboBox18.Items.Add(camera_dic[7][0]);
            comboBox18.Text = camera_dic[7][0];
            textBox21.Text = camera_dic[7][1];
            if (camera_dic[7][2] == "true")
                checkBox8.CheckState = CheckState.Checked;
            comboBox17.Items.Add(camera_dic[7][3]);
            comboBox17.Text = camera_dic[7][3];

            comboBox20.Items.Add(camera_dic[8][0]);
            comboBox20.Text = camera_dic[8][0];
            textBox20.Text = camera_dic[8][1];
            if (camera_dic[8][2] == "true")
                checkBox7.CheckState = CheckState.Checked;
            comboBox19.Items.Add(camera_dic[8][3]);
            comboBox19.Text = camera_dic[8][3];

            comboBox4.Items.Add(camera_dic[9][0]);
            comboBox4.Text = camera_dic[9][0];
            textBox24.Text = camera_dic[9][1];
            if (camera_dic[9][2] == "true")
                checkBox11.CheckState = CheckState.Checked;

            comboBox21.Items.Add(camera_dic[10][0]);
            comboBox21.Text = camera_dic[10][0];
            textBox41.Text = camera_dic[10][1];
            if (camera_dic[10][2] == "true")
                checkBox12.CheckState = CheckState.Checked;

            textBox40.Text = wdini.ReadString("change", "1", "");
            textBox39.Text = wdini.ReadString("change", "2", "");
            textBox38.Text = wdini.ReadString("change", "3", "");
            textBox37.Text = wdini.ReadString("change", "4", "");
            textBox36.Text = wdini.ReadString("change", "5", "");
            textBox35.Text = wdini.ReadString("change", "6", "");
            textBox34.Text = wdini.ReadString("change", "7", "");
            textBox33.Text = wdini.ReadString("change", "8", "");
            textBox25.Text = wdini.ReadString("path", "1", "");
            textBox26.Text = wdini.ReadString("path", "2", "");
            textBox28.Text = wdini.ReadString("path", "3", "");
            textBox27.Text = wdini.ReadString("path", "4", "");
            textBox32.Text = wdini.ReadString("path", "5", "");
            textBox31.Text = wdini.ReadString("path", "6", "");
            textBox30.Text = wdini.ReadString("path", "7", "");
            textBox29.Text = wdini.ReadString("path", "8", "");

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
                Text = "Omron Read PLC Demo";
                label24.Text = "Unit Num";
                label23.Text = "PC Net Num";

                label1.Text = "Ip:";
                label3.Text = "Port:";
                button1.Text = "Connect";
                button2.Text = "Disconnect";
                label21.Text = "Address:";
                label6.Text = "address:";
                label7.Text = "result:";

                button_read_bool.Text = "Read Bit";
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
                groupBox4.Text = "Message reading test, hex string needs to be filled in";
            }
        }

        private void FormSiemens_FormClosing( object sender, FormClosingEventArgs e )
        {
            e.Cancel = true;
            this.Visible = false;
        }

        #region Connect And Close

        ErrorLog MsgErroeLog = new ErrorLog();

        private void button1_Click( object sender, EventArgs e )
        {
            // 连接
            if (!System.Net.IPAddress.TryParse( textBox1.Text, out System.Net.IPAddress address ))
            {
                MsgErroeLog.WriteLog(DemoUtils.IpAddressInputWrong );
                return;
            }

            if (!int.TryParse( textBox2.Text, out int port ))
            {
                MsgErroeLog.WriteLog( DemoUtils.PortInputWrong );
                return;
            }


            if (!byte.TryParse( textBox15.Text, out byte SA1 ))
            {
                MsgErroeLog.WriteLog( "SA1 Input Wrong！" );
                return;
            }


            if (!byte.TryParse( textBox16.Text, out byte DA2 ))
            {
                MsgErroeLog.WriteLog( "PLC DA2 input wrong！" );
                return;
            }
            
            omronFinsNet.IpAddress = textBox1.Text;
            omronFinsNet.Port = port;
            omronFinsNet.SA1 = SA1;
            omronFinsNet.DA2 = DA2;
            omronFinsNet.ByteTransform.DataFormat = (HslCommunication.Core.DataFormat)comboBox1.SelectedItem;

            // OperateResult connect = OperateResult.CreateSuccessResult( ); 
            OperateResult connect = omronFinsNet.ConnectServer( );
            if (connect.IsSuccess)
            {
                MsgErroeLog.WriteLog( HslCommunication.StringResources.Language.ConnectedSuccess );
                _omronAutoReconnect = true; // ch:连接成功后才允许断线自动重连
                button2.Enabled = true;
                button1.Enabled = false;
                panel2.Enabled = true;

                userControlCurve1.ReadWriteNet = omronFinsNet;
            }
            else
            {
                MsgErroeLog.WriteLog( HslCommunication.StringResources.Language.ConnectedFailed );
            }
        }

        private void button2_Click( object sender, EventArgs e )
        {
            // 断开连接
            _omronAutoReconnect = false; // ch:手动断开后不再自动重连，尊重操作者意图
            omronFinsNet.ConnectClose( );
            button2.Enabled = false;
            button1.Enabled = true;
            panel2.Enabled = false;
        }

        // ch:OMRON 断线自动重连（节流 5s，防止重连风暴；连接成功/失败均记日志便于排查）
        private void TryAutoReconnectOmron( )
        {
            if ( !_omronAutoReconnect || omronFinsNet == null ) return;
            long now = Environment.TickCount;
            if ( _omronLastReconnectTick != 0 && now - _omronLastReconnectTick < 5000 ) return;
            lock ( _omronReconnectLock )
            {
                if ( _omronLastReconnectTick != 0 && Environment.TickCount - _omronLastReconnectTick < 5000 ) return;
                _omronLastReconnectTick = Environment.TickCount;
                try
                {
                    OperateResult r2 = omronFinsNet.ConnectServer( );
                    if ( r2.IsSuccess )
                        MsgErroeLog.WriteLog( "OMRON PLC 自动重连成功" );
                    else
                        MsgErroeLog.WriteLog( "OMRON PLC 自动重连失败:" + r2.Message );
                }
                catch ( Exception ex )
                {
                    MsgErroeLog.WriteLog( "OMRON PLC 自动重连异常:" + ex.Message );
                }
            }
        }
        

        #endregion

        #region 单数据读取测试


        private void button_read_bool_Click( object sender, EventArgs e )
        {
            // 读取bool变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadBool( textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_short_Click( object sender, EventArgs e )
        {
            // 读取short变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadInt16( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_ushort_Click( object sender, EventArgs e )
        {
            // 读取ushort变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadUInt16( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_int_Click( object sender, EventArgs e )
        {
            // 读取int变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadInt32( textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_uint_Click( object sender, EventArgs e )
        {
            // 读取uint变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadUInt32( textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_long_Click( object sender, EventArgs e )
        {
            // 读取long变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_ulong_Click( object sender, EventArgs e )
        {
            // 读取ulong变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadUInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_float_Click( object sender, EventArgs e )
        {
            // 读取float变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadFloat( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_double_Click( object sender, EventArgs e )
        {
            // 读取double变量
            DemoUtils.ReadResultRender( omronFinsNet.ReadDouble( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_string_Click( object sender, EventArgs e )
        {
            // 读取字符串
            DemoUtils.ReadResultRender( omronFinsNet.ReadString( textBox3.Text, ushort.Parse( textBox5.Text ) ), textBox3.Text, textBox4 );
        }


        #endregion

        #region 单数据写入测试


        private void button24_Click( object sender, EventArgs e )
        {
            // bool写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, bool.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button22_Click( object sender, EventArgs e )
        {
            // short写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, short.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button21_Click( object sender, EventArgs e )
        {
            // ushort写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, ushort.Parse( textBox7.Text ) ), textBox8.Text );
        }


        private void button20_Click( object sender, EventArgs e )
        {
            // int写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, int.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button19_Click( object sender, EventArgs e )
        {
            // uint写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, uint.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button18_Click( object sender, EventArgs e )
        {
            // long写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, long.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button17_Click( object sender, EventArgs e )
        {
            // ulong写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, ulong.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button16_Click( object sender, EventArgs e )
        {
            // float写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, float.Parse( textBox7.Text ) ), textBox8.Text );
        }

        private void button15_Click( object sender, EventArgs e )
        {
            // double写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, double.Parse( textBox7.Text ) ), textBox8.Text );
        }


        private void button14_Click( object sender, EventArgs e )
        {
            // string写入
            DemoUtils.WriteResultRender( () => omronFinsNet.Write( textBox8.Text, textBox7.Text ), textBox8.Text );
        }
        
        #endregion

        #region 批量读取测试

        private void button25_Click( object sender, EventArgs e )
        {
            DemoUtils.BulkReadRenderResult( omronFinsNet, textBox6, textBox9, textBox10 );
        }



        #endregion

        #region 报文读取测试


        private void button26_Click( object sender, EventArgs e )
        {
            OperateResult<byte[]> read = omronFinsNet.ReadFromCoreServer( HslCommunication.BasicFramework.SoftBasic.HexStringToBytes( textBox13.Text ) );
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
        
        private void test()
        {
            // 读取操作，这里的D100可以替换成C100,A100,W100,H100效果时一样的
            bool D100_7 = omronFinsNet.ReadBool( "D100.7" ).Content;  // 读取D100.7是否通断，注意D100.0等同于D100
            short short_D100 = omronFinsNet.ReadInt16( "D100" ).Content; // 读取D100组成的字
            ushort ushort_D100 = omronFinsNet.ReadUInt16( "D100" ).Content; // 读取D100组成的无符号的值
            int int_D100 = omronFinsNet.ReadInt32( "D100" ).Content;         // 读取D100-D101组成的有符号的数据
            uint uint_D100 = omronFinsNet.ReadUInt32( "D100" ).Content;      // 读取D100-D101组成的无符号的值
            float float_D100 = omronFinsNet.ReadFloat( "D100" ).Content;   // 读取D100-D101组成的单精度值
            long long_D100 = omronFinsNet.ReadInt64( "D100" ).Content;      // 读取D100-D103组成的大数据值
            ulong ulong_D100 = omronFinsNet.ReadUInt64( "D100" ).Content;   // 读取D100-D103组成的无符号大数据
            double double_D100 = omronFinsNet.ReadDouble( "D100" ).Content; // 读取D100-D103组成的双精度值
            string str_D100 = omronFinsNet.ReadString( "D100", 5 ).Content;// 读取D100-D104组成的ASCII字符串数据

            // 写入操作，这里的D100可以替换成C100,A100,W100,H100效果时一样的
            omronFinsNet.Write( "D100", (byte)0x33 );            // 写单个字节
            omronFinsNet.Write( "D100", (short)12345 );          // 写双字节有符号
            omronFinsNet.Write( "D100", (ushort)45678 );         // 写双字节无符号
            omronFinsNet.Write( "D100", (uint)3456789123 );      // 写双字无符号
            omronFinsNet.Write( "D100", 123.456f );              // 写单精度
            omronFinsNet.Write( "D100", 1234556434534545L );     // 写大整数有符号
            omronFinsNet.Write( "D100", 523434234234343UL );     // 写大整数无符号
            omronFinsNet.Write( "D100", 123.456d );              // 写双精度
            omronFinsNet.Write( "D100", "K123456789" );// 写ASCII字符串

            OperateResult<byte[]> read = omronFinsNet.Read( "D100", 5 );
            {
                if (read.IsSuccess)
                {
                    // 此处需要根据实际的情况来自定义来处理复杂的数据
                    short D100 = omronFinsNet.ByteTransform.TransInt16( read.Content, 0 );
                    short D101 = omronFinsNet.ByteTransform.TransInt16( read.Content, 2 );
                    short D102 = omronFinsNet.ByteTransform.TransInt16( read.Content, 4 );
                    short D103 = omronFinsNet.ByteTransform.TransInt16( read.Content, 6 );
                    short D104 = omronFinsNet.ByteTransform.TransInt16( read.Content, 7 );
                }
                else
                {
                    // 发生了异常
                    // 
                }
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

        private void dataGridView1_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            dataGridView1.ClearSelection();
            x = 999;
            y = 999;
        }
        bool fins_lunxunen = false;
       public   bool fins_en = false;
        decimal lunxun_time = 0;
        public bool chushihua = false;
        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox1.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
                else
                {
                    fins_lunxunen = false;
                }
                wdini.WriteString("fins", "fins_lunxunen", fins_lunxunen.ToString());
            }
        }

        private void dataGridView1_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            bool xuanzhong_temp = false;
           
            if (sender is DataGridView)
            {
                DataGridView dgv = (DataGridView)sender;
                if (e.RowIndex%2==1&&e.ColumnIndex>=0)//如果该行为表头
                {
                    
                    
                    x = e.ColumnIndex;
                    y = e.RowIndex;
                    foreach (var pair in fins_data)
                    {
                        if (pair.Value[0] == x&& pair.Value[1] == y)
                        {
                            foreach(var p_temp in fins_dic)
                            {
                                if((pair.Key + address_qishi)>= int.Parse( p_temp.Value[1])&& (pair.Key + address_qishi )< int.Parse(p_temp.Value[1])+ int.Parse(p_temp.Value[2]))
                                {
                                    xuanzhong_temp = true;
                                    numericUpDown5.Value = int.Parse(p_temp.Value[1]);
                                    x = fins_data[int.Parse(p_temp.Value[1])- int.Parse(p_temp.Value[1])][0];
                                    y = fins_data[int.Parse(p_temp.Value[1])- int.Parse(p_temp.Value[1])][1];
                                    numericUpDown4.Value =int.Parse(p_temp.Value[2]);
                                    textBox12.Text = p_temp.Value[0];
                                    comboBox2.Text= p_temp.Value[3];
                                    comboBox3.Text = p_temp.Value[4];
                                }
                                
                            }
                            if (xuanzhong_temp == false)
                            {
                                numericUpDown5.Value = pair.Key+address_qishi;
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
        private void button3_Click(object sender, EventArgs e)
        {
            try
            {
                fins_lunxunen = false;
                decimal xuanzhong_temp = 0;
                bool chongdie = false;
                if (comboBox3.Text.Length > 1 && comboBox2.Text.Length > 1 && textBox12.Text.Length > 0)
                {
                    if ((comboBox3.Text == "string" || comboBox3.Text == "int") || (numericUpDown5.Value % 2 == 0))
                    {
                        if (numericUpDown5.Value >= numericUpDown1.Value && numericUpDown1.Value + numericUpDown2.Value >= numericUpDown4.Value + numericUpDown5.Value)
                        {
                            if (textBox12.Text.Length > 0)
                            {
                                if (fins_dic.Count < 10)
                                {
                                    if(fins_dic.Count>0)
                                    {
                                        foreach(var pat in fins_dic)
                                        {
                                            if(decimal.Parse(pat.Value[1])>= numericUpDown5.Value+ numericUpDown4.Value || decimal.Parse(pat.Value[1])+ decimal.Parse(pat.Value[2])<= numericUpDown5.Value)
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
                                        fins_dic.Add(textBox12.Text, new string[] { textBox12.Text, numericUpDown5.Value.ToString(), numericUpDown4.Value.ToString(), comboBox2.Text, comboBox3.Text });
                                        for (int i = 0; i < numericUpDown4.Value; i++)
                                        {
                                            xuanzhong_temp = numericUpDown5.Value - numericUpDown1.Value + i;
                                            dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                                            dataGridView1[fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1]].Style.BackColor = Color.Green;
                                            if (xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                            dataGridView1[fins_name[int.Parse(xuanzhong_temp.ToString())][0], fins_name[int.Parse(xuanzhong_temp.ToString())][1]].Value = textBox12.Text;
                                        }
                                        geshu = fins_dic.Count;
                                        wdini.WriteString("fins", "geshu", fins_dic.Count.ToString());
                                        wdini.WriteString(geshu.ToString(), "name", textBox12.Text);
                                        wdini.WriteString(geshu.ToString(), "qishi", numericUpDown5.Value.ToString());
                                        wdini.WriteString(geshu.ToString(), "changdu", numericUpDown4.Value.ToString());
                                        wdini.WriteString(geshu.ToString(), "gaodiwei", comboBox2.Text);
                                        wdini.WriteString(geshu.ToString(), "geshi", comboBox3.Text);
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
                if(checkBox1.CheckState==CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
                if (checkBox1.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
        }

        private void numericUpDown4_ValueChanged(object sender, EventArgs e)
        {

           // dataGridView1[fins_name[int.Parse(numericUpDown4.Value.ToString())][0], fins_name[int.Parse(numericUpDown4.Value.ToString())][1]].Style.BackColor = Color.Green;
            //dataGridView1[fins_data[int.Parse(numericUpDown4.Value.ToString())][0], fins_data[int.Parse(numericUpDown4.Value.ToString())][1]].Style.BackColor = Color.Green;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            if (button4.Text=="显示")
            {
                tabControl1.Visible=true;
                button4.Text = "隐藏";
            }
            else
            {
                tabControl1.Visible = false;
                button4.Text = "显示";
            }
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox2.CheckState == CheckState.Checked)
                {
                    fins_en = true;
                }
                else
                {
                    fins_en = false;
                }
                wdini.WriteString("fins", "fins_en", fins_en.ToString());
               
            }
        }

        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                lunxun_time = numericUpDown3.Value;
                wdini.WriteString("fins", "lunxun_time", lunxun_time.ToString());
               
            }
        }

        private void numericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                address_length = numericUpDown2.Value;
                wdini.WriteString("fins", "zongchang", numericUpDown2.Value.ToString());
            }
        }

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
                wdini.WriteString("fins", "qishi", numericUpDown1.Value.ToString());
                RefreshFinsTable(); // ch:起始地址变化后立即刷新表格（清空并按新起点重绘）
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {
            clearing = true; // ch:清除期间暂停轮询与输出线程，防止清除后旧数据写回
            try
            {
            dataGridView1.Visible = false;
            wdini.WriteString("fins", "geshu", "0");
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
            for(int i=0;i<10;i++)
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
            button6_Click(null, null);
            }
            finally
            {
                clearing = false;
            }
        }
       public bool [] fins_xie = new bool[] { false, false, false, false, false, false, false, false, false,false };
        private Thread fins_duxie;
        private volatile bool clearing = false; // ch:清除配置期间暂停轮询/输出，防止清除后被旧数据写回
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

        void  Fins_duxie()
        {
            while (true)
            {
                // ch:R10-3 原 int.Parse(decimal.ToString()) 在区域小数点/ini 带小数时抛 FormatException，且在 try 外直接杀死轮询线程；
                // 另：lunxun_time<=0 时补 50ms 小睡，避免 while(true) 全速空转吃满一核
                Thread.Sleep(lunxun_time > 0 ? (int)lunxun_time : 50);
                if (lunxun_time > 0)
                {
                    try
                    {
                        if (chushihua)
                        {
                            // ch:N6 门控与 RTU/FormModbus 对齐：fins_en(通讯总开关) && fins_lunxunen(轮询使能) && !clearing
                            if (fins_en && fins_lunxunen && !clearing)
                            {
                                if (omronFinsNet == null) continue; // ch:N5 未连接 PLC 跳过本轮并保持轮询线程存活（原 return 会永久杀掉 while(true) 轮询线程）
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
                                                var readOmron = omronFinsNet.ReadInt16("D" + (int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readOmron.IsSuccess;
                                                DemoUtils.ReadResultRender1(readOmron, "D" + (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "string")
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readOmron = omronFinsNet.ReadString("D" + (int.Parse(par.Value[1]) + j).ToString(), 1);
                                                commFailed |= !readOmron.IsSuccess;
                                                DemoUtils.ReadResultRender1(readOmron, "D" + (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "long" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readOmron = omronFinsNet.ReadInt32("D" + (int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readOmron.IsSuccess;
                                                DemoUtils.ReadResultRender1(readOmron, "D" + (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "float" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                var readOmron = omronFinsNet.ReadFloat("D" + (int.Parse(par.Value[1]) + j).ToString());
                                                commFailed |= !readOmron.IsSuccess;
                                                DemoUtils.ReadResultRender1(readOmron, "D" + (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                        }
                                        if (commFailed) TryAutoReconnectOmron(); // ch:检测到读取失败则触发（方法内部节流）断线自动重连
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
                                                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(shuju_temp, pap.Key.ToString());
                                                                getData(this, E);
                                                            }
                                                            else
                                                            {
                                                                lock (_omronIoLock)
                                                                {
                                                                    if (!camera_dic[10][1].Contains("无"))
                                                                    {
                                                                        camera_dic[10][4] = camera_dic[10][1];
                                                                    }
                                                                }
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
                                                                SelectionChangedEventArgs E = new SelectionChangedEventArgs(shuju_temp, pap.Key.ToString());
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
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog(ex.Message);
                    }
                }
            }
        }
        // ch:R4 单事务入口：登记该相机结果并立即在同一把锁内消费（Monitor 可重入，xie 内部再次加锁不死锁）
        public void WriteCameraResult(int camIndex, string value)
        {
            lock (_omronIoLock)
            {
                if (clearing || !chushihua || !fins_en || !camera_dic.ContainsKey(camIndex))
                return;
                camera_dic[camIndex][4] = value;
                camera_dic[camIndex][5] = camera_dic[camIndex][3];
                if (camIndex >= 1 && camIndex <= fins_xie.Length)
                fins_xie[camIndex - 1] = true;
                xie(value);
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

        // ch:P0 方案切换写回暂存：与 xie 消费共用 _omronIoLock，保证 [4](值)/[5](通道) 原子配对，消除跨线程撕裂
        public void SetSwitchPending(int camIndex)
        {
            lock (_omronIoLock)
            {
                if (camera_dic.ContainsKey(camIndex) && !camera_dic[camIndex][1].Contains("无"))
                {
                    camera_dic[camIndex][4] = camera_dic[camIndex][1];
                    camera_dic[camIndex][5] = camera_dic[camIndex][0];
                    if (camIndex >= 1 && camIndex <= fins_xie.Length) fins_xie[camIndex - 1] = true;
                }
            }
        }
        public void xie(string value)
        {
            lock (_omronIoLock) { // ch:R4 串行化 pending 全表遍历消费
            try
            {
                if (!chushihua || fins_dic.Count == 0) return;
                if (clearing) return; // ch:清除配置期间暂停输出

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
                        if (omronFinsNet == null) // ch:P2 客户端为空也计入失败：否则 pending 永不超限、每帧 NRE 刷日志
                        {
                            MsgErroeLog.WriteLog("omron 写回失败: 客户端为空 cam=" + pat.Key);
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

                                if (fmt == "int")
                                {
                                    // 逗号分隔 → short数组, 批量写 (Omron FINS memory write)
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    short[] vals = new short[parts.Length];
                                    for (int i = 0; i < parts.Length; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    if (!DemoUtils.WriteResultRenderOk(() => omronFinsNet.Write("D" + addr_start.ToString(), vals), "D" + addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
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
                                    if (!DemoUtils.WriteResultRenderOk(() => omronFinsNet.Write("D" + addr_start.ToString(), vals), "D" + addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
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
                                    if (!DemoUtils.WriteResultRenderOk(() => omronFinsNet.Write("D" + addr_start.ToString(), vals), "D" + addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
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
                                        if (!DemoUtils.WriteResultRenderOk(() => omronFinsNet.Write("D" + (addr_start + j).ToString(), parts[j].Trim()), "D" + (addr_start + j).ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④ 逐寄存器聚合，避免末次成功掩盖前次失败
                                        if (gridUi)
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
                MsgErroeLog.WriteLog(ex.Message);
            }
            } // ch:R4 串行化 pending 全表遍历消费(闭合)
        }
        // ch:P2 相机9 周期写回：① 与 xie 消费共用 _omronIoLock，保证 [4](值)/[5](通道) 原子配对；
        //   ② 通道取"反馈通道"[3]（原实现误取触发通道[0] → 每秒把返回值写进触发寄存器，且破坏配对可写错地址段）
        public void SetCamera9PeriodicPending()
        {
            lock (_omronIoLock)
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

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("fins", "abcd", comboBox1.Text);
            }
        }

        private void comboBox5_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox5.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox5.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox5_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][0] = comboBox5.Text;
                wdini.WriteString("c1", "chufa", comboBox5.Text);
            }
        }

        private void comboBox6_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][3] = comboBox6.Text;
                wdini.WriteString("c1", "fankui", comboBox6.Text);
            }
        }

        private void comboBox6_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            { 
                comboBox6.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox6.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void textBox14_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][1] = textBox14.Text;
                wdini.WriteString("c1", "fanhuizhi", textBox14.Text);
            }
        }

        private void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox3.CheckState == CheckState.Checked)
                {
                    camera_dic[1][2] = "true";
                    wdini.WriteString("c1", "fanhuien", "true");
                }
                else
                {
                    camera_dic[1][2] = "false";
                    wdini.WriteString("c1", "fanhuien", "false");
                }
            }
        }

        private void comboBox8_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][0] = comboBox8.Text;
                wdini.WriteString("c2", "chufa", comboBox8.Text);
            }
        }

        private void comboBox10_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][0] = comboBox10.Text;
                wdini.WriteString("c3", "chufa", comboBox10.Text);
            }
        }

        private void comboBox12_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][0] = comboBox12.Text;
                wdini.WriteString("c4", "chufa", comboBox12.Text);
            }
        }

        private void comboBox14_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][0] = comboBox14.Text;
                wdini.WriteString("c5", "chufa", comboBox14.Text);
            }
        }

        private void comboBox16_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][0] = comboBox16.Text;
                wdini.WriteString("c6", "chufa", comboBox16.Text);
            }
        }

        private void comboBox18_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][0] = comboBox18.Text;
                wdini.WriteString("c7", "chufa", comboBox18.Text);
            }
        }

        private void comboBox20_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][0] = comboBox20.Text;
                wdini.WriteString("c8", "chufa", comboBox20.Text);
            }
        }

        private void comboBox8_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox8.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox8.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox10_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox10.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox10.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox12_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox12.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox12.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox14_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox14.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox14.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox16_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox16.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox16.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox18_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox18.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox18.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox20_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox20.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox20.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void textBox17_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][1] = textBox17.Text;
                wdini.WriteString("c2", "fanhuizhi", textBox17.Text);
            }
        }

        private void textBox19_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][1] = textBox19.Text;
                wdini.WriteString("c3", "fanhuizhi", textBox19.Text);
            }
        }

        private void textBox18_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][1] = textBox18.Text;
                wdini.WriteString("c4", "fanhuizhi", textBox18.Text);
            }
        }

        private void textBox23_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][1] = textBox23.Text;
                wdini.WriteString("c5", "fanhuizhi", textBox23.Text);
            }
        }

        private void textBox22_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][1] = textBox22.Text;
                wdini.WriteString("c6", "fanhuizhi", textBox22.Text);
            }
        }

        private void textBox21_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][1] = textBox21.Text;
                wdini.WriteString("c7", "fanhuizhi", textBox21.Text);
            }
        }

        private void textBox20_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][1] = textBox20.Text;
                wdini.WriteString("c8", "fanhuizhi", textBox20.Text);
            }
        }

        private void checkBox4_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    camera_dic[2][2] = "true";
                    wdini.WriteString("c2", "fanhuien", "true");
                }
                else
                {
                    camera_dic[2][2] = "false";
                    wdini.WriteString("c2", "fanhuien", "false");
                }
            }
        }

        private void checkBox5_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox5.CheckState == CheckState.Checked)
                {
                    camera_dic[3][2] = "true";
                    wdini.WriteString("c3", "fanhuien", "true");
                }
                else
                {
                    camera_dic[3][2] = "false";
                    wdini.WriteString("c3", "fanhuien", "false");
                }
            }
        }

        private void checkBox6_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox6.CheckState == CheckState.Checked)
                {
                    camera_dic[4][2] = "true";
                    wdini.WriteString("c4", "fanhuien", "true");
                }
                else
                {
                    camera_dic[4][2] = "false";
                    wdini.WriteString("c4", "fanhuien", "false");
                }
            }
        }

        private void checkBox10_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox10.CheckState == CheckState.Checked)
                {
                    camera_dic[5][2] = "true";
                    wdini.WriteString("c5", "fanhuien", "true");
                }
                else
                {
                    camera_dic[5][2] = "false";
                    wdini.WriteString("c5", "fanhuien", "false");
                }
            }
        }

        private void checkBox9_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox9.CheckState == CheckState.Checked)
                {
                    camera_dic[6][2] = "true";
                    wdini.WriteString("c6", "fanhuien", "true");
                }
                else
                {
                    camera_dic[6][2] = "false";
                    wdini.WriteString("c6", "fanhuien", "false");
                }
            }
        }

        private void checkBox8_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox8.CheckState == CheckState.Checked)
                {
                    camera_dic[7][2] = "true";
                    wdini.WriteString("c7", "fanhuien", "true");
                }
                else
                {
                    camera_dic[7][2] = "false";
                    wdini.WriteString("c7", "fanhuien", "false");
                }
            }
        }

        private void checkBox7_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox7.CheckState == CheckState.Checked)
                {
                    camera_dic[8][2] = "true";
                    wdini.WriteString("c8", "fanhuien", "true");
                }
                else
                {
                    camera_dic[8][2] = "false";
                    wdini.WriteString("c8", "fanhuien", "false");
                }
            }
        }

        private void comboBox7_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][3] = comboBox7.Text;
                wdini.WriteString("c2", "fankui", comboBox7.Text);
            }
        }

        private void comboBox9_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][3] = comboBox9.Text;
                wdini.WriteString("c3", "fankui", comboBox9.Text);
            }
        }

        private void comboBox11_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][3] = comboBox11.Text;
                wdini.WriteString("c4", "fankui", comboBox11.Text);
            }
        }

        private void comboBox13_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][3] = comboBox13.Text;
                wdini.WriteString("c5", "fankui", comboBox13.Text);
            }
        }

        private void comboBox15_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][3] = comboBox15.Text;
                wdini.WriteString("c6", "fankui", comboBox15.Text);
            }
        }

        private void comboBox17_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][3] = comboBox17.Text;
                wdini.WriteString("c7", "fankui", comboBox17.Text);
            }
        }

        private void comboBox19_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][3] = comboBox19.Text;
                wdini.WriteString("c8", "fankui", comboBox19.Text);
            }
        }

        private void comboBox7_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox7.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox7.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox9_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox9.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox9.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox11_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox11.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox11.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox13_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox13.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox13.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox15_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox15.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox15.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox17_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox17.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox17.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox19_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox19.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("反馈"))
                        {
                            comboBox19.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            comboBox4.Text = "";
            comboBox5.Text = "";
            comboBox6.Text = "";
            comboBox7.Text = "";
            comboBox8.Text = "";
            comboBox9.Text = "";
            comboBox10.Text = "";
            comboBox11.Text = "";
            comboBox12.Text = "";
            comboBox13.Text = "";
            comboBox14.Text = "";
            comboBox15.Text = "";
            comboBox16.Text = "";
            comboBox17.Text = "";
            comboBox18.Text = "";
            comboBox19.Text = "";
            comboBox20.Text = "";
            comboBox21.Text = "";
            textBox14.Text = "";
            textBox17.Text = "";
            textBox19.Text = "";
            textBox18.Text = "";
            textBox23.Text = "";
            textBox22.Text = "";
            textBox21.Text = "";
            textBox20.Text = "";
            textBox24.Text = "";
            textBox41.Text = "";
            checkBox3.CheckState = CheckState.Unchecked;
            checkBox4.CheckState = CheckState.Unchecked;
            checkBox5.CheckState = CheckState.Unchecked;
            checkBox6.CheckState = CheckState.Unchecked;
            checkBox7.CheckState = CheckState.Unchecked;
            checkBox8.CheckState = CheckState.Unchecked;
            checkBox9.CheckState = CheckState.Unchecked;
            checkBox10.CheckState = CheckState.Unchecked;
            checkBox11.CheckState = CheckState.Unchecked;
            checkBox12.CheckState = CheckState.Unchecked;
            comboBox4_SelectedIndexChanged(null, null);
            comboBox5_SelectedIndexChanged(null, null);
            comboBox6_SelectedIndexChanged(null, null);
            comboBox7_SelectedIndexChanged(null, null);
            comboBox8_SelectedIndexChanged(null, null);
            comboBox9_SelectedIndexChanged(null, null);
            comboBox10_SelectedIndexChanged(null, null);
            comboBox11_SelectedIndexChanged(null, null);
            comboBox12_SelectedIndexChanged(null, null);
            comboBox13_SelectedIndexChanged(null, null);
            comboBox14_SelectedIndexChanged(null, null);
            comboBox15_SelectedIndexChanged(null, null);
            comboBox16_SelectedIndexChanged(null, null);
            comboBox17_SelectedIndexChanged(null, null);
            comboBox18_SelectedIndexChanged(null, null);
            comboBox19_SelectedIndexChanged(null, null);
            comboBox20_SelectedIndexChanged(null, null);
            comboBox21_SelectedIndexChanged(null, null);

        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("fins", "ip", textBox1.Text);
            }
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("fins", "port", textBox2.Text);
            }
        }

        private void textBox16_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("fins", "cell", textBox16.Text);
            }
        }

        private void textBox15_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("fins", "local", textBox15.Text);
            }
        }

        private void userControlHead1_Load(object sender, EventArgs e)
        {

        }

        private void comboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][0] = comboBox4.Text;
                wdini.WriteString("c9", "chufa", comboBox4.Text);
            }
        }

        private void comboBox4_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox4.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("心跳"))
                        {
                            comboBox4.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void checkBox11_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox11.CheckState == CheckState.Checked)
                {
                    camera_dic[9][2] = "true";
                    wdini.WriteString("c9", "fanhuien", "true");
                }
                else
                {
                    camera_dic[9][2] = "false";
                    wdini.WriteString("c9", "fanhuien", "false");
                }
            }
        }

        private void textBox24_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][1] = textBox24.Text;
                wdini.WriteString("c9", "fanhuizhi", textBox24.Text);
            }
        }

        private void tabPage2_Click(object sender, EventArgs e)
        {

        }

        private void comboBox21_DropDown(object sender, EventArgs e)
        {
            if (chushihua)
            {
                comboBox21.Items.Clear();
                if (fins_dic.Count > 0)
                {
                    foreach (var par in fins_dic)
                    {
                        if (par.Value[3].Contains("触发"))
                        {
                            comboBox21.Items.Add(par.Value[0]);

                        }
                    }
                }
            }
        }

        private void comboBox21_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[10][0] = comboBox21.Text;
                wdini.WriteString("c10", "chufa", comboBox21.Text);
            }
        }

        private void checkBox12_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox12.CheckState == CheckState.Checked)
                {
                    camera_dic[10][2] = "true";
                    wdini.WriteString("c10", "fanhuien", "true");
                }
                else
                {
                    camera_dic[10][2] = "false";
                    wdini.WriteString("c10", "fanhuien", "false");
                }
            }
        }

        private void textBox25_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox25.Text = openFileDialog.FileName;
            }
        }

        private void textBox26_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox26.Text = openFileDialog.FileName;
            }
        }

        private void textBox28_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox28.Text = openFileDialog.FileName;
            }
        }

        private void textBox27_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox27.Text = openFileDialog.FileName;
            }
        }

        private void textBox32_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox32.Text = openFileDialog.FileName;
            }
        }

        private void textBox31_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox31.Text = openFileDialog.FileName;
            }
        }

        private void textBox30_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox30.Text = openFileDialog.FileName;
            }
        }

        private void textBox29_DoubleClick(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox29.Text = openFileDialog.FileName;
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            wdini.WriteString("change", "1", textBox40.Text);
            wdini.WriteString("change", "2", textBox39.Text);
            wdini.WriteString("change", "3", textBox38.Text);
            wdini.WriteString("change", "4", textBox37.Text);
            wdini.WriteString("change", "5", textBox36.Text);
            wdini.WriteString("change", "6", textBox35.Text);
            wdini.WriteString("change", "7", textBox34.Text);
            wdini.WriteString("change", "8", textBox33.Text);
          
            wdini.WriteString("path", "1", textBox25.Text);
            wdini.WriteString("path", "2", textBox26.Text);
            wdini.WriteString("path", "3", textBox28.Text);
            wdini.WriteString("path", "4", textBox27.Text);
            wdini.WriteString("path", "5", textBox32.Text);
            wdini.WriteString("path", "6", textBox31.Text);
            wdini.WriteString("path", "7", textBox30.Text);
            wdini.WriteString("path", "8", textBox29.Text);
        }
        private int qiehuan(string aa)
        {
            if (textBox40.Text == aa)
            {
                zifu = aa;
                lujing = textBox25.Text;
                return 1;
            }
            else if (textBox39.Text == aa)
            {
                zifu = aa;
                lujing = textBox26.Text;
                return 1;
            }
            else if (textBox38.Text == aa)
            {
                zifu = aa;
                lujing = textBox28.Text;
                return 1;
            }
            else if (textBox37.Text == aa)
            {
                zifu = aa;
                lujing = textBox27.Text;
                return 1;
            }
            else if (textBox36.Text == aa)
            {
                zifu = aa;
                lujing = textBox32.Text;
                return 1;
            }
            else if (textBox35.Text == aa)
            {
                zifu = aa;
                lujing = textBox31.Text;
                return 1;
            }
            else if (textBox34.Text == aa)
            {
                zifu = aa;
                lujing = textBox30.Text;
                return 1;
            }
            else if (textBox33.Text == aa)
            {
                zifu = aa;
                lujing = textBox29.Text;
                return 1;
            }
            else
            {
                return 0;
            }
        }

        private void textBox41_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[10][1] = textBox41.Text;
                wdini.WriteString("c10", "fanhuizhi", textBox41.Text);
            }
        }
    }
}
