using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

internal static class Vigem
{
    public const uint Success = 0x20000000;

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vigem_alloc();

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void vigem_free(IntPtr client);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_connect(IntPtr client);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void vigem_disconnect(IntPtr client);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vigem_target_ds4_alloc();

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void vigem_target_free(IntPtr target);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_target_add(
        IntPtr client,
        IntPtr target);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_target_remove(
        IntPtr client,
        IntPtr target);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_target_ds4_update_ex(
        IntPtr client,
        IntPtr target,
        DS4_REPORT_EX report);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DS4_TOUCH
    {
        public byte bPacketCounter;
        public byte bIsUpTrackingNum1;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] bTouchData1;

        public byte bIsUpTrackingNum2;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] bTouchData2;

        public static DS4_TOUCH Empty()
        {
            return new DS4_TOUCH
            {
                bPacketCounter = 0,
                bIsUpTrackingNum1 = 0x80,
                bTouchData1 = new byte[3],
                bIsUpTrackingNum2 = 0x80,
                bTouchData2 = new byte[3]
            };
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DS4_REPORT_EX
    {
        public byte bThumbLX;
        public byte bThumbLY;
        public byte bThumbRX;
        public byte bThumbRY;

        public ushort wButtons;

        public byte bSpecial;
        public byte bTriggerL;
        public byte bTriggerR;

        public ushort wTimestamp;

        public byte bBatteryLvl;

        public short wGyroX;
        public short wGyroY;
        public short wGyroZ;

        public short wAccelX;
        public short wAccelY;
        public short wAccelZ;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] Unknown1;

        public byte bBatteryLvlSpecial;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] Unknown2;

        public byte bTouchPacketsN;

        public DS4_TOUCH sCurrentTouch;
        public DS4_TOUCH sPreviousTouch1;
        public DS4_TOUCH sPreviousTouch2;

        public static DS4_REPORT_EX Create()
        {
            return new DS4_REPORT_EX
            {
                bThumbLX = 128,
                bThumbLY = 128,
                bThumbRX = 128,
                bThumbRY = 128,

                wButtons = 8,

                bBatteryLvl = 0xFF,

                Unknown1 = new byte[5],

                bBatteryLvlSpecial = 0x1A,

                Unknown2 = new byte[2],

                bTouchPacketsN = 1,

                sCurrentTouch = DS4_TOUCH.Empty(),
                sPreviousTouch1 = DS4_TOUCH.Empty(),
                sPreviousTouch2 = DS4_TOUCH.Empty()
            };
        }
    }
}

internal sealed class Bridge : IDisposable
{
    const int Port = 26760;

    const uint Version = 1001;

    const uint MsgVersion = 0x00100000;
    const uint MsgPorts   = 0x00100001;
    const uint MsgPad     = 0x00100002;

    readonly UdpClient udp = new();

    readonly IPEndPoint portal;

    readonly IntPtr client;
    readonly IntPtr target;

    readonly uint id =
        (uint)Random.Shared.Next(1, int.MaxValue);

    ushort timestamp;

    long lastPrint;
    long lastSubscribe;

    public Bridge(string ip)
    {
        portal =
            new IPEndPoint(
                IPAddress.Parse(ip),
                Port);

        client = Vigem.vigem_alloc();

        if (client == IntPtr.Zero)
            throw new Exception(
                "vigem_alloc failed");

        uint error =
            Vigem.vigem_connect(client);

        if (error != Vigem.Success)
            throw new Exception(
                "ViGEm connect failed: 0x" +
                error.ToString("X8"));

        target =
            Vigem.vigem_target_ds4_alloc();

        if (target == IntPtr.Zero)
            throw new Exception(
                "DS4 allocation failed");

        error =
            Vigem.vigem_target_add(
                client,
                target);

        if (error != Vigem.Success)
            throw new Exception(
                "DS4 target_add failed: 0x" +
                error.ToString("X8"));
    }

