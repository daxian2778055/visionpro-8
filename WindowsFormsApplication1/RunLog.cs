using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;

namespace WindowsFormsApplication1
{
    class RunLog
    {
        private int processCount;
        private int iTemp;
        private string sOrg;
        private int iflag1;
        private int IOK;
        private int ING;
        private int sumend;
        private int sumline;
        private int sumline1;
        int iTemp1;
        int Sumss;
        ErrorLog Errorwrite = new ErrorLog();
        /*按照年份创建文件夹*/
        public void CreateDirectoryCsvPath(string path22)
        {
            try
            {
                string strYear = DateTime.Now.Year.ToString();
                string strMoth = DateTime.Now.ToString("Y");
                string strDirectoryCsvPath = @"E:\生产统计\" + "\\" + path22 + "\\" + strYear;
                string strDirectoryCsvPath1 = @"E:\每日统计\" + "\\" + path22 + "\\" + strYear + "\\" + strMoth;
                if (!Directory.Exists(strDirectoryCsvPath))
                {
                    Directory.CreateDirectory(strDirectoryCsvPath);
                }
                if (!Directory.Exists(strDirectoryCsvPath1))
                {
                    Directory.CreateDirectory(strDirectoryCsvPath1);
                }
            }
            catch (Exception ex)
            {
                Errorwrite.WriteLog("日志文件路径生成出错！" + ex.Message);
            };
        }

        /*按照月份创建csv*/
        public void CreateCsvPath(string path22,string shuju)
        {
            try
            {
                string strYear = DateTime.Now.Year.ToString();
                string strMoth = DateTime.Now.ToString("Y");
                string strDay = DateTime.Now.ToString("m");
                string strCsvPath = @"E:\生产统计\" + path22 + "\\" + strYear + "\\" + strMoth + ".csv";
                string strCsvPath2 = @"E:\每日统计\" + path22 + "\\" + strYear + "\\" + strMoth + "\\" + strDay + ".csv";
                FileStream file = null;
                if (!File.Exists(strCsvPath))
                {
                    file = new FileStream(strCsvPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    file.Close();
                    StreamWriter sw = new StreamWriter(File.OpenWrite(strCsvPath), Encoding.Default);
                    sw.BaseStream.Seek(0, SeekOrigin.Begin);
                    sw.Write("日期,总量,OK,NG,型号,合格率");
                    sw.Flush();
                    sw.Close();
                }
                FileStream file2 = null;
                if (!File.Exists(strCsvPath2))
                {
                    file2 = new FileStream(strCsvPath2, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    file2.Close();
                    StreamWriter sw2 = new StreamWriter(File.OpenWrite(strCsvPath2), Encoding.Default);
                    sw2.BaseStream.Seek(0, SeekOrigin.Begin);
                    sw2.WriteLine("序号,日期,"+shuju+"结果");
                    sw2.Flush();
                    sw2.Close();
                }
            }
            catch (Exception ex)
            {
                Errorwrite.WriteLog("日志文件生成出错!"+ex.Message);
            };


        }

        public void WriteDate(int okss, int ng1, string path22)
        {
            try
            {
                string strYear = DateTime.Now.Year.ToString();
                string strMoth = DateTime.Now.ToString("Y");
                string str = DateTime.Now.ToString("m");
                string strCsvPath = @"E:\生产统计\" + path22 + "\\" + strYear + "\\" + strMoth + ".csv";
                // ch:P2-④ 写入前确保目录存在，避免 E: 盘/目录缺失时 FileStream 打开静默失败
                try { Directory.CreateDirectory(Path.GetDirectoryName(strCsvPath)); } catch (Exception ex) { Errorwrite.WriteLog("生产统计目录创建失败:" + ex.Message); }
                sOrg = "mode";
                // ch:R3 改为按行读入内存 → 直接替换目标行 → 全量重写，彻底消除原"逐字节计数 + Seek(iTemp / iTemp-20)"
                //   在含中文(GBK 双字节)时偏移错位、写坏 CSV 的问题。
                List<string> lines = new List<string>();
                if (File.Exists(strCsvPath))
                {
                    using (StreamReader reader = new StreamReader(strCsvPath, System.Text.Encoding.Default))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            lines.Add(line);
                    }
                }
                sumline1 = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] != "")
                        sumline1++;
                }
                string[] strs = (lines.Count > 0) ? lines[lines.Count - 1].Split(',') : new string[5];
                processCount = 0;
                sumend = 0;
                sumline = 0;

