using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:存图设置窗口（菜单 查找 → 存图设置）。
    ///   只做"定制存图"这一条链路的配置：根目录、相机目录、日期文件夹格式、JPG/BMP、质量，
    ///   以及每台相机的 存图目录 / 存图名称 / 每产品张数。
    ///   触发地址仍由 FormModbus 的 c{n}_modbustcp 映射配置，本窗口不重复配置。
    ///   全代码构建（无 Designer/resx），避免改动主界面资源。
    /// </summary>
    public sealed class FormSaveImageConfig : Form
    {
        private static readonly Color Accent = Color.FromArgb(0, 120, 212);
        private static readonly Color AccentHover = Color.FromArgb(0, 100, 180);
        private static readonly Color TextMain = Color.FromArgb(51, 51, 51);
        private static readonly Color TextSub = Color.FromArgb(120, 120, 120);
        private static readonly Color BorderGray = Color.FromArgb(200, 205, 212);
        private static readonly Color GridHeaderBack = Color.FromArgb(238, 243, 250);
        private static readonly Color GridAltBack = Color.FromArgb(248, 250, 253);

        private readonly CheckBox _cbEnable = new CheckBox();
        private readonly TextBox _tbRoot = new TextBox();
        private readonly TextBox _tbDate = new TextBox();
        private readonly ComboBox _cbFormat = new ComboBox();
        private readonly NumericUpDown _nudQuality = new NumericUpDown();
        private readonly NumericUpDown _nudKeepDays = new NumericUpDown();
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _txtPreview = new TextBox();
        private bool _loading;

        private SaveImageSettings _settings;

        public FormSaveImageConfig(SaveImageSettings current)
        {
            _settings = (current ?? new SaveImageSettings()).Clone();

            Text = "存图设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(246, 248, 251);
            ClientSize = new Size(920, 680);
            MinimumSize = new Size(860, 640);

            BuildUi();
            LoadToUi();
        }

        /// <summary>ch:窗口点"保存"后的新配置；取消时调用方不要读取。</summary>
        public SaveImageSettings Settings
        {
            get { return _settings; }
        }

        #region 界面构建

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14, 12, 14, 12);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 204));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            Controls.Add(root);

            root.Controls.Add(BuildBasicGroup(), 0, 0);
            root.Controls.Add(BuildCameraGroup(), 0, 1);
            root.Controls.Add(BuildFooter(), 0, 2);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        private GroupBox BuildBasicGroup()
        {
            GroupBox gb = new GroupBox();
            gb.Text = "基本设置";
            gb.Dock = DockStyle.Fill;
            gb.ForeColor = TextMain;
            gb.Padding = new Padding(12, 6, 12, 6);

            TableLayoutPanel tlp = new TableLayoutPanel();
            tlp.Dock = DockStyle.Fill;
            tlp.ColumnCount = 4;
            tlp.RowCount = 5;
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            gb.Controls.Add(tlp);

            _cbEnable.Text = "启用定制存图";
            _cbEnable.AutoSize = true;
            _cbEnable.Font = new Font(Font, FontStyle.Bold);
            _cbEnable.Margin = new Padding(0, 6, 0, 0);
            tlp.Controls.Add(_cbEnable, 0, 0);
            tlp.SetColumnSpan(_cbEnable, 4);

            tlp.Controls.Add(MakeLabel("根目录："), 0, 1);
            _tbRoot.Dock = DockStyle.Fill;
            _tbRoot.Margin = new Padding(0, 4, 8, 4);
            tlp.Controls.Add(_tbRoot, 1, 1);

            Button btnBrowse = MakeButton("浏览...", false);
            btnBrowse.Margin = new Padding(0, 3, 8, 3);
            btnBrowse.Dock = DockStyle.Fill;
            btnBrowse.Click += OnBrowse;
            tlp.Controls.Add(btnBrowse, 2, 1);

            Button btnOpenRoot = MakeButton("打开目录", false);
            btnOpenRoot.Margin = new Padding(0, 3, 0, 3);
            btnOpenRoot.Dock = DockStyle.Fill;
            btnOpenRoot.Click += OnOpenRoot;
            tlp.Controls.Add(btnOpenRoot, 3, 1);

            tlp.Controls.Add(MakeLabel("日期格式："), 0, 2);

            Panel pDate = new Panel();
            pDate.Dock = DockStyle.Fill;
            pDate.Margin = new Padding(0, 0, 0, 0);
            _tbDate.Location = new Point(0, 5);
            _tbDate.Width = 150;
            pDate.Controls.Add(_tbDate);
            Label lblDateHint = MakeHint("例：yyyy-MM-dd（也可 yyyyMMdd，每天自动一个文件夹）");
            lblDateHint.Location = new Point(160, 8);
            pDate.Controls.Add(lblDateHint);
            tlp.Controls.Add(pDate, 1, 2);
            tlp.SetColumnSpan(pDate, 3);

            tlp.Controls.Add(MakeLabel("图片格式："), 0, 3);

            Panel pFormat = new Panel();
            pFormat.Dock = DockStyle.Fill;
            pFormat.Margin = new Padding(0, 0, 0, 0);
            _cbFormat.Location = new Point(0, 5);
            _cbFormat.Width = 90;
            _cbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbFormat.Items.Add("BMP");
            _cbFormat.Items.Add("JPG");
            _cbFormat.SelectedIndexChanged += delegate
            {
                _nudQuality.Enabled = _cbFormat.SelectedIndex == 1;
                UpdatePreview();
            };
            pFormat.Controls.Add(_cbFormat);

            Label lblQuality = MakeHint("JPG 质量：");
            lblQuality.Location = new Point(104, 8);
            pFormat.Controls.Add(lblQuality);

            _nudQuality.Location = new Point(174, 5);
            _nudQuality.Width = 64;
            _nudQuality.Minimum = SaveImageSettings.MinJpegQuality;
            _nudQuality.Maximum = SaveImageSettings.MaxJpegQuality;
            _nudQuality.Increment = 5;
            pFormat.Controls.Add(_nudQuality);

            Label lblQualityHint = MakeHint("（50-100，仅 JPG 生效）");
            lblQualityHint.Location = new Point(246, 8);
            pFormat.Controls.Add(lblQualityHint);
            tlp.Controls.Add(pFormat, 1, 3);
            tlp.SetColumnSpan(pFormat, 3);

            // ch:保留天数：每次启动软件后台清理更早的“日期”文件夹（0=不删）
            tlp.Controls.Add(MakeLabel("保存天数："), 0, 4);

            Panel pKeep = new Panel();
            pKeep.Dock = DockStyle.Fill;
            pKeep.Margin = new Padding(0, 0, 0, 0);
            _nudKeepDays.Location = new Point(0, 5);
            _nudKeepDays.Width = 90;
            _nudKeepDays.Minimum = 0;
            _nudKeepDays.Maximum = SaveImageSettings.MaxRetentionDays;
            _nudKeepDays.Value = 30;
            pKeep.Controls.Add(_nudKeepDays);

            Label lblKeepUnit = MakeHint("天（0 = 不自动删除）");
            lblKeepUnit.Location = new Point(98, 8);
            pKeep.Controls.Add(lblKeepUnit);

            Label lblKeepHint = MakeHint("每次启动软件清理更早的“日期”文件夹及其中的图片，仅启用定制存图时生效");
            lblKeepHint.Location = new Point(238, 8);
            pKeep.Controls.Add(lblKeepHint);
            tlp.Controls.Add(pKeep, 1, 4);
            tlp.SetColumnSpan(pKeep, 3);

            return gb;
        }

        private GroupBox BuildCameraGroup()
        {
            GroupBox gb = new GroupBox();
            gb.Text = "相机配置";
            gb.Dock = DockStyle.Fill;
            gb.ForeColor = TextMain;
            gb.Padding = new Padding(12, 6, 12, 10);

            TableLayoutPanel tlp = new TableLayoutPanel();
            tlp.Dock = DockStyle.Fill;
            tlp.ColumnCount = 1;
            tlp.RowCount = 2;
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gb.Controls.Add(tlp);

            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.ColumnCount = 2;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.Controls.Add(bar, 0, 0);

            Label lblCamHint = MakeHint("勾选要存图的相机；存图目录填同一个名字 = 合并到同一文件夹");
            lblCamHint.Anchor = AnchorStyles.Left;
            lblCamHint.Margin = new Padding(0, 9, 0, 0);
            bar.Controls.Add(lblCamHint, 0, 0);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.AutoSize = true;
            tools.Dock = DockStyle.Fill;
            tools.FlowDirection = FlowDirection.LeftToRight;
            tools.WrapContents = false;
            tools.Margin = new Padding(0);

            Button btnEnableAll = MakeButton("全部启用", false);
            btnEnableAll.Width = 88;
            btnEnableAll.Height = 26;
            btnEnableAll.Margin = new Padding(0, 4, 6, 0);
            btnEnableAll.Click += delegate { SetAllEnabled(true); };
            tools.Controls.Add(btnEnableAll);

            Button btnDisableAll = MakeButton("全部停用", false);
            btnDisableAll.Width = 88;
            btnDisableAll.Height = 26;
            btnDisableAll.Margin = new Padding(0, 4, 6, 0);
            btnDisableAll.Click += delegate { SetAllEnabled(false); };
            tools.Controls.Add(btnDisableAll);

            Button btnApplyDir = MakeButton("目录应用到全部", false);
            btnApplyDir.Width = 120;
            btnApplyDir.Height = 26;
            btnApplyDir.Margin = new Padding(0, 4, 0, 0);
            btnApplyDir.Click += OnApplyDirToAll;
            tools.Controls.Add(btnApplyDir);

            bar.Controls.Add(tools, 1, 0);

            BuildGrid();
            tlp.Controls.Add(_grid, 0, 1);

            return gb;
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.Margin = new Padding(0, 4, 0, 0);
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.ColumnHeadersHeight = 40;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.EnableHeadersVisualStyles = false;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.FromArgb(228, 232, 238);
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.RowTemplate.Height = 28;
            _grid.ScrollBars = ScrollBars.Vertical;

            _grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeaderBack;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = TextMain;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeaderBack;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;

            _grid.DefaultCellStyle.BackColor = Color.White;
            _grid.DefaultCellStyle.ForeColor = TextMain;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 233, 255);
            _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
            _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = GridAltBack;

            DataGridViewCheckBoxColumn colEnabled = new DataGridViewCheckBoxColumn();
            colEnabled.HeaderText = "启用";
            colEnabled.FillWeight = 8;
            colEnabled.MinimumWidth = 58;
            colEnabled.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(colEnabled);

            DataGridViewTextBoxColumn colCam = new DataGridViewTextBoxColumn();
            colCam.HeaderText = "相机";
            colCam.FillWeight = 8;
            colCam.MinimumWidth = 72;
            colCam.ReadOnly = true;
            colCam.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colCam.DefaultCellStyle.ForeColor = TextSub;
            _grid.Columns.Add(colCam);

            DataGridViewTextBoxColumn colDir = new DataGridViewTextBoxColumn();
            colDir.HeaderText = "存图目录";
            colDir.ToolTipText = "根目录下的第一层目录，可留空；多台相机填同一个名字 = 合并到同一文件夹";
            colDir.FillWeight = 30;
            colDir.MinimumWidth = 150;
            _grid.Columns.Add(colDir);

            DataGridViewTextBoxColumn colName = new DataGridViewTextBoxColumn();
            colName.HeaderText = "存图名称（不含扩展名）";
            colName.ToolTipText = "支持 {相机} {序号} {日期} {时间} {文件夹}；留空自动用 相机N";
            colName.FillWeight = 42;
            colName.MinimumWidth = 200;
            _grid.Columns.Add(colName);

            DataGridViewTextBoxColumn colSlots = new DataGridViewTextBoxColumn();
            colSlots.HeaderText = "每产品张数";
            colSlots.ToolTipText = "同一相机在同一产品文件夹里的循环槽位数：内壁/外壁填 3，顶部/底部填 1";
            colSlots.FillWeight = 12;
            colSlots.MinimumWidth = 112;
            colSlots.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(colSlots);

            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += delegate { UpdatePreview(); };
            _grid.CurrentCellChanged += delegate { UpdatePreview(); };
        }

        private readonly Button _btnSave = new Button();
        private readonly Button _btnCancel = new Button();

        private TableLayoutPanel BuildFooter()
        {
            TableLayoutPanel tlp = new TableLayoutPanel();
            tlp.Dock = DockStyle.Fill;
            tlp.ColumnCount = 2;
            tlp.RowCount = 2;
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label lblPreview = new Label();
            lblPreview.Text = "预览：";
            lblPreview.Dock = DockStyle.Fill;
            lblPreview.TextAlign = ContentAlignment.MiddleLeft;
            lblPreview.ForeColor = TextSub;
            tlp.Controls.Add(lblPreview, 0, 0);

            _txtPreview.ReadOnly = true;
            _txtPreview.Dock = DockStyle.Fill;
            _txtPreview.Margin = new Padding(0, 8, 0, 8);
            _txtPreview.BackColor = Color.White;
            _txtPreview.BorderStyle = BorderStyle.FixedSingle;
            _txtPreview.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _txtPreview.ForeColor = Color.FromArgb(30, 60, 120);
            _txtPreview.TabStop = false;
            tlp.Controls.Add(_txtPreview, 1, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 6, 0, 0);
            tlp.Controls.Add(buttons, 0, 1);
            tlp.SetColumnSpan(buttons, 2);

            _btnSave.Text = "保存";
            _btnSave.Width = 104;
            _btnSave.Height = 32;
            _btnSave.FlatStyle = FlatStyle.Flat;
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.BackColor = Accent;
            _btnSave.ForeColor = Color.White;
            _btnSave.Font = new Font(Font, FontStyle.Bold);
            _btnSave.Cursor = Cursors.Hand;
            _btnSave.Margin = new Padding(8, 0, 0, 0);
            _btnSave.Click += OnSave;
            _btnSave.MouseEnter += delegate { _btnSave.BackColor = AccentHover; };
            _btnSave.MouseLeave += delegate { _btnSave.BackColor = Accent; };
            buttons.Controls.Add(_btnSave);

            _btnCancel.Text = "取消";
            _btnCancel.Width = 104;
            _btnCancel.Height = 32;
            _btnCancel.FlatStyle = FlatStyle.Flat;
            _btnCancel.FlatAppearance.BorderColor = BorderGray;
            _btnCancel.BackColor = Color.White;
            _btnCancel.ForeColor = TextMain;
            _btnCancel.Cursor = Cursors.Hand;
            _btnCancel.DialogResult = DialogResult.Cancel;
            _btnCancel.Margin = new Padding(0, 0, 0, 0);
            buttons.Controls.Add(_btnCancel);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_cbEnable, "关闭时完全不影响现有存图逻辑；开启后按本窗口的目录/名称存图");
            tip.SetToolTip(_tbRoot, "示例：E:\\fuNok");
            tip.SetToolTip(_tbDate, "日期文件夹格式，默认 yyyy-MM-dd");
            tip.SetToolTip(_nudKeepDays, "保留天数：每次启动软件删除更早的“日期”文件夹及其图片；0 = 不自动删除");
            tip.SetToolTip(_grid, "勾选启用；存图目录留空表示不增加这一层；每产品张数=同一相机的循环槽位数");
            tip.SetToolTip(_btnSave, "保存到 code.ini 并立即生效");

            return tlp;
        }

        private Label MakeLabel(string text)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.ForeColor = TextMain;
            lbl.Margin = new Padding(0);
            return lbl;
        }

        private Label MakeHint(string text)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.AutoSize = true;
            lbl.ForeColor = TextSub;
            lbl.Margin = new Padding(0);
            return lbl;
        }

        private Button MakeButton(string text, bool primary)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = BorderGray;
            btn.BackColor = primary ? Accent : Color.White;
            btn.ForeColor = primary ? Color.White : TextMain;
            btn.Cursor = Cursors.Hand;
            if (primary) btn.FlatAppearance.BorderSize = 0;
            btn.UseVisualStyleBackColor = false;
            return btn;
        }

        #endregion

        #region 数据绑定与操作

        private void LoadToUi()
        {
            _loading = true;
            try
            {
                _cbEnable.Checked = _settings.Enabled;
                _tbRoot.Text = _settings.Root;
                _tbDate.Text = _settings.DateFormat;
                _cbFormat.SelectedIndex = _settings.Jpeg ? 1 : 0;
                _nudQuality.Value = Math.Min(SaveImageSettings.MaxJpegQuality,
                    Math.Max(SaveImageSettings.MinJpegQuality, _settings.JpegQuality));
                _nudQuality.Enabled = _settings.Jpeg;
                _nudKeepDays.Value = Math.Min(SaveImageSettings.MaxRetentionDays,
                    Math.Max(0, _settings.RetentionDays));

                _grid.Rows.Clear();
                for (int i = 0; i < SaveImageSettings.CameraCount; i++)
                {
                    CameraSaveSetting c = _settings.Cameras[i] ?? new CameraSaveSetting();
                    string name = (c.Name ?? "").Trim();
                    if (name.Length == 0) name = "相机" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    _grid.Rows.Add(
                        c.Enabled,
                        "相机" + (i + 1).ToString(CultureInfo.InvariantCulture),
                        c.Folder ?? "",
                        name,
                        Math.Max(1, c.Slots).ToString(CultureInfo.InvariantCulture));
                }
                if (_grid.Rows.Count > 0)
                    _grid.CurrentCell = _grid.Rows[0].Cells[2];
            }
            finally
            {
                _loading = false;
            }
            UpdatePreview();
        }

        private void SetAllEnabled(bool enabled)
        {
            _grid.EndEdit();
            for (int i = 0; i < _grid.Rows.Count; i++)
                _grid.Rows[i].Cells[0].Value = enabled;
            UpdatePreview();
        }

        private void OnApplyDirToAll(object sender, EventArgs e)
        {
            if (_grid.CurrentCell == null) return;
            int src = _grid.CurrentCell.RowIndex;
            if (src < 0 || src >= _grid.Rows.Count) return;
            _grid.EndEdit();
            string dir = Convert.ToString(_grid.Rows[src].Cells[2].Value, CultureInfo.InvariantCulture) ?? "";
            if (MessageBox.Show(this,
                "把相机" + (src + 1).ToString(CultureInfo.InvariantCulture) + " 的存图目录“" + dir
                + "”应用到全部 " + _grid.Rows.Count.ToString(CultureInfo.InvariantCulture) + " 台相机？\r\n"
                + "（留空表示不增加这一层）",
                "存图设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            for (int i = 0; i < _grid.Rows.Count; i++)
                _grid.Rows[i].Cells[2].Value = dir;
            UpdatePreview();
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择存图根目录（其下自动按 相机目录\\日期\\触发字符串 建子文件夹）";
                string cur = (_tbRoot.Text ?? "").Trim();
                if (cur.Length > 0 && Directory.Exists(cur))
                    dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _tbRoot.Text = dlg.SelectedPath;
            }
        }

        private void OnOpenRoot(object sender, EventArgs e)
        {
            try
            {
                string root = (_tbRoot.Text ?? "").Trim();
                if (root.Length == 0 || !Directory.Exists(root))
                {
                    MessageBox.Show(this, "根目录不存在，请先选择或创建。", "存图设置",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：" + ex.Message, "存图设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private CameraSaveSetting ReadRow(int rowIndex)
        {
            CameraSaveSetting c = new CameraSaveSetting();
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return c;
            DataGridViewRow row = _grid.Rows[rowIndex];

            object v = row.Cells[0].Value;
            c.Enabled = v != null && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            c.Folder = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture) ?? "";
            c.Name = Convert.ToString(row.Cells[3].Value, CultureInfo.InvariantCulture) ?? "";

            int slots;
            c.Slots = int.TryParse(Convert.ToString(row.Cells[4].Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out slots)
                ? Math.Max(1, Math.Min(SaveImageSettings.MaxSlots, slots))
                : 1;
            return c;
        }

        private void UpdatePreview()
        {
            if (_loading) return;
            try
            {
                string root = (_tbRoot.Text ?? "").Trim();
                string dateFormat = (_tbDate.Text ?? "").Trim();
                DateTime now = DateTime.Now;
                int rowIndex = _grid.CurrentCell != null ? _grid.CurrentCell.RowIndex : 0;
                CameraSaveSetting cam = ReadRow(rowIndex);
                string folder = SaveImageSettingsStore.BuildFolderPath(root, dateFormat, "产品编号", now, cam.Folder);
                if (folder.Length == 0)
                {
                    _txtPreview.Text = "请先填写根目录";
                    return;
                }
                string name = SaveImageSettingsStore.RenderFileName(cam, rowIndex + 1, "产品编号", now, 1);
                string ext = _cbFormat.SelectedIndex == 1 ? ".jpg" : ".bmp";
                _txtPreview.Text = Path.Combine(folder, name + ext);
            }
            catch (Exception ex)
            {
                _txtPreview.Text = "预览失败：" + ex.Message;
            }
        }

        private void OnSave(object sender, EventArgs e)
        {
            try
            {
                _grid.EndEdit();

                string root = (_tbRoot.Text ?? "").Trim();
                if (root.Length == 0)
                {
                    Warn("请填写存图根目录。", _tbRoot);
                    return;
                }
                string rootFull;
                try
                {
                    if (!Path.IsPathRooted(root))
                    {
                        Warn("根目录要填完整路径，例如 E:\\fuNok。", _tbRoot);
                        return;
                    }
                    rootFull = Path.GetFullPath(root);
                }
                catch (Exception)
                {
                    Warn("根目录不是合法路径，例如 E:\\fuNok。", _tbRoot);
                    return;
                }
                if (rootFull.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                    || rootFull.IndexOfAny(new char[] { '"', '<', '>', '|', '?', '*' }) >= 0)
                {
                    Warn("根目录含有 Windows 不允许的字符，例如 E:\\fuNok。", _tbRoot);
                    return;
                }
                if (File.Exists(rootFull))
                {
                    Warn("根目录指向的是一个文件，请选择文件夹。", _tbRoot);
                    return;
                }
                // ch:磁盘/共享不存在时保存后每帧都会存图失败（日志还被限流），提前拦下更好排查
                string rootDrive = Path.GetPathRoot(rootFull);
                if (!string.IsNullOrEmpty(rootDrive) && !Directory.Exists(rootDrive))
                {
                    Warn("根目录所在磁盘/共享不存在：" + rootDrive, _tbRoot);
                    return;
                }
                string dateFormat = (_tbDate.Text ?? "").Trim();
                if (dateFormat.Length == 0) dateFormat = SaveImageSettings.DefaultDateFormat;
                try
                {
                    DateTime.Now.ToString(dateFormat, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    Warn("日期格式不合法，例如：yyyy-MM-dd。", _tbDate);
                    return;
                }

                SaveImageSettings s = new SaveImageSettings();
                s.Enabled = _cbEnable.Checked;
                s.Root = rootFull;
                s.DateFormat = dateFormat;
                s.Jpeg = _cbFormat.SelectedIndex == 1;
                s.JpegQuality = (int)_nudQuality.Value;
                s.RetentionDays = (int)_nudKeepDays.Value;

                bool anyCamera = false;
                for (int i = 0; i < SaveImageSettings.CameraCount; i++)
                {
                    CameraSaveSetting c = ReadRow(i);

                    int slots;
                    string slotsText = Convert.ToString(_grid.Rows[i].Cells[4].Value, CultureInfo.InvariantCulture) ?? "";
                    if (!int.TryParse(slotsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out slots)
                        || slots < 1 || slots > SaveImageSettings.MaxSlots)
                    {
                        Warn("相机" + (i + 1).ToString(CultureInfo.InvariantCulture) + " 的每产品张数必须是 1-"
                            + SaveImageSettings.MaxSlots.ToString(CultureInfo.InvariantCulture) + " 的整数。", _grid);
                        _grid.CurrentCell = _grid.Rows[i].Cells[4];
                        return;
                    }

                    string name = (c.Name ?? "").Trim();
                    if (name.Length == 0)
                        name = "相机" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    c.Name = name;
                    c.Folder = (c.Folder ?? "").Trim();
                    s.Cameras[i] = c;

                    if (c.Enabled)
                        anyCamera = true;
                }
                if (s.Enabled && !anyCamera)
                {
                    Warn("已勾选“启用定制存图”，但没有任何相机被启用。", _grid);
                    return;
                }

                // ch:同目录 + 同名称 = 两台相机写同一个文件，直接拦下（覆盖只应发生在同一相机自己的槽位上）。
                //   用清洗后的目录 + 1..Slots 实际文件名比较，A/B 与 A_B、超长名截断、槽位后缀撞名都能拦住。
                string conflict = SaveImageSettingsStore.FindOutputConflict(s, DateTime.Now);
                if (conflict != null)
                {
                    Warn(conflict, _grid);
                    return;
                }

                _settings = s;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存存图设置失败：" + ex.Message, "存图设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Warn(string text, Control focus)
        {
            MessageBox.Show(this, text, "存图设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (focus != null) focus.Focus();
        }

        #endregion
    }
}
