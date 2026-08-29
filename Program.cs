using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Diagnostics;

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

    // Raw 63-byte DS4_REPORT_EX.
    // This avoids nested C# struct-marshalling ambiguity.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DS4_REPORT_EX
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)]
        public byte[] Report;

        public static DS4_REPORT_EX Create()
        {
            var r = new DS4_REPORT_EX
            {
                Report = new byte[63]
            };

            // Analog sticks centered
            r.Report[0] = 128; // LX
            r.Report[1] = 128; // LY
            r.Report[2] = 128; // RX
            r.Report[3] = 128; // RY

            // D-pad neutral
            r.Report[4] = 8;

            // Battery
            r.Report[11] = 0xFF;

            // Battery / connection information
            r.Report[29] = 0x1A;

            // Touch packet count
            r.Report[32] = 1;

            // Touch fingers UP
            r.Report[34] = 0x80;
            r.Report[38] = 0x80;

            r.Report[43] = 0x80;
            r.Report[47] = 0x80;

            r.Report[52] = 0x80;
            r.Report[56] = 0x80;

            return r;
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
        (uint)Random.Shared.Next(
            1,
            int.MaxValue);

    ushort timestamp;
    long lastReportTicks = Stopwatch.GetTimestamp();

    long lastPrint;
    long lastSubscribe;

    public Bridge(string ip)
    {
        portal =
            new IPEndPoint(
                IPAddress.Parse(ip),
                Port);

        client =
            Vigem.vigem_alloc();

        if (client == IntPtr.Zero)
        {
            throw new Exception(
                "ViGEmClient.dll: vigem_alloc failed.");
        }

        uint error =
            Vigem.vigem_connect(client);

        if (error != Vigem.Success)
        {
            throw new Exception(
                $"ViGEm connect failed: 0x{error:X8}");
        }

        target =
            Vigem.vigem_target_ds4_alloc();

        if (target == IntPtr.Zero)
        {
            throw new Exception(
                "vigem_target_ds4_alloc failed.");
        }

        error =
            Vigem.vigem_target_add(
                client,
                target);

        if (error != Vigem.Success)
        {
            throw new Exception(
                $"ViGEm target_add failed: 0x{error:X8}");
        }
    }

    public void Run()
    {
        Console.Clear();

        Console.WriteLine(
            "============================================================");

        Console.WriteLine(
            " ODIN 2 PORTAL DSU -> VIRTUAL DS4 BRIDGE v12 STEAM-MOTION");

        Console.WriteLine(
            "============================================================");

        Console.WriteLine();

        Console.WriteLine(
            $"Portal DSU : {portal.Address}:{Port}");

        Console.WriteLine(
            "Virtual DS4: READY");

        Console.WriteLine();

        Console.WriteLine(
            "DSU ACCEL:");

        Console.WriteLine(
            "  X @76");

        Console.WriteLine(
            "  Y @80");

        Console.WriteLine(
            "  Z @84");

        Console.WriteLine();

        Console.WriteLine(
            "DSU GYRO:");

        Console.WriteLine(
            "  X @88");

        Console.WriteLine(
            "  Y @92");

        Console.WriteLine(
            "  Z @96");

        Console.WriteLine();

        Console.WriteLine(
            "Move Odin - GYRO and DS4 RAW values must change.");

        Console.WriteLine();

        Subscribe();

        while (true)
        {
            // Refresh AndroidDSU subscription every second.
            if (Environment.TickCount64 -
                lastSubscribe >= 1000)
            {
                Send(
                    MsgPad,
                    new byte[8]);

                lastSubscribe =
                    Environment.TickCount64;
            }

            IPEndPoint remote =
                new IPEndPoint(
                    IPAddress.Any,
                    0);

            byte[] p =
                udp.Receive(
                    ref remote);

            // We need up to offset 99.
            if (p.Length < 100)
                continue;

            // DSUS response signature
            if (p[0] != (byte)'D' ||
                p[1] != (byte)'S' ||
                p[2] != (byte)'U' ||
                p[3] != (byte)'S')
            {
                continue;
            }

            uint type =
                BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        p.AsSpan(16, 4));

            if (type != MsgPad)
                continue;

            // DSU connected state
            if (p[21] != 2)
                continue;

            // ========================================================
            // READ ANDROID DSU MOTION
            // ========================================================

            float ax =
                BitConverter.ToSingle(
                    p,
                    76);

            float ay =
                BitConverter.ToSingle(
                    p,
                    80);

            float az =
                BitConverter.ToSingle(
                    p,
                    84);

            float gx =
                BitConverter.ToSingle(
                    p,
                    88);

            float gy =
                BitConverter.ToSingle(
                    p,
                    92);

            float gz =
                BitConverter.ToSingle(
                    p,
                    96);

            // Reject broken packets.
            if (!float.IsFinite(ax) ||
                !float.IsFinite(ay) ||
                !float.IsFinite(az) ||
                !float.IsFinite(gx) ||
                !float.IsFinite(gy) ||
                !float.IsFinite(gz))
            {
                continue;
            }

            // ========================================================
            // CREATE RAW DS4 REPORT
            // ========================================================

            Vigem.DS4_REPORT_EX report =
                Vigem.DS4_REPORT_EX.Create();

            byte[] r =
                report.Report;

            // ========================================================
            // STICKS
            // ========================================================

            r[0] = p[40];
            r[1] = p[41];
            r[2] = p[42];
            r[3] = p[43];

            // ========================================================
            // BUTTONS
            // ========================================================

            CopyButtonsRaw(
                p,
                r);

            // ========================================================
            // TIMESTAMP
            // ========================================================

            // Real DS4 timestamp: one unit is approximately 5.333 us.
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedSeconds =
                (nowTicks - lastReportTicks) /
                (double)Stopwatch.Frequency;

            int timestampDelta =
                (int)Math.Round(elapsedSeconds / 0.000005333);

            timestampDelta =
                Math.Clamp(timestampDelta, 1, 18750);

            timestamp =
                unchecked((ushort)(timestamp + timestampDelta));

            lastReportTicks = nowTicks;

            WriteU16(
                r,
                9,
                timestamp);

            // ========================================================
            // GYROSCOPE
            //
            // AndroidDSU:
            // degrees/sec
            //
            // DS4:
            // 16 raw units = 1 degree/sec
            // ========================================================

            // DS4 scale = 16 raw units per degree/sec.
            // Inverse ViGEm calibration, matching Sunshine/Apollo.
            short gyroX =
                ToShort(((gx * 16.0) + 1.0) / 0.977596);

            short gyroY =
                ToShort((gy * 16.0) / 0.972370);

            short gyroZ =
                ToShort((gz * 16.0) / 0.971550);

            WriteI16(
                r,
                12,
                gyroX);

            WriteI16(
                r,
                14,
                gyroY);

            WriteI16(
                r,
                16,
                gyroZ);

            // ========================================================
            // ACCELEROMETER
            //
            // AndroidDSU:
            // approximately g
            //
            // DS4:
            // 8192 raw units = 1 g
            // ========================================================

            // AndroidDSU values here are approximately g.
            // DS4 scale = 8192 raw units per g, with inverse ViGEm calibration.
            short accelX =
                ToShort(((ax * 8192.0) - 297.0) / 1.010796);

            short accelY =
                ToShort(((ay * 8192.0) - 42.0) / 1.014614);

            short accelZ =
                ToShort(((az * 8192.0) - 512.0) / 1.024768);

            WriteI16(
                r,
                18,
                accelX);

            WriteI16(
                r,
                20,
                accelY);

            WriteI16(
                r,
                22,
                accelZ);

            // ========================================================
            // SEND REPORT TO VIRTUAL DS4
            // ========================================================

            uint error =
                Vigem.vigem_target_ds4_update_ex(
                    client,
                    target,
                    report);

            if (error != Vigem.Success)
            {
                throw new Exception(
                    $"ViGEm DS4 update failed: 0x{error:X8}");
            }

            // ========================================================
            // DEBUG OUTPUT
            // ========================================================

            if (Environment.TickCount64 -
                lastPrint >= 250)
            {
                Console.WriteLine(
                    $"GYRO dps " +
                    $"X={gx,8:F2} " +
                    $"Y={gy,8:F2} " +
                    $"Z={gz,8:F2}   |   " +
                    $"DS4 RAW " +
                    $"X={gyroX,6} " +
                    $"Y={gyroY,6} " +
                    $"Z={gyroZ,6} " +
                    $"TS={timestamp,5} " +
                    $"dTS={timestampDelta,5}");

                lastPrint =
                    Environment.TickCount64;
            }
        }
    }

    static void CopyButtonsRaw(
        byte[] p,
        byte[] r)
    {
        byte b1 =
            p[36];

        byte b2 =
            p[37];

        // ============================================================
        // DPAD
        // ============================================================

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

        ushort buttons =
            dpad;

        // ============================================================
        // FACE / SHOULDER BUTTONS
        // ============================================================

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

        WriteU16(
            r,
            4,
            buttons);

        // ============================================================
        // PS / TOUCHPAD
        // ============================================================

        byte special = 0;

        if (p[38] != 0)
            special |= 0x01;

        if (p[39] != 0)
            special |= 0x08;

        r[6] =
            special;

        // ============================================================
        // ANALOG TRIGGERS
        // ============================================================

        r[7] =
            p[35];

        r[8] =
            p[34];
    }

    static short ToShort(
        double value)
    {
        return (short)Math.Clamp(
            Math.Round(value),
            short.MinValue,
            short.MaxValue);
    }

    static void WriteI16(
        byte[] buffer,
        int offset,
        short value)
    {
        BinaryPrimitives
            .WriteInt16LittleEndian(
                buffer.AsSpan(
                    offset,
                    2),
                value);
    }

    static void WriteU16(
        byte[] buffer,
        int offset,
        ushort value)
    {
        BinaryPrimitives
            .WriteUInt16LittleEndian(
                buffer.AsSpan(
                    offset,
                    2),
                value);
    }

    // ================================================================
    // ANDROID DSU SUBSCRIPTION
    // ================================================================

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

        // DSUC
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

        // CRC temporarily zero
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

        // Calculate CRC
        BinaryPrimitives
            .WriteUInt32LittleEndian(
                p.AsSpan(8, 4),
                Crc32(p));

        udp.Send(
            p,
            p.Length,
            portal);
    }

    static uint Crc32(
        byte[] b)
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

            crc ^=
                x;

            for (int j = 0;
                 j < 8;
                 j++)
            {
                crc =
                    (crc >> 1) ^
                    (
                        0xEDB88320u &
                        (uint)-(int)(
                            crc & 1)
                    );
            }
        }

        return ~crc;
    }

    // ================================================================
    // CLEANUP
    // ================================================================

    public void Dispose()
    {
        try
        {
            if (target != IntPtr.Zero)
            {
                Vigem.vigem_target_remove(
                    client,
                    target);
            }
        }
        catch
        {
        }

        try
        {
            if (target != IntPtr.Zero)
            {
                Vigem.vigem_target_free(
                    target);
            }
        }
        catch
        {
        }

        try
        {
            if (client != IntPtr.Zero)
            {
                Vigem.vigem_disconnect(
                    client);
            }
        }
        catch
        {
        }

        try
        {
            if (client != IntPtr.Zero)
            {
                Vigem.vigem_free(
                    client);
            }
        }
        catch
        {
        }

        udp.Dispose();
    }
}

internal static class Program
{
    static void Main(
        string[] args)
    {
        Console.WriteLine(
            "Odin 2 Portal DSU -> Virtual DS4 Bridge v12 STEAM-MOTION");

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
                new Bridge(
                    ip.Trim());

            bridge.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine();

            Console.WriteLine(
                "ERROR:");

            Console.WriteLine(
                ex);

            Console.WriteLine();

            Console.WriteLine(
                "Press ENTER to exit...");

            Console.ReadLine();
        }
    }
}
