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

        // ch:R13 CSV 读缓存：WriteDate(月) 与 WriteDate1(日) 在同一条记录里各把文件全量读一遍，
        //   而这些文件几乎只由本程序写。用「路径+长度+mtime」判失效，命中直接用内存副本，
        //   每条记录省掉一次 O(文件大小) 的磁盘读（月文件/日文件都是只增不减，越大越划算）。
        //   命中返回副本——调用方会就地改写 lines，绝不能直接把缓存本体交出去。
        //   写入成功后按真实 stat 刷新；写失败时磁盘 mtime 未变、内存链条仍保留已写内容，
        //   下次命中继续在正确的基础上累加，不会双计也不会丢行（这正是不做延迟落盘的原因）。
        //   两套字段相互独立：月文件与日文件路径不同，共用一套会互相踢失效、缓存永远不命中。
        private string _csvPathM; private long _csvLenM = -1; private DateTime _csvMTimeM; private List<string> _csvLinesM;
        private string _csvPathD; private long _csvLenD = -1; private DateTime _csvMTimeD; private string _csvLastD; private bool _csvHasD;

        private static bool TryStat(string path, out long len, out DateTime mtime)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                len = fi.Exists ? fi.Length : -1L;
                mtime = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
                return true;
            }
            catch
            {
                // ch:路径非法等 stat 失败一律视为「拿不到状态」，调用方退回原本的直读路径
                len = -1L;
                mtime = DateTime.MinValue;
                return false;
            }
        }

        // ch:月文件：WriteDate 需要全量行(sumline1/末行)，命中则返回副本
        private List<string> ReadLinesMonthly(string path)
        {
            long len; DateTime mt;
            // ch:先取 stat 再读：以「读之前」的磁盘状态入缓存，读写窗口内的外部改动会让下次 stat 不符而重读
            bool statOk = TryStat(path, out len, out mt);
            if (statOk && _csvLinesM != null && _csvPathM == path && _csvLenM == len && _csvMTimeM == mt)
                return new List<string>(_csvLinesM);
            List<string> lines = new List<string>();
            if (File.Exists(path))
            {
                using (StreamReader reader = new StreamReader(path, System.Text.Encoding.Default))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }
            }
            if (statOk)
            {
                _csvPathM = path; _csvLenM = len; _csvMTimeM = mt;
                _csvLinesM = new List<string>(lines);
            }
            return lines;
        }

        // ch:月文件整文件写成功后，用「刚写出的内容 + 写后 stat」刷新缓存，下一帧可直接命中
        private void StoreLinesMonthly(string path, List<string> lines)
        {
            long len; DateTime mt;
            if (TryStat(path, out len, out mt))
            {
                _csvPathM = path; _csvLenM = len; _csvMTimeM = mt;
                _csvLinesM = new List<string>(lines);
            }
            else
            {
                _csvLinesM = null; // ch:存不下就作废，让下帧老实重读
            }
        }

        // ch:日文件：WriteDate1 只用末行续号，却整文件扫一遍；缓存末行 + stat 判失效
        private void StoreLastLineDaily(string path, string lastLine)
        {
            long len; DateTime mt;
            if (TryStat(path, out len, out mt) && len >= 0)
            {
                _csvPathD = path; _csvLenD = len; _csvMTimeD = mt;
                _csvLastD = lastLine; _csvHasD = true;
            }
            else
            {
                _csvHasD = false;
            }
        }

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
                // ch:R13 改走「路径+长度+mtime」读缓存：命中直接用内存副本（返回的是副本，下面会就地改写）
                List<string> lines = ReadLinesMonthly(strCsvPath);
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
                        StoreLinesMonthly(strCsvPath, lines); // ch:R13 写成功(WriteAllLinesSafe 内部已兜底)后按真实 stat 刷新读缓存
                    }
                    else
                    {
                        // ch:R3 追加新行；原 sumline1>=35 分支为 Seek 后写入空串（等价不改动文件），这里保持不写
                        if (sumline1 < 35)
                        {
                            // ch:R11-1 跨月/冷启动新建文件时先补表头（与 CreateCsvPath 同格式），原首条即裸数据行
                            if (lines.Count == 0)
                                lines.Add("日期,总量,OK,NG,型号,合格率");
                            string s = str + "," + (okss + ng1) + "," + okss + "," + ng1 + "," + sOrg + "," + okss * 1.0f / (okss + ng1);
                            lines.Add(s);
                            WriteAllLinesSafe(strCsvPath, lines);
                            StoreLinesMonthly(strCsvPath, lines); // ch:R13 同上：新建文件分支也刷新读缓存
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
        public void WriteDate1(string jilu, string path22, int okss, string biaotou = null)
        {
            string jieguo;
            string strYear = DateTime.Now.Year.ToString();
            string strMoth = DateTime.Now.ToString("Y");
            string str = DateTime.Now.ToString("m");
            string strCsvPath = @"E:\每日统计\" + path22 + "\\" + strYear + "\\" + strMoth + "\\" + str + ".csv";
            // ch:P2-④ 写入前确保目录存在，避免 E: 盘/目录缺失时 FileStream 打开静默失败
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(strCsvPath));
            }
            catch (Exception ex) { Errorwrite.WriteLog("每日统计目录创建失败:" + ex.Message); }
            // ch:R10-4 删除整文件逐字节扫描死代码（iTemp1 的消费端 Seek 已注释，processCount-temp 恒 0，原为每条记录 O(N^2)）；
            //   读取仅为取末行续接序号。FileMode.Open→OpenOrCreate 且纳入 try：原实现文件不存在时在 try 外抛异常，
            //   落到 Form1 统计 catch 里再次 runlog1，造成月度总量双计（异常被当作流程控制）。
            // ch:R12 表头判据改用「文件是否为空」而非 lastLine==null：原实现里读取失败(catch 保 lastLine 仍为 null)
            //   会在已有内容的文件中部重复插一行表头。打开前先记录长度，读不出来(len<0)则不补表头（该路径本就退化为按新建续号）。
            // ch:R13 长度探测与末行读取合并为一次 stat：日文件每条记录都整文件扫一遍只为取末行续号，
            //   命中「路径+长度+mtime」缓存后直接用内存里的末行，省掉 O(文件大小) 的读。
            long fileLen = -1;
            long statLen = -1; DateTime statMTime = DateTime.MinValue; bool statOk = false;
            try { FileInfo fi = new FileInfo(strCsvPath); fileLen = fi.Exists ? fi.Length : 0; statLen = fi.Exists ? fi.Length : -1; statMTime = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue; statOk = true; } catch (Exception ex) { Errorwrite.WriteLog("每日统计文件长度探测失败(不补表头):" + ex.Message); }
            bool needHeader = (fileLen == 0);
            string lastLine = null;
            // ch:R13 命中判据 = 路径+长度+mtime 全等，且文件确实存在(statLen>=0)；
            //   文件不存在时保持原来的直读路径（OpenOrCreate 会把它建出来，行为不变）
            bool cacheHit = statOk && statLen >= 0 && _csvHasD && _csvPathD == strCsvPath && _csvLenD == statLen && _csvMTimeD == statMTime;
            if (cacheHit)
            {
                lastLine = _csvLastD; // ch:R13 命中读缓存，跳过整文件扫描
            }
            else
            {
                bool readOk = false;
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
                    readOk = true;
                }
                catch (Exception ex)
                {
                    Errorwrite.WriteLog("每日统计读取失败(按新建续号):" + ex.Message);
                }
                if (readOk && statOk && statLen >= 0)
                {
                    // ch:R13 用「读之前」取的 stat 入缓存：读写窗口内被外部改动会让下次 stat 不符而自动重读
                    _csvPathD = strCsvPath; _csvLenD = statLen; _csvMTimeD = statMTime; _csvLastD = lastLine; _csvHasD = true;
                }
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
                    // ch:R11-1 OpenOrCreate 后原靠 FileNotFoundException→CreateCsvPath 写表头的路径不再触发；
                    //   跨天/冷启动首条数据前先补表头（与 CreateCsvPath 同格式），否则日文件永远缺表头
                    // ch:R12 判据改 needHeader(文件长度为 0)，原 lastLine==null 在读取失败时会于文件中部重复插表头
                    if (needHeader)
                        sw4.WriteLine("序号,日期," + (biaotou ?? "") + "结果");
                    sw4.WriteLine(s);
                    sw4.Flush();
                }
                // ch:R13 追加成功(已出 using、句柄已释放)后按写后 stat 刷新日文件读缓存：末行就是刚写的 s
                StoreLastLineDaily(strCsvPath, s);
            }
            catch (Exception ex)
            {
                // ch:P2-④ 原空 catch 会静默吞掉写入异常，导致每日统计缺行却无任何线索
                Errorwrite.WriteLog("每日统计写入失败:" + ex.Message);
            }
        }
    }

}
