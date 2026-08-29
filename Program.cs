
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
        IntPtr client, IntPtr target, ref DS4_REPORT_EX report);

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
        public byte unknown1_0;
        public byte unknown1_1;
        public byte unknown1_2;
        public byte unknown1_3;
        public byte unknown1_4;
        public byte bBatteryLvlSpecial;
        public byte unknown2_0;
        public byte unknown2_1;
        public byte bTouchPacketsN;

        // 10 bytes touch structures (3 + 1 + 3 + 1?).
        // We only need a correctly sized 63-byte report.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31)]
        public byte[] TouchAndTail;

        public static DS4_REPORT_EX Create()
        {
            return new DS4_REPORT_EX
            {
                TouchAndTail = new byte[31]
            };
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

    readonly UdpClient udp = new();
    readonly IPEndPoint portal;
    readonly IntPtr client;
    readonly IntPtr target;
    readonly uint id = (uint)Random.Shared.Next(1, int.MaxValue);

    public Bridge(string ip)
    {
        portal = new IPEndPoint(IPAddress.Parse(ip), Port);

        client = Vigem.vigem_alloc();
        if (client == IntPtr.Zero) throw new Exception("ViGEmClient.dll: vigem_alloc failed.");

        var e = Vigem.vigem_connect(client);
        if (e != Vigem.Success) throw new Exception($"ViGEm connect failed: 0x{e:X8}");

        target = Vigem.vigem_target_ds4_alloc();
        if (target == IntPtr.Zero) throw new Exception("vigem_target_ds4_alloc failed.");

        e = Vigem.vigem_target_add(client, target);
        if (e != Vigem.Success) throw new Exception($"ViGEm target_add failed: 0x{e:X8}");
    }

    public void Run()
    {
        Console.WriteLine($"Portal DSU: {portal.Address}:{Port}");
        Console.WriteLine("Virtual DualShock 4: READY");
        Console.WriteLine("Waiting for AndroidDSU packets...");
        Console.WriteLine();

        Send(MsgVersion, new byte[] { 0xE9, 0x03 });
        Send(MsgPorts, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 });
        Send(MsgPad, new byte[8]);

        long last = 0;
        while (true)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            byte[] p = udp.Receive(ref remote);

            if (p.Length < 100) continue;
            if (p[0] != 'D' || p[1] != 'S' || p[2] != 'U' || p[3] != 'S') continue;

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16, 4));
            if (type != MsgPad) continue;

            // DSU response layout, from the protocol specification:
            // absolute 20 = slot
            // 21 = connection state
            // 36 = buttons byte 1
            // 37 = buttons byte 2
            // 38 = HOME
            // 39 = touch
            // 40..43 = sticks
            // 76..87 = accel
            // 88..99 = gyro
            if (p[21] != 2) continue; // connected

            byte b1 = p[36], b2 = p[37];
            byte home = p[38], touch = p[39];
            byte lx = p[40], ly = p[41], rx = p[42], ry = p[43];

            float ax = BitConverter.ToSingle(p, 76);
            float ay = BitConverter.ToSingle(p, 80);
            float az = BitConverter.ToSingle(p, 84);
            float gx = BitConverter.ToSingle(p, 88);
            float gy = BitConverter.ToSingle(p, 92);
            float gz = BitConverter.ToSingle(p, 96);

            var r = Vigem.DS4_REPORT_EX.Create();
            r.bThumbLX = lx; r.bThumbLY = ly;
            r.bThumbRX = rx; r.bThumbRY = ry;

            // DS4 D-pad encoding is a nibble:
            // 0=N,1=NE,2=E,3=SE,4=S,5=SW,6=W,7=NW,8=neutral.
            bool left  = (b1 & 0x80) != 0;
            bool down  = (b1 & 0x40) != 0;
            bool right = (b1 & 0x20) != 0;
            bool up    = (b1 & 0x10) != 0;
            int d = 8;
            if (up && right) d = 1;
            else if (right && down) d = 3;
            else if (down && left) d = 5;
            else if (left && up) d = 7;
            else if (up) d = 0;
            else if (right) d = 2;
            else if (down) d = 4;
            else if (left) d = 6;

            ushort buttons = (ushort)d;

            // DSU byte 2: Y B A X R1 L1 R2 L2.
            if ((b2 & 0x01) != 0) buttons |= 0x0080; // Y -> Triangle
            if ((b2 & 0x02) != 0) buttons |= 0x0040; // B -> Circle
            if ((b2 & 0x04) != 0) buttons |= 0x0020; // A -> Cross
            if ((b2 & 0x08) != 0) buttons |= 0x0010; // X -> Square
            if ((b2 & 0x10) != 0) buttons |= 0x0200; // R1
            if ((b2 & 0x20) != 0) buttons |= 0x0100; // L1
            if ((b2 & 0x40) != 0) buttons |= 0x0800; // R2 digital
            if ((b2 & 0x80) != 0) buttons |= 0x0400; // L2 digital

            r.wButtons = buttons;

            // DS4 special: L3, R3, Share, Options, PS, Touchpad.
            byte special = 0;
            if ((b1 & 0x02) != 0) special |= 0x02; // L3
            if ((b1 & 0x04) != 0) special |= 0x04; // R3
            if ((b1 & 0x01) != 0) special |= 0x10; // Share
            if ((b1 & 0x08) != 0) special |= 0x20; // Options
            if (home != 0) special |= 0x01;         // PS
            if (touch != 0) special |= 0x08;        // Touchpad
            r.bSpecial = special;

            // Analog trigger values from DSU. Non-analog games still see digital flags above.
            r.bTriggerL = p[35];
            r.bTriggerR = p[34];

            r.wTimestamp = (ushort)(Environment.TickCount & 0xFFFF);
            r.bBatteryLvl = 0xFF;

            // ViGEm DS4 extended report: gyro and accel are signed 16-bit.
            // DS4/ViGEm uses gyro in roughly 16 counts per deg/s and accel in 8192 counts/g.
            r.wGyroX = ToI16(gx * 16.0);
            r.wGyroY = ToI16(gy * 16.0);
            r.wGyroZ = ToI16(gz * 16.0);
            r.wAccelX = ToI16(ax * 8192.0);
            r.wAccelY = ToI16(ay * 8192.0);
            r.wAccelZ = ToI16(az * 8192.0);

            var err = Vigem.vigem_target_ds4_update_ex(client, target, ref r);
            if (err != Vigem.Success)
                throw new Exception($"ViGEm update failed: 0x{err:X8}");

            if (Environment.TickCount64 - last > 1000)
            {
                Console.WriteLine(
                    $"DSU OK  | gyro {gx,8:F2} {gy,8:F2} {gz,8:F2} dps | " +
                    $"accel {ax,6:F2} {ay,6:F2} {az,6:F2} g");
                last = Environment.TickCount64;
            }
        }
    }

    static short ToI16(double x) =>
        (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue);

    void Send(uint type, byte[] payload)
    {
        byte[] p = new byte[20 + payload.Length];
        p[0] = (byte)'D'; p[1] = (byte)'S'; p[2] = (byte)'U'; p[3] = (byte)'C';
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6, 2), (ushort)(4 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16, 4), type);
        payload.CopyTo(p, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8, 4), Crc32(p));
        udp.Send(p, p.Length, portal);
    }

    static uint Crc32(byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < b.Length; i++)
        {
            byte x = (i >= 8 && i < 12) ? (byte)0 : b[i];
            crc ^= x;
            for (int j = 0; j < 8; j++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    public void Dispose()
    {
        try { if (target != IntPtr.Zero) Vigem.vigem_target_remove(client, target); } catch {}
        try { if (target != IntPtr.Zero) Vigem.vigem_target_free(target); } catch {}
        try { if (client != IntPtr.Zero) Vigem.vigem_disconnect(client); } catch {}
        try { if (client != IntPtr.Zero) Vigem.vigem_free(client); } catch {}
        udp.Dispose();
    }
}

static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Odin 2 Portal DSU -> Virtual DS4 bridge v2");
        Console.WriteLine();

        string ip = args.Length > 0 ? args[0] : "";
        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.Write("Portal IP (e.g. 192.168.1.108): ");
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
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
