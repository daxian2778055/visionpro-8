using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WindowsFormsApplication1
{
    public partial class Frm2 : Form
    {
        // ch:R48 P2-7 后台线程写、UI 定时器读：volatile 保证可见性，避免刷不出 start=1 导致进度窗不关闭
        public volatile int start;
        public int end;
        public Frm2()
        {
            InitializeComponent();
            
        }

        private void Form2_Load(object sender, EventArgs e)
        {
           // Task.Run(() =>
          //  {
                progressBar1.Minimum = 0;
            progressBar1.Maximum = 10;
            progressBar1.MarqueeAnimationSpeed = 70;
            timer1.Start();
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            end = 1;
            this.DesktopLocation = new Point(145, 145);
          //  });
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (start == 1)
            {

                this.Hide();
                timer1.Stop();
                this.Close();
            }
        }
    }
}
