using Cognex.VisionPro.ToolBlock;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    public partial class Form7 : Form
        
    {
        ErrorLog MsgErroeLog = new ErrorLog();
        CogToolBlock block2;
        string jilu;
        DataSet myDataSet = new DataSet();
        DataTable myTable;
        string[] shuju;
       // WebReference.PackWebServiceActionService client;
        public delegate void GetSeletionData(object Sender, SelectionChangedEventArgs e);
        public event GetSeletionData getData;
        public Form7()
        {
            InitializeComponent();
        }
        //cogToolBlockEdit1绑定事件

        private void Form7_Load(object sender, EventArgs e)
        {
            jilu = "";
          //  client = new WebReference.PackWebServiceActionService();
          //  client.fullsizecheckCompleted += new fullsizecheckCompletedEventHandler(client_fullsize);
            myTable = new DataTable();
             this.dataGridView1.DataSource = myTable;//将List的数据绑定到DataGridView中
            myTable.Clear();
            DataRow dr = myTable.NewRow();
            myTable.Rows.Add(dr);
            myTable.Columns.Add("psn", typeof(String));
            myTable.Columns.Add("result", typeof(String));
            myTable.Columns.Add("modulelength", typeof(String));
            myTable.Columns.Add("modulewidth", typeof(String));
            myTable.Columns.Add("modulediagonal2", typeof(String));
            myTable.Columns.Add("modulediagonal3", typeof(String));
            myTable.Columns.Add("modulediagonal4", typeof(String));
            myTable.Columns.Add("modulediagonal5", typeof(String));
            myTable.Columns.Add("modulemountinghole1", typeof(String));
            myTable.Columns.Add("modulemountinghole2", typeof(String));
            myTable.Columns.Add("modulemountinghole3", typeof(String));
            myTable.Columns.Add("Moduleflatness", typeof(String));
            myTable.Columns.Add("Cellflatness", typeof(String));
            myTable.Columns.Add("Cellheightdiffmax", typeof(String));
            myTable.Columns.Add("Cellheightdiffmin", typeof(String));

            myTable.Columns.Add("tab", typeof(String));
            myTable.Columns.Add("line", typeof(String));
            myTable.Columns.Add("esn", typeof(String));


            myTable.Columns.Add("modulebottomflatness", typeof(String));
            myTable.Columns.Add("modulemountinghole4", typeof(String));
            myTable.Columns.Add("modulemountinghole5", typeof(String));
            myTable.Columns.Add("modulemountinghole6", typeof(String));
            myTable.Columns.Add("modulediagonal1", typeof(String));
            myTable.Columns.Add("modulediagonal6", typeof(String));
            myTable.Columns.Add("time", System.Type.GetType("System.DateTime"));
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
        private void Form7_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            this.Visible = false;
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
        private string xuliehua()
        {
            var DataList = new List<Message>();
            string temp = "";
            string end = "";
            if (myTable.Rows[0][1].ToString() == "1")
                end = "OK";
            else
                end = "NG";
            DataList.Add(new Message
            {
                cmd = "put",
                data = new Datas
                {
                    tab = "fullsizecheck",
                    tags = new Tags
                    {
                        line = "ML2",
                        psn = myTable.Rows[0][0].ToString(),
                        esn = "cc001"
                    },
                    fields = new Fields
                    {
                        modulelength = myTable.Rows[0][2].ToString(),
                        modulewidth = myTable.Rows[0][3].ToString(),
                        modulebottomflatness = "0",
                        modulemountinghole1 = myTable.Rows[0][8].ToString(),
                        modulemountinghole2 = myTable.Rows[0][9].ToString(),
                        modulemountinghole3 = myTable.Rows[0][10].ToString(),
                        modulemountinghole4 = "0",
                        modulemountinghole5 = "0",
                        modulemountinghole6 = "0",
                        Moduleflatness = myTable.Rows[0][11].ToString(),
                        Cellflatness = myTable.Rows[0][12].ToString(),
                        Cellheightdiffmax = myTable.Rows[0][13].ToString(),
                        Cellheightdiffmin = myTable.Rows[0][14].ToString(),
                        modulediagonal1 = "0",
                        modulediagonal2 = myTable.Rows[0][4].ToString(),
                        modulediagonal3 = myTable.Rows[0][5].ToString(),
                        modulediagonal4 = myTable.Rows[0][6].ToString(),
                        modulediagonal5 = myTable.Rows[0][7].ToString(),
                        modulediagonal6 = "0",
                        result = end,
                        time = DateTime.Now.ToString().Replace("/", "-")
                    }
                }
            }
            );
            temp = JsonConvert.SerializeObject(DataList);
            return temp;
        }
        void send_mes(string str)
        {
            try
            {
                shuju = null;
                shuju = new string[30];
                shuju = str.Split(',', ';');
                myTable.Rows.Clear();
                DataRow dr = myTable.NewRow();
                myTable.Rows.Add(dr);
                for (int i = 0; i < myTable.Columns.Count; i++)
                {
                    try
                    {
                        if (myTable.Columns[i].ColumnName != "time")
                            myTable.Rows[0][i] = shuju[i];
                        else
                            myTable.Rows[0][i] = DateTime.Now.ToString().Replace("/", "-");
                    }
                    catch
                    {
                        myTable.Rows[0][i] = "0";
                    }
                }
                if (myTable.Rows[0][0].ToString().Count() > 10)
                    jilu = "发送时间:" + DateTime.Now + ":" + (xuliehua().Remove(0, 1)).Replace("]", "");
                xie(jilu);
               // client.fullsizecheckAsync((xuliehua().Remove(0, 1)).Replace("]", ""));
            }
            catch (Exception ex)
            {
                jilu = "发送时间:" + DateTime.Now + ":" + ex.Message;
                txtReceive.Text = jilu;
                xie(jilu);
                textBox2.Text = "false";
                button3_Click(null, null);
            }
        }
        //void client_fullsize(object sender, fullsizecheckCompletedEventArgs e)
        //{
        //    try
        //    {
        //        string results;
        //        results = e.Result;
        //        label10.Text = results;
        //        jilu = "接收时间:" + DateTime.Now + ":" + results;
        //        if (results.Contains("OK"))
        //        {
        //            textBox2.Text = "true";
        //            button3_Click(null, null);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        txtReceive.Text = ex.Message + "----" + e.Error;
        //        jilu = "接收时间:" + DateTime.Now + ":" + ex.Message + "----" + e.Error;
        //        textBox2.Text = "false";
        //        button3_Click(null, null);
        //    };
        //    xie(jilu);
        //}
        private void xie(string qqa)
        {
            try
            {
                if (!File.Exists("D:\\MES日志" + "\\" + DateTime.Now.Month + "月"))
                {
                    Directory.CreateDirectory("D:\\MES日志" + "\\" + DateTime.Now.Month + "月");
                }
                if (!File.Exists("D:\\MES日志" + "\\" + DateTime.Now.Month + "月\\" + DateTime.Now.Day + ".txt"))
                {
                    // ch:使用 using 确保即使写入异常也能释放文件句柄
                    using (FileStream fs1 = new FileStream("D:\\MES日志" + "\\" + DateTime.Now.Month + "月\\" + DateTime.Now.Day + ".txt", FileMode.Create, FileAccess.Write))
                    using (StreamWriter sw = new StreamWriter(fs1))
                    {
                        sw.WriteLine(qqa);
                    }
                }
                else
                {
                    using (FileStream fs1 = new FileStream("D:\\MES日志" + "\\" + DateTime.Now.Month + "月\\" + DateTime.Now.Day + ".txt", FileMode.Append, FileAccess.Write))
                    using (StreamWriter sw = new StreamWriter(fs1))
                    {
                        sw.WriteLine(qqa);
                    }
                }
            }
            catch (Exception ex)
            {
                txtReceive.Text = "\r\n" + ex.Message;
            }
        }
        private void button8_Click(object sender, EventArgs e)
        {
            try
            {
                txtReceive.Text = (xuliehua().Remove(0, 1)).Replace("]", "");
            }
            catch (Exception ex) { MsgErroeLog.WriteLog("异常:" + ex.Message); }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            if (getData != null)
            {
              SelectionChangedEventArgs E = new SelectionChangedEventArgs(textBox2.Text);
              getData(this, E);
            }
        }

        private void button9_Click(object sender, EventArgs e)
        {
            try
            {
                dataGridView1.Visible = false;
                jieshou jie = new jieshou();
                jie = Fxuliehua(txtReceive.Text);
                myTable.Rows[0][1] = jie.Data.fields.result;
                myTable.Rows[0][16] = jie.Data.tags.line;
                dataGridView1.Visible=true;
            }
            catch (Exception ex)

            {
                MessageBox.Show(ex.Message);
            }
        }
        private jieshou Fxuliehua(string jieshou)
        {
            jieshou jie = new jieshou();
            jie = JsonConvert.DeserializeObject<jieshou>(jieshou);
            return jie;
        }

        private void button7_Click(object sender, EventArgs e)
        {
            //    client.fullsizecheckAsync(
            //   "{ \"cmd\": \"put\","+
            //     "\"data\": {"+
            //      "\"tab\": \"fullsizecheck\","+
            //       " \"tags\": {"+
            //            "\"line\": \"ML2\","+
            //    "\"psn\": \"202311141516\","+
            //    "\"esn\": \"cc001\""+
            //"},"+
            //"\"fields\": {"+
            //            "\"modulelength\": \"120\","+
            //    "\"modulewidth\": \"50\","+
            //    "\"modulebottomflatness\": \"1\","+
            //    "\"modulemountinghole1\": \"1\","+
            //    "\"modulemountinghole2\": \"1\","+
            //    "\"modulemountinghole3\": \"1\","+
            //    "\"modulemountinghole4\": \"1\","+
            //    "\"modulemountinghole5\": \"1\"," +
            //    "\"modulemountinghole6\": \"1\"," +
            //   "\"Moduleflatness\": \"0.2\"," +
            //    "\"Cellflatness\": \"0.5\"," +
            //    "\"Cellheightdiffmax\": \"0.9\"," +
            //    "\"Cellheightdiffmin\": \"0.1\"," +
            //   " \"modulediagonal1\": \"0.5\"," +
            //    "\"modulediagonal2\": \"0.9\"," +
            //   " \"modulediagonal3\": \"0.1\"," +
            //   " \"modulediagonal4\": \"0.5\"," +
            //   " \"modulediagonal5\": \"0.9\"," +
            //   " \"modulediagonal6\": \"0.1\"," +
            //   " \"result\": \"OK\"," +
            //   " \"time\": \"2022-11-14 10:30:57\"" +
            //"}"+
            //    "}"+
            //"}");

           // client.fullsizecheckAsync((xuliehua().Remove(0, 1)).Replace("]", ""));
        }
    }
}
