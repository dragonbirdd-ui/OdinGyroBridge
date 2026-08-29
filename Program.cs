using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Threading;

class Program
{
    const int Port = 26760;

    const uint Version = 1001;
    const uint MsgVersion = 0x00100000;
    const uint MsgPorts   = 0x00100001;
    const uint MsgPad     = 0x00100002;

    static readonly uint ClientId =
        (uint)Random.Shared.Next(1, int.MaxValue);

    static readonly UdpClient udp = new UdpClient();
    static IPEndPoint portal = null!;

    static void Main(string[] args)
    {
        Console.WriteLine("==============================================");
        Console.WriteLine(" Odin 2 Portal DSU Motion Detector v7.1 FIXED");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        string ip;

        if (args.Length > 0)
        {
            ip = args[0];
        }
        else
        {
            Console.Write("Portal IP (e.g. 192.168.1.108): ");
            ip = Console.ReadLine() ?? "";
        }

        try
        {
            portal = new IPEndPoint(
                IPAddress.Parse(ip.Trim()), Port);

            // CRITICAL FIX:
            // Do not allow Receive() to block forever.
            udp.Client.ReceiveTimeout = 200;

            Console.WriteLine();
            Console.WriteLine($"Portal DSU: {portal.Address}:{Port}");
            Console.WriteLine();

            SendInitialRequests();

            Console.WriteLine("Waiting for first DSU packet...");

            byte[] first = WaitForFirstPacket();

            Console.WriteLine();
            Console.WriteLine(
                $"DSU packet received. Length = {first.Length} bytes");
            Console.WriteLine();

            // ============================================
            // PHASE 1
            // ============================================

            Console.WriteLine("==============================================");
            Console.WriteLine(" PHASE 1 - KEEP ODIN COMPLETELY STILL");
            Console.WriteLine("==============================================");
            Console.WriteLine();
            Console.WriteLine("Put Odin on a table.");
            Console.WriteLine("DO NOT TOUCH OR MOVE IT.");
            Console.WriteLine();
            Console.WriteLine("Starting in 3 seconds...");

            Thread.Sleep(3000);

            Console.WriteLine();
            Console.WriteLine(">>> RECORDING STILL DATA FOR 5 SECONDS <<<");
            Console.WriteLine();

            List<byte[]> stillPackets =
                CaptureTimed(5000);

            Console.WriteLine();
            Console.WriteLine(
                $"STILL finished: {stillPackets.Count} packets");
            Console.WriteLine();

            // ============================================
            // PHASE 2
            // ============================================

            Console.WriteLine("==============================================");
            Console.WriteLine(" PHASE 2 - MOVE ODIN STRONGLY");
            Console.WriteLine("==============================================");
            Console.WriteLine();
            Console.WriteLine("Pick Odin up.");
            Console.WriteLine();
            Console.WriteLine("During the test:");
            Console.WriteLine("  - rotate LEFT / RIGHT");
            Console.WriteLine("  - tilt UP / DOWN");
            Console.WriteLine("  - roll clockwise / counter-clockwise");
            Console.WriteLine();
            Console.WriteLine("Starting in 3 seconds...");

            Thread.Sleep(3000);

            Console.WriteLine();
            Console.WriteLine(">>> MOVE / ROTATE ODIN NOW! <<<");
            Console.WriteLine();

            List<byte[]> movingPackets =
                CaptureTimed(10000);

            Console.WriteLine();
            Console.WriteLine(
                $"MOVING finished: {movingPackets.Count} packets");
            Console.WriteLine();

            // ============================================
            // ANALYSIS
            // ============================================

            if (stillPackets.Count < 5 ||
                movingPackets.Count < 5)
            {
                Console.WriteLine("ERROR:");
                Console.WriteLine(
                    "Not enough DSU packets were captured.");
                Console.WriteLine();
                Console.WriteLine(
                    "Keep AndroidDSU running on Odin during BOTH phases.");
                Console.WriteLine();
                Console.WriteLine("Press ENTER to exit.");
                Console.ReadLine();
                return;
            }

            Analyze(stillPackets, movingPackets);

            Console.WriteLine();
            Console.WriteLine("==============================================");
            Console.WriteLine(" TEST FINISHED");
            Console.WriteLine("==============================================");
            Console.WriteLine();
            Console.WriteLine(
                "Take photos of TOP BYTE MOTION CANDIDATES");
            Console.WriteLine(
                "and FLOAT32 MOTION CANDIDATES.");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");

            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================");
            Console.WriteLine(" ERROR");
            Console.WriteLine("==============================================");
            Console.WriteLine();
            Console.WriteLine(ex);
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
    }

    static void SendInitialRequests()
    {
        Send(
            MsgVersion,
            new byte[] { 0xE9, 0x03 });

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
    }

    static byte[] WaitForFirstPacket()
    {
        long lastRequest =
            Environment.TickCount64;

        while (true)
        {
            byte[]? p = TryReceivePadPacket();

            if (p != null)
                return p;

            // Re-send subscription periodically.
            if (Environment.TickCount64 - lastRequest > 1000)
            {
                Send(MsgPad, new byte[8]);
                lastRequest = Environment.TickCount64;
            }
        }
    }

    static List<byte[]> CaptureTimed(int durationMs)
    {
        List<byte[]> packets = new();

        long start =
            Environment.TickCount64;

        long lastPrint = start;
        long lastRequest = start;

        while (Environment.TickCount64 - start < durationMs)
        {
            byte[]? p =
                TryReceivePadPacket();

            if (p != null)
                packets.Add(p);

            long now =
                Environment.TickCount64;

            // Keep subscription alive just in case.
            if (now - lastRequest >= 1000)
            {
                Send(MsgPad, new byte[8]);
                lastRequest = now;
            }

            if (now - lastPrint >= 500)
            {
                double elapsed =
                    (now - start) / 1000.0;

                Console.Write(
                    $"\rTime: {elapsed:F1}s   Packets: {packets.Count}      ");

                lastPrint = now;
            }
        }

        Console.WriteLine();

        return packets;
    }

    static byte[]? TryReceivePadPacket()
    {
        try
        {
            IPEndPoint remote =
                new IPEndPoint(
                    IPAddress.Any, 0);

            byte[] p =
                udp.Receive(ref remote);

            if (p.Length < 20)
                return null;

            if (p[0] != (byte)'D' ||
                p[1] != (byte)'S' ||
                p[2] != (byte)'U' ||
                p[3] != (byte)'S')
            {
                return null;
            }

            uint type =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    p.AsSpan(16, 4));

            if (type != MsgPad)
                return null;

            return p;
        }
        catch (SocketException ex)
        {
            // 10060 = Receive timeout on Windows.
            if (ex.SocketErrorCode ==
                    SocketError.TimedOut ||
                ex.SocketErrorCode ==
                    SocketError.WouldBlock)
            {
                return null;
            }

            throw;
        }
    }

