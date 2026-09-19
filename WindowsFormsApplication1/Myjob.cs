using Cognex.VisionPro;
using Cognex.VisionPro.CalibFix;
using Cognex.VisionPro.ImageFile;
using Cognex.VisionPro.QuickBuild;
using Cognex.VisionPro.ToolBlock;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    class Myjob
    {
        public int trriger;
        public int yun;
        public CogJob job;
        public CogToolBlock block;
        public int sum;
        public int oksum;
        public int ngsum;
        public float rate;
        public int number;
        public int numberng;
        public string pathhead_ok;
        public string pathhead_ng;
        public FileInfo fileok;
        public FileInfo fileng;
        public bool Color;
        public CogImageFileBMP Cogbmp;
        public bool trrigerEn;
        public int out_end;
        public int trrigersum;
        public Bitmap img;
        public int master;
        public int timespace;
        public string path_number;
        public bool cunok;
        public bool cunng;
        public ICogRecord newrecod;
        public int temptu;
        public int index;
        public int fit;
        public int ok1;
        public int ng1;
        public double runtime;
        public int runcishu;
        public int en;
        public string triggerMode;
        public string triggerZifu;
        public string jieshouZifu;
        public bool shijianEn;
        // ch:P2 跨线程标志：检测线程写 true（Form1.cs 取图渲染前）、UI 线程写 false（FinishOcxPaint/入队失败等），读取侧 IsCameraDisplayBusy 无锁。
        //   仅 true/false 赋值、无复合读改写，volatile 保证可见性即可，无需并入 _ocxPaintLock。
        public volatile bool jiasu;
        public string danwu_time;
        public int danwu_cishu;
        public DataTable myTable;
        public DataTable myTable1;
        public bool tishi;
        public bool modbustcp;
        public bool tcp;
        public bool serial;
        public bool modbustemp;
        public bool cuntu;
        public bool xuanran;
        public bool IO;
        public FolderBrowserDialog dlg;
        public int IOyanshi;
        public int ioOkUntil;
        public int ioNgUntil;
        public bool ioOkHigh;
        public bool ioNgHigh;
        public string state;
        public long time;
        public bool roi;
        public int changdu;
        public string address;
        public string biaotou;
        public string tianbiao;
        public CogCalibNPointToNPointTool calib;
        public  float [] record =new float [] {0.99f,0.99f,0.99f};
        public int records = 0;
        public bool zidongbaoguang = false;
        public float baoguang = 0;
        public int changel1 = 0;
        public int changel2 = 0;
        public int outputok2 = 0;
        public int outputng2 = 0;
        public object locker_ok = new object();
        public object locker_ng = new object();
        // ch:P0 工具块并发保护锁：轮询/通讯事件线程写 block.Inputs 与检测线程 block.Run() 必须互斥，
        //   VisionPro CogToolBlock 非线程安全，并发可致崩溃或读到半帧。
        public readonly object blockLock = new object();
        // ch:P2 并发安全：载入线程 Clear/Add 与 UI 端 list_block[n] 读并发，普通 Dictionary 会内部损坏/抛异常。
        //   改 ConcurrentDictionary（Clear/Count/索引器/枚举均线程安全）；.Add(k,v) 是显式接口实现，写入点统一改索引器赋值。
        public ConcurrentDictionary<int, CogToolBlock> list_block = new ConcurrentDictionary<int, CogToolBlock>();
        public int outputok
        {
            get { return changel1; }
            set
            {
                changel1 = value;
            }
        }
        public int outputng
        {
            get { return changel2; }
            set
            {
                changel2 = value;
            }
        }

    }
}
