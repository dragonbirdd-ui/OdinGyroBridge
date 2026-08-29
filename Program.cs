using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

internal sealed class DsuScanner : IDisposable
{
    const int Port = 26760;
    const uint Version = 1001;
    const uint MsgVersion = 0x00100000;
    const uint MsgPorts = 0x00100001;
    const uint MsgPad = 0x00100002;

    readonly UdpClient udp = new UdpClient();
    readonly IPEndPoint portal;
    readonly uint id = (uint)Random.Shared.Next(1, int.MaxValue);

    long lastPrint = 0;
    int packetCount = 0;

    public DsuScanner(string ip)
    {
        portal = new IPEndPoint(IPAddress.Parse(ip), Port);
    }

    public void Run()
    {
        Console.WriteLine("Odin 2 Portal DSU v6 MOTION SCANNER");
        Console.WriteLine();
        Console.WriteLine("Portal: " + portal.Address + ":" + Port);
        Console.WriteLine();
        Console.WriteLine("This version scans offsets 60-99 as Float32.");
        Console.WriteLine();
        Console.WriteLine("TEST:");
        Console.WriteLine("1. Keep Odin still.");
        Console.WriteLine("2. Rotate strongly LEFT/RIGHT.");
        Console.WriteLine("3. Rotate strongly UP/DOWN.");
        Console.WriteLine("4. Roll the device clockwise/counter-clockwise.");
        Console.WriteLine();
        Console.WriteLine("Take a photo while MOVING the Odin.");
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

            if (p[0] != 'D' ||
                p[1] != 'S' ||
                p[2] != 'U' ||
                p[3] != 'S')
                continue;

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(
                p.AsSpan(16, 4));

            if (type != MsgPad)
                continue;

            if (p[21] != 2)
                continue;

            packetCount++;

            if (Environment.TickCount64 - lastPrint >= 500)
            {
                PrintPacket(p);
                lastPrint = Environment.TickCount64;
            }
        }
    }

    static void PrintPacket(byte[] p)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("MOTION SCAN");

        Console.WriteLine();
        Console.WriteLine("HEX 60-99:");

        for (int i = 60; i <= 99; i++)
        {
            Console.Write(p[i].ToString("X2") + " ");
        }

        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("ALIGNED FLOAT32:");

        PrintFloat(p, 60);
        PrintFloat(p, 64);
        PrintFloat(p, 68);
        PrintFloat(p, 72);
        PrintFloat(p, 76);
        PrintFloat(p, 80);
        PrintFloat(p, 84);
        PrintFloat(p, 88);
        PrintFloat(p, 92);
        PrintFloat(p, 96);

        Console.WriteLine();
        Console.WriteLine("ALL CANDIDATE OFFSETS 60-96:");

        for (int offset = 60; offset <= 96; offset++)
        {
            float value = BitConverter.ToSingle(p, offset);

            if (float.IsNaN(value) || float.IsInfinity(value))
                continue;

            if (Math.Abs(value) > 1000000.0f)
                continue;

            Console.Write(
                offset.ToString("D2") +
                "=" +
                value.ToString("0.000") +
                "  ");

            if ((offset - 59) % 4 == 0)
                Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("============================================================");
    }

    static void PrintFloat(byte[] p, int offset)
    {
        float value = BitConverter.ToSingle(p, offset);

        Console.WriteLine(
            "OFFSET " +
            offset.ToString("D2") +
            " = " +
            value.ToString("0.000000"));
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
        udp.Dispose();
    }
}

static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Odin 2 Portal DSU v6 MOTION SCANNER");
        Console.WriteLine();

        string ip = args.Length > 0 ? args[0] : "";

        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.Write("Portal IP (e.g. 192.168.1.108): ");
            ip = Console.ReadLine() ?? "";
        }

        try
        {
            using var scanner = new DsuScanner(ip.Trim());
            scanner.Run();
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
