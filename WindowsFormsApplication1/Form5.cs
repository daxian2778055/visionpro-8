using demo;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace WindowsFormsApplication1
{
    public partial class Form5 : Form
    {
        public Form5()
        {
            wdini.ReadINIFile(AppDomain.CurrentDomain.BaseDirectory + "//test.ini");
            InitializeComponent();
        }
        public int mark1;
        public int monitor;
        public int mark;
        private ClassIni wdini = new ClassIni();
        private string code1;
        private string code2;
        Stopwatch timewatch;
        private void Form5_Load(object sender, EventArgs e)
        {
            mark1 = 0;
            monitor=0;
            mark = 0;
            timewatch = new Stopwatch();
            comboBox1.Items.Add (wdini.ReadString("userName", "user1", "空"));
            comboBox1.Items.Add(wdini.ReadString("userName", "user2", "空"));
            code1 = wdini.ReadString("userCode", "code1", "空");
            code2 = wdini.ReadString("userCode", "code2", "空");
        }

        private void button2_Click(object sender, EventArgs e)
        {
            comboBox1.Text = "";
            textBox1.Text = "";
            this.textBox1.Focus();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if(comboBox1.Text=="")
            {
                MessageBox.Show("请选择有效的用户名！", "提示",MessageBoxButtons.OK,MessageBoxIcon.Exclamation);
            }
            if(comboBox1.Text=="厂家")
            {
                // ch:密码从 test.ini [password] 读取，默认 Abc1234，可现场修改
                if(textBox1.Text == wdini.ReadString("password", "factory", "Abc1234").Replace("\0", ""))
                {
                    textBox1.Clear();
                    MessageBox.Show("厂家登录成功!");
                    mark = 1;
                    mark1 = 1;
                    timewatch.Reset();
                    timewatch.Start();
                }
                else
                {
                    mark1 = 0;
                    textBox1.Clear();
                    this.textBox1.Focus();
                    MessageBox.Show("密码不正确!");
                }
            }
            if(comboBox1.Text=="操作工")
            {
                // ch:密码从 test.ini [password] 读取，默认 123456，可现场修改
                if(textBox1.Text == wdini.ReadString("password", "operator", "123456").Replace("\0", ""))
                {
                    mark1 = 0;
                    mark = 1;
                    timewatch.Reset();
                    timewatch.Start();
                    textBox1.Clear();
                    MessageBox.Show("操作工登录成功!");
                }
                else
                {
                    mark1 = 0;
                    textBox1.Clear();
                    this.textBox1.Focus();
                    MessageBox.Show("密码不正确!");
                }
            }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if(mark1==1&&mark==1)
            {
                label4.Text = "厂家";
            }
            if(mark1==0&&mark==1)
            {
                label4.Text = "操作工";
            }
            if(mark==0)
            {
                label4.Text = "未登录";
            }
            if (mark == 1)
            {
                if (monitor == 1)
                    timewatch.Start();
                else
                    timewatch.Stop();
            }
            try
            {
                label3.Text = (timewatch.ElapsedMilliseconds / 1000).ToString();
                if (timewatch.ElapsedMilliseconds > 30000 && mark1 == 0)
                {
                    mark = 0;
                    timewatch.Stop();
                }
           
            if(mark==0)
            {
                timewatch.Stop();
            }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        private void Form5_FormClosing(object sender, FormClosingEventArgs e)
        {
          
                e.Cancel = true;
                this.Visible = false;      
        }

        private void textBox1_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == 13)
            {
                if (label4.Text == "厂家")
                {
                    this.Visible = false;
                }
                else
                    button1_Click(null, null);
            }
        }
        private bool front = false;
        private void timer2_Tick(object sender, EventArgs e)
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
