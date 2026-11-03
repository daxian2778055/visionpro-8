using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    /// <summary>
    /// ch:R12 方案表编辑面板（纯代码构造，无 Designer/resx，可挂 TabPage 或弹窗宿主）。
    ///   前 8 行与窗体既有 8 对"载图串/方案路径"文本框双向同步（映射顺序由调用方传入），
    ///   第 9..100 行只在表内编辑；任一侧修改立即回写 SchemeStore，qiehuan 匹配读快照即时生效，
    ///   无需先保存 ini（与历史"文本框改完即生效"行为一致）。
    /// </summary>
    public class SchemeTablePanel : Panel
    {
        private readonly SchemeStore _store;
        private readonly TextBox[] _zifuBoxes;
        private readonly TextBox[] _pathBoxes;
        private readonly DataGridView _grid;
        private readonly Button _btnAdd;
        private readonly Button _btnDel;
        private readonly Label _lblInfo;
        private bool _suppress;

        public SchemeTablePanel(SchemeStore store, TextBox[] zifuBoxes, TextBox[] pathBoxes)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (zifuBoxes == null || zifuBoxes.Length != SchemeStore.LegacyRows) throw new ArgumentException("zifuBoxes 必须为 8 个");
            if (pathBoxes == null || pathBoxes.Length != SchemeStore.LegacyRows) throw new ArgumentException("pathBoxes 必须为 8 个");
            _store = store;
            _zifuBoxes = zifuBoxes;
            _pathBoxes = pathBoxes;
            Dock = DockStyle.Fill;

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersWidth = 34;
            _grid.SelectionMode = DataGridViewSelectionMode.RowHeaderSelect;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            var colKey = new DataGridViewTextBoxColumn();
            colKey.HeaderText = "载图串";
            colKey.Width = 200;
            var colPath = new DataGridViewTextBoxColumn();
            colPath.HeaderText = "方案路径";
            colPath.Width = 430;
            _grid.Columns.Add(colKey);
            _grid.Columns.Add(colPath);
            _grid.CellValueChanged += Grid_CellChanged;
            _grid.CurrentCellDirtyStateChanged += Grid_DirtyFlush;
            _grid.CellContentDoubleClick += Grid_Browse; // ch:R13-3 双击路径列=浏览 vpp（第 9..100 行也有入口）

            var bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 34;
            bottom.FlowDirection = FlowDirection.LeftToRight;
            _btnAdd = new Button();
            _btnAdd.Text = "添加方案行";
            _btnAdd.Width = 100;
            _btnAdd.Click += BtnAdd_Click;
            _btnDel = new Button();
            _btnDel.Text = "删除选中行";
            _btnDel.Width = 100;
            _btnDel.Click += BtnDel_Click;
            _lblInfo = new Label();
            _lblInfo.AutoSize = true;
            _lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            _lblInfo.Margin = new Padding(10, 8, 0, 0);
            bottom.Controls.Add(_btnAdd);
            bottom.Controls.Add(_btnDel);
            bottom.Controls.Add(_lblInfo);

            Controls.Add(_grid);
            Controls.Add(bottom);

            for (int i = 0; i < _zifuBoxes.Length; i++)
            {
                if (_zifuBoxes[i] != null) _zifuBoxes[i].TextChanged += Boxes_TextChanged;
                if (_pathBoxes[i] != null) _pathBoxes[i].TextChanged += Boxes_TextChanged;
            }

            SyncAllFromStore();
        }

        /// <summary>ini 装载后外部改动了 store 时调用：网格与 8 文本框按 store 全量刷新。</summary>
        public void SyncAllFromStore()
        {
            List<string[]> rows = PadToLegacy(_store.GetRows());
            _suppress = true;
            try
            {
                FillGrid(rows);
                FillBoxes(rows);
            }
            finally
            {
                _suppress = false;
            }
            UpdateInfo(SchemeStore.TrimTrailingEmpty(rows)); // ch:R13-3 计数按"有效行"口径，与保存裁剪一致
        }

        /// <summary>保存用：网格内容为准，去表尾空行、上限 100。</summary>
        public List<string[]> GetNormalizedRows()
        {
            return SchemeStore.TrimTrailingEmpty(ReadGridRows());
        }

        /// <summary>ch:R13-3 双击"方案路径"列弹 vpp 选择框；写回单元格后经 Grid_CellChanged 正常同步链落库。</summary>
        private void Grid_Browse(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "VP vpp File|*.vpp*";
                try { dlg.FileName = Convert.ToString(_grid[e.ColumnIndex, e.RowIndex].Value ?? ""); } catch (Exception) { }
                Form host = FindForm();
                if (dlg.ShowDialog(host) != DialogResult.OK) return;
                // ch:R13-4 编辑态先提交，避免赋值被编辑控件里的旧文本覆盖
                try { _grid.EndEdit(); } catch (Exception) { }
                _grid[e.ColumnIndex, e.RowIndex].Value = dlg.FileName; // 触发 CellValueChanged → store/文本框同步
            }
        }

        private void Boxes_TextChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            List<string[]> rows = ReadGridRows();
            for (int i = 0; i < SchemeStore.LegacyRows && i < rows.Count; i++)
            {
                // ch:R13-3 null 控件跳过（与构造期判空订阅同一契约）：保留原值而非抹成空串
                if (_zifuBoxes[i] != null) rows[i][0] = _zifuBoxes[i].Text;
                if (_pathBoxes[i] != null) rows[i][1] = _pathBoxes[i].Text;
            }
            rows = SchemeStore.TrimTrailingEmpty(rows);
            _store.SetRows(rows);
            _suppress = true;
            try
            {
                FillGrid(PadToLegacy(rows));
            }
            finally
            {
                _suppress = false;
            }
            UpdateInfo(rows);
        }

        private void Grid_DirtyFlush(object sender, EventArgs e)
        {
            // 编辑中的文本单元格离焦前即提交，保证 CellValueChanged 及时触发
            try { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); } catch (Exception) { }
        }

        private void Grid_CellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppress) return;
            List<string[]> rows = ReadGridRows();
            rows = SchemeStore.TrimTrailingEmpty(rows);
            _store.SetRows(rows);
            _suppress = true;
            try
            {
                FillBoxes(PadToLegacy(rows));
            }
            finally
            {
                _suppress = false;
            }
            UpdateInfo(rows);
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            List<string[]> rows = SchemeStore.TrimTrailingEmpty(ReadGridRows());
            if (rows.Count >= SchemeStore.MaxSchemes)
            {
                MessageBox.Show("方案数已达上限 " + SchemeStore.MaxSchemes + " 行。", "方案表", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            rows.Add(new string[] { "", "" });
            _store.SetRows(rows);
            _suppress = true;
            try
            {
                // ch:R13-4 与其它刷新路径一致：网格至少保留前 8 行视图，避免添加行后
                //   网格缩到有效行数、而上方 8 个文本框仍存在导致后续文本框编辑落不到网格
                FillGrid(PadToLegacy(rows));
                _grid.CurrentCell = _grid.Rows[rows.Count - 1].Cells[0];
                _grid.BeginEdit(true);
            }
            finally
            {
                _suppress = false;
            }
            UpdateInfo(SchemeStore.TrimTrailingEmpty(rows)); // ch:R13-4 新加的空行不计入有效方案，与保存裁剪口径一致
        }

        private void BtnDel_Click(object sender, EventArgs e)
        {
            DataGridViewRow row = _grid.CurrentRow;
            if (row == null || row.Index < 0)
                return;
            List<string[]> rows = ReadGridRows();
            if (row.Index >= rows.Count)
                return;
            if (row.Index < SchemeStore.LegacyRows)
            {
                // 前 8 行与窗体文本框同步存在：删除=清空该行配置（不缩表位，防映射错位）
                rows[row.Index][0] = "";
                rows[row.Index][1] = "";
            }
            else
            {
                rows.RemoveAt(row.Index);
            }
            rows = SchemeStore.TrimTrailingEmpty(rows);
            _store.SetRows(rows);
            _suppress = true;
            try
            {
                FillGrid(PadToLegacy(rows));
                FillBoxes(PadToLegacy(rows));
            }
            finally
            {
                _suppress = false;
            }
            UpdateInfo(rows);
        }

        private List<string[]> ReadGridRows()
        {
            List<string[]> rows = new List<string[]>();
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                DataGridViewRow r = _grid.Rows[i];
                if (r.IsNewRow) continue;
                object k = r.Cells[0].Value;
                object p = r.Cells[1].Value;
                rows.Add(new string[] { k == null ? "" : k.ToString(), p == null ? "" : p.ToString() });
            }
            return rows;
        }

        private static List<string[]> PadToLegacy(List<string[]> rows)
        {
            // ch:R13-4 返回补齐后的副本，不修改入参：调用方随后用原 rows 调 UpdateInfo，
            //   若原地补齐会把"有效行"算成至少 8 行（保存时又裁剪，计数前后不一致）。
            List<string[]> copy = new List<string[]>();
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                    copy.Add(rows[i] ?? new string[] { "", "" });
            }
            while (copy.Count < SchemeStore.LegacyRows)
                copy.Add(new string[] { "", "" });
            return copy;
        }

        private void FillGrid(List<string[]> rows)
        {
            _grid.Rows.Clear();
            for (int i = 0; i < rows.Count; i++)
                _grid.Rows.Add(rows[i][0], rows[i][1]);
        }

        private void FillBoxes(List<string[]> rows)
        {
            for (int i = 0; i < SchemeStore.LegacyRows; i++)
            {
                string k = i < rows.Count ? rows[i][0] : "";
                string p = i < rows.Count ? rows[i][1] : "";
                if (_zifuBoxes[i] != null) _zifuBoxes[i].Text = k;
                if (_pathBoxes[i] != null) _pathBoxes[i].Text = p;
            }
        }

        private void UpdateInfo(List<string[]> rows)
        {
            _lblInfo.Text = "有效方案 " + rows.Count + " / 上限 " + SchemeStore.MaxSchemes + " 行（前 8 行与上方文本框同步）";
        }
    }
}
