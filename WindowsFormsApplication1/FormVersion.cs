using System;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:P2 版本信息窗口：显示当前软件版本、发布日期与版本时间线。
    /// 版本号 = 推送仓库时打标签的版本（语义化 主.次.修订 + 发布日期），非随机字符串。
    /// </summary>
    public partial class FormVersion : Form
    {
        // 当前版本（与推送仓库的 git tag 一一对应）：语义化版本 + 发布日期
        public const string AppVersion = "1.3.6";
        public const string AppVersionDate = "2026-09-23";
        public const string AppRepoName = "正泰 VP八相机连续海康（M4 - 加密）";

        // 版本时间线：与仓库 git tag 一一对应（新版本在最前）
        private static readonly string[] VersionTimeline = new string[]
        {
            "v1.3.6  2026-09-23  第10轮修复(独立复核清单)：SubSet JobCount 5/6/7 档位 groupBox 启用修正(原 5 号窗恒禁用且高档位提前打开)；CSV 统计回退防月/日双计(monthCounted)+回退目录改本相机 path/biaotou(原硬编码串目录)，WriteDate1 读取纳入 try+OpenOrCreate+删逐字节扫描死代码(原 try 外抛异常正是双计触发源)；三通讯窗体轮询 Sleep 改 (int) 强转并防 lunxun_time<=0 空转，ini 轮询/地址读取改 TryParse+回退默认+夹取控件范围(原区域小数点/手改小数会 FormatException 杀死轮询线程)；Form3 三处 socketClient.Send 判空+发送同入 _clientSocketLock(与重连置空串行化)；串口接收空 catch、TCP 合格/不合格发送及连接失败静默 catch 补限流日志(5s 一条)；Form6/Form8 结果标签改循环前清空+逐条追加(原只显示最后一条)、Value 用 ToString 防强转、Form6 补 try；identifier 失败弹窗改记日志；ShowPictureList 删用前 dlg.Dispose(原必抛 ObjectDisposedException)，唯一 Dispose 留设置关闭处；getCode 覆盖二维码前释放旧位图防 GDI 泄漏。轮询线程退出标志与跨线程检查恢复(#5/#9)留单独一轮。",
            "v1.3.5  2026-09-21  第9轮审查修复(加载/相机/ToolBlock 并发)+en 使能收紧：P0—trriger_set 尾段回 UI 线程并加 TryLockAllCameras 超时跳过；bnClose 释放存图缓冲持 bufLock、尺寸仅随真正释放清零；ToolBlock 锁外访问族入 blockLock(gongjukuai/triggerZifu/comboBox38/DataChange 每相机独立 try+入锁，三处 block 替换簇由切换门闩让路+sync_job_meta 入锁)；启动菜单构造收进 UI Invoke(等 IsHandleCreated)。P1—ini→NumericUpDown 赋值前夹取防吞启动；Load 期 Frm2 按门闩定 start 防常驻遮挡；ClassIni FileName 前置赋值(原失败残留 null→静默读写 win.ini)；重连恢复触发模式绕过登录门(sender==null)；重连成功后补探测/下发 GigE 最佳包大小；存 NG 图 case2 删除错位的 myjob1.ng1 门。P2—终结器线程去弹窗；回调注册失败记日志(9处)；CSV 改 Task 局部快照并删 Myjob.tianbiao；display() 相机1 屏蔽补隐藏面板；参数自动下发按 en 收紧(baoguang_set/两处 bnSetParam)。",
            "v1.3.4  2026-09-19  Form3 无协议接收加固：TCP 客户端与服务端连接开启 KeepAlive 并压到 30s 探活(SIO_KEEPALIVE_VALS)，PLC 掉电/网线拔出约 30s 内触发重连（原为系统默认 2 小时）；未匹配触发串时增加粘包/拆包限流诊断日志(含整段内容与长度，正常报文不记)。说明：现场协议是 8 条精确匹配触发串、无分隔符与长度前缀，故不改变解析行为，避免猜协议导致系统性失效。",
            "v1.3.3  2026-09-19  写回 pending TTL 加固：失败计数改按“结果身份”([5]+[4])计数，新帧登记即自动重置(不再继承旧失败计数/旧起始时刻被误丢)；客户端为空与写路径异常也计入失败(原 NRE 被 per-camera catch 吞掉→无限重试+刷日志；TCP 普通写回漏判空一并补齐)；TTL 时间支路改 int 差值；Modbus.cs 读响应等待改差值式超时(原绝对比较跨 TickCount 回绕会死等)。",
            "v1.3.2  2026-09-19  方案切换门闩原子化：xinghao_qiehuan 改 Interlocked.CompareExchange 根除双切换 TOCTOU；Form1 闩读改 Volatile.Read(含回调热路径/自旋等待/断线检测门控)、写改 Volatile.Write；三窗体回执闩改 volatile；cam10 内层分支补 else 复位(原内层不成立时无复位路径)。",
            "v1.3.1  2026-09-19  第二轮审查修复：极速写清 pending 移入锁内并与本值比对、cam9 周期写回加锁且改用反馈通道、方案切换门闩异常也复位、SubSet 工具字典对象修正(7处)、Global\\ 互斥量降级 Local\\、jiasu 兜底复位、blockLock 补漏(Inputs/CreateLastRunRecord)、手参写入入锁(8处)、cbImage 强引用防 GC、写回每相机独立 try、list_block 改并发字典、pending TTL(3次/10秒)、离线单图检测移后台、Form3 显式 GBK 解码。",
            "v1.3.0  2026-09-19  并发与数据一致性修复：blockLock 覆盖 Outputs 读与结果快照、CSV 串帧改锁内快照、普通/极速写回仅成功才清 pending（按 bool 逐次聚合）、RunStatus 空值保护、曲线线程代际校验、RTU 重连加锁与字节序缓存、触发字边沿记忆、Form3 目标字典线程安全、极速写 string 加载期校验；新增「通讯 → 版本」窗口。",
            "v1.2.0  2026-09-14  新增 GitHub Actions CI（单元测试自动构建）。",
            "v1.1.0  2026-09-14  R4 补齐 Omron pending 单事务提交 + 新增单元测试 19 项 + 行尾归一。",
            "v1.0.0  2026-09-13  仓库首个提交：8 相机 Cognex VisionPro 视觉检测（含 P1/P2 审查修复）。09-03~09-05 的通信多实例化与「连接设备」管理器成果已并入此提交（仓库历史压缩，无独立提交）。",
        };

        private static FormVersion _instance;

        /// <summary>
        /// 单例显示：已打开则前置激活，不重复创建（与项目内其它工具窗一致）。
        /// </summary>
        public static void ShowVersion(IWin32Window owner)
        {
            if (_instance == null || _instance.IsDisposed)
                _instance = new FormVersion();
            if (!_instance.Visible)
                _instance.Show(owner);
            _instance.BringToFront();
            _instance.Activate();
        }

        public FormVersion()
        {
            InitializeComponent();

            lblVersion.Text = "当前版本：v" + AppVersion;
            lblReleaseDate.Text = "发布日期：" + AppVersionDate;
            lblRepo.Text = "所属仓库：" + AppRepoName;
            lblTag.Text = "仓库标签：v" + AppVersion + "（git tag，随推送仓库发布）";

            try
            {
                string exePath = Application.ExecutablePath;
                lblBuild.Text = "程序生成时间：" + System.IO.File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                lblBuild.Text = "程序生成时间：-";
            }

            lstTimeline.Items.Clear();
            foreach (string line in VersionTimeline)
                lstTimeline.Items.Add(line);
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