                try
                {

                    if (strs != null && strs.Length > 4 && strs[0] == str)
                    {

                        if (strs[4].Contains(sOrg))
                        {
                            if (okss == 1)
                            {

                                IOK = int.Parse(strs[2]) + 1;
                                ING = int.Parse(strs[3]);
                            }
                            else
                            {
                                IOK = int.Parse(strs[2]);
                                ING = int.Parse(strs[3]) + 1;
                            }

                        }
                        else
                        {
                            if (okss == 1)
                            {
                                IOK = int.Parse(strs[2]) + 1;
                                ING = int.Parse(strs[3]);
                            }
                            else
                            {
                                IOK = int.Parse(strs[2]);
                                ING = int.Parse(strs[3]) + 1;
                            }
                            sOrg = strs[4] + sOrg;
                        }
                        // ch:R3 直接替换最后一行（原为 Seek 定位覆盖），全量重写
                        string s = str + "," + (IOK + ING) + "," + IOK + "," + ING + "," + sOrg + "," + IOK * 1.0f / (IOK + ING);
                        if (lines.Count > 0)
                            lines[lines.Count - 1] = s;
                        else
                            lines.Add(s);
                        WriteAllLinesSafe(strCsvPath, lines);
                    }
                    else
                    {
                        // ch:R3 追加新行；原 sumline1>=35 分支为 Seek 后写入空串（等价不改动文件），这里保持不写
                        if (sumline1 < 35)
                        {
                            string s = str + "," + (okss + ng1) + "," + okss + "," + ng1 + "," + sOrg + "," + okss * 1.0f / (okss + ng1);
                            lines.Add(s);
                            WriteAllLinesSafe(strCsvPath, lines);
                        }
                    }


                }
                catch (Exception ex)
                {
                    Errorwrite.WriteLog(ex.Message + "记录1");
                };
            }
            catch (Exception ex)
            {

                Errorwrite.WriteLog(ex.Message + "记录2");
            };


        }

        // ch:R3 全量重写 CSV（统一 \r\n 行尾），替代原 File.OpenWrite + BaseStream.Seek 的字节级就地修改
        // ch:P1 改为「先写 .tmp 再原子替换」：直接截断目标文件在写入中途掉电/异常会导致整月产量归零。
        private void WriteAllLinesSafe(string path, List<string> lines)
        {
            string tmp = path + ".tmp";
            using (StreamWriter sw = new StreamWriter(tmp, false, Encoding.Default))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                        sw.Write("\r\n");
                    sw.Write(lines[i]);
                }
                sw.Flush();
            }
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, path + ".bak"); // 原子替换，旧文件留作 .bak
                else
                    File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Errorwrite.WriteLog("CSV 原子替换失败，回退为直接覆盖:" + ex.Message);
                try { File.Copy(tmp, path, true); } catch (Exception ex2) { Errorwrite.WriteLog("CSV 回退写入失败:" + ex2.Message); }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex3) { Errorwrite.WriteLog("CSV 临时文件清理失败:" + ex3.Message); }
            }
        }
        public void WriteDate1(string jilu, string path22, int okss)
        {
            string jieguo;
            string strYear = DateTime.Now.Year.ToString();
            string strMoth = DateTime.Now.ToString("Y");
            string str = DateTime.Now.ToString("m");
            string strCsvPath = @"E:\每日统计\" + path22 + "\\" + strYear + "\\" + strMoth + "\\" + str + ".csv";
            // ch:P2-④ 写入前确保目录存在，避免 E: 盘/目录缺失时 FileStream 打开静默失败
            try { Directory.CreateDirectory(Path.GetDirectoryName(strCsvPath)); } catch (Exception ex) { Errorwrite.WriteLog("每日统计目录创建失败:" + ex.Message); }
            // ch:R10-4 删除整文件逐字节扫描死代码（iTemp1 的消费端 Seek 已注释，processCount-temp 恒 0，原为每条记录 O(N^2)）；
            //   读取仅为取末行续接序号。FileMode.Open→OpenOrCreate 且纳入 try：原实现文件不存在时在 try 外抛异常，
            //   落到 Form1 统计 catch 里再次 runlog1，造成月度总量双计（异常被当作流程控制）。
            string lastLine = null;
            try
            {
                using (FileStream fs = new FileStream(strCsvPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs, System.Text.Encoding.Default))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lastLine = line;
                    }
                }
            }
            catch (Exception ex)
            {
                Errorwrite.WriteLog("每日统计读取失败(按新建续号):" + ex.Message);
            }
            try
            {
                string[] strs = lastLine != null ? lastLine.Split(',') : new string[0];
                // ch:P2-④ 空文件/仅表头时序号列为 null 或非数字，按 0 续写
                string seqRaw = strs.Length > 0 ? strs[0] : null;
                if (string.IsNullOrEmpty(seqRaw) || seqRaw == "次序" || seqRaw == "序号")
                    seqRaw = "0";
                if (!int.TryParse(seqRaw, out Sumss))
                {
                    Errorwrite.WriteLog("每日统计序号解析失败，按 0 续写。原始值：" + (seqRaw ?? "null"));
                    Sumss = 0;
                }
                Sumss = Sumss + 1;
                string strsecond = DateTime.Now.ToString("s");
                if (okss == 1)
                    jieguo = "OK";
                else
                    jieguo = "NG";
                string s = Sumss + "," + strsecond + "," + jilu + "," + jieguo;
                using (StreamWriter sw4 = new StreamWriter(strCsvPath, true, Encoding.Default))
                {
                    sw4.WriteLine(s);
                    sw4.Flush();
                }
            }
            catch (Exception ex)
            {
                // ch:P2-④ 原空 catch 会静默吞掉写入异常，导致每日统计缺行却无任何线索
                Errorwrite.WriteLog("每日统计写入失败:" + ex.Message);
            }
        }
    }

}
