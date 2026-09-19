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
        public const string AppVersion = "1.3.2";
        public const string AppVersionDate = "2026-09-19";
        public const string AppRepoName = "正泰 VP八相机连续海康（M4 - 加密）";

        // 版本时间线：与仓库 git tag 一一对应（新版本在最前）
        private static readonly string[] VersionTimeline = new string[]
        {
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
