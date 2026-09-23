using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HslCommunication.ModBus;
using HslCommunication;
using System.Threading;

namespace WindowsFormsApplication1
{
    public class Modbus
    {
        public SerialPort port;
        ErrorLog MsgErroeLog = new ErrorLog();
        // ch:P1-⑤ 响应判定改为按发送站号匹配，消除站号硬编码 0x01 的地雷（默认 0x01 兼容既有 station-1 现场）
        // ch:P1-⑥ 增加读取超时（2s），避免 PLC 无响应时 while 永久阻塞挂死调用线程
        // ch:修复：原 buffer1 = buffer 为引用别名（非拷贝，buffer1 与 buffer 同体），且轮询内层判定仍硬编码 0x01；
        //   现统一直接使用 buffer、内层同样按 station 匹配，并对短帧/空读做保护，避免用残留数据误判。
        protected int ReadData(byte[] buffer, out string b, int expectCount, byte station = 0x01)
        {
            try
            {
                b = "";
                if (buffer == null || buffer.Length < 2)
                    return 0;
                int r = port.Read(buffer, 0, buffer.Length);
                if (expectCount == 8)
                {
                    // 写寄存器回执：不解析数据，仅清空输入缓冲
                    port.DiscardInBuffer();
                    return r;
                }
                if (buffer[0] == station && buffer[1] == 0X03)
                {
                    // ch:P1-10 按本次实读长度 r 解析，不再按 buffer.Length（避免读到短帧/残留时把无效字节拼进 b）
                    for (int i = 0; i < r; i++)
                    {
                        if (i >= 3 && i <= r - 3)
                            b += buffer[i].ToString("X2") + " ";
                    }
                    port.DiscardInBuffer();
                    return r;
                }
                // 首帧不匹配：轮询等待直到匹配（按 station）或超时
                // ch:P2 改差值式超时：原 long deadline = TickCount + 2000L; if (TickCount > deadline) 在 TickCount 回绕
                //   （int.MaxValue→int.MinValue）的瞬间会永远不成立 → 死等约 24.9 天。差值式在 24.9 天窗口内恒正确。
                int waitStart = Environment.TickCount;
                while (buffer[0] != station || buffer[1] != 0X03)
                {
                    if (unchecked(Environment.TickCount - waitStart) > 2000)
                    {
                        MsgErroeLog.WriteLog("Modbus 读响应超时(站号" + station.ToString("X2") + ")");
                        break;
                    }
                    int n = port.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                        continue; // 未读到数据：不用残留内容判定，继续等待
                    r = n;
                    if (buffer[0] == station && buffer[1] == 0X03)
                    {
                        // ch:P1-10 按本次实读 n 解析（原 buffer.Length 会读入未更新/残留字节）
                        for (int i = 0; i < n; i++)
                        {
                            b += buffer[i].ToString("X2") + " ";
                        }
                        port.DiscardInBuffer();
                        break;
                    }
                }
                return r;
            }
            catch(Exception ex)
            {
                b = "";
                MsgErroeLog.WriteLog(ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// 读取保持型寄存器 功能码03
        /// </summary>
        /// <param name="stationID">站号</param>
        /// <param name="addr">寄存器地址</param>
        /// <param name="length">寄存器数量</param>
        /// <param name="buffer">接收数据的缓存</param>
        /// <returns>收到的字节数</returns>
        public int ReadRegister(int stationID, ushort addr, int length,out string a,out string b, byte[] buffer)
        {
            a = "";
            b = "";
            try
            {
                //OpenPort();
                byte[] cmdHead = new byte[6];
                cmdHead[0] = (byte)stationID;
                cmdHead[1] = 0x03;
                cmdHead[2] = (byte)((addr >> 8) & 0xFF);
                cmdHead[3] = (byte)(addr & 0xFF);
                cmdHead[4] = (byte)((length >> 8) & 0xFF);
                cmdHead[5] = (byte)(length & 0xFF);
                byte[] command = GetCRC16(cmdHead);
                for (int i = 0; i < command.Length; i++)
                {
                    a += command[i].ToString("X2") + " ";
                }
                //发送指令
                port.Write(command, 0, command.Length);
            }
            catch(Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message);
            }
              Thread.Sleep(200);
                int bytes = ReadData(buffer, out b, 5 + 2 * length, (byte)stationID); 
            return bytes;

        }


        /// <summary>
        /// 写入保持型寄存器 功能码06
        /// </summary>
        /// <param name="stationID">站号</param>
        /// <param name="addr">寄存器地址</param>
        /// <param name="value">写入值</param>
        /// <returns></returns>
        public int WriteRegister(int stationID, ushort addr, int value,out string a, byte[] buffer = null)
        {
            //OpenPort();
            a = "";
            string b="";
            try
            {
                byte[] cmdHead = new byte[6];
                cmdHead[0] = (byte)stationID;
                cmdHead[1] = 0x06;
                cmdHead[2] = (byte)((addr >> 8) & 0xFF);
                cmdHead[3] = (byte)(addr & 0xFF);
                cmdHead[4] = (byte)((value >> 8) & 0xFF);
                cmdHead[5] = (byte)(value & 0xFF);
                byte[] command = GetCRC16(cmdHead);
                for (int i = 0; i < command.Length; i++)
                {
                    a += command[i].ToString("X2") + " ";
                }
                //发送指令。
                port.Write(command, 0, command.Length);
                int bytes = ReadData(command, out b, 8, (byte)stationID);
                //  int bytes = 8;
                return bytes;
            }
            catch(Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message);
            }
               
            return 0;
        }

        /// <summary>
        /// 写入一段连续的寄存器 功能码10
        /// </summary>
        /// <param name="stationID">站号</param>
        /// <param name="addr">寄存器地址</param>
        /// <param name="value">写入值</param>
        /// <returns></returns>
        public int WriteRegisters(int stationID, ushort addr, byte[] value,out string a, byte[] buffer = null)
        {
            a = "";
            string b = "";
            // OpenPort();
            try
            {
                int registerCount = value.Length / 2;
                byte[] cmdHead = new byte[7 + value.Length];
                cmdHead[0] = (byte)stationID;            
                cmdHead[1] = 0x10;
                cmdHead[2] = (byte)((addr >> 8) & 0xFF);
                cmdHead[3] = (byte)(addr & 0xFF);
                cmdHead[4] = (byte)((registerCount >> 8) & 0xFF);
                cmdHead[5] = (byte)(registerCount & 0xFF);
                cmdHead[6] = (byte)(value.Length & 0xFF);
                for (int i = 0; i < value.Length; i++)
                {
                    cmdHead[7 + i] = value[i];
                }
                byte[] command = GetCRC16(cmdHead);
                for (int i = 0; i < command.Length; i++)
                {
                    a += command[i].ToString("X2") + " ";
                }
                //发送指令。
                port.Write(command, 0, command.Length);
                int bytes = ReadData(command, out b, 8, (byte)stationID);
                //  int bytes = 8;
                return bytes;
            }
            catch(Exception ex)
            {
                MsgErroeLog.WriteLog(ex.Message);
            }
            return 0;
        }
        /// <summary>
        /// CRC校验，参数data为byte数组
        /// </summary>
        /// <param name="data">校验数据，字节数组</param>
        /// <returns>原数据 + 追加的两个校验字节，帧尾顺序为 [低8位, 高8位]</returns>
        public static byte[] GetCRC16(byte[] data)
        {
            // ch:R14 算法本体抽到 Core\ModbusCrc.cs：Tests\ModbusCrcTests 直接链接同一份源文件做回归，
            //   测的是生产真代码而非"逐行复刻"(复刻版会与生产悄悄分叉)。签名不变，本文件 3 个调用点无需改动。
            //   注：原 returns 注释写"字节0是高8位，字节1是低8位"，与实现相反(实现追加顺序为 [低, 高])，一并更正。
            return ModbusCrc.Append(data);
        }
    }
}
