namespace WindowsFormsApplication1
{
    partial class FormVersion
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源。
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            this.lblHeader = new System.Windows.Forms.Label();
            this.lblVersion = new System.Windows.Forms.Label();
            this.lblReleaseDate = new System.Windows.Forms.Label();
            this.lblRepo = new System.Windows.Forms.Label();
            this.lblTag = new System.Windows.Forms.Label();
            this.lblBuild = new System.Windows.Forms.Label();
            this.lblTimelineTitle = new System.Windows.Forms.Label();
            this.lstTimeline = new System.Windows.Forms.ListBox();
            this.btnOk = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblHeader
            // 
            this.lblHeader.AutoSize = true;
            this.lblHeader.Font = new System.Drawing.Font("微软雅黑", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.lblHeader.Location = new System.Drawing.Point(16, 14);
            this.lblHeader.Name = "lblHeader";
            this.lblHeader.Size = new System.Drawing.Size(212, 22);
            this.lblHeader.TabIndex = 0;
            this.lblHeader.Text = "工业视觉检测软件 · 版本信息";
            // 
            // lblVersion
            // 
            this.lblVersion.AutoSize = true;
            this.lblVersion.Location = new System.Drawing.Point(18, 52);
            this.lblVersion.Name = "lblVersion";
            this.lblVersion.Size = new System.Drawing.Size(89, 12);
            this.lblVersion.TabIndex = 1;
            this.lblVersion.Text = "当前版本：v1.0.0";
            // 
            // lblReleaseDate
            // 
            this.lblReleaseDate.AutoSize = true;
            this.lblReleaseDate.Location = new System.Drawing.Point(18, 76);
            this.lblReleaseDate.Name = "lblReleaseDate";
            this.lblReleaseDate.Size = new System.Drawing.Size(101, 12);
            this.lblReleaseDate.TabIndex = 2;
            this.lblReleaseDate.Text = "发布日期：0000-00-00";
            // 
            // lblRepo
            // 
            this.lblRepo.AutoSize = true;
            this.lblRepo.Location = new System.Drawing.Point(18, 100);
            this.lblRepo.Name = "lblRepo";
            this.lblRepo.Size = new System.Drawing.Size(65, 12);
            this.lblRepo.TabIndex = 3;
            this.lblRepo.Text = "所属仓库：-";
            // 
            // lblTag
            // 
            this.lblTag.AutoSize = true;
            this.lblTag.Location = new System.Drawing.Point(18, 124);
            this.lblTag.Name = "lblTag";
            this.lblTag.Size = new System.Drawing.Size(65, 12);
            this.lblTag.TabIndex = 4;
            this.lblTag.Text = "仓库标签：-";
            // 
            // lblBuild
            // 
            this.lblBuild.AutoSize = true;
            this.lblBuild.Location = new System.Drawing.Point(18, 148);
            this.lblBuild.Name = "lblBuild";
            this.lblBuild.Size = new System.Drawing.Size(65, 12);
            this.lblBuild.TabIndex = 5;
            this.lblBuild.Text = "程序生成时间：-";
            // 
            // lblTimelineTitle
            // 
            this.lblTimelineTitle.AutoSize = true;
            this.lblTimelineTitle.Location = new System.Drawing.Point(18, 180);
            this.lblTimelineTitle.Name = "lblTimelineTitle";
            this.lblTimelineTitle.Size = new System.Drawing.Size(65, 12);
            this.lblTimelineTitle.TabIndex = 6;
            this.lblTimelineTitle.Text = "版本时间线：";
            // 
            // lstTimeline
            // 
            this.lstTimeline.FormattingEnabled = true;
            this.lstTimeline.HorizontalScrollbar = true;
            this.lstTimeline.ItemHeight = 12;
            this.lstTimeline.Location = new System.Drawing.Point(20, 202);
            this.lstTimeline.Name = "lstTimeline";
            this.lstTimeline.Size = new System.Drawing.Size(586, 160);
            this.lstTimeline.TabIndex = 7;
            // 
            // btnOk
            // 
            this.btnOk.Location = new System.Drawing.Point(524, 374);
            this.btnOk.Name = "btnOk";
            this.btnOk.Size = new System.Drawing.Size(82, 28);
            this.btnOk.TabIndex = 8;
            this.btnOk.Text = "确定";
            this.btnOk.UseVisualStyleBackColor = true;
            this.btnOk.Click += new System.EventHandler(this.btnOk_Click);
            // 
            // FormVersion
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(626, 414);
            this.Controls.Add(this.btnOk);
            this.Controls.Add(this.lstTimeline);
            this.Controls.Add(this.lblTimelineTitle);
            this.Controls.Add(this.lblBuild);
            this.Controls.Add(this.lblTag);
            this.Controls.Add(this.lblRepo);
            this.Controls.Add(this.lblReleaseDate);
            this.Controls.Add(this.lblVersion);
            this.Controls.Add(this.lblHeader);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "FormVersion";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "版本信息";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label lblHeader;
        private System.Windows.Forms.Label lblVersion;
        private System.Windows.Forms.Label lblReleaseDate;
        private System.Windows.Forms.Label lblRepo;
        private System.Windows.Forms.Label lblTag;
        private System.Windows.Forms.Label lblBuild;
        private System.Windows.Forms.Label lblTimelineTitle;
        private System.Windows.Forms.ListBox lstTimeline;
        private System.Windows.Forms.Button btnOk;
    }
}
