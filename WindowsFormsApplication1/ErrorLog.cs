using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace WindowsFormsApplication1
{
    class ErrorLog
    {
        // ch:日志写入全局锁：多线程（相机回调/通讯/界面）并发写同一日志文件时串行化，防止 IOException
        private static readonly object logLock = new object();

        public void WriteLog(string msg)
        {
            // ch:整体保护：日志失败（目录无权限/磁盘满等）绝不允许向外抛异常，否则会击穿调用方 catch
            try
            {
                lock (logLock)
                {
                    string filePath = AppDomain.CurrentDomain.BaseDirectory + "Log";
                    if (!Directory.Exists(filePath))
                    {
                        Directory.CreateDirectory(filePath);
                    }
                    string logPath = AppDomain.CurrentDomain.BaseDirectory + "Log\\" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
                    using (StreamWriter sw = File.AppendText(logPath))
                    {
                        sw.WriteLine("消息" + msg + "/");
                        sw.WriteLine("时间" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss/"));
                        sw.WriteLine("-------------------------------------------------/");
                        sw.WriteLine();
                        sw.Flush();
                    }
                }
            }
            catch
            {
                // ch:日志失败静默丢弃，保证日志功能永不导致程序异常
            }
        }

        public string ReadLog(string path)
        {
            try
            {
                string logPath = path;

                using (StreamReader sr = File.OpenText(logPath))
                {
                    string str = sr.ReadToEnd();
                    sr.Close();
                    return str;
                }
            }
            catch (IOException)
            {
                return "Error：所选日期并没有报告";
            }
        }
    }
}
