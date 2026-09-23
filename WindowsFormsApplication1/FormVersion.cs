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
        public const string AppVersion = "1.3.10";
        public const string AppVersionDate = "2026-09-23";
        public const string AppRepoName = "正泰 VP八相机连续海康（M4 - 加密）";

        // 版本时间线：与仓库 git tag 一一对应（新版本在最前）
        private static readonly string[] VersionTimeline = new string[]
        {
            "v1.3.10  2026-09-23  第15轮(显示管线优化·4相机CPU90+%专项)：①上屏总节拍封顶≈15fps—KickOcxPaint 入口按 _lastOcxKickMs+OcxMinIntervalMs(66ms) 收口，直接投递/续排/定时器三条路径统一限速(旧实现只有定时器路径 8~16ms 有节流、直连路径可绕过)，ScheduleOcxPaint 间隔同步 8~16ms→66ms(用户操作中或单次上屏>30ms 退到 132ms)；4 相机下全分辨率 ToBitmap 约 60~100 次/秒的「分配+整帧转换+Dispose」降到 ≤15 次/秒，采集/检测/输出/feng=0 全部不动(与 VisionMaster 同款「只显示最新帧」语义)；②原图入框前经 DownscaleToBox 等比缩放到 PictureBox 实际尺寸(与渲染模式 Fit 对齐、背景同 60,60,60)：常驻位图十几 MB→几百 KB、后续 WM_PAINT 全走小图，原先 1:1 超出框被裁掉的边缘改为整图可见；③窗体最小化时提前 return 跳过上屏转换(finally 照常复位 jiasu/busy)。",
            "v1.3.9  2026-09-23  第14轮(线程退出+工程卫生)：①Form3 轮询/重连线程 lunxun_monitor、clientmonitor 原 while(true) 无退出标志——进程退出阶段(Form1 已 closing、正在关 socket/冲刷日志)它们仍在发请求、按 checkBox3 自动重连，新增 volatile _exiting + RequestExit()，由 Form1_FormClosing 置位(注意只在真正退出时调用：窗体被隐藏但不销毁的路径 e.Cancel=true 不置位，否则下次打开配置窗轮询已停、功能失效)；②ErrorLog 增日志轮转：Log\\*.txt 按 LastWriteTime 保留 30 天，24h 节流、跑在落盘线程不阻塞调用方、单文件失败只跳过，解决按天分文件只增不减会占满磁盘；③Modbus CRC 算法本体抽到 Core\\ModbusCrc.cs，Tests/UnitTests.csproj 链接同一份源文件让 CRC 回归直连生产代码(原为「逐行复刻 CrcProduction」，复刻只在写下的那一刻与生产一致、之后生产改动它不跟着变)，并更正 GetCRC16 与实现相反的 returns 注释(实际帧尾为 [低,高])；④RunLog 删只写不读的死字段 processCount/iTemp/iTemp1/sumend/sumline/iflag1 与三行无效清零(R10-4 已删其消费端)；⑤.gitignore 目录规则改 audit-*/ 通配(原逐个列举 audit-build/recheck/third，后增的 audit-fix 等目录会漏)。",
            "v1.3.8  2026-09-23  第12+13轮(R12补遗+合理化优化/输出方式总闸)：①输出方式全局互斥—配置窗新增原生 ComboBox cbOutputMode(ini camera/output_mode，-1=自动按原配置=零回退)，ApplyOutputMode 统一改写 myjob1..8 的 IO/串口/TCP/ModbusTCP/配置窗/RTU 开关，getrecord 的 6 个输出分支与 3 个配置窗分支按所选放行、快照只读选中项，运行期选一种即其它全不发(顺带补 myjobN.tcp 无 ini 持久化的缺口)；②同步尾部埋点—_perfTailStart/Ticks/Count 在 getrecord 的 finally 取起点、DetectWorkerLoop 取差值，PerfReport 输出「尾部 X ms」，为判断 CreateLastRunRecord 与 12 处 Task.Run 能否异步化提供依据；③RasterizeRecordToBitmap 在 src 为 Bitmap 时直接交出所有权，去掉每帧一次的整幅位图拷贝；④缺陷统计表 8 段重复逻辑收敛为 UpdateDefectTable 增量更新(存在则原地改计数、否则末尾追加，去掉每帧两次 dataGridView.Visible 开关与 Rows.Clear 全量重建，并剔除初始化残留的空行)；⑤setbox6..13 八处 UI 同步 Invoke 改 BeginInvoke、SetPictureBoxImage 的 Refresh 改 Invalidate；⑥ErrorLog 改入队+后台线程批量落盘(队列封顶2万条、UTF-8无BOM、500ms 批量)，FormClosing/ProcessExit 显式 FlushPending 冲刷，写日志不再持全局锁阻塞相机回调；⑦RunLog 月/日 CSV 增「路径+长度+mtime」读缓存(命中用副本、写后按真实 stat 刷新，不做延迟落盘以保数据完整)，每条记录省两次 O(文件大小) 全量读；⑧serial 的 if 判定移出 Task.Run 外层，与其余 5 路写法对齐。R12补遗：Form3 服务端接入 socket 补 SendTimeout=3000；三通讯窗体 ini 解析失败补日志、小数值统一取整、通道空名/重名兜底(原任一问题都因 Load 的 catch 只记一行日志而静默丢配置)。",
            "v1.3.7  2026-09-23  第11轮修复(第10轮回归+补课)：①跨天/冷启动日文件缺表头回归修复—WriteDate1 新建文件首条数据前补写表头(OpenOrCreate 后原 FileNotFoundException→CreateCsvPath 表头路径不再触发)，WriteDate 跨月同补(并修 v1.3.6 提交说明中'地址读取'未落实部分)；②三通讯窗体 ini 读取族统一加固—bool.Parse(使能)/decimal.Parse(qishi/zongchang)/int.Parse(geshu)/每通道 qishi·changdu 改 TryParse+回退默认+夹取 NUD/循环上界[0,64]·[0,1000](原任一 ini 值手改坏即抛异常，Load 的 catch 只记一行日志并跳过其后 IP/串口/通道配置与自动连接)；int.Parse(小数.ToString()) 改强转+取格越界守卫；③Form3 客户端 socket 补 SendTimeout=3000(发送持 _clientSocketLock，对端不读时持锁时长有界，不再拖住重连/接收收尾)；CloseResources 的 socketClient Close/置空纳入 _clientSocketLock(与 Send/重连全链路串行化，退出不再写无意义 ObjectDisposed 日志)。",
            "v1.3.6  2026-09-23  第10轮修复(独立复核清单)：SubSet JobCount 5/6/7 档位 groupBox 启用修正(原 5 号窗恒禁用且高档位提前打开)；CSV 统计回退防月/日双计(monthCounted)+回退目录改本相机 path/biaotou(原硬编码串目录)，WriteDate1 读取纳入 try+OpenOrCreate+删逐字节扫描死代码(原 try 外抛异常正是双计触发源)；三通讯窗体轮询 Sleep 改 (int) 强转并防 lunxun_time<=0 空转，ini 轮询读取改 TryParse+回退默认+夹取控件范围(原区域小数点/手改小数会 FormatException 杀死轮询线程；地址/通道数读取族遗漏，v1.3.7 补齐)；Form3 三处 socketClient.Send 判空+发送同入 _clientSocketLock(与重连置空串行化)；串口接收空 catch、TCP 合格/不合格发送及连接失败静默 catch 补限流日志(5s 一条)；Form6/Form8 结果标签改循环前清空+逐条追加(原只显示最后一条)、Value 用 ToString 防强转、Form6 补 try；identifier 失败弹窗改记日志；ShowPictureList 删用前 dlg.Dispose(原必抛 ObjectDisposedException)，唯一 Dispose 留设置关闭处；getCode 覆盖二维码前释放旧位图防 GDI 泄漏。轮询线程退出标志与跨线程检查恢复(#5/#9)留单独一轮。",
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
