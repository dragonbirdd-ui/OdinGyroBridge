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
    public static extern uint vigem_target_add(IntPtr client, IntPtr target);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_target_remove(IntPtr client, IntPtr target);

    [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint vigem_target_ds4_update_ex(
        IntPtr client, IntPtr target, DS4_REPORT_EX report);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DS4_REPORT_EX
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)]
        public byte[] Report;

        public static DS4_REPORT_EX Create()
        {
            return new DS4_REPORT_EX { Report = new byte[63] };
        }
    }
}

internal sealed class Bridge : IDisposable
{
    const int Port = 26760;
    const uint Version = 1001;
    const uint MsgVersion = 0x00100000;
    const uint MsgPorts = 0x00100001;
    const uint MsgPad = 0x00100002;

    readonly UdpClient udp = new UdpClient();
    readonly IPEndPoint portal;
    readonly IntPtr client;
    readonly IntPtr target;
    readonly uint id = (uint)Random.Shared.Next(1, int.MaxValue);

    byte[] previousPacket = null;

    long lastStatus = 0;
    long lastDebug = 0;

    public Bridge(string ip)
    {
        portal = new IPEndPoint(IPAddress.Parse(ip), Port);

        client = Vigem.vigem_alloc();

        if (client == IntPtr.Zero)
            throw new Exception("ViGEmClient.dll: vigem_alloc failed.");

        var e = Vigem.vigem_connect(client);

        if (e != Vigem.Success)
            throw new Exception("ViGEm connect failed: 0x" + e.ToString("X8"));

        target = Vigem.vigem_target_ds4_alloc();

        if (target == IntPtr.Zero)
            throw new Exception("vigem_target_ds4_alloc failed.");

        e = Vigem.vigem_target_add(client, target);

        if (e != Vigem.Success)
            throw new Exception("ViGEm target_add failed: 0x" + e.ToString("X8"));
    }

    public void Run()
    {
        Console.WriteLine("Odin 2 Portal DSU -> Virtual DS4 bridge v5 DEBUG");
        Console.WriteLine();
        Console.WriteLine("Portal DSU: " + portal.Address + ":" + Port);
        Console.WriteLine("Virtual DualShock 4: READY");
        Console.WriteLine("Watching DSU bytes 20-99...");
        Console.WriteLine();
        Console.WriteLine("TEST:");
        Console.WriteLine("Keep Odin still for 5 seconds.");
        Console.WriteLine("Then move/rotate Odin strongly.");
        Console.WriteLine();

        Send(MsgVersion, new byte[] { 0xE9, 0x03 });
        Send(MsgPorts, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 });
        Send(MsgPad, new byte[8]);

