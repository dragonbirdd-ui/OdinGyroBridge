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

    static UdpClient udp = new UdpClient();
    static IPEndPoint portal = null!;

    static void Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine(" Odin 2 Portal DSU Motion Detector v7");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        string ip;

        if (args.Length > 0)
            ip = args[0];
        else
        {
            Console.Write("Portal IP (e.g. 192.168.1.108): ");
            ip = Console.ReadLine() ?? "";
        }

        try
        {
            portal = new IPEndPoint(
                IPAddress.Parse(ip.Trim()), Port);

            Console.WriteLine();
            Console.WriteLine($"Portal DSU: {portal.Address}:{Port}");
            Console.WriteLine();

            // Start DSU connection
            Send(MsgVersion, new byte[] { 0xE9, 0x03 });

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

            Send(MsgPad, new byte[8]);

            Console.WriteLine("Waiting for DSU packets...");
            Console.WriteLine();

            // Make sure packets are actually arriving.
            byte[] first = ReceivePadPacket();

            Console.WriteLine(
                $"DSU packet received. Length = {first.Length} bytes");

            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine(" PHASE 1 - KEEP ODIN COMPLETELY STILL");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine(
                "Put the Odin on a table and DO NOT TOUCH IT.");
            Console.WriteLine();
            Console.WriteLine("Starting in 3 seconds...");

            Thread.Sleep(3000);

            Console.WriteLine();
            Console.WriteLine("Recording STILL data for 5 seconds...");
            Console.WriteLine();

            var stillPackets = Capture(5000);

            Console.WriteLine(
                $"Captured {stillPackets.Count} STILL packets.");

            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine(" PHASE 2 - MOVE / ROTATE ODIN STRONGLY");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine(
                "Pick up the Odin NOW.");
            Console.WriteLine(
                "Rotate LEFT/RIGHT, UP/DOWN and tilt it.");
            Console.WriteLine();
            Console.WriteLine("Starting in 3 seconds...");

            Thread.Sleep(3000);

            Console.WriteLine();
            Console.WriteLine(">>> MOVE ODIN NOW <<<");
            Console.WriteLine();

            var movingPackets = Capture(10000);

            Console.WriteLine();
            Console.WriteLine(
                $"Captured {movingPackets.Count} MOVING packets.");

            Console.WriteLine();
            Console.WriteLine("Analyzing...");
            Console.WriteLine();

            Analyze(stillPackets, movingPackets);

            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine(" TEST FINISHED");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine(
                "Take a clear photo of ALL results and send it.");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");

            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
    }

    static List<byte[]> Capture(int milliseconds)
    {
        List<byte[]> packets = new();

        long start = Environment.TickCount64;

        while (Environment.TickCount64 - start < milliseconds)
        {
            byte[] p = ReceivePadPacket();

            packets.Add(p);

            if (packets.Count % 100 == 0)
            {
                Console.Write(
                    $"\rPackets: {packets.Count}");
            }
        }

        Console.WriteLine();

        return packets;
    }

    static byte[] ReceivePadPacket()
    {
        while (true)
        {
            IPEndPoint remote =
                new IPEndPoint(IPAddress.Any, 0);

            byte[] p = udp.Receive(ref remote);

            if (p.Length < 20)
                continue;

            if (p[0] != (byte)'D' ||
                p[1] != (byte)'S' ||
                p[2] != (byte)'U' ||
                p[3] != (byte)'S')
                continue;

            uint type =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    p.AsSpan(16, 4));

            if (type != MsgPad)
                continue;

            return p;
        }
    }

    static void Analyze(
        List<byte[]> stillPackets,
        List<byte[]> movingPackets)
    {
        if (stillPackets.Count == 0 ||
            movingPackets.Count == 0)
        {
            Console.WriteLine(
                "Not enough packets for analysis.");
            return;
        }

        int length = Math.Min(
            stillPackets.Min(x => x.Length),
            movingPackets.Min(x => x.Length));

        Console.WriteLine(
            $"Common packet length: {length}");
        Console.WriteLine();

        List<ByteResult> results = new();

        // Ignore DSU header area.
        // Start at byte 20.
        for (int offset = 20; offset < length; offset++)
        {
            double stillMean =
                stillPackets.Average(p => (double)p[offset]);

            double movingMean =
                movingPackets.Average(p => (double)p[offset]);

            double stillVariation =
                AverageDifference(
                    stillPackets,
                    offset);

            double movingVariation =
                AverageDifference(
                    movingPackets,
                    offset);

            double score =
                movingVariation /
                Math.Max(0.05, stillVariation);

            double rangeStill =
                Range(stillPackets, offset);

            double rangeMoving =
                Range(movingPackets, offset);

            // Strong motion candidates should change
            // much more while moving than while stationary.
            double rangeScore =
                rangeMoving /
                Math.Max(1.0, rangeStill);

            double finalScore =
                score * rangeScore;

            results.Add(
                new ByteResult
                {
                    Offset = offset,
                    StillMean = stillMean,
                    MovingMean = movingMean,
                    StillVariation = stillVariation,
                    MovingVariation = movingVariation,
                    StillRange = rangeStill,
                    MovingRange = rangeMoving,
                    Score = finalScore
                });
        }

        Console.WriteLine(
            "===========================================");
        Console.WriteLine(
            " TOP BYTE MOTION CANDIDATES");
        Console.WriteLine(
            "===========================================");
        Console.WriteLine();

        foreach (var r in results
            .OrderByDescending(x => x.Score)
            .Take(30))
        {
            Console.WriteLine(
                $"OFFSET {r.Offset,3} | " +
                $"score {r.Score,10:F2} | " +
                $"stillVar {r.StillVariation,7:F2} | " +
                $"moveVar {r.MovingVariation,7:F2} | " +
                $"stillRange {r.StillRange,6:F0} | " +
                $"moveRange {r.MovingRange,6:F0}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "===========================================");
        Console.WriteLine(
            " FLOAT32 MOTION SCAN");
        Console.WriteLine(
            "===========================================");
        Console.WriteLine();

        List<FloatResult> floatResults = new();

        // Test EVERY byte alignment, not only
        // multiples of four.
        for (int offset = 20; offset <= length - 4; offset++)
        {
            var stillValues =
                ExtractFloats(stillPackets, offset);

            var movingValues =
                ExtractFloats(movingPackets, offset);

            if (stillValues.Count < 5 ||
                movingValues.Count < 5)
                continue;

            // Remove impossible / NaN / gigantic floats.
            stillValues = stillValues
                .Where(IsReasonableFloat)
                .ToList();

            movingValues = movingValues
                .Where(IsReasonableFloat)
                .ToList();

            if (stillValues.Count < 5 ||
                movingValues.Count < 5)
                continue;

            double stillRange =
                FloatRange(stillValues);

            double movingRange =
                FloatRange(movingValues);

            double stillVariation =
                FloatAverageDifference(stillValues);

            double movingVariation =
                FloatAverageDifference(movingValues);

            double score =
                (movingVariation /
                 Math.Max(0.0001, stillVariation))
                *
                (movingRange /
                 Math.Max(0.001, stillRange));

            // Ignore essentially dead fields.
            if (movingRange < 0.001)
                continue;

            floatResults.Add(
                new FloatResult
                {
                    Offset = offset,
                    StillRange = stillRange,
                    MovingRange = movingRange,
                    StillVariation = stillVariation,
                    MovingVariation = movingVariation,
                    Score = score
                });
        }

        foreach (var r in floatResults
            .OrderByDescending(x => x.Score)
            .Take(30))
        {
            Console.WriteLine(
                $"FLOAT @{r.Offset,3} | " +
                $"score {r.Score,12:F2} | " +
                $"stillRange {r.StillRange,12:F6} | " +
                $"moveRange {r.MovingRange,12:F6} | " +
                $"stillVar {r.StillVariation,12:F6} | " +
                $"moveVar {r.MovingVariation,12:F6}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "===========================================");
        Console.WriteLine(
            " KNOWN SENSOR AREA 60-99");
        Console.WriteLine(
            "===========================================");
        Console.WriteLine();

        int end = Math.Min(99, length - 1);

        for (int offset = 60; offset <= end; offset++)
        {
            var r = results.First(x => x.Offset == offset);

            Console.WriteLine(
                $"{offset,3}: " +
                $"stillVar={r.StillVariation,7:F2} " +
                $"moveVar={r.MovingVariation,7:F2} " +
                $"range={r.MovingRange,6:F0} " +
                $"score={r.Score,10:F2}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "===========================================");
        Console.WriteLine(
            " FLOAT VALUES 60-99 FROM LAST MOVING PACKET");
        Console.WriteLine(
            "===========================================");
        Console.WriteLine();

        byte[] last = movingPackets[^1];

        for (int offset = 60;
             offset <= Math.Min(96, last.Length - 4);
             offset++)
        {
            float value =
                BitConverter.ToSingle(last, offset);

            if (IsReasonableFloat(value))
            {
                Console.WriteLine(
                    $"OFFSET {offset,3} = {value,14:F6}");
            }
        }
    }

    static double AverageDifference(
        List<byte[]> packets,
        int offset)
    {
        if (packets.Count < 2)
            return 0;

        double total = 0;

        for (int i = 1; i < packets.Count; i++)
        {
            total += Math.Abs(
                packets[i][offset] -
                packets[i - 1][offset]);
        }

        return total / (packets.Count - 1);
    }

    static double Range(
        List<byte[]> packets,
        int offset)
    {
        byte min = 255;
        byte max = 0;

        foreach (byte[] p in packets)
        {
            byte v = p[offset];

            if (v < min) min = v;
            if (v > max) max = v;
        }

        return max - min;
    }

    static List<float> ExtractFloats(
        List<byte[]> packets,
        int offset)
    {
        List<float> values = new();

        foreach (byte[] p in packets)
        {
            if (offset + 4 > p.Length)
                continue;

            float f =
                BitConverter.ToSingle(p, offset);

            if (!float.IsNaN(f) &&
                !float.IsInfinity(f))
            {
                values.Add(f);
            }
        }

        return values;
    }

    static bool IsReasonableFloat(float f)
    {
        if (float.IsNaN(f) ||
            float.IsInfinity(f))
            return false;

        return Math.Abs(f) < 100000.0f;
    }

    static double FloatRange(List<float> v)
    {
        if (v.Count == 0)
            return 0;

        return v.Max() - v.Min();
    }

    static double FloatAverageDifference(
        List<float> v)
    {
        if (v.Count < 2)
            return 0;

        double total = 0;

        for (int i = 1; i < v.Count; i++)
        {
            total +=
                Math.Abs(v[i] - v[i - 1]);
        }

        return total / (v.Count - 1);
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

        payload.CopyTo(p, 20);

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

    sealed class ByteResult
    {
        public int Offset;
        public double StillMean;
        public double MovingMean;
        public double StillVariation;
        public double MovingVariation;
        public double StillRange;
        public double MovingRange;
        public double Score;
    }

    sealed class FloatResult
    {
        public int Offset;
        public double StillRange;
        public double MovingRange;
        public double StillVariation;
        public double MovingVariation;
        public double Score;
    }
}
