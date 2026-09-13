using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Management;
using System.Security.Cryptography;
namespace WindowsFormsApplication1
{
    public partial class Form4 : Form
    {
     
       
        public Form4()
        {
            InitializeComponent();
        }
     
        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void Form4_Load(object sender, EventArgs e)
        {
            String[] ports = SerialPort.GetPortNames();
            try
            {
                foreach (string port in ports)
                {
                    comboBox1.Items.Add(port);
                }
                comboBox1.SelectedIndex = 0;
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            comboBox2.Items.Add("4800");
            comboBox2.Items.Add("9600");
            comboBox2.Items.Add("19200");
            comboBox2.Items.Add("38400");
            comboBox2.Items.Add("57600");
            comboBox2.Items.Add("115200");
            comboBox2.SelectedIndex = 1;
            comboBox3.Items.Add("6");
            comboBox3.Items.Add("7");
            comboBox3.Items.Add("8");
            comboBox3.Items.Add("9");
            comboBox3.SelectedIndex = 2;
            comboBox4.Items.Add("0");
            comboBox4.Items.Add("1");
            comboBox4.Items.Add("2");
            comboBox4.SelectedIndex = 1;
            comboBox5.Items.Add("无");
            comboBox5.Items.Add("奇");
            comboBox5.Items.Add("偶");
            comboBox5.SelectedIndex = 0;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Form3.strportName = comboBox1.Text;
            Form3.strbaudRate = comboBox2.Text;
            Form3.strDataBits = comboBox3.Text;
            Form3.strStopBits = comboBox4.Text;
            Form3.strjiaoyan = comboBox5.SelectedIndex.ToString(); // ch:P2-16 校验位读校验位下拉框 comboBox5(无/奇/偶)；原误读停止位 comboBox4，导致选"无/奇/偶"不生效
            DialogResult = DialogResult.OK;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            //ManagementClass mc1 = new ManagementClass("Win32_PhysicalMedia");
            ////网上有提到，用Win32_DiskDrive，但是用Win32_DiskDrive获得的硬盘信息中并不包含SerialNumber属性。   
            //ManagementObjectCollection moc1 = mc1.GetInstances();
            //string strID = null;
            //foreach (ManagementObject mo in moc1)
            //{
            //    strID = mo.Properties["SerialNumber"].Value.ToString();
            //    break;
            //}
            string cpuSerialnumber1 = string.Empty;
            using (MD5 md5Hash = MD5.Create())
            {

                byte[] data =md5Hash.ComputeHash(Encoding.UTF8.GetBytes(textBox1.Text.Trim()));
                byte[] data1 = new byte[] { 0x16,0xa2,0xa8};
                byte[] data2 = md5Hash.ComputeHash(Encoding.UTF8.GetBytes("123"));
                StringBuilder sBuilder = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    sBuilder.Append(data[i].ToString("x2"));
                }
                for (int i = 0; i < data1.Length; i++)
                {
                    sBuilder.Append(data1[i].ToString("x2"));
                }
                for (int i = 0; i < data2.Length; i++)
                {
                    sBuilder.Append(data2[i].ToString("x2"));
                }
                string id = sBuilder.ToString();
                textBox2.Text =id;
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

        private void comboBox5_SelectedIndexChanged(object sender, EventArgs e)
        {

        }
    }
}