        while (true)
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] p = udp.Receive(ref remote);

            if (p.Length < 100)
                continue;

            if (p[0] != 'D' || p[1] != 'S' || p[2] != 'U' || p[3] != 'S')
                continue;

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(
                p.AsSpan(16, 4));

            if (type != MsgPad)
                continue;

            if (p[21] != 2)
                continue;

            if (previousPacket != null &&
                Environment.TickCount64 - lastDebug > 500)
            {
                PrintChangedBytes(previousPacket, p);
                lastDebug = Environment.TickCount64;
            }

            previousPacket = (byte[])p.Clone();

            byte b1 = p[36];
            byte b2 = p[37];
            byte home = p[38];
            byte touch = p[39];

            byte lx = p[40];
            byte ly = p[41];
            byte rx = p[42];
            byte ry = p[43];

            float ax = BitConverter.ToSingle(p, 76);
            float ay = BitConverter.ToSingle(p, 80);
            float az = BitConverter.ToSingle(p, 84);

            float gx = BitConverter.ToSingle(p, 88);
            float gy = BitConverter.ToSingle(p, 92);
            float gz = BitConverter.ToSingle(p, 96);

            var r = Vigem.DS4_REPORT_EX.Create();
            byte[] q = r.Report;

            q[0] = lx;
            q[1] = ly;
            q[2] = rx;
            q[3] = ry;

            bool left = (b1 & 0x80) != 0;
            bool down = (b1 & 0x40) != 0;
            bool right = (b1 & 0x20) != 0;
            bool up = (b1 & 0x10) != 0;

            int d = 8;

            if (up && right)
                d = 1;
            else if (right && down)
                d = 3;
            else if (down && left)
                d = 5;
            else if (left && up)
                d = 7;
            else if (up)
                d = 0;
            else if (right)
                d = 2;
            else if (down)
                d = 4;
            else if (left)
                d = 6;

            ushort buttons = (ushort)d;

            if ((b2 & 0x01) != 0) buttons |= 0x0080;
            if ((b2 & 0x02) != 0) buttons |= 0x0040;
            if ((b2 & 0x04) != 0) buttons |= 0x0020;
            if ((b2 & 0x08) != 0) buttons |= 0x0010;
            if ((b2 & 0x10) != 0) buttons |= 0x0200;
            if ((b2 & 0x20) != 0) buttons |= 0x0100;
            if ((b2 & 0x40) != 0) buttons |= 0x0800;
            if ((b2 & 0x80) != 0) buttons |= 0x0400;

            BinaryPrimitives.WriteUInt16LittleEndian(
                q.AsSpan(4, 2), buttons);

            byte special = 0;

            if ((b1 & 0x02) != 0) special |= 0x02;
            if ((b1 & 0x04) != 0) special |= 0x04;
            if ((b1 & 0x01) != 0) special |= 0x10;
            if ((b1 & 0x08) != 0) special |= 0x20;
            if (home != 0) special |= 0x01;
            if (touch != 0) special |= 0x08;

            q[6] = special;
            q[7] = p[35];
            q[8] = p[34];

            ushort ts = (ushort)(Environment.TickCount & 0xFFFF);

            BinaryPrimitives.WriteUInt16LittleEndian(
                q.AsSpan(9, 2), ts);

            q[11] = 0x1B;

            short rawGx = ToI16(gx * 16.0);
            short rawGy = ToI16(gy * 16.0);
            short rawGz = ToI16(gz * 16.0);

            short rawAx = ToI16(ax * 8192.0);
            short rawAy = ToI16(ay * 8192.0);
            short rawAz = ToI16(az * 8192.0);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(12, 2), rawGx);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(14, 2), rawGy);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(16, 2), rawGz);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(18, 2), rawAx);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(20, 2), rawAy);

            BinaryPrimitives.WriteInt16LittleEndian(
                q.AsSpan(22, 2), rawAz);

            q[32] = 1;
            q[33] = (byte)(Environment.TickCount & 0xFF);
            q[34] = 0x80;
            q[38] = 0x80;

            var err = Vigem.vigem_target_ds4_update_ex(
                client, target, r);

            if (err != Vigem.Success)
                throw new Exception(
                    "ViGEm update failed: 0x" + err.ToString("X8"));

            if (Environment.TickCount64 - lastStatus > 1000)
            {
                Console.WriteLine(
                    "GYRO@88 " +
                    gx.ToString("F2") + " " +
                    gy.ToString("F2") + " " +
                    gz.ToString("F2") +
                    " | RAW " +
                    rawGx + " " +
                    rawGy + " " +
                    rawGz +
                    " | ViGEm OK");

                lastStatus = Environment.TickCount64;
            }
        }
    }

    static void PrintChangedBytes(byte[] oldPacket, byte[] newPacket)
    {
        int end = Math.Min(
            99,
            Math.Min(oldPacket.Length - 1, newPacket.Length - 1));

        Console.Write("CHANGED: ");

        bool any = false;

        for (int i = 20; i <= end; i++)
        {
            if (oldPacket[i] != newPacket[i])
            {
                Console.Write(
                    i +
                    ":" +
                    oldPacket[i].ToString("X2") +
                    ">" +
                    newPacket[i].ToString("X2") +
                    " ");

                any = true;
            }
        }

        if (!any)
            Console.Write("NONE");

        Console.WriteLine();

        Console.Write("HEX60-99: ");

        for (int i = 60; i <= end; i++)
        {
            Console.Write(newPacket[i].ToString("X2"));
            Console.Write(" ");
        }

        Console.WriteLine();
    }

    static short ToI16(double x)
    {
        return (short)Math.Clamp(
            Math.Round(x),
            short.MinValue,
            short.MaxValue);
    }

    void Send(uint type, byte[] payload)
    {
        byte[] p = new byte[20 + payload.Length];

        p[0] = (byte)'D';
        p[1] = (byte)'S';
        p[2] = (byte)'U';
        p[3] = (byte)'C';

        BinaryPrimitives.WriteUInt16LittleEndian(
            p.AsSpan(4, 2),
            (ushort)Version);

        BinaryPrimitives.WriteUInt16LittleEndian(
            p.AsSpan(6, 2),
            (ushort)(4 + payload.Length));

        BinaryPrimitives.WriteUInt32LittleEndian(
            p.AsSpan(8, 4),
            0);

        BinaryPrimitives.WriteUInt32LittleEndian(
            p.AsSpan(12, 4),
            id);

        BinaryPrimitives.WriteUInt32LittleEndian(
            p.AsSpan(16, 4),
            type);

        payload.CopyTo(p, 20);

        BinaryPrimitives.WriteUInt32LittleEndian(
            p.AsSpan(8, 4),
            Crc32(p));

        udp.Send(p, p.Length, portal);
    }

    static uint Crc32(byte[] b)
    {
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < b.Length; i++)
        {
            byte x =
                (i >= 8 && i < 12)
                ? (byte)0
                : b[i];

            crc ^= x;

            for (int j = 0; j < 8; j++)
            {
                crc =
                    (crc >> 1) ^
                    (0xEDB88320u &
                    (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    public void Dispose()
    {
        try
        {
            if (target != IntPtr.Zero)
                Vigem.vigem_target_remove(client, target);
        }
        catch { }

        try
        {
            if (target != IntPtr.Zero)
                Vigem.vigem_target_free(target);
        }
        catch { }

        try
        {
            if (client != IntPtr.Zero)
                Vigem.vigem_disconnect(client);
        }
        catch { }

        try
        {
            if (client != IntPtr.Zero)
                Vigem.vigem_free(client);
        }
        catch { }

        udp.Dispose();
    }
}

static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine(
            "Odin 2 Portal DSU -> Virtual DS4 bridge v5 DEBUG");

        Console.WriteLine();

        string ip =
            args.Length > 0
            ? args[0]
            : "";

        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.Write(
                "Portal IP (e.g. 192.168.1.108): ");

            ip = Console.ReadLine() ?? "";
        }

        try
        {
            using var b = new Bridge(ip.Trim());
            b.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
