namespace WindowsFormsApplication1
{
    /// <summary>相机数选择：只支持连续启用 1..N 台，N 夹取到 1..8。</summary>
    public static class CameraCountState
    {
        public const int MaxCameras = 8;

        public static int Normalize(int count)
        {
            if (count < 1) return 1;
            if (count > MaxCameras) return MaxCameras;
            return count;
        }

        public static bool[] CreateEnabled(int count)
        {
            int normalized = Normalize(count);
            var enabled = new bool[MaxCameras];
            for (int i = 0; i < normalized; i++) enabled[i] = true;
            return enabled;
        }

        public static int CountEnabled(bool[] enabled)
        {
            if (enabled == null) return 0;
            int count = 0;
            for (int i = 0; i < enabled.Length; i++)
                if (enabled[i]) count++;
            return count;
        }
    }
}
