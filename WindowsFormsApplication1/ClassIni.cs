using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace demo
{
    class ClassIni
    {
        public string FileName; //INI文件名
        //声明读写INI文件的API函数
        [DllImport("kernel32")]
        private static extern bool WritePrivateProfileString(string section, string key, string val, string filePath);
        [DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section, string key, string def, byte[] retVal, int size, string filePath);
        //类的构造函数，传递INI文件名
        public void ReadINIFile(string AFileName)
        {
            // 判断文件是否存在
            FileInfo fileInfo = new FileInfo(AFileName);
            // ch:P1-7 FileName 必须先解析赋值：原实现在方法末尾赋值，创建文件失败先抛异常时 FileName 保持 null，
            //   调用方吞掉异常后 ReadString 以 null 路径调 GetPrivateProfileString → 实际静默读写 C:\Windows\win.ini
            FileName = fileInfo.FullName;
            if ((!fileInfo.Exists))
            { //|| (FileAttributes.Directory in fileInfo.Attributes))
                //文件不存在，建立文件；失败仅记日志不抛（后续按系统默认节键回退读取）
                try
                {
                    using (System.IO.StreamWriter sw = new System.IO.StreamWriter(FileName, false, System.Text.Encoding.Default))
                    {
                        sw.Write("#ParamSet");
                    }
                }
                catch (Exception ex)
                {
                    WindowsFormsApplication1.ErrorLog IniErrLog = new WindowsFormsApplication1.ErrorLog();
                    IniErrLog.WriteLog("ClassIni 创建Ini文件失败(" + FileName + "):" + ex.Message);
                }
            }
        }
        //写INI文件
        public void WriteString(string Section, string Ident, string Value)
        {
            if (!WritePrivateProfileString(Section, Ident, Value, FileName))
            {

                throw (new ApplicationException("写Ini文件出错"));
            }
        }
        //读取INI文件指定
        public string ReadString(string Section, string Ident, string Default)
        {
            Byte[] Buffer = new Byte[65535];
            int bufLen = GetPrivateProfileString(Section, Ident, Default, Buffer, Buffer.Length, FileName);
            //必须设定0（系统默认的代码页）的编码方式，否则无法支持中文
            string s = Encoding.GetEncoding(0).GetString(Buffer);
            s = s.Substring(0, bufLen);
            return s.Trim();
        }

        //读整数
        public int ReadInteger(string Section, string Ident, int Default)
        {
            string intStr = ReadString(Section, Ident, Convert.ToString(Default));
            try
            {
                return Convert.ToInt32(intStr);

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Default;
            }
        }

        //写整数
        public void WriteInteger(string Section, string Ident, int Value)
        {
            WriteString(Section, Ident, Value.ToString());
        }

        //读整数
        public double ReadDouble(string Section, string Ident, double Default)
        {
            string intStr = ReadString(Section, Ident, Convert.ToString(Default));
            try
            {
                return Convert.ToDouble(intStr);

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Default;
            }
        }

        //写双精度
        public void WriteDouble(string Section, string Ident, double Value)
        {
            WriteString(Section, Ident, Value.ToString());
        }
        //读布尔
        public bool ReadBool(string Section, string Ident, bool Default)
        {
            try
            {
                return Convert.ToBoolean(ReadString(Section, Ident, Convert.ToString(Default)));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Default;
            }
        }

        //写Bool
        public void WriteBool(string Section, string Ident, bool Value)
        {
            WriteString(Section, Ident, Convert.ToString(Value));
        }

        //从Ini文件中，将指定的Section名称中的所有Ident添加到列表中
        public void ReadSection(string Section, StringCollection Idents)
        {
            Byte[] Buffer = new Byte[16384];
            //Idents.Clear();

            int bufLen = GetPrivateProfileString(Section, null, null, Buffer, Buffer.Length,
             FileName);
            //对Section进行解析
            GetStringsFromBuffer(Buffer, bufLen, Idents);
        }

        private void GetStringsFromBuffer(Byte[] Buffer, int bufLen, StringCollection Strings)
        {
            Strings.Clear();
            if (bufLen != 0)
            {
                int start = 0;
                for (int i = 0; i < bufLen; i++)
                {
                    if ((Buffer[i] == 0) && ((i - start) > 0))
                    {
                        String s = Encoding.GetEncoding(0).GetString(Buffer, start, i - start);
                        Strings.Add(s);
                        start = i + 1;
                    }
                }
            }
        }
        //从Ini文件中，读取所有的Sections的名称
        public void ReadSections(StringCollection SectionList)
        {
            //Note:必须得用Bytes来实现，StringBuilder只能取到第一个Section
            byte[] Buffer = new byte[65535];
            int bufLen = 0;
            bufLen = GetPrivateProfileString(null, null, null, Buffer,
             Buffer.Length, FileName);
            GetStringsFromBuffer(Buffer, bufLen, SectionList);
        }
        //读取指定的Section的所有Value到列表中
        public void ReadSectionValues(string Section, NameValueCollection Values)
        {
            StringCollection KeyList = new StringCollection();
            ReadSection(Section, KeyList);
            Values.Clear();
            foreach (string key in KeyList)
            {
                Values.Add(key, ReadString(Section, key, ""));

            }
        }


        //清除某个Section
        public void EraseSection(string Section)
        {
            //
            if (!WritePrivateProfileString(Section, null, null, FileName))
            {

                throw (new ApplicationException("无法清除Ini文件中的Section"));
            }
        }
        //删除某个Section下的键
        public void DeleteKey(string Section, string Ident)
        {
            WritePrivateProfileString(Section, Ident, null, FileName);
        }
        //Note:对于Win9X，来说需要实现UpdateFile方法将缓冲中的数据写入文件
        //在Win NT, 2000和XP上，都是直接写文件，没有缓冲，所以，无须实现UpdateFile
        //执行完对Ini文件的修改之后，应该调用本方法更新缓冲区。
        public void UpdateFile()
        {
            WritePrivateProfileString(null, null, null, FileName);
        }

        //检查某个Section下的某个键值是否存在
        public bool ValueExists(string Section, string Ident)
        {
            //
            StringCollection Idents = new StringCollection();
            ReadSection(Section, Idents);
            return Idents.IndexOf(Ident) > -1;
        }
        //确保资源的释放
        ~ClassIni()
        {
            UpdateFile();
        }
    }
}
