using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using HslCommunication;

namespace WindowsFormsApplication1.DemoControl
{
    public partial class UserControlCurve : UserControl
    {
        public UserControlCurve()
        {
            InitializeComponent( );
        }

        private void UserControlCurve_Load( object sender, EventArgs e )
        {
            if (Program.Language == 2)
            {
                groupBox5.Text = "Timed reading, curve display";
                label15.Text = "Address:";
                label18.Text = "Interval";
                button27.Text = "Start";
                label17.Text = "This assumes that the type of data is determined for short:";
            }

            userCurve1.SetLeftCurve( "A", new float[0], Color.Tomato );
        }


        // 外加曲线显示

        private Thread thread = null;              // 后台读取的线程
        private int timeSleep = 300;               // 读取的间隔
        private bool isThreadRun = false;          // 用来标记线程的运行状态
        // ch:P2-new 线程代际号：每次启动递增，旧线程醒来发现代际不符立即退出（快速停止→再启动时避免旧线程继续读 PLC 叠加）
        private int _threadGeneration = 0;

        private void button27_Click( object sender, EventArgs e )
        {
            // 启动后台线程，定时读取PLC中的数据，然后在曲线控件中显示
            if (!isThreadRun)
            {
                if (!int.TryParse( textBox14.Text, out timeSleep ))
                {
                    MessageBox.Show( "Time input wrong！" );
                    return;
                }
                if (timeSleep <= 0)
                {
                    // ch:P2-19 间隔必须为正整数：原实现 0 会高频空转、-1 无限等待、-2 触发未处理 Sleep 异常
                    MessageBox.Show( "间隔必须为正整数！" );
                    return;
                }
                button27.Text = "Stop";
                isThreadRun = true;
                int myGen = ++_threadGeneration; // ch:P2-new 递增代际；旧线程醒来检测代际不符立即退出
                thread = new Thread( new ThreadStart( delegate { ThreadReadServer( myGen ); } ) ); // ch:P2-new 显式 ThreadStart 消除 CS0121 二义性
                thread.IsBackground = true;
                thread.Start( );
            }
            else
            {
                button27.Text = "Start";
                isThreadRun = false;
            }
        }


        private void ThreadReadServer(int generation)
        {
            if (ReadWriteNet != null)
            {
                while (isThreadRun && generation == _threadGeneration) // ch:P2-new 代际不符（已停止又启动）立即退出
                {
                    Thread.Sleep( timeSleep );
                    // ch:P2 醒来后复核代际：避免"已停止又启动"的旧线程再补一次陈旧数据
                    if (!isThreadRun || generation != _threadGeneration) break;

                    try
                    {
                        OperateResult<short> read = ReadWriteNet.ReadInt16( textBox12.Text );
                        if (read.IsSuccess)
                        {
                            // 显示曲线
                            if (isThreadRun && !IsDisposed) Invoke( new Action<short, int>( AddDataCurve ), read.Content, generation ); // ch:P2-④ 传代际+释放检查，避免旧线程/已释放控件补点
                        }
                    }
                    catch (Exception ex)
                    {
                        new ErrorLog().WriteLog("曲线读取失败:" + ex.Message); // ch:P2-④ 后台线程不弹模态框，避免阻塞读线程
                    }
                }
            }
        }


        private void AddDataCurve( short data, int generation )
        {
            // ch:P2-④ 控件已释放、代际不符(已停止又启动的旧线程)、或已停止，均不补陈旧数据点
            if (IsDisposed || generation != _threadGeneration || !isThreadRun) return;
            userCurve1.AddCurveData( "A", data );
        }

        /// <summary>
        /// 退出线程信息
        /// </summary>
        public void ThreadQuit()
        {
            isThreadRun = false;
        }

        [Browsable(false)]
        public HslCommunication.Core.IReadWriteNet ReadWriteNet { get; set; }

        [Category( "Appearance" )]
        [Description( "设置或获取默认的地址信息" )]
        [DefaultValue( "" )]
        public string AddressExample
        {
            set { textBox12.Text = value; }
            get { return textBox12.Text; }
        }

        private void groupBox5_Enter(object sender, EventArgs e)
        {

        }
    }
}