    static void Analyze(
        List<byte[]> stillPackets,
        List<byte[]> movingPackets)
    {
        int length =
            Math.Min(
                stillPackets.Min(p => p.Length),
                movingPackets.Min(p => p.Length));

        Console.WriteLine("==============================================");
        Console.WriteLine(" ANALYSIS");
        Console.WriteLine("==============================================");
        Console.WriteLine();
        Console.WriteLine($"Common packet length: {length}");
        Console.WriteLine(
            $"Still packets : {stillPackets.Count}");
        Console.WriteLine(
            $"Moving packets: {movingPackets.Count}");
        Console.WriteLine();

        List<ByteResult> byteResults = new();

        for (int offset = 20;
             offset < length;
             offset++)
        {
            double stillVar =
                ByteAverageDifference(
                    stillPackets,
                    offset);

            double moveVar =
                ByteAverageDifference(
                    movingPackets,
                    offset);

            double stillRange =
                ByteRange(
                    stillPackets,
                    offset);

            double moveRange =
                ByteRange(
                    movingPackets,
                    offset);

            double variationRatio =
                moveVar /
                Math.Max(0.05, stillVar);

            double rangeRatio =
                moveRange /
                Math.Max(1.0, stillRange);

            double score =
                variationRatio * rangeRatio;

            byteResults.Add(
                new ByteResult
                {
                    Offset = offset,
                    StillVariation = stillVar,
                    MovingVariation = moveVar,
                    StillRange = stillRange,
                    MovingRange = moveRange,
                    Score = score
                });
        }

        Console.WriteLine("==============================================");
        Console.WriteLine(" TOP BYTE MOTION CANDIDATES");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        foreach (ByteResult r in byteResults
            .OrderByDescending(r => r.Score)
            .Take(30))
        {
            Console.WriteLine(
                $"BYTE {r.Offset,3} | " +
                $"SCORE {r.Score,10:F2} | " +
                $"stillVar {r.StillVariation,7:F2} | " +
                $"moveVar {r.MovingVariation,7:F2} | " +
                $"stillRange {r.StillRange,6:F0} | " +
                $"moveRange {r.MovingRange,6:F0}");
        }

        Console.WriteLine();
        Console.WriteLine("==============================================");
        Console.WriteLine(" FLOAT32 MOTION CANDIDATES");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        List<FloatResult> floatResults =
            new();

        // IMPORTANT:
        // Scan every possible byte alignment.
        for (int offset = 20;
             offset <= length - 4;
             offset++)
        {
            List<float> still =
                ExtractFloats(
                    stillPackets,
                    offset);

            List<float> moving =
                ExtractFloats(
                    movingPackets,
                    offset);

            still =
                still.Where(IsReasonableFloat)
                     .ToList();

            moving =
                moving.Where(IsReasonableFloat)
                      .ToList();

            if (still.Count < 5 ||
                moving.Count < 5)
            {
                continue;
            }

            double stillRange =
                FloatRange(still);

            double moveRange =
                FloatRange(moving);

            double stillVar =
                FloatAverageDifference(still);

            double moveVar =
                FloatAverageDifference(moving);

            if (moveRange < 0.000001)
                continue;

            double variationRatio =
                moveVar /
                Math.Max(0.000001, stillVar);

            double rangeRatio =
                moveRange /
                Math.Max(0.000001, stillRange);

            double score =
                variationRatio * rangeRatio;

            floatResults.Add(
                new FloatResult
                {
                    Offset = offset,
                    StillVariation = stillVar,
                    MovingVariation = moveVar,
                    StillRange = stillRange,
                    MovingRange = moveRange,
                    Score = score
                });
        }

        foreach (FloatResult r in floatResults
            .OrderByDescending(r => r.Score)
            .Take(30))
        {
            Console.WriteLine(
                $"FLOAT @{r.Offset,3} | " +
                $"SCORE {r.Score,12:F2} | " +
                $"stillRange {r.StillRange,12:F6} | " +
                $"moveRange {r.MovingRange,12:F6} | " +
                $"stillVar {r.StillVariation,12:F6} | " +
                $"moveVar {r.MovingVariation,12:F6}");
        }

        Console.WriteLine();
        Console.WriteLine("==============================================");
        Console.WriteLine(" OFFSETS 60-99 DETAIL");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        foreach (ByteResult r in byteResults
            .Where(r =>
                r.Offset >= 60 &&
                r.Offset <= 99))
        {
            Console.WriteLine(
                $"{r.Offset,3} | " +
                $"stillVar={r.StillVariation,7:F2} | " +
                $"moveVar={r.MovingVariation,7:F2} | " +
                $"stillRange={r.StillRange,6:F0} | " +
                $"moveRange={r.MovingRange,6:F0} | " +
                $"score={r.Score,10:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("==============================================");
        Console.WriteLine(" LAST PACKET FLOAT32 VALUES");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        byte[] last =
            movingPackets[^1];

        for (int offset = 60;
             offset <= Math.Min(
                 96,
                 last.Length - 4);
             offset++)
        {
            float value =
                BitConverter.ToSingle(
                    last,
                    offset);

            if (IsReasonableFloat(value))
            {
                Console.WriteLine(
                    $"FLOAT @{offset,3} = {value,14:F6}");
            }
        }
    }

    static double ByteAverageDifference(
        List<byte[]> packets,
        int offset)
    {
        if (packets.Count < 2)
            return 0;

        double total = 0;

        for (int i = 1;
             i < packets.Count;
             i++)
        {
            total +=
                Math.Abs(
                    packets[i][offset] -
                    packets[i - 1][offset]);
        }

        return total /
               (packets.Count - 1);
    }

    static double ByteRange(
        List<byte[]> packets,
        int offset)
    {
        int min = 255;
        int max = 0;

        foreach (byte[] p in packets)
        {
            int v = p[offset];

            if (v < min)
                min = v;

            if (v > max)
                max = v;
        }

        return max - min;
    }

    static List<float> ExtractFloats(
        List<byte[]> packets,
        int offset)
    {
        List<float> result =
            new();

        foreach (byte[] p in packets)
        {
            if (offset + 4 > p.Length)
                continue;

            float f =
                BitConverter.ToSingle(
                    p,
                    offset);

            if (!float.IsNaN(f) &&
                !float.IsInfinity(f))
            {
                result.Add(f);
            }
        }

        return result;
    }

    static bool IsReasonableFloat(float f)
    {
        if (float.IsNaN(f) ||
            float.IsInfinity(f))
        {
            return false;
        }

        return Math.Abs(f) <
               100000.0f;
    }

    static double FloatRange(
        List<float> values)
    {
        if (values.Count == 0)
            return 0;

        return
            (double)values.Max() -
            values.Min();
    }

    static double FloatAverageDifference(
        List<float> values)
    {
        if (values.Count < 2)
            return 0;

        double total = 0;

        for (int i = 1;
             i < values.Count;
             i++)
        {
            total +=
                Math.Abs(
                    values[i] -
                    values[i - 1]);
        }

        return total /
               (values.Count - 1);
    }

    static void Send(
        uint type,
        byte[] payload)
    {
        byte[] p =
            new byte[20 + payload.Length];

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
            ClientId);

        BinaryPrimitives.WriteUInt32LittleEndian(
            p.AsSpan(16, 4),
            type);

        payload.CopyTo(
            p,
            20);

        BinaryPrimitives.WriteUInt32LittleEndian(
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
                (i >= 8 && i < 12)
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
                     (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    sealed class ByteResult
    {
        public int Offset;
        public double StillVariation;
        public double MovingVariation;
        public double StillRange;
        public double MovingRange;
        public double Score;
    }

    sealed class FloatResult
    {
        public int Offset;
        public double StillVariation;
        public double MovingVariation;
        public double StillRange;
        public double MovingRange;
        public double Score;
    }
}
