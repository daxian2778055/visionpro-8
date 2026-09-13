using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    public partial class ReadErrorLog : Form
    {
        ErrorLog ReadError = new ErrorLog();
        public ReadErrorLog()
        {
            InitializeComponent();
        }

        private void ReadErrorLog_Load(object sender, EventArgs e)
        {
            this.BeginInvoke(new EventHandler(delegate {
                if (comboBox1.Items.Count == 0)
                {
                    for (int i = 2020; 2020 <= i && i < 2050; i++)
                    {
                        comboBox1.Items.Add(i.ToString());
                    }
                    for (int i = 1; i < 13; i++)
                    {
                        if (i < 10)
                            comboBox2.Items.Add("0" + i.ToString());
                        else
                            comboBox2.Items.Add(i.ToString());
                    }
                    for (int i = 1; i < 32; i++)
                    {
                        if (i < 10)
                            comboBox3.Items.Add("0" + i.ToString());
                        else
                            comboBox3.Items.Add(i.ToString());
                    }
                }
                comboBox1.SelectedItem = DateTime.Now.Year.ToString();
                if (DateTime.Now.Month.ToString().Length == 1)
                    comboBox2.SelectedItem = "0" + DateTime.Now.Month.ToString();
                else
                    comboBox2.SelectedItem = DateTime.Now.Month.ToString();
                if (DateTime.Now.Day.ToString().Length == 1)
                    comboBox3.SelectedItem = "0" + DateTime.Now.Day.ToString();
                else
                    comboBox3.SelectedItem = DateTime.Now.Day.ToString();
            }));
        }

        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void button1_Click(object sender, EventArgs e)
        {
            string str = ReadError.ReadLog(AppDomain.CurrentDomain.BaseDirectory + "Log\\" + comboBox1.SelectedItem + "-" + comboBox2.SelectedItem + "-" + comboBox3.SelectedItem + ".txt");
            string[] arr = str.Split('/');

            this.BeginInvoke(new EventHandler(delegate {
                listBox1.Visible = false;
                foreach (string s in arr)
            {
                listBox1.Items.Add(s);
                listBox1.SelectedIndex = listBox1.Items.Count - 1;
            }
                listBox1.Visible =true;
            }));
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
    }
}
