using System.Collections.Generic;

namespace WindowsFormsApplication1
{
    // ch:P2-① 移除未使用的重复模型 Message1/Tags1/Fields1 及 jieshou 上对应冗余属性；
    //      仅保留反序列化实际使用的 jieshou.Data(Datas1)。Datas/Tags/Fields 留在 Datas.cs（Form7 另作他用）。
    internal class jieshou
    {
        public Datas1 Data { get; set; }
    }
    internal class Datas1
    {
        public string tab { get; set; }
        public Tags tags { get; set; }
        public Fields fields { get; set; }
    }
}
