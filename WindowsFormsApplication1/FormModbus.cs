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
using demo;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace WindowsFormsApplication1
{
    public partial class FormModbus : Form
    {
        public Label labelFallbackHint = new Label();

        public FormModbus()
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

            // ch:极速写入开关（持久化）：勾选走独立长连接 xie_wu，取消走 HSL 安全写 xie
            checkBoxXieWu = new CheckBox();
            checkBoxXieWu.AutoSize = true;
            checkBoxXieWu.Name = "checkBoxXieWu";
            checkBoxXieWu.Text = "极速写入";
            checkBoxXieWu.Location = new Point(checkBox2.Left, checkBox2.Bottom + 4);
            tabPage1.Controls.Add(checkBoxXieWu);
            ToolTip xieWuTip = new ToolTip();
            xieWuTip.SetToolTip(checkBoxXieWu, "勾选：独立长连接极速写(xie_wu)，不占用轮询连接。\r\n取消：走 HSL 安全写(xie)。设置自动保存。");
            checkBoxXieWu.CheckedChanged += checkBoxXieWu_CheckedChanged;
            checkBox1.CheckedChanged += (s, e) => { xieWuAddrFromZero = checkBox1.Checked; };
        }

        private ClassIni wdini = new ClassIni();
        private ModbusTcpNet busTcpClient = null;
        private ushort xieWuTransactionId = 0;
        private volatile bool clearing = false; // ch:清除配置期间暂停轮询/输出，防止清除后被旧数据写回
        public volatile bool useXieWu = true; // ch:true=xie_wu 长连接极速写，false=xie 安全写
        private CheckBox checkBoxXieWu;
        private Socket xieWuSocket;
        private readonly object xieWuLock = new object();
        private readonly object modbusIoLock = new object(); // ch:轮询读与极速写互斥，避免双连接并发把寄存器冲成 0
        private int _tcpDataFormatIndex = 0; // ch:P2-⑤ 缓存 DataFormat 选择，避免后台重连线程跨线程读 comboBox1.SelectedIndex
        // ch:P2 pending TTL：写失败保留 pending 后若长期写不出去（PLC 断线/掉线），恢复瞬间会把旧结果发给 PLC（错误工位数据）。
        //   记录"首次失败时刻 + 连续失败次数"，连续 3 次或超过 10 秒即丢弃并告警，避免无限重试。
        private const int PendingMaxFailTimes = 3;
        private const int PendingMaxAgeMs = 10000;
        private readonly Dictionary<int, long> _pendingFirstFailMs = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _pendingFailCount = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _pendingFailId = new Dictionary<int, string>(); // ch:P2 失败计数绑定的"结果身份"([5]+[4])
        private readonly object _pendingTtlLock = new object();

        // ch:P2 写回失败登记：返回 true 表示该结果已超限，调用方应丢弃 pending（不再重试）。
        //   以 camera_dic[cam] 的 [5](通道)+[4](值) 作为"结果身份"：身份变化（新帧登记覆盖了 pending）即重新计数，
        //   避免新结果继承旧失败计数与旧起始时刻而被误丢（原实现只按 camIndex 计数，有此缺陷）。
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
                    // 结果已更换（新帧登记）：重新开始计数
                    _pendingFailId[camIndex] = id;
                    _pendingFirstFailMs[camIndex] = now;
                    _pendingFailCount[camIndex] = 1;
                    return false;
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

        // ch:P2 写回成功：清除失败计数
        private void NotePendingWriteOk(int camIndex)
        {
            lock (_pendingTtlLock)
            {
                _pendingFailId.Remove(camIndex);
                _pendingFirstFailMs.Remove(camIndex);
                _pendingFailCount.Remove(camIndex);
            }
        }
        private System.Collections.Generic.HashSet<int> _xieWuStringWarned = new System.Collections.Generic.HashSet<int>(); // ch:P2-③ 极速写 string 格式仅提示一次，避免每帧刷屏（访问均处于 modbusIoLock 内）
        private volatile bool xieWuClosing = false;
        private string xieWuCachedIp = "";
        private int xieWuCachedPort = 0;
        private byte xieWuCachedStation = 1;
        private volatile bool xieWuAddrFromZero = true;
        private volatile int xieWuDataFmt = 2;
        private int failCount = 0; // ch:连续读取失败计数（日志限流用）
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

        private void FormSiemens_Load( object sender, EventArgs e )
        {
            try
            {
            panel2.Enabled = false;

            comboBox1.SelectedIndex = 0;
            _tcpDataFormatIndex = 0; // ch:P2-⑤ 缓存初始 DataFormat 索引

            comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;
            checkBox3.CheckedChanged += CheckBox3_CheckedChanged;

            Language( Program.Language );

            // ch:R23 反馈诊断日志开关 → R26 默认关闭：现场已测试定案(-32768/恒0 属客户侧改写，非我方问题)，取证完成。
            //   判定由「非0即开」收紧为「仅显式=1才开」——键缺失/非法一律静默，现场 test.ini 无需改动即生效；
            //   需再取证时在 test.ini [camera] fankui_diag=1 重开(若现场已显式存在 =1 需改为0或删该行)。
            _fankuiDiag = wdini.ReadString("camera", "fankui_diag", "0").Replace("\0", "") == "1";

            fins_duxie = new Thread(new ThreadStart(Fins_duxie));
            fins_duxie.IsBackground = true;
            fins_duxie.Start(); // ch:恢复原版时序：轮询线程尽早启动（Load 后续异常不影响轮询）
            decimal xuanzhong_temp = 0;

           // comboBox1.DataSource = HslCommunication.BasicFramework.SoftBasic.GetEnumValues<HslCommunication.Core.DataFormat>();
           // comboBox1.SelectedItem = HslCommunication.Core.DataFormat.CDAB;
           

            for (int i = 0; i < 10; i++)
            {
                dataGridView1.Rows.Add();
            }
            // ch:预填 0-49，与表格容量一致（10列×5组=50槽）
            for (int i = 0; i < 50; i++)
            {
                fins_data.Add(i, new int[] { i % 10, i / 10 * 2 + 1 });
                fins_name.Add(i, new int[] { i % 10, i / 10 * 2 });
                fins_value.Add(i, new byte[] { 0x00, 0x00 });
            }

            // ch:R11-2 ini 使能/地址/通道数读取族统一 TryParse+回退默认+夹取 NUD 范围：
            //   原裸 bool.Parse/decimal.Parse 任一值被现场改坏即抛异常 → Load 的 catch 只记一行日志，
            //   跳过其后 IP/端口/字节序/通道配置与自动连接，通讯窗体静默不工作。
            // ch:R12 在此基础上补两点：
            //   ①解析失败/取整不再静默——原 TryParse 失败直接换默认值，现场改了 ini 不生效无从排查；
            //   ②统一取整——qishi/changdu/geshu/lunxun_time 均为整数语义，保留小数会被存成 "10.5"，
            //     轮询线程 int.Parse(par.Value[1]/[2])、int.Parse(address_qishi.ToString()) 抛 FormatException，
            //     整轮 foreach 中断、其后通道全部不读；lunxun_time 保留 (0,1) 小数还会被 (int) 截成 0 → Sleep(0) 忙等打满单核。
            decimal IniDec(string s, decimal dft)
            {
                decimal v;
                if (!decimal.TryParse(s, out v))
                {
                    MsgErroeLog.WriteLog("modbustcp ini 解析失败，回退默认值 原值=[" + (s ?? "null") + "] 默认=" + dft);
                    v = dft;
                }
                decimal t = Math.Truncate(v);
                if (t != v)
                    MsgErroeLog.WriteLog("modbustcp ini 小数值已取整 [" + v + "]→" + t);
                return t;
            }
            decimal ClampNud(NumericUpDown nud, decimal v) { return Math.Max(nud.Minimum, Math.Min(nud.Maximum, v)); }
            bool bl_ini;
            // ch:R12 TryParse 失败时 out 被置 false，语义与原写法一致；补日志，避免 "1"/"是" 等历史写法静默变 false
            if (!bool.TryParse(wdini.ReadString("modbustcp", "modbus_lunxunen", "false"), out bl_ini))
                MsgErroeLog.WriteLog("modbustcp ini modbus_lunxunen 非布尔值，按 false 处理");
            fins_lunxunen = bl_ini;
            if (!bool.TryParse(wdini.ReadString("modbustcp", "modbus_en", "false"), out bl_ini))
                MsgErroeLog.WriteLog("modbustcp ini modbus_en 非布尔值，按 false 处理");
            fins_en = bl_ini;
            address_qishi = ClampNud(numericUpDown1, IniDec(wdini.ReadString("modbustcp", "qishi", "0"), 0));
            address_length = ClampNud(numericUpDown2, IniDec(wdini.ReadString("modbustcp", "zongchang", "1"), 1));
            lunxun_time = ClampNud(numericUpDown3, IniDec(wdini.ReadString("modbustcp", "lunxun_time", "20"), 20));
            numericUpDown1.Value = address_qishi;
            numericUpDown2.Value = address_length;
            numericUpDown3.Value = lunxun_time;

            comboBox1.Text = wdini.ReadString("modbustcp", "abcd", "CDAB").Replace("\0", "");
            textBox1.Text = wdini.ReadString("modbustcp", "ip", "127.0.0.1").Replace("\0", "");
            textBox2.Text = wdini.ReadString("modbustcp", "port", "9600").Replace("\0", "");
            textBox15.Text = wdini.ReadString("modbustcp", "cell", "0").Replace("\0", "");
            try
            {
                useXieWu = bool.Parse(wdini.ReadString("modbustcp", "xie_wu", "true").Replace("\0", ""));
            }
            catch
            {
                useXieWu = true;
            }
            if (checkBoxXieWu != null)
                checkBoxXieWu.Checked = useXieWu;
            xieWuAddrFromZero = checkBox1.Checked;
            xieWuDataFmt = comboBox1.SelectedIndex;
            //textBox15.Text = wdini.ReadString("modbustcp", "local", "192").Replace("\0", "");
            if (fins_lunxunen)
            {
                checkBox4.CheckState = CheckState.Checked;
            }
            if (fins_en)
            {
                checkBox2.CheckState = CheckState.Checked;
                button1_Click(null, null);
            }
            geshu = (int)IniDec(wdini.ReadString("modbustcp", "geshu", "0"), 0); // ch:R11-2 TryParse+下限 0（负值直接跳过循环）
            geshu = Math.Max(0, Math.Min(64, geshu)); // 上限 64：通道数即循环上界，防 ini 手改天文数字把 Load 卡死
            if (geshu > 0)
            {
                for (int i = 0; i < geshu; i++)
                {
                    fins_mingcheng = wdini.ReadString((i + 1).ToString()+ "_modbustcp", "name", "").Replace("\0", "").Trim();
                    // ch:R12 name 缺省为 ""，geshu>=2 且部分通道未填名时两次 Add("") → Dictionary 重复键抛 ArgumentException，
                    //   落到 Load 的 catch 只记一行日志，其后 IP/串口/通道配置/自动连接全部跳过（正是本轮要消灭的"改坏 ini 后通讯窗体静默不工作"）
                    if (fins_mingcheng.Length == 0)
                    {
                        fins_mingcheng = "通道" + (i + 1);
                        MsgErroeLog.WriteLog("modbustcp 第" + (i + 1) + "段 name 为空，改用默认名[" + fins_mingcheng + "]");
                    }
                    else if (fins_dic.ContainsKey(fins_mingcheng))
                    {
                        MsgErroeLog.WriteLog("modbustcp 第" + (i + 1) + "段 name 与既有通道重名[" + fins_mingcheng + "]，降级为唯一名");
                        fins_mingcheng = fins_mingcheng + "_" + (i + 1);
                    }
                    fins_qishi = IniDec(wdini.ReadString((i + 1).ToString() + "_modbustcp", "qishi", "0"), 0);
                    fins_length = Math.Max(0, Math.Min(1000, IniDec(wdini.ReadString((i + 1).ToString() + "_modbustcp", "changdu", "0"), 0))); // ch:R11-2 内层循环上界，夹 [0,1000]（IniDec 已取整）
                    ABCD = wdini.ReadString((i + 1).ToString() + "_modbustcp", "gaodiwei", "触发").Replace("\0", "");
                    fins_style = wdini.ReadString((i + 1).ToString() + "_modbustcp", "geshi", "int").Replace("\0", "");
                    fins_dic.Add(fins_mingcheng, new string[] { fins_mingcheng, fins_qishi.ToString(), fins_length.ToString(), ABCD, fins_style });
                    for (int j = 0; j < fins_length; j++)
                    {
                        xuanzhong_temp = fins_qishi - address_qishi + j;
                        int t = (int)xuanzhong_temp; // ch:R11-2 原 int.Parse(decimal.ToString()) 遇小数值抛 FormatException
                        // ch:地址越界（负值/超出预填范围）时跳过，防止 KeyNotFoundException
                        // ch:R12 起始条件用原始 decimal——(int)(-0.5)==0 会误判通过，把上色打到 0 号格
                        if (xuanzhong_temp >= 0 && xuanzhong_temp < 50 && t >= 0 && t < 50 && fins_name.ContainsKey(t) && fins_data.ContainsKey(t))
                        {
                            dataGridView1[fins_name[t][0], fins_name[t][1]].Style.BackColor = Color.Green;
                            dataGridView1[fins_data[t][0], fins_data[t][1]].Style.BackColor = Color.Green;
                            dataGridView1[fins_name[t][0], fins_name[t][1]].Value = fins_mingcheng;
                        }
                    }
                }
            }
            // ch:P2-③ 加载期校验：极速写(Send-and-Forget)不支持 string 格式，启动即提示，避免运行期才发现该通道永远写不出去
            if (useXieWu)
            {
                System.Collections.Generic.List<string> xieWuStringNames = new System.Collections.Generic.List<string>();
                foreach (var fd in fins_dic)
                {
                    if (fd.Value.Length > 4 && string.Equals(fd.Value[4], "string", StringComparison.OrdinalIgnoreCase))
                        xieWuStringNames.Add(fd.Key);
                }
                if (xieWuStringNames.Count > 0)
                    MsgErroeLog.WriteLog("modbustcp 极速写不支持 string 格式通道：" + string.Join(",", xieWuStringNames.ToArray()) + "；请改为 int/long/float 或关闭极速写改用普通写回");
            }
            for (int i = 0; i < 10; i++)
            {
                string temp_jian = wdini.ReadString("c" + (i + 1).ToString() + "_modbustcp", "chufa", " ").Replace("\0", "");
                camera_dic.Add(i + 1, new string[] { temp_jian, wdini.ReadString("c" + (i + 1).ToString() + "_modbustcp", "fanhuizhi", "0").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString() + "_modbustcp", "fanhuien", "false").Replace("\0", ""), wdini.ReadString("c" + (i + 1).ToString() + "_modbustcp", "fankui", "0").Replace("\0", ""), "无", "无" });

                if (!fins_zuhe.ContainsKey(temp_jian))
                {
                    fins_zuhe.Add(temp_jian, (i + 1).ToString());
                }
                else
                    fins_zuhe[temp_jian] += (i + 1).ToString();

            }
            // ch:R21 载入期存量重复绑定扫描：ini 里两相机绑同一反馈通道=同地址互相覆盖，仅记日志不弹窗、不改配置
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
            comboBox5.Items.Add(camera_dic[1][0]);
            comboBox5.Text = camera_dic[1][0];
            textBox42.Text = camera_dic[1][1];
            if (camera_dic[1][2] == "true")
                checkBox14.CheckState = CheckState.Checked;
            comboBox6.Items.Add(camera_dic[1][3]);
            comboBox6.Text = camera_dic[1][3];

            comboBox8.Items.Add(camera_dic[2][0]);
            comboBox8.Text = camera_dic[2][0];
            textBox17.Text = camera_dic[2][1];
            if (camera_dic[2][2] == "true")
                checkBox13.CheckState = CheckState.Checked;
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

            textBox40.Text = wdini.ReadString("change_modbustcp", "1", "");
            textBox39.Text = wdini.ReadString("change_modbustcp", "2", "");
            textBox38.Text = wdini.ReadString("change_modbustcp", "3", "");
            textBox37.Text = wdini.ReadString("change_modbustcp", "4", "");
            textBox36.Text = wdini.ReadString("change_modbustcp", "5", "");
            textBox35.Text = wdini.ReadString("change_modbustcp", "6", "");
            textBox34.Text = wdini.ReadString("change_modbustcp", "7", "");
            textBox33.Text = wdini.ReadString("change_modbustcp", "8", "");
            textBox25.Text = wdini.ReadString("path_modbustcp", "1", "");
            textBox26.Text = wdini.ReadString("path_modbustcp", "2", "");
            textBox28.Text = wdini.ReadString("path_modbustcp", "3", "");
            textBox27.Text = wdini.ReadString("path_modbustcp", "4", "");
            textBox32.Text = wdini.ReadString("path_modbustcp", "5", "");
            textBox31.Text = wdini.ReadString("path_modbustcp", "6", "");
            textBox30.Text = wdini.ReadString("path_modbustcp", "7", "");
            textBox29.Text = wdini.ReadString("path_modbustcp", "8", "");

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
            catch (Exception ex)
            {
                ErrorLog MsgErroeLog = new ErrorLog();
                MsgErroeLog.WriteLog("FormSiemens_Load 初始化异常:" + ex);
            }
        }


        private void Language( int language )
        {
            if (language == 2)
            {
                Text = "Modbus Tcp Read Demo";

                label1.Text = "Ip:";
                label3.Text = "Port:";
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
                button24.Text = "w-coil";
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

                button3.Text = "Pressure test, r/w 3,000s";

                label4.Text = "Account";
                label2.Text = "Pwd";
                label5.Text = "When the server is a server built by hsl, login with account name and password is supported.";

            }
        }
        private void ComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (busTcpClient != null)
            {
                switch (comboBox1.SelectedIndex)
                {
                    case 0: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.ABCD; break;
                    case 1: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.BADC; break;
                    case 2: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.CDAB; break;
                    case 3: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.DCBA; break;
                    default: break;
                }
            }
            _tcpDataFormatIndex = comboBox1.SelectedIndex; // ch:P2-⑤ UI 线程缓存，供后台重连线程使用
        }
        // ch:P1-⑦ 重连/自动创建新 ModbusTcpNet 后，新连接 DataFormat 会退回 HSL 默认(ABCD)，
        //   必须按当前 comboBox1 选择重新设回，否则重连后 float/long 静默写错。
        private void ApplyTcpDataFormat()
        {
            if (busTcpClient == null) return;
            switch (_tcpDataFormatIndex) // ch:P2-⑤ 用 UI 线程缓存的索引，避免后台线程跨线程读 comboBox1
            {
                case 0: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.ABCD; break;
                case 1: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.BADC; break;
                case 2: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.CDAB; break;
                case 3: busTcpClient.DataFormat = HslCommunication.Core.DataFormat.DCBA; break;
            }
        }

        private void CheckBox3_CheckedChanged(object sender, EventArgs e)
        {
            if (busTcpClient != null)
            {
                busTcpClient.IsStringReverse = checkBox3.Checked;
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
            // 连接
            if (!System.Net.IPAddress.TryParse( textBox1.Text, out System.Net.IPAddress address ))
            {
                MsgErroeLog.WriteLog( DemoUtils.IpAddressInputWrong );
                return;
            }


            if(!int.TryParse(textBox2.Text,out int port))
            {
                MsgErroeLog.WriteLog( DemoUtils.PortInputWrong );
                return;
            }


            if(!byte.TryParse(textBox15.Text,out byte station))
            {
                MsgErroeLog.WriteLog( "Station input is wrong！" );
                return;
            }

            busTcpClient?.ConnectClose( );
            CloseXieWuSocket();
            busTcpClient = new ModbusTcpNet( textBox1.Text, port, station );
            busTcpClient.AddressStartWithZero = checkBox1.Checked;

            busTcpClient.SetLoginAccount( textBox14.Text, textBox12.Text );

            ComboBox1_SelectedIndexChanged( null, new EventArgs( ) );  // 设置数据服务
            busTcpClient.IsStringReverse = checkBox3.Checked;

            try
            {
                OperateResult connect = busTcpClient.ConnectServer( );
                if (connect.IsSuccess)
                {
                    xieWuTransactionId = 0;
                    xieWuCachedIp = textBox1.Text.Trim();
                    xieWuCachedPort = port;
                    xieWuCachedStation = station;
                    xieWuAddrFromZero = checkBox1.Checked;
                    xieWuDataFmt = comboBox1.SelectedIndex;

                    MsgErroeLog.WriteLog( HslCommunication.StringResources.Language.ConnectedSuccess );
                    SyncConnectedUiState(); // ch:R22 Load 的 Task.Run 在池线程调 button1_Click，UI 状态更新封送到 UI 线程
                }
                else
                {
                    MsgErroeLog.WriteLog( HslCommunication.StringResources.Language.ConnectedFailed + connect.Message );
                }
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

            userControlCurve1.ReadWriteNet = busTcpClient;
        }

        private void button2_Click( object sender, EventArgs e )
        {
            // 断开连接
            busTcpClient.ConnectClose( );
            CloseXieWuSocket();
            button2.Enabled = false;
            button1.Enabled = true;
            panel2.Enabled = false;
        }
        
        #endregion

        #region 单数据读取测试


        private void button_read_bool_Click( object sender, EventArgs e )
        {
            // 读取bool变量
            DemoUtils.ReadResultRender( busTcpClient.ReadCoil( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button4_Click_1( object sender, EventArgs e )
        {
            // 读取离散输入bool变量
            DemoUtils.ReadResultRender( busTcpClient.ReadDiscrete( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_short_Click( object sender, EventArgs e )
        {
            // 读取short变量
            DemoUtils.ReadResultRender( busTcpClient.ReadInt16( textBox3.Text ), textBox3.Text, textBox4 );

            // 这一行是测试读取short数组的代码，忽略就行
            // short[] values = busTcpClient.ReadInt16( "100", 2 ).Content;
        }

        private void button_read_ushort_Click( object sender, EventArgs e )
        {
            // 读取ushort变量
            DemoUtils.ReadResultRender( busTcpClient.ReadUInt16( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_int_Click( object sender, EventArgs e )
        {
            // 读取int变量
            DemoUtils.ReadResultRender( busTcpClient.ReadInt32(  textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_uint_Click( object sender, EventArgs e )
        {
            // 读取uint变量
            DemoUtils.ReadResultRender( busTcpClient.ReadUInt32( textBox3.Text ), textBox3.Text, textBox4 );
        }
        private void button_read_long_Click( object sender, EventArgs e )
        {
            // 读取long变量
            DemoUtils.ReadResultRender( busTcpClient.ReadInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_ulong_Click( object sender, EventArgs e )
        {
            // 读取ulong变量
            DemoUtils.ReadResultRender( busTcpClient.ReadUInt64( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_float_Click( object sender, EventArgs e )
        {
            // 读取float变量
            DemoUtils.ReadResultRender( busTcpClient.ReadFloat( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_double_Click( object sender, EventArgs e )
        {
            // 读取double变量
            DemoUtils.ReadResultRender( busTcpClient.ReadDouble( textBox3.Text ), textBox3.Text, textBox4 );
        }

        private void button_read_string_Click( object sender, EventArgs e )
        {
            // 读取字符串
            DemoUtils.ReadResultRender( busTcpClient.ReadString( textBox3.Text , ushort.Parse( textBox5.Text ) ), textBox3.Text, textBox4 );
        }


        #endregion

        #region 单数据写入测试


        private void button24_Click( object sender, EventArgs e )
        {
            // bool写入
            try
            {
                DemoUtils.WriteResultRender( busTcpClient.WriteCoil( textBox8.Text, bool.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , short.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , ushort.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , int.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , uint.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , long.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , ulong.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , float.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , double.Parse( textBox7.Text ) ), textBox8.Text );
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
                DemoUtils.WriteResultRender( busTcpClient.Write( textBox8.Text , textBox7.Text ), textBox8.Text );
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
            DemoUtils.BulkReadRenderResult( busTcpClient, textBox6, textBox9, textBox10 );
        }



        #endregion

        #region 报文读取测试


        private void button26_Click( object sender, EventArgs e )
        {
            OperateResult<byte[]> read = busTcpClient.ReadFromCoreServer( HslCommunication.BasicFramework.SoftBasic.HexStringToBytes( textBox13.Text ) );
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

        #region 压力测试

        private void button4_Click( object sender, EventArgs e )
        {
            PressureTest2( );
        }

        private int thread_status = 0;
        private int failed = 0;
        private readonly object _pressLock = new object(); // ch:N2 压力测试读写事务互斥，避免多线程 Write/Read 应答错配
        private DateTime thread_time_start = DateTime.Now;
        // 压力测试，开3个线程，每个线程进行读写操作，看使用时间
        private void PressureTest2( )
        {
            thread_status = 3;
            failed = 0;
            thread_time_start = DateTime.Now;
            new Thread( new ThreadStart( thread_test2 ) ) { IsBackground = true, }.Start( );
            new Thread( new ThreadStart( thread_test2 ) ) { IsBackground = true, }.Start( );
            new Thread( new ThreadStart( thread_test2 ) ) { IsBackground = true, }.Start( );
            button3.Enabled = false;
        }

        private void thread_test2( )
        {
            int count = 500;
            while (count > 0)
            {
                // ch:N2 压测的"写+读"为复合事务，加锁避免 3 个线程互相插队导致应答错配
                lock (_pressLock)
                {
                    if (!busTcpClient.Write( "100", (short)1234 ).IsSuccess) failed++;
                    if (!busTcpClient.ReadInt16( "100" ).IsSuccess) failed++;
                }
                count--;
            }
            thread_end( );
        }

        private void thread_end( )
        {
            if (Interlocked.Decrement( ref thread_status ) == 0)
            {
                // 执行完成
                Invoke( new Action( ( ) =>
                {
                    button3.Enabled = true;
                    MessageBox.Show( "Spend：" + (DateTime.Now - thread_time_start).TotalSeconds + Environment.NewLine + " Read Failed：" + failed );
                } ) );
            }
        }
        
        #endregion

        #region Test Function


        private void Test1()
        {
            OperateResult<bool[]> read = busTcpClient.ReadCoil( "100", 10 );
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
            OperateResult write = busTcpClient.WriteCoil( "100", values );
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
        private bool front = false;

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (this.Visible == true && front == false)
            {
                front = true;
                this.BringToFront();
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
        public bool fins_en = false;
        decimal lunxun_time = 0;
        public bool chushihua = false;
        private int _lastGridUiTick;

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
                    return; // ch:R21 窗口未创建/已销毁不入队
                if (InvokeRequired)
                {
                    // ch:R21 头号嫌疑修复：轮询/写回线程直写 dataGridView1 单元格被
                    // CheckForIllegalCrossThreadCalls=false 掩盖为控件状态累积损坏(配置窗越开越慢)，
                    // 改为 BeginInvoke 封送到 UI 线程；调用方已有 Visible+250ms 门控，入队量有界
                    BeginInvoke(new Action<int, int, object>(SetModbusGridValue), new object[] { col, row, value });
                    return;
                }
                dataGridView1[col, row].Value = value;
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
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
                                    textBox16.Text = p_temp.Value[0];
                                    comboBox2.Text = p_temp.Value[3];
                                    comboBox3.Text = p_temp.Value[4];
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
                wdini.WriteString("modbustcp", "qishi", address_qishi.ToString());
                RefreshFinsTable(); // ch:起始地址变化后立即刷新表格（清空并按新起点重绘）
            }
        }

        private void numericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                address_length = numericUpDown2.Value;
                wdini.WriteString("modbustcp", "zongchang", numericUpDown2.Value.ToString());
            }
        }

        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                lunxun_time = Math.Truncate(numericUpDown3.Value); // ch:R12 取整，(0,1) 小数会让 Sleep((int)v)=Sleep(0) 忙等
                wdini.WriteString("modbustcp", "lunxun_time", lunxun_time.ToString());

            }
        }

        private void checkBox4_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
                else
                {
                    fins_lunxunen = false;
                }
                wdini.WriteString("modbustcp", "modbus_lunxunen", fins_lunxunen.ToString());
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
                wdini.WriteString("modbustcp", "modbus_en", fins_en.ToString());

            }
        }

        private void checkBoxXieWu_CheckedChanged(object sender, EventArgs e)
        {
            useXieWu = checkBoxXieWu != null && checkBoxXieWu.Checked;
            if (!useXieWu)
                CloseXieWuSocket();
            if (chushihua)
                wdini.WriteString("modbustcp", "xie_wu", useXieWu.ToString());
        }

        private void button5_Click(object sender, EventArgs e)
        {
            clearing = true; // ch:清除期间暂停轮询与输出线程，防止清除后旧数据写回
            try
            {
            dataGridView1.Visible = false;
            wdini.WriteString("modbustcp", "geshu", "0");
            fins_dic.Clear();
            // ch:清掉相机反馈映射，使检测结果输出彻底停止（fins_dic 为空时本不会写，双保险）
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
            button6_Click(null, null);
            }
            finally
            {
                clearing = false;
            }
        }
        public bool[] fins_xie = new bool[] { false, false, false, false, false, false, false, false, false, false };
        private Thread fins_duxie;

        private void button9_Click(object sender, EventArgs e)
        {
            if (button9.Text == "显示")
            {
                tabControl1.Visible = true;
                button9.Text = "隐藏";
            }
            else
            {
                tabControl1.Visible = false;
                button9.Text = "显示";
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            try
            {
                fins_lunxunen = false;
                decimal xuanzhong_temp = 0;
                bool chongdie = false;
                if (comboBox3.Text.Length > 1 && comboBox2.Text.Length > 1 && textBox16.Text.Length > 0)
                {
                    if ((comboBox3.Text == "string" || comboBox3.Text == "int") || (numericUpDown5.Value % 2 == 0))
                    {
                        if (numericUpDown5.Value >= numericUpDown1.Value && numericUpDown1.Value + numericUpDown2.Value >= numericUpDown4.Value + numericUpDown5.Value)
                        {
                            if (textBox16.Text.Length > 0)
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
                                    // ch:R12 手动新增通道与 Load 路径同款加固：
                                    //   ①重名 → Dictionary.Add 重复键抛异常（原只靠 catch 弹窗，fins_dic 已增而 ini 未写，状态不一致）；
                                    //   ②地址/长度取整后入 fins_dic 与 ini，否则轮询线程 int.Parse(par.Value[1]/[2]) 抛 FormatException；
                                    //   ③上色索引改 (int) 强转 + 越界守卫（原 int.Parse(decimal.ToString()) 遇小数抛 FormatException，
                                    //     且只挡住了 Value 写入、没挡 BackColor 两行 → 越界时 fins_name[...] 会 KeyNotFound）
                                    string nmAdd = textBox16.Text.Trim();
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
                                        fins_dic.Add(nmAdd, new string[] { nmAdd, qAdd.ToString(), lenAdd.ToString(), comboBox2.Text, comboBox3.Text });
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
                                        wdini.WriteString("modbustcp", "geshu", fins_dic.Count.ToString());
                                        wdini.WriteString(geshu.ToString() + "_modbustcp", "name", nmAdd);
                                        wdini.WriteString(geshu.ToString() + "_modbustcp", "qishi", qAdd.ToString());
                                        wdini.WriteString(geshu.ToString() + "_modbustcp", "changdu", lenAdd.ToString());
                                        wdini.WriteString(geshu.ToString() + "_modbustcp", "gaodiwei", comboBox2.Text);
                                        wdini.WriteString(geshu.ToString() + "_modbustcp", "geshi", comboBox3.Text);
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
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                if (checkBox4.CheckState == CheckState.Checked)
                {
                    fins_lunxunen = true;
                }
            }
        }
        public string GetMiddleValue(string str, string sta, string end)
        {
            Regex rg = new Regex("(?<=(" + sta + "))[.\\s\\S]*?(?=(" + end + "))", RegexOptions.Multiline | RegexOptions.Singleline);
            return rg.Match(str).Value;
        }
        void Fins_duxie()
        {
            while (true)
            {
                // ch:R10-3 原 int.Parse(decimal.ToString()) 在区域小数点/ini 带小数时抛 FormatException，且在 try 外直接杀死轮询线程；
                // 另：lunxun_time<=0 时补 50ms 小睡，避免 while(true) 全速空转吃满一核
                // ch:R12 判据由 >0 改为 >=1：原 0<lunxun_time<1 时 (int) 截成 0 → Sleep(0) 忙等，
                //   且 if 仍为真 → 全速轮询 + 单核打满 + Modbus 请求风暴（NUD3 最小值为 0，ini 手改 0.5 即触发）
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
                                lock (modbusIoLock)
                                {
                                if (busTcpClient == null)
                                {
                                    // ch:未连接时用当前界面配置自动创建并连接（修改 IP 后无需重启即可生效）
                                    try
                                    {
                                        busTcpClient = new ModbusTcpNet(textBox1.Text.Trim(), int.Parse(textBox2.Text.Trim()), byte.Parse(textBox15.Text.Trim()));
                                        busTcpClient.AddressStartWithZero = checkBox1.Checked;
                                        busTcpClient.IsStringReverse = checkBox3.Checked;
                                        busTcpClient.SetLoginAccount(textBox14.Text, textBox12.Text);
                                        busTcpClient.ConnectServer();
                                        ApplyTcpDataFormat(); // ch:P1-⑦ 新连接补设字节序，避免重连后 float/long 写错
                                    }
                                    catch (Exception ex) { MsgErroeLog.WriteLog("ModbusTCP 自动连接失败:" + ex.Message); }
                                    if (busTcpClient == null) continue;
                                }
                                bool gridUi = AllowModbusGridRefresh();
                                int xuanzhong_temp = 0;
                                string fins_temp = "";
                                string shuju_temp = "";
                                if (fins_dic.Count > 0)
                                {
                                    foreach (var par in fins_dic)
                                    {
                                        shuju_temp = "";
                                        for (int j = 0; j < int.Parse(par.Value[2]); j++)
                                        {
                                            if (par.Value[4] == "int")
                                            {

                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取short变量
                                                string addrRd = (int.Parse(par.Value[1]) + j).ToString(); // ch:R24 地址只算一次
                                                OperateResult<short> rdR24 = busTcpClient.ReadInt16(addrRd);
                                                DemoUtils.ReadResultRender1(rdR24, addrRd, out fins_temp);
                                                FankuiDiagLogByKey("读回|" + addrRd, "反馈诊断 读回 addr=" + addrRd + " val=" + (rdR24.IsSuccess ? rdR24.Content.ToString() : "读失败")); // ch:R24 环④ 线上寄存器原值，与环②发出值同日志时间轴夹击改写时刻
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                    SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "string")
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                DemoUtils.ReadResultRender1(busTcpClient.ReadString((int.Parse(par.Value[1]) + j).ToString(), 1), (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                    SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "long" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                DemoUtils.ReadResultRender1(busTcpClient.ReadInt32((int.Parse(par.Value[1]) + j).ToString()), (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                    SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                            else if (par.Value[4] == "float" && j % 2 == 0)
                                            {
                                                xuanzhong_temp = int.Parse(par.Value[1]) - int.Parse(address_qishi.ToString()) + j;
                                                // 读取字符串
                                                DemoUtils.ReadResultRender1(busTcpClient.ReadFloat((int.Parse(par.Value[1]) + j).ToString()), (int.Parse(par.Value[1]) + j).ToString(), out fins_temp);
                                                if (gridUi && xuanzhong_temp >= 0 && xuanzhong_temp < 50)
                                                    SetModbusGridValue(fins_data[int.Parse(xuanzhong_temp.ToString())][0], fins_data[int.Parse(xuanzhong_temp.ToString())][1], fins_temp);
                                                shuju_temp += GetMiddleValue(fins_temp, " ", "\r");
                                            }
                                        }
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
                                                                // ch:记录已处理的触发值，防止 PLC 触发字保持期间重复触发
                                                                camera_dic[pap.Key][1] = shuju_temp;
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
                                    failCount++;
                                    if (failCount % 30 == 1)
                                        MsgErroeLog.WriteLog(fins_temp + "_modbustcp");
                                    // ch:读取失败自动用当前界面配置重连（修改 IP/端口后无需重启即可生效）
                                    try
                                    {
                                        busTcpClient?.ConnectClose();
                                        busTcpClient = new ModbusTcpNet(textBox1.Text.Trim(), int.Parse(textBox2.Text.Trim()), byte.Parse(textBox15.Text.Trim()));
                                        busTcpClient.AddressStartWithZero = checkBox1.Checked;
                                        busTcpClient.IsStringReverse = checkBox3.Checked;
                                        busTcpClient.SetLoginAccount(textBox14.Text, textBox12.Text);
                                        busTcpClient.ConnectServer();
                                        ApplyTcpDataFormat(); // ch:P1-⑦ 重连分支补设字节序，避免重连后 float/long 写错
                                    }
                                    catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                                }
                                else
                                {
                                    failCount = 0;
                                }
                                } // ch:modbusIoLock
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MsgErroeLog.WriteLog(ex.Message + "modbustcp");
                    }
                }
                else
                {
                    // ch:R12 轮询关闭期的空闲小睡；顶部三元在 pollOn=false 时已 Sleep(50)，此处再睡一次是冗余的，
                    //   但仅让关闭期循环从 50ms 变 100ms（更省 CPU），无功能影响，故保留不动原结构
                }
            }
        }
        public void xie(string value)
        {
            try
            {
                if (!chushihua || !fins_en || fins_dic.Count == 0) return;

                lock (modbusIoLock)
                {
                bool gridUi = AllowModbusGridRefresh();
                string fins_temp = "";
                foreach (var pat in camera_dic)
                {
                    if (pat.Value[5] != "无")
                    {
                        try // ch:P2 每相机独立 try：单个相机的坏值/越界不再中断整表写回（原实现一个异常饿死其余相机）
                        {
                        fins_temp = ""; // ch:P2-② 每轮重置写回渲染结果，避免上一相机失败串入本相机判定
                        bool anyWriteFailed = false; // ch:P2-④ 聚合本相机所有写回的真实结果（多寄存器循环写逐次与）
                        if (busTcpClient == null) // ch:P2 客户端为空也计入失败：否则 pending 永不超限、每帧 NRE 刷日志（与 RTU/Omron 对齐）
                        {
                            MsgErroeLog.WriteLog("modbustcp 写回失败: 客户端为空 cam=" + pat.Key);
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
                                int regLen; // ch:R21 写回长度截断：与 XieWuWriteOne 对齐，值数按通道登记长度 Math.Min 截断，防溢入相邻通道(多相机数据互串)
                                if (!int.TryParse(par.Value[2], out regLen)) { anyWriteFailed = true; MsgErroeLog.WriteLog("写回通道长度解析失败(保留pending):" + par.Value[2]); break; } // ch:R21 与地址解析同款处理

                                if (fmt == "int")
                                {
                                    // 逗号分隔 → short数组, FC16批量写
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R21 写回截断：值数按通道登记长度截断，与 XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R21 仅截断时记一条
                                    short[] vals = new short[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (short)Math.Round(double.Parse(parts[i].Trim()));
                                    FankuiDiagLog(pat.Key, "普通写解析", "addr=" + addr_start + " vals=" + string.Join("/", Array.ConvertAll(vals, v => v.ToString()))); // ch:R23 环② 真正写入 FC16 的寄存器值
                                    // 先尝试FC16批量写，失败则降级为FC06逐地址写
                                    OperateResult batchResult = busTcpClient.Write(addr_start.ToString(), vals);
                                    if (!batchResult.IsSuccess && vals.Length > 1)
                                    {
                                        // ch:P1-11 聚合降级逐寄存器写的真实结果：任一失败则整体记失败，不再无条件显示"成功"
                                        OperateResult fallbackResult = OperateResult.CreateSuccessResult();
                                        for (int j = 0; j < vals.Length; j++)
                                        {
                                            OperateResult wr = busTcpClient.Write((addr_start + j).ToString(), vals[j]);
                                            if (wr != null && !wr.IsSuccess)
                                                fallbackResult = wr;
                                        }
                                        if (!DemoUtils.WriteResultRenderOk(() => fallbackResult, addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    }
                                    else
                                    {
                                        if (!DemoUtils.WriteResultRenderOk(() => batchResult, addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    }
                                    for (int j = 0; j < vals.Length; j++)
                                    {
                                        if (!gridUi) break;
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R21 网格越界守卫：PLC已写成功，仅跳过UI回写，防 KeyNotFound 被 catch 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "long")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R21 long 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R21 写回截断，与 XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R21 仅截断时记一条
                                    int[] vals = new int[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = (int)Math.Round(double.Parse(parts[i].Trim()));
                                    FankuiDiagLog(pat.Key, "普通写解析", "addr=" + addr_start + " vals=" + JoinHex32(vals)); // ch:R24 环② long 分支补记(带位HEX，重叠通道覆盖可当场识别)
                                    if (!DemoUtils.WriteResultRenderOk(() => busTcpClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        if (!gridUi) break;
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R21 网格越界守卫，防 KeyNotFound 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "float")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int valLen = Math.Max(1, regLen / 2); // ch:R21 float 占2寄存器
                                    int writeCount = Math.Min(parts.Length, valLen); // ch:R21 写回截断，与 XieWuWriteOne 对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R21 仅截断时记一条
                                    float[] vals = new float[writeCount];
                                    for (int i = 0; i < writeCount; i++)
                                        vals[i] = float.Parse(parts[i].Trim());
                                    FankuiDiagLog(pat.Key, "普通写解析", "addr=" + addr_start + " vals=" + JoinHexF(vals)); // ch:R24 环② float 分支补记(带位HEX，float位模式可含0x80)
                                    if (!DemoUtils.WriteResultRenderOk(() => busTcpClient.Write(addr_start.ToString(), vals), addr_start.ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④
                                    for (int j = 0; j < vals.Length * 2; j++)
                                    {
                                        if (!gridUi) break;
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (xuanzhong_temp < 0 || !fins_data.ContainsKey(xuanzhong_temp)) continue; // ch:R21 网格越界守卫，防 KeyNotFound 误判写回失败
                                        SetModbusGridValue(fins_data[xuanzhong_temp][0], fins_data[xuanzhong_temp][1], fins_temp);
                                    }
                                }
                                else if (fmt == "string")
                                {
                                    string[] parts = pat.Value[4].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int writeCount = Math.Min(parts.Length, regLen); // ch:R21 string 每值占1寄存器，截断与 XieWuWriteOne 语义对齐
                                    if (writeCount < parts.Length) MsgErroeLog.WriteLog("写回超长截断 cam=" + pat.Key + " ch=" + par.Value[0] + " 值数=" + parts.Length + ">登记长度" + regLen); // ch:R21 仅截断时记一条
                                    FankuiDiagLog(pat.Key, "普通写解析", "addr=" + addr_start + " vals=" + string.Join("/", parts, 0, Math.Min(writeCount, parts.Length))); // ch:R24 环② string 分支补记(截断后真正写入部分)
                                    for (int j = 0; j < writeCount; j++)
                                    {
                                        int xuanzhong_temp = addr_start - int.Parse(address_qishi.ToString()) + j;
                                        if (!DemoUtils.WriteResultRenderOk(() => busTcpClient.Write((addr_start + j).ToString(), parts[j].Trim()), (addr_start + j).ToString(), out fins_temp)) anyWriteFailed = true; // ch:P2-④ 逐寄存器聚合，避免末次成功掩盖前次失败
                                        if (gridUi && xuanzhong_temp >= 0 && fins_data.ContainsKey(xuanzhong_temp)) // ch:R21 网格越界守卫，防 KeyNotFound 误判写回失败
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
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "modbustcp");
            }
        }

        #region 无需握手的极速写入 (Send-and-Forget)

        private byte[] BuildWriteMultipleRegistersFrame(ushort startAddress, short[] values, byte station)
        {
            int byteCount = values.Length * 2;
            byte[] frame = new byte[13 + byteCount];
            int offset = 0;

            frame[offset++] = (byte)(xieWuTransactionId >> 8);
            frame[offset++] = (byte)xieWuTransactionId;
            frame[offset++] = 0x00;
            frame[offset++] = 0x00;
            int len = 6 + byteCount;
            frame[offset++] = (byte)(len >> 8);
            frame[offset++] = (byte)len;
            frame[offset++] = station;
            frame[offset++] = 0x10;
            frame[offset++] = (byte)(startAddress >> 8);
            frame[offset++] = (byte)startAddress;
            frame[offset++] = (byte)(values.Length >> 8);
            frame[offset++] = (byte)values.Length;
            frame[offset++] = (byte)byteCount;
            for (int i = 0; i < values.Length; i++)
            {
                frame[offset++] = (byte)(values[i] >> 8);
                frame[offset++] = (byte)values[i];
            }

            return frame;
        }

        private byte[] BuildWriteMultipleRegistersFrame(ushort startAddress, int[] values, byte station, int dataFmt)
        {
            int byteCount = values.Length * 4;
            byte[] frame = new byte[13 + byteCount];
            int offset = 0;

            frame[offset++] = (byte)(xieWuTransactionId >> 8);
            frame[offset++] = (byte)xieWuTransactionId;
            frame[offset++] = 0x00;
            frame[offset++] = 0x00;
            ushort qty = (ushort)(values.Length * 2);
            int len = 6 + byteCount;
            frame[offset++] = (byte)(len >> 8);
            frame[offset++] = (byte)len;
            frame[offset++] = station;
            frame[offset++] = 0x10;
            frame[offset++] = (byte)(startAddress >> 8);
            frame[offset++] = (byte)startAddress;
            frame[offset++] = (byte)(qty >> 8);
            frame[offset++] = (byte)qty;
            frame[offset++] = (byte)byteCount;

            byte[] b = new byte[4];
            for (int i = 0; i < values.Length; i++)
            {
                b[0] = (byte)(values[i] >> 24);
                b[1] = (byte)(values[i] >> 16);
                b[2] = (byte)(values[i] >> 8);
                b[3] = (byte)values[i];
                Order4Bytes(b, dataFmt);
                frame[offset++] = b[0];
                frame[offset++] = b[1];
                frame[offset++] = b[2];
                frame[offset++] = b[3];
            }

            return frame;
        }

        private byte[] BuildWriteMultipleRegistersFrame(ushort startAddress, float[] values, byte station, int dataFmt)
        {
            int byteCount = values.Length * 4;
            byte[] frame = new byte[13 + byteCount];
            int offset = 0;

            frame[offset++] = (byte)(xieWuTransactionId >> 8);
            frame[offset++] = (byte)xieWuTransactionId;
            frame[offset++] = 0x00;
            frame[offset++] = 0x00;
            ushort qty = (ushort)(values.Length * 2);
            int len = 6 + byteCount;
            frame[offset++] = (byte)(len >> 8);
            frame[offset++] = (byte)len;
            frame[offset++] = station;
            frame[offset++] = 0x10;
            frame[offset++] = (byte)(startAddress >> 8);
            frame[offset++] = (byte)startAddress;
            frame[offset++] = (byte)(qty >> 8);
            frame[offset++] = (byte)qty;
            frame[offset++] = (byte)byteCount;

            byte[] b = new byte[4];
            for (int i = 0; i < values.Length; i++)
            {
                b = BitConverter.GetBytes(values[i]);
                Order4Bytes(b, dataFmt);
                frame[offset++] = b[0];
                frame[offset++] = b[1];
                frame[offset++] = b[2];
                frame[offset++] = b[3];
            }

            return frame;
        }

        /// <summary>
        /// 根据 dataFmt（comboBox1.SelectedIndex）重新排列 4 个字节
        /// </summary>
        private void Order4Bytes(byte[] b, int dataFmt)
        {
            byte t;
            switch (dataFmt)
            {
                case 0: // ABCD: 保持原样
                    break;
                case 1: // BADC: 交换字节对内的两个字节
                    t = b[0]; b[0] = b[1]; b[1] = t;
                    t = b[2]; b[2] = b[3]; b[3] = t;
                    break;
                case 2: // CDAB: 交换高16位和低16位
                    t = b[0]; b[0] = b[2]; b[2] = t;
                    t = b[1]; b[1] = b[3]; b[3] = t;
                    break;
                case 3: // DCBA: 完全反转
                    t = b[0]; b[0] = b[3]; b[3] = t;
                    t = b[1]; b[1] = b[2]; b[2] = t;
                    break;
            }
        }

        // ch:R23 反馈发送诊断：三环裁决「相机1第2位 实际0/PLC收-32768、相机2第2,3位恒0」——
        //   ①原始串(视觉 ToolBlock 输出) → ②解析后(我们真正写入 FC16 的寄存器值) → ③PLC 读回，
        //   逐环对齐即可定位问题在视觉输出、解析换算、还是 PLC 侧/第二写入者。
        //   变化触发 + 每相机每类 10 秒节流防刷屏；ini camera/fankui_diag=1 打开。
        //   ch:R26 默认关闭(现场取证完成，坏值定案为客户侧改写)：关闭态 FankuiDiagLogByKey 首行早返回，零锁零写开销。
        private bool _fankuiDiag = false;
        private readonly object _fankuiDiagLock = new object();
        private readonly System.Collections.Generic.Dictionary<string, string> _fankuiDiagLast = new System.Collections.Generic.Dictionary<string, string>();
        private readonly System.Collections.Generic.Dictionary<string, int> _fankuiDiagTick = new System.Collections.Generic.Dictionary<string, int>();

        private void FankuiDiagLog(int cam, string tag, string text)
        {
            FankuiDiagLogByKey(cam + "|" + tag, "反馈诊断 cam=" + cam + " " + tag + "=" + text);
        }

        // ch:R24 任意 key(如「读回|1101」)登记的同款限频日志：变化触发 + 每键 10 秒节流，供环④轮询读回复用
        private void FankuiDiagLogByKey(string key, string message)
        {
            if (!_fankuiDiag) return;
            lock (_fankuiDiagLock)
            {
                string prev;
                int lastTick;
                bool changed = !_fankuiDiagLast.TryGetValue(key, out prev) || prev != message;
                bool due = !_fankuiDiagTick.TryGetValue(key, out lastTick) || unchecked(Environment.TickCount - lastTick) >= 10000;
                if (!(changed && due)) return;
                _fankuiDiagLast[key] = message;
                _fankuiDiagTick[key] = Environment.TickCount;
            }
            MsgErroeLog.WriteLog(message);
        }

        // ch:R24 long 写出值格式化：十进制(32位HEX)，位型可直接判有无 0x80
        private static string JoinHex32(int[] vs)
        {
            string s = "";
            for (int i = 0; i < vs.Length; i++) s += (i > 0 ? "/" : "") + vs[i] + "(0x" + vs[i].ToString("X8") + ")";
            return s;
        }

        // ch:R24 float 写出值格式化：十进制(原始位HEX)，float 位模式可能含 0x80 字节
        private static string JoinHexF(float[] vs)
        {
            string s = "";
            for (int i = 0; i < vs.Length; i++) s += (i > 0 ? "/" : "") + vs[i] + "(0x" + BitConverter.ToInt32(BitConverter.GetBytes(vs[i]), 0).ToString("X8") + ")";
            return s;
        }

        public void WriteCameraResult(int camIndex, string value)
        {
            try
            {
                // 单一写入模式下，登记 pending 与消费必须是同一事务；否则同一相机的
                // 下一帧可能在本帧清标志前覆盖/被本帧清掉。xie 内部和
                // XieWuWriteOne 内部都使用同一把锁，Monitor 可重入。
                lock (modbusIoLock)
                {
                    if (clearing) return;
                    if (!chushihua || !fins_en) return;
                    if (!camera_dic.ContainsKey(camIndex)) return;
                    camera_dic[camIndex][4] = value;
                    camera_dic[camIndex][5] = camera_dic[camIndex][3];
                    FankuiDiagLog(camIndex, "原始串", value); // ch:R23 环① 视觉 ToolBlock 输出的原样值串
                    if (camIndex >= 1 && camIndex <= fins_xie.Length)
                        fins_xie[camIndex - 1] = true;
                    if (useXieWu)
                        XieWuWriteOne(camIndex, value);
                    else
                        xie(value);
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "WriteCameraResult");
            }
        }
        // ch:P0 方案切换写回暂存：与 xie/XieWuWriteOne 消费共用 modbusIoLock，保证 [4](值)/[5](通道) 原子配对，消除跨线程撕裂
        public void SetSwitchPending(int camIndex)
        {
            lock (modbusIoLock)
            {
                if (camera_dic.ContainsKey(camIndex) && !camera_dic[camIndex][1].Contains("无"))
                {
                    camera_dic[camIndex][4] = camera_dic[camIndex][1];
                    camera_dic[camIndex][5] = camera_dic[camIndex][0];
                    if (camIndex >= 1 && camIndex <= fins_xie.Length) fins_xie[camIndex - 1] = true;
                }
            }
        }

        public void CloseXieWuSocket()
        {
            xieWuClosing = true;
            lock (xieWuLock)
            {
                Socket s = xieWuSocket;
                xieWuSocket = null;
                if (s != null)
                {
                    try { s.Shutdown(SocketShutdown.Both); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                    try { s.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                    try { s.Dispose(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                }
            }
            xieWuClosing = false;
        }

        private bool EnsureXieWuSocket()
        {
            if (xieWuClosing) return false;
            if (xieWuSocket != null && xieWuSocket.Connected) return true;
            if (xieWuSocket != null)
            {
                try { xieWuSocket.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                xieWuSocket = null;
            }
            if (string.IsNullOrEmpty(xieWuCachedIp) || xieWuCachedPort <= 0) return false;
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            s.SendTimeout = 500;
            s.ReceiveTimeout = 1;
            s.LingerState = new LingerOption(true, 0);
            try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
            IAsyncResult ar = s.BeginConnect(xieWuCachedIp, xieWuCachedPort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(800, true))
            {
                try { s.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                return false;
            }
            s.EndConnect(ar);
            xieWuSocket = s;
            return true;
        }

        private void DrainXieWuSocket()
        {
            try
            {
                if (xieWuSocket == null) return;
                while (xieWuSocket.Available > 0)
                {
                    int n = Math.Min(256, xieWuSocket.Available);
                    byte[] buf = new byte[n];
                    if (xieWuSocket.Receive(buf, 0, n, SocketFlags.None) <= 0) break;
                }
            }
            catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
        }

        private bool SendXieWuFrame(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return false;
            lock (modbusIoLock)
            {
            lock (xieWuLock)
            {
                if (xieWuClosing) return false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (!EnsureXieWuSocket()) continue;
                        DrainXieWuSocket();
                        int sent = 0;
                        while (sent < frame.Length)
                        {
                            int n = xieWuSocket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                            if (n <= 0) throw new SocketException();
                            sent += n;
                        }
                        DrainXieWuSocket();
                        return true;
                    }
                    catch
                    {
                        Socket s = xieWuSocket;
                        xieWuSocket = null;
                        if (s != null)
                        {
                            try { s.Close(); } catch (Exception ex) { new ErrorLog().WriteLog(ex.ToString()); }
                        }
                    }
                }
                return false;
            }
            }
        }

        // ch:P1-⑧ 极速写：① 写成功后才清 pending(pat[5])，写失败/客户端空/格式不支持均保留 pending 以便下次重试，避免结果永久丢失；
        //   ② 数值/地址解析改用 TryParse，解析失败记录日志且不发起写；③ 未知格式(string 等)不再静默丢弃，记日志后保留 pending。
        private void XieWuWriteOne(int camIndex, string value)
        {
            if (!camera_dic.ContainsKey(camIndex)) return;
            string[] pat = camera_dic[camIndex];
            string fbName = pat[5];
            if (string.IsNullOrEmpty(fbName) || fbName == "无") return;
            string[] par = null;
            foreach (var kv in fins_dic)
            {
                if (kv.Value[0] == fbName)
                {
                    par = kv.Value;
                    break;
                }
            }
            if (par == null) { pat[5] = "无"; return; } // ch:P1-⑧ 无匹配寄存器，永久无法写，清理避免重复重试
            string addrText = par[1];
            ushort addr_start;
            if (!ushort.TryParse(addrText, out addr_start)) { MsgErroeLog.WriteLog("modbustcp 极速写地址解析失败:" + addrText); pat[5] = "无"; return; }
            string fmt = par[4];
            int regLen;
            if (!int.TryParse(par[2], out regLen)) { MsgErroeLog.WriteLog("modbustcp 极速写寄存器长度解析失败:" + par[2]); pat[5] = "无"; return; }
            if (string.IsNullOrEmpty(value) || value == "无") return;
            string[] parts = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            OperateResult wr = null;
            string consumedCh = pat[5]; // ch:P2 记录本次消费的 [5](通道)，清除前比对
            string consumedVal = pat[4]; // ch:P2 记录本次消费的 [4](值)，清除前比对
            lock (modbusIoLock)
            {
                if (busTcpClient == null)
                {
                    // ch:P1-⑧ 不清除 pending，下次触发重试；ch:P2 但必须计入失败：
                    //   否则客户端长期为空时 pending 永不超限、每帧刷日志
                    MsgErroeLog.WriteLog("modbustcp 极速写失败: 客户端为空 cam=" + camIndex);
                    if (NotePendingWriteFailed(camIndex)) pat[5] = "无";
                    return;
                }
                if (fmt == "int")
                {
                    int writeCount = Math.Min(parts.Length, regLen);
                    short[] vals = new short[writeCount];
                    for (int i = 0; i < writeCount; i++)
                    {
                        double d;
                        if (!double.TryParse(parts[i].Trim(), out d)) { wr = new OperateResult { Message = "数值解析失败:" + parts[i] }; break; }
                        vals[i] = (short)Math.Round(d);
                    }
                    if (wr == null)
                    {
                        FankuiDiagLog(camIndex, "极速写解析", "addr=" + addrText + " vals=" + string.Join("/", Array.ConvertAll(vals, v => v.ToString()))); // ch:R23 环② 真正写入 FC16 的寄存器值(仅实际发起写时)
                        wr = busTcpClient.Write(addrText, vals);
                    }
                }
                else if (fmt == "long")
                {
                    int valLen = Math.Max(1, regLen / 2);
                    int writeCount = Math.Min(parts.Length, valLen);
                    int[] vals = new int[writeCount];
                    for (int i = 0; i < writeCount; i++)
                    {
                        double d;
                        if (!double.TryParse(parts[i].Trim(), out d)) { wr = new OperateResult { Message = "数值解析失败:" + parts[i] }; break; }
                        vals[i] = (int)Math.Round(d);
                    }
                    if (wr == null)
                    {
                        FankuiDiagLog(camIndex, "极速写解析", "addr=" + addrText + " vals=" + JoinHex32(vals)); // ch:R24 环② 极速写 long 分支补记(带位HEX)
                        wr = busTcpClient.Write(addrText, vals);
                    }
                }
                else if (fmt == "float")
                {
                    int valLen = Math.Max(1, regLen / 2);
                    int writeCount = Math.Min(parts.Length, valLen);
                    float[] vals = new float[writeCount];
                    for (int i = 0; i < writeCount; i++)
                    {
                        float f;
                        if (!float.TryParse(parts[i].Trim(), out f)) { wr = new OperateResult { Message = "数值解析失败:" + parts[i] }; break; }
                        vals[i] = f;
                    }
                    if (wr == null)
                    {
                        FankuiDiagLog(camIndex, "极速写解析", "addr=" + addrText + " vals=" + JoinHexF(vals)); // ch:R24 环② 极速写 float 分支补记(位模式可含0x80)
                        wr = busTcpClient.Write(addrText, vals);
                    }
                }
                else
                {
                    // ch:P2-③ string/未知格式无法写入：仅首次提示一次避免每帧刷屏；pending 保留以便后续支持或人工排查
                    if (!_xieWuStringWarned.Contains(camIndex))
                    {
                        _xieWuStringWarned.Add(camIndex);
                        MsgErroeLog.WriteLog("modbustcp 极速写不支持的格式:" + fmt + " cam=" + camIndex + " (仅提示一次，pending 保留)");
                    }
                    if (NotePendingWriteFailed(camIndex)) pat[5] = "无"; // ch:P2 该帧永远写不出去，超限后丢弃，避免永久重试
                    return;
                }
                // ch:P2 清除 pending 必须在锁内、且与"本次消费的 [4]/[5]"比对：
                //   否则与并发 WriteCameraResult/SetSwitchPending 新登记的 pending 竞争，会把新帧结果清掉 → 新结果永久丢失
                if (wr != null && wr.IsSuccess)
                {
                    if (pat[5] == consumedCh && pat[4] == consumedVal)
                    {
                        pat[5] = "无"; // ch:P1-⑧ 仅在写成功后才清除 pending
                        NotePendingWriteOk(camIndex); // ch:P2 写成功清除失败计数
                    }
                    else
                        MsgErroeLog.WriteLog("modbustcp 极速写完成但 pending 已被新帧覆盖，保留新结果 cam=" + camIndex);
                }
                else if (wr != null)
                {
                    MsgErroeLog.WriteLog("modbustcp 极速写失败 cam=" + camIndex + " addr=" + addr_start + " fmt=" + fmt + " err=" + wr.Message + " value=[" + value + "]");
                    if (NotePendingWriteFailed(camIndex)) pat[5] = "无"; // ch:P2 超限/超时丢弃，防断线恢复后补发旧结果
                }
            }
        }

        public void xie_wu(string value)
        {
            try
            {
                if (clearing || !chushihua || !fins_en) return;
                foreach (var pat in camera_dic)
                {
                    if (pat.Value[5] != "无")
                    {
                        try // ch:P2 每相机独立 try：单个相机异常不再中断其余相机的极速写
                        {
                        string writeVal = string.IsNullOrEmpty(pat.Value[4]) || pat.Value[4] == "无" ? value : pat.Value[4];
                        XieWuWriteOne(pat.Key, writeVal);
                        }
                        catch (Exception exPer) { MsgErroeLog.WriteLog("极速写单相机异常 cam=" + pat.Key + ":" + exPer.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message + "xie_wu");
            }
        }

        #endregion

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

        // ch:P2 相机9 周期写回：① 与 xie/XieWuWriteOne 消费共用 modbusIoLock，保证 [4](值)/[5](通道) 原子配对；
        //   ② 通道取"反馈通道"[3]（原实现误取触发通道[0] → 每秒把返回值写进触发寄存器，且破坏配对可写错地址段）
        public void SetCamera9PeriodicPending()
        {
            lock (modbusIoLock)
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

        private void comboBox20_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][0] = comboBox20.Text;
                wdini.WriteString("c8_modbustcp", "chufa", comboBox20.Text);
            }
        }

        private void comboBox5_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][0] = comboBox5.Text;
                wdini.WriteString("c1_modbustcp", "chufa", comboBox5.Text);
            }
        }

        private void comboBox8_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][0] = comboBox8.Text;
                wdini.WriteString("c2_modbustcp", "chufa", comboBox8.Text);
            }
        }

        private void comboBox10_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][0] = comboBox10.Text;
                wdini.WriteString("c3_modbustcp", "chufa", comboBox10.Text);
            }
        }

        private void comboBox12_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][0] = comboBox12.Text;
                wdini.WriteString("c4_modbustcp", "chufa", comboBox12.Text);
            }
        }

        private void comboBox14_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][0] = comboBox14.Text;
                wdini.WriteString("c5_modbustcp", "chufa", comboBox14.Text);
            }
        }

        private void comboBox16_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][0] = comboBox16.Text;
                wdini.WriteString("c6_modbustcp", "chufa", comboBox16.Text);
            }
        }

        private void comboBox18_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][0] = comboBox18.Text;
                wdini.WriteString("c7_modbustcp", "chufa", comboBox18.Text);
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

        // ch:R21 反馈通道绑定唯一性：多相机绑同一反馈通道 = 同地址互相覆盖（多寄存器数据互串根因之一）
        private bool _fankuiSyncing; // ch:R21 回退赋值时防重入

        private static bool IsUnboundFankui(string v)
        {
            return string.IsNullOrEmpty(v) || v == "0" || v == "无"; // ch:R21 未绑定态（ini 默认 "0"）不参与查重
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
                        MsgErroeLog.WriteLog("反馈通道重复绑定被拒: 通道[" + newText + "] 已被相机" + kv.Key + "占用，相机" + cam + " 回退为[" + oldText + "]"); // ch:R21 只记日志不写入
                        _fankuiSyncing = true;
                        try
                        {
                            // ch:R21 DropDownList 下 Text 可能不在 Items（如未绑定 "0"），用 SelectedIndex 回退（-1=清空）
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

        private void comboBox6_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(1, comboBox6, "c1_modbustcp");
        }

        private void comboBox7_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(2, comboBox7, "c2_modbustcp");
        }

        private void comboBox9_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(3, comboBox9, "c3_modbustcp");
        }

        private void comboBox11_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(4, comboBox11, "c4_modbustcp");
        }

        private void comboBox13_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(5, comboBox13, "c5_modbustcp");
        }

        private void comboBox15_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(6, comboBox15, "c6_modbustcp");
        }

        private void comboBox17_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(7, comboBox17, "c7_modbustcp");
        }

        private void comboBox19_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua) BindFankui(8, comboBox19, "c8_modbustcp");
        }

        private void textBox42_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[1][1] = textBox42.Text;
                wdini.WriteString("c1_modbustcp", "fanhuizhi", textBox42.Text);
            }
        }

        private void textBox17_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[2][1] = textBox17.Text;
                wdini.WriteString("c2_modbustcp", "fanhuizhi", textBox17.Text);
            }
        }

        private void textBox19_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[3][1] = textBox19.Text;
                wdini.WriteString("c3_modbustcp", "fanhuizhi", textBox19.Text);
            }
        }

        private void textBox18_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[4][1] = textBox18.Text;
                wdini.WriteString("c4_modbustcp", "fanhuizhi", textBox18.Text);
            }
        }

        private void textBox23_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[5][1] = textBox23.Text;
                wdini.WriteString("c5_modbustcp", "fanhuizhi", textBox23.Text);
            }
        }

        private void textBox22_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[6][1] = textBox22.Text;
                wdini.WriteString("c6_modbustcp", "fanhuizhi", textBox22.Text);
            }
        }

        private void textBox21_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[7][1] = textBox21.Text;
                wdini.WriteString("c7_modbustcp", "fanhuizhi", textBox21.Text);
            }
        }

        private void textBox20_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[8][1] = textBox20.Text;
                wdini.WriteString("c8_modbustcp", "fanhuizhi", textBox20.Text);
            }
        }

        private void checkBox14_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox14.CheckState == CheckState.Checked)
                {
                    camera_dic[1][2] = "true";
                    wdini.WriteString("c1_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[1][2] = "false";
                    wdini.WriteString("c1_modbustcp", "fanhuien", "false");
                }
            }
        }

        private void checkBox13_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox13.CheckState == CheckState.Checked)
                {
                    camera_dic[2][2] = "true";
                    wdini.WriteString("c2_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[2][2] = "false";
                    wdini.WriteString("c2_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c3_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[3][2] = "false";
                    wdini.WriteString("c3_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c4_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[4][2] = "false";
                    wdini.WriteString("c4_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c5_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[5][2] = "false";
                    wdini.WriteString("c5_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c6_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[6][2] = "false";
                    wdini.WriteString("c6_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c7_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[7][2] = "false";
                    wdini.WriteString("c7_modbustcp", "fanhuien", "false");
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
                    wdini.WriteString("c8_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[8][2] = "false";
                    wdini.WriteString("c8_modbustcp", "fanhuien", "false");
                }
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

        private void comboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][0] = comboBox4.Text;
                wdini.WriteString("c9_modbustcp", "chufa", comboBox4.Text);
            }
        }

        private void textBox24_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[9][1] = textBox24.Text;
                wdini.WriteString("c9_modbustcp", "fanhuizhi", textBox24.Text);
            }
        }

        private void checkBox11_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox11.CheckState == CheckState.Checked)
                {
                    camera_dic[9][2] = "true";
                    wdini.WriteString("c9_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[9][2] = "false";
                    wdini.WriteString("c9_modbustcp", "fanhuien", "false");
                }
            }
        }

        private void button8_Click(object sender, EventArgs e)
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
            textBox42.Text = "";
            textBox17.Text = "";
            textBox19.Text = "";
            textBox18.Text = "";
            textBox23.Text = "";
            textBox22.Text = "";
            textBox21.Text = "";
            textBox20.Text = "";
            textBox24.Text = "";
            textBox41.Text = "";
            checkBox14.CheckState = CheckState.Unchecked;
            checkBox13.CheckState = CheckState.Unchecked;
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
                wdini.WriteString("c10_modbustcp", "chufa", comboBox21.Text);
            }
        }

        private void textBox41_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                camera_dic[10][1] = textBox41.Text;
                wdini.WriteString("c10_modbustcp", "fanhuizhi", textBox41.Text);
            }
        }

        private void textBox25_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox25.Text = openFileDialog.FileName;
            }
        }

        private void textBox26_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox26.Text = openFileDialog.FileName;
            }
        }

        private void textBox28_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox28.Text = openFileDialog.FileName;
            }
        }

        private void textBox27_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox27.Text = openFileDialog.FileName;
            }
        }

        private void textBox32_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox32.Text = openFileDialog.FileName;
            }
        }

        private void textBox31_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox31.Text = openFileDialog.FileName;
            }
        }

        private void textBox30_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox30.Text = openFileDialog.FileName;
            }
        }

        private void textBox29_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "VP vpp File|*.vpp*";
            DialogResult openFileRes = openFileDialog.ShowDialog();
            if (DialogResult.OK == openFileRes)
            {
                textBox29.Text = openFileDialog.FileName;
            }
        }

        private void checkBox12_CheckedChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                if (checkBox12.CheckState == CheckState.Checked)
                {
                    camera_dic[10][2] = "true";
                    wdini.WriteString("c10_modbustcp", "fanhuien", "true");
                }
                else
                {
                    camera_dic[10][2] = "false";
                    wdini.WriteString("c10_modbustcp", "fanhuien", "false");
                }
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            wdini.WriteString("change_modbustcp", "1", textBox40.Text);
            wdini.WriteString("change_modbustcp", "2", textBox39.Text);
            wdini.WriteString("change_modbustcp", "3", textBox38.Text);
            wdini.WriteString("change_modbustcp", "4", textBox37.Text);
            wdini.WriteString("change_modbustcp", "5", textBox36.Text);
            wdini.WriteString("change_modbustcp", "6", textBox35.Text);
            wdini.WriteString("change_modbustcp", "7", textBox34.Text);
            wdini.WriteString("change_modbustcp", "8", textBox33.Text);

            wdini.WriteString("path_modbustcp", "1", textBox25.Text);
            wdini.WriteString("path_modbustcp", "2", textBox26.Text);
            wdini.WriteString("path_modbustcp", "3", textBox28.Text);
            wdini.WriteString("path_modbustcp", "4", textBox27.Text);
            wdini.WriteString("path_modbustcp", "5", textBox32.Text);
            wdini.WriteString("path_modbustcp", "6", textBox31.Text);
            wdini.WriteString("path_modbustcp", "7", textBox30.Text);
            wdini.WriteString("path_modbustcp", "8", textBox29.Text);
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbustcp", "ip", textBox1.Text);
            }
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbustcp", "port", textBox2.Text);
            }
        }

        private void textBox15_TextChanged(object sender, EventArgs e)
        {
            if (chushihua)
            {
                wdini.WriteString("modbustcp", "cell", textBox15.Text);
            }
        }

        private void comboBox1_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            xieWuDataFmt = comboBox1.SelectedIndex;
            _tcpDataFormatIndex = comboBox1.SelectedIndex; // ch:P2-⑤ UI 线程缓存，供后台重连线程使用
            if (chushihua)
            {
                wdini.WriteString("modbustcp", "abcd", comboBox1.Text);
            }
        }
    }
}
