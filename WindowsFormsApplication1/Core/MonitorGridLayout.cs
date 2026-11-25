namespace WindowsFormsApplication1
{
    /// <summary>
    /// 监控画面 3×3 网格的纯托管槽位计算；WinForms 层只负责把面板放到返回的格位。
    /// 格位编号 = 行号×3 + 列号；4相机时取 [0,1,3,4]，即左上、右上、左下、右下（第3列保留1%窄边）。
    /// </summary>
    public sealed class MonitorGridLayout
    {
        public int EnabledCount { get; private set; }
        public int[] Slots { get; private set; }
        public bool SpanSingleCamera { get { return EnabledCount <= 1; } }

        private MonitorGridLayout(int enabledCount, int[] slots)
        {
            EnabledCount = enabledCount;
            Slots = slots;
        }

        public static MonitorGridLayout Create(bool[] enabled)
        {
            if (enabled == null || enabled.Length != 8)
                throw new System.ArgumentException("enabled 必须恰好包含 8 个相机状态", "enabled");

            int count = 0;
            for (int i = 0; i < enabled.Length; i++)
                if (enabled[i]) count++;

            int[] slots;
            if (count == 0)
                slots = new int[0];
            else
            {
                if (count <= 1)
                    slots = new[] { 0 };
                else if (count == 2)
                    slots = new[] { 0, 1 };
                else if (count <= 4)
                    slots = new[] { 0, 1, 3, 4 };
                else if (count <= 6)
                    slots = new[] { 0, 1, 2, 3, 4, 5 };
                else
                    slots = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
                System.Array.Resize(ref slots, count);
            }

            return new MonitorGridLayout(count, slots);
        }
    }
}
