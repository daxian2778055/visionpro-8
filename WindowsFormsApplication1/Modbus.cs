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
        // ch:R40 H4 串口短临界区：frm3.mdcan 同时被检测线程（数据路寄存器写）与 Form3 原始帧写调用，
        //   同一 SerialPort 的"写请求+读响应"事务必须原子；Form3 的直接 port.Write 复用本锁。
        public readonly object PortLock = new object();
        ErrorLog MsgErroeLog = new ErrorLog();
        // ch:P1-⑤ 响应判定改为按发送站号匹配，消除站号硬编码 0x01 的地雷（默认 0x01 兼容既有 station-1 现场）
        // ch:P1-⑥ 增加读取超时（2s），避免 PLC 无响应时 while 永久阻塞挂死调用线程
        // ch:修复：原 buffer1 = buffer 为引用别名（非拷贝，buffer1 与 buffer 同体），且轮询内层判定仍硬编码 0x01；
        //   现统一直接使用 buffer、内层同样按 station 匹配，并对短帧/空读做保护，避免用残留数据误判。
        // ch:R42 错帧轮询窗口 500ms（原 2000ms）：本循环在 PortLock 内，一台不回/坏线的站在旧窗口下
        //   会把所有串口写拖住 2s+；500ms 与读超时(ReadTimeout=500)同级，健康设备响应通常几十 ms 内到。
        private const int MismatchPollWindowMs = 500;

        protected int ReadData(byte[] buffer, out string b, int expectCount, byte station = 0x01)
        {
            b = "";
            if (buffer == null || expectCount < 4 || buffer.Length < expectCount)
                return 0;
            try
            {
                // ch:R43 读超时必须为有限值（SerialPort 默认 -1 无限），否则一台不回的站会在 PortLock 内永久阻塞串口事务
                if (port.ReadTimeout <= 0)
                {
                    try { port.ReadTimeout = 500; } catch { }
                }
                // ch:R43 只读取一帧：粘包时切出第一帧，剩余字节留在串口缓冲由下一次事务读取；
                //   短读在窗口内拼满再校验；写回执(FC06/FC10)同样先过 CRC 才返回成功
                int frameLen = ReadOneFrame(buffer, buffer.Length);
                if (frameLen <= 0)
                {
                    try { port.DiscardInBuffer(); } catch { } // 半截/未知帧无法定界，丢弃以恢复同步
                    return 0;
                }
                if (buffer[0] != station)
                {
                    LogFrameMismatchThrottled(station, frameLen, buffer[1], "站号不符");
                    return 0; // 该帧已完整消费，下一帧留给下一次事务
                }
                if ((buffer[1] & 0x80) != 0)
                {
                    LogFrameMismatchThrottled(station, frameLen, buffer[1], "从站异常响应");
                    return 0;
                }
                if (expectCount == 8)
                {
                    // 写寄存器回执也是 8 字节：长度 + 功能码 + CRC 全部通过才返回成功
                    if (frameLen == 8 && (buffer[1] == 0x06 || buffer[1] == 0x10) && Crc16Ok(buffer, 8))
                        return 8;
                    LogCrcFailThrottled(station);
                    return 0;
                }
                if (frameLen != expectCount || buffer[1] != 0x03)
                {
                    LogFrameMismatchThrottled(station, frameLen, buffer[1], "功能码/长度不符");
                    return 0;
                }
                if (!Crc16Ok(buffer, frameLen))
                {
                    LogCrcFailThrottled(station);
                    return 0;
                }
                // 仅解析数据区（跳过站号/功能码/字节数/CRC），与原首帧成功路径口径一致
                for (int i = 3; i <= frameLen - 3; i++)
                    b += buffer[i].ToString("X2") + " ";
                return frameLen;
            }
            catch (Exception ex)
            {
                b = "";
                MsgErroeLog.WriteLog(ex.Message);
                try { port.DiscardInBuffer(); } catch { }
                return 0;
            }
        }

        // ch:R43 读取恰好 count 字节；超时/短读返回 false（调用方丢弃半截帧）
        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            int got = 0;
            int start = Environment.TickCount;
            while (got < count)
            {
                if (unchecked(Environment.TickCount - start) > MismatchPollWindowMs)
                    return false;
                int n = port.Read(buffer, offset + got, count - got);
                if (n <= 0)
                    continue;
                got += n;
            }
            return true;
        }

        // ch:R43 按功能码读出完整一帧（绝不预读下一帧）：FC03 先读字节数再读数据；FC06/FC10=8；异常响应=5。
        //   未知功能码无法定界，丢弃输入缓冲并返回 -1，由下一次事务重新同步。
        private int ReadOneFrame(byte[] buffer, int maxLen)
        {
            if (maxLen < 5 || !ReadExact(buffer, 0, 2))
                return -1;
            byte func = buffer[1];
            int total;
            if ((func & 0x80) != 0)
                total = 5;
            else if (func == 0x03)
            {
                if (!ReadExact(buffer, 2, 1))
                    return -1;
                total = 3 + buffer[2] + 2;
            }
            else if (func == 0x06 || func == 0x10)
                total = 8;
            else
                return -1;
            if (total < 5 || total > maxLen)
                return -1;
            if (!ReadExact(buffer, 3, total - 3))
                return -1;
            return total;
        }
        // ch:R42 响应帧 CRC 校验：复用 ModbusCrc.Append（单测直链回归的那份实现，帧尾 [低,高]）。
        //   仅对长度完整(r==expectCount)的 FC03 响应调用；不通过则帧被丢弃，坏线噪点不再被当成寄存器数据。
        private static bool Crc16Ok(byte[] frame, int len)
        {
            if (frame == null || len < 4)
                return false;
            byte[] body = new byte[len - 2];
            Array.Copy(frame, body, len - 2);
            byte[] withCrc = ModbusCrc.Append(body);
            return withCrc[len - 2] == frame[len - 2] && withCrc[len - 1] == frame[len - 1];
        }
        // ch:R42 CRC 失败日志 10s 限流：坏线况下每次收包都可能失败，避免日志被刷爆
        private static int _lastCrcFailLogTick = 0;
        private void LogCrcFailThrottled(byte station)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastCrcFailLogTick) < 10000)
                return;
            _lastCrcFailLogTick = now;
            MsgErroeLog.WriteLog("Modbus 响应 CRC 校验失败(站号" + station.ToString("X2") + ")：帧已丢弃，继续等待下一帧");
        }
        // ch:R43 帧不匹配日志（站号/功能码/长度），与 CRC 日志共用 10s 限流
        private void LogFrameMismatchThrottled(byte station, int len, byte func, string reason)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastCrcFailLogTick) < 10000)
                return;
            _lastCrcFailLogTick = now;
            MsgErroeLog.WriteLog("Modbus 响应帧不匹配(期望站号0x" + station.ToString("X2")
                + " len=" + len + " func=0x" + func.ToString("X2") + " 原因=" + reason
                + ")：该帧已完整消费，剩余字节保留给下次事务");
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
            lock (PortLock) // ch:R40 H4 串口短临界区：写请求+读响应为一个事务，与其他串口调用串行
            {
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
                // ch:R41 去掉锁内固定 200ms 睡眠：port.Read 本身会阻塞到有响应或 ReadTimeout 到期（Form3 配 500ms），
                //   睡眠只是锁内叠加延迟；一台不回的站以前每次事务 ≥700ms，现在上界≈读超时，不再长时间拖住所有串口写
                int bytes = ReadData(buffer, out b, 5 + 2 * length, (byte)stationID); 
            return bytes;
            } // lock (PortLock)

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
            lock (PortLock) // ch:R40 H4 串口短临界区（写请求+读响应事务原子）
            {
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
            lock (PortLock) // ch:R40 H4 串口短临界区（写请求+读响应事务原子）
            {
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