    public void Run()
    {
        Console.WriteLine(
            "==============================================");

        Console.WriteLine(
            " ODIN DSU -> VIRTUAL DS4 MOTION BRIDGE v10");

        Console.WriteLine(
            "==============================================");

        Console.WriteLine();

        Console.WriteLine(
            "DSU source : " +
            portal.Address +
            ":" +
            Port);

        Console.WriteLine(
            "Virtual DS4: READY");

        Console.WriteLine();

        Console.WriteLine(
            "DSU layout:");

        Console.WriteLine(
            "Accel = Float32 @ 76 / 80 / 84");

        Console.WriteLine(
            "Gyro  = Float32 @ 88 / 92 / 96");

        Console.WriteLine();

        Console.WriteLine(
            "Move Odin. Values below MUST change.");

        Console.WriteLine();

        Subscribe();

        while (true)
        {
            if (Environment.TickCount64 -
                lastSubscribe > 1000)
            {
                Send(MsgPad, new byte[8]);

                lastSubscribe =
                    Environment.TickCount64;
            }

            IPEndPoint remote =
                new IPEndPoint(
                    IPAddress.Any,
                    0);

            byte[] p =
                udp.Receive(ref remote);

            if (p.Length < 100)
                continue;

            if (p[0] != (byte)'D' ||
                p[1] != (byte)'S' ||
                p[2] != (byte)'U' ||
                p[3] != (byte)'S')
                continue;

            uint type =
                BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        p.AsSpan(16, 4));

            if (type != MsgPad)
                continue;

            if (p[21] != 2)
                continue;

            float ax =
                BitConverter.ToSingle(p, 76);

            float ay =
                BitConverter.ToSingle(p, 80);

            float az =
                BitConverter.ToSingle(p, 84);

            float gx =
                BitConverter.ToSingle(p, 88);

            float gy =
                BitConverter.ToSingle(p, 92);

            float gz =
                BitConverter.ToSingle(p, 96);

            var report =
                Vigem.DS4_REPORT_EX.Create();

            // ------------------------------------------------
            // Controller state
            // ------------------------------------------------

            report.bThumbLX = p[40];
            report.bThumbLY = p[41];
            report.bThumbRX = p[42];
            report.bThumbRY = p[43];

            CopyButtons(p, ref report);

            // ------------------------------------------------
            // DS4 timestamp
            //
            // DS4 commonly updates around 800 Hz.
            // Incrementing continuously is important for
            // motion consumers.
            // ------------------------------------------------

            timestamp += 188;

            report.wTimestamp =
                timestamp;

            // ------------------------------------------------
            // MOTION
            //
            // AndroidDSU gyro = degrees/sec
            //
            // DS4:
            // 16 raw units = 1 degree/sec
            //
            // AndroidDSU accel values observed are in g.
            //
            // DS4:
            // 8192 raw units = 1 g
            // ------------------------------------------------

            report.wGyroX =
                ToShort(gx * 16.0);

            report.wGyroY =
                ToShort(gy * 16.0);

            report.wGyroZ =
                ToShort(gz * 16.0);

            report.wAccelX =
                ToShort(ax * 8192.0);

            report.wAccelY =
                ToShort(ay * 8192.0);

            report.wAccelZ =
                ToShort(az * 8192.0);

            uint error =
                Vigem.vigem_target_ds4_update_ex(
                    client,
                    target,
                    report);

            if (error != Vigem.Success)
            {
                throw new Exception(
                    "DS4 update failed: 0x" +
                    error.ToString("X8"));
            }

            if (Environment.TickCount64 -
                lastPrint >= 250)
            {
                Console.WriteLine(
                    $"GYRO dps " +
                    $"X={gx,8:F2} " +
                    $"Y={gy,8:F2} " +
                    $"Z={gz,8:F2}   |   " +
                    $"DS4 RAW " +
                    $"X={report.wGyroX,6} " +
                    $"Y={report.wGyroY,6} " +
                    $"Z={report.wGyroZ,6}");

                lastPrint =
                    Environment.TickCount64;
            }
        }
    }

    static void CopyButtons(
        byte[] p,
        ref Vigem.DS4_REPORT_EX report)
    {
        byte b1 = p[36];
        byte b2 = p[37];

        bool left =
            (b1 & 0x80) != 0;

        bool down =
            (b1 & 0x40) != 0;

        bool right =
            (b1 & 0x20) != 0;

        bool up =
            (b1 & 0x10) != 0;

        ushort dpad = 8;

        if (up && right)
            dpad = 1;
        else if (right && down)
            dpad = 3;
        else if (down && left)
            dpad = 5;
        else if (left && up)
            dpad = 7;
        else if (up)
            dpad = 0;
        else if (right)
            dpad = 2;
        else if (down)
            dpad = 4;
        else if (left)
            dpad = 6;

        ushort buttons = dpad;

        if ((b2 & 0x01) != 0)
            buttons |= 0x0080;

        if ((b2 & 0x02) != 0)
            buttons |= 0x0040;

        if ((b2 & 0x04) != 0)
            buttons |= 0x0020;

        if ((b2 & 0x08) != 0)
            buttons |= 0x0010;

        if ((b2 & 0x10) != 0)
            buttons |= 0x0200;

        if ((b2 & 0x20) != 0)
            buttons |= 0x0100;

        if ((b2 & 0x40) != 0)
            buttons |= 0x0800;

        if ((b2 & 0x80) != 0)
            buttons |= 0x0400;

        report.wButtons =
            buttons;

        byte special = 0;

        if (p[38] != 0)
            special |= 0x01;

        if (p[39] != 0)
            special |= 0x08;

        report.bSpecial =
            special;

        report.bTriggerL =
            p[35];

        report.bTriggerR =
            p[34];
    }

    static short ToShort(double value)
    {
        return (short)Math.Clamp(
            Math.Round(value),
            short.MinValue,
            short.MaxValue);
    }

    void Subscribe()
    {
        Send(
            MsgVersion,
            new byte[]
            {
                0xE9,
                0x03
            });

        Send(
            MsgPorts,
            new byte[]
            {
                0x01,
                0x00,
                0x00,
                0x00,
                0x00
            });

        Send(
            MsgPad,
            new byte[8]);

        lastSubscribe =
            Environment.TickCount64;
    }

    void Send(
        uint type,
        byte[] payload)
    {
        byte[] p =
            new byte[
                20 +
                payload.Length];

        p[0] = (byte)'D';
        p[1] = (byte)'S';
        p[2] = (byte)'U';
        p[3] = (byte)'C';

        BinaryPrimitives
            .WriteUInt16LittleEndian(
                p.AsSpan(4, 2),
                (ushort)Version);

        BinaryPrimitives
            .WriteUInt16LittleEndian(
                p.AsSpan(6, 2),
                (ushort)(
                    4 +
                    payload.Length));

        BinaryPrimitives
            .WriteUInt32LittleEndian(
                p.AsSpan(8, 4),
                0);

        BinaryPrimitives
            .WriteUInt32LittleEndian(
                p.AsSpan(12, 4),
                id);

        BinaryPrimitives
            .WriteUInt32LittleEndian(
                p.AsSpan(16, 4),
                type);

        payload.CopyTo(
            p,
            20);

        BinaryPrimitives
            .WriteUInt32LittleEndian(
                p.AsSpan(8, 4),
                Crc32(p));

        udp.Send(
            p,
            p.Length,
            portal);
    }

    static uint Crc32(byte[] b)
    {
        uint crc =
            0xFFFFFFFF;

        for (int i = 0;
             i < b.Length;
             i++)
        {
            byte x =
                (i >= 8 &&
                 i < 12)
                    ? (byte)0
                    : b[i];

            crc ^= x;

            for (int j = 0;
                 j < 8;
                 j++)
            {
                crc =
                    (crc >> 1) ^
                    (0xEDB88320u &
                    (uint)-(int)(
                        crc & 1));
            }
        }

        return ~crc;
    }

    public void Dispose()
    {
        try
        {
            if (target != IntPtr.Zero)
                Vigem.vigem_target_remove(
                    client,
                    target);
        }
        catch { }

        try
        {
            if (target != IntPtr.Zero)
                Vigem.vigem_target_free(
                    target);
        }
        catch { }

        try
        {
            if (client != IntPtr.Zero)
                Vigem.vigem_disconnect(
                    client);
        }
        catch { }

        try
        {
            if (client != IntPtr.Zero)
                Vigem.vigem_free(
                    client);
        }
        catch { }

        udp.Dispose();
    }
}

internal static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine(
            "Odin DSU -> Virtual DS4 Motion Bridge v10");

        Console.WriteLine();

        string ip =
            args.Length > 0
                ? args[0]
                : "";

        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.Write(
                "Portal IP (e.g. 192.168.1.108): ");

            ip =
                Console.ReadLine() ?? "";
        }

        try
        {
            using var bridge =
                new Bridge(ip.Trim());

            bridge.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR:");
            Console.WriteLine(ex);
            Console.WriteLine();
            Console.WriteLine(
                "Press ENTER to exit.");

            Console.ReadLine();
        }
    }
}
