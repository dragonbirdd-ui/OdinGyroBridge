using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

internal static class Program
{
    const int Port = 26760;
    const uint Version = 1001;

    const uint MsgVersion = 0x00100000;
    const uint MsgPorts   = 0x00100001;
    const uint MsgPad     = 0x00100002;

    static readonly UdpClient udp = new();
    static IPEndPoint portal = null!;
    static readonly uint id = (uint)Random.Shared.Next(1, int.MaxValue);

    // We compare individual bytes and aligned/unaligned Float32 values.
    // Packet length observed on your Odin/AndroidDSU = 100 bytes.
    const int ScanStart = 60;
    const int ScanEnd = 99;

    sealed class Capture
    {
        public string Name = "";
        public readonly List<byte[]> Packets = new();
    }

    static void Main(string[] args)
    {
        Console.Clear();

        Console.WriteLine("============================================================");
        Console.WriteLine(" Odin 2 Portal DSU Gyro AXIS FINDER v8");
        Console.WriteLine("============================================================");
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
            portal = new IPEndPoint(IPAddress.Parse(ip.Trim()), Port);

            Console.WriteLine();
            Console.WriteLine($"Portal DSU: {portal.Address}:{Port}");
            Console.WriteLine();

            Subscribe();

            Console.WriteLine("Waiting for AndroidDSU packet...");

            byte[] first = ReceivePad();

            Console.WriteLine($"DSU packet received. Length = {first.Length} bytes");
            Console.WriteLine();

            Console.WriteLine("IMPORTANT:");
            Console.WriteLine("During each movement phase move ONLY in the requested way.");
            Console.WriteLine("Move continuously back and forth for the whole test.");
            Console.WriteLine();

            WaitEnter("Press ENTER to start.");

            // --------------------------------------------------------
            // PHASE 0 - STILL
            // --------------------------------------------------------

            Console.Clear();
            Header("PHASE 0 / BASELINE - KEEP ODIN COMPLETELY STILL");

            Console.WriteLine();
            Console.WriteLine("Put Odin flat on a table.");
            Console.WriteLine("DO NOT TOUCH IT.");
            Console.WriteLine();

            Countdown();

            Capture still = CapturePackets("STILL", 6);

            // --------------------------------------------------------
            // PHASE 1
            // --------------------------------------------------------

            Console.Clear();
            Header("PHASE 1 - LEFT / RIGHT TILT");

            Console.WriteLine();
            Console.WriteLine("Hold the Odin normally in front of you.");
            Console.WriteLine();
            Console.WriteLine("Tilt it repeatedly:");
            Console.WriteLine();
            Console.WriteLine("        LEFT  <---->  RIGHT");
            Console.WriteLine();
            Console.WriteLine("Like tilting a steering wheel.");
            Console.WriteLine();
            Console.WriteLine("Do NOT move forward/backward if possible.");
            Console.WriteLine();

            WaitEnter("Press ENTER when ready.");

            Countdown();

            Capture leftRight = CapturePackets("LEFT-RIGHT", 8);

            // --------------------------------------------------------
            // PHASE 2
            // --------------------------------------------------------

            Console.Clear();
            Header("PHASE 2 - FORWARD / BACKWARD TILT");

            Console.WriteLine();
            Console.WriteLine("Now repeatedly tilt the top edge of the Odin");
            Console.WriteLine("toward you and away from you.");
            Console.WriteLine();
            Console.WriteLine("       FORWARD  <---->  BACKWARD");
            Console.WriteLine();
            Console.WriteLine("Try not to roll left/right.");
            Console.WriteLine();

            WaitEnter("Press ENTER when ready.");

            Countdown();

            Capture forwardBack = CapturePackets("FORWARD-BACK", 8);

            // --------------------------------------------------------
            // PHASE 3
            // --------------------------------------------------------

            Console.Clear();
            Header("PHASE 3 - FLAT ROTATION / YAW");

            Console.WriteLine();
            Console.WriteLine("Keep the Odin approximately FLAT.");
            Console.WriteLine();
            Console.WriteLine("Rotate it repeatedly like this:");
            Console.WriteLine();
            Console.WriteLine("       COUNTER-CLOCKWISE <----> CLOCKWISE");
            Console.WriteLine();
            Console.WriteLine("Imagine turning it on a table.");
            Console.WriteLine();

            WaitEnter("Press ENTER when ready.");

            Countdown();

            Capture yaw = CapturePackets("YAW", 8);

            // --------------------------------------------------------
            // ANALYSIS
            // --------------------------------------------------------

            Console.Clear();

            Header("ANALYSIS");

            Console.WriteLine();
            Console.WriteLine($"STILL packets       : {still.Packets.Count}");
            Console.WriteLine($"LEFT-RIGHT packets  : {leftRight.Packets.Count}");
            Console.WriteLine($"FORWARD-BACK packets: {forwardBack.Packets.Count}");
            Console.WriteLine($"YAW packets         : {yaw.Packets.Count}");
            Console.WriteLine();

            AnalyzeBytes(still, leftRight, forwardBack, yaw);

            Console.WriteLine();
            Console.WriteLine();
            AnalyzeFloat32(still, leftRight, forwardBack, yaw);

            Console.WriteLine();
            Console.WriteLine();
            PrintLastPacketFloats(yaw);

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine(" TEST COMPLETE");
            Console.WriteLine("============================================================");
            Console.WriteLine();
            Console.WriteLine("Take clear photos of:");
            Console.WriteLine();
            Console.WriteLine("  1. TOP BYTE AXIS CANDIDATES");
            Console.WriteLine("  2. TOP FLOAT32 AXIS CANDIDATES");
            Console.WriteLine("  3. LAST PACKET FLOAT32 VALUES");
            Console.WriteLine();
            Console.WriteLine("Send those photos to ChatGPT.");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR:");
            Console.WriteLine(ex);
            Console.WriteLine();
            Console.WriteLine("Press ENTER.");
            Console.ReadLine();
        }
    }

    static void Header(string text)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine($" {text}");
        Console.WriteLine("============================================================");
    }

    static void WaitEnter(string text)
    {
        Console.WriteLine(text);
        Console.ReadLine();
    }

    static void Countdown()
    {
        Console.WriteLine();
        Console.WriteLine("Starting in:");

        for (int i = 3; i >= 1; i--)
        {
            Console.WriteLine($"{i}...");
            Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine(">>> GO <<<");
        Console.WriteLine();
    }

    static Capture CapturePackets(string name, int seconds)
    {
        var c = new Capture { Name = name };

        long start = Environment.TickCount64;
        long end = start + seconds * 1000L;
        long lastPrint = start;

        while (Environment.TickCount64 < end)
        {
            byte[] p = ReceivePad();

            // Clone because we want an independent stored packet.
            c.Packets.Add((byte[])p.Clone());

            long now = Environment.TickCount64;

            if (now - lastPrint >= 1000)
            {
                double elapsed = (now - start) / 1000.0;

                Console.WriteLine(
                    $"{name,-14}  Time: {elapsed,5:F1}s   Packets: {c.Packets.Count}");

                lastPrint = now;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{name} finished: {c.Packets.Count} packets");

        Thread.Sleep(1200);

        return c;
    }

    // ------------------------------------------------------------
    // BYTE ANALYSIS
    // ------------------------------------------------------------

    sealed class ByteResult
    {
        public int Offset;

        public double StillVar;
        public double LRVar;
        public double FBVar;
        public double YawVar;

        public int StillRange;
        public int LRRange;
        public int FBRange;
        public int YawRange;

        public double LRScore;
        public double FBScore;
        public double YawScore;
    }

    static void AnalyzeBytes(
        Capture still,
        Capture lr,
        Capture fb,
        Capture yaw)
    {
        Header("TOP BYTE AXIS CANDIDATES");

        var results = new List<ByteResult>();

        int maxLength = new[]
        {
            MaxPacketLength(still),
            MaxPacketLength(lr),
            MaxPacketLength(fb),
            MaxPacketLength(yaw)
        }.Min();

        int end = Math.Min(ScanEnd, maxLength - 1);

        for (int off = ScanStart; off <= end; off++)
        {
            double sv = ByteVariance(still, off);
            double lv = ByteVariance(lr, off);
            double fv = ByteVariance(fb, off);
            double yv = ByteVariance(yaw, off);

            int sr = ByteRange(still, off);
            int lrr = ByteRange(lr, off);
            int fbr = ByteRange(fb, off);
            int yr = ByteRange(yaw, off);

            // A useful gyro byte should change much more during one
            // movement than when stationary.
            double baseline = sv + 0.05;

            results.Add(new ByteResult
            {
                Offset = off,

                StillVar = sv,
                LRVar = lv,
                FBVar = fv,
                YawVar = yv,

                StillRange = sr,
                LRRange = lrr,
                FBRange = fbr,
                YawRange = yr,

                LRScore = lv / baseline,
                FBScore = fv / baseline,
                YawScore = yv / baseline
            });
        }

        PrintByteAxis("LEFT / RIGHT", results, x => x.LRScore, x => x.LRVar, x => x.LRRange);
        PrintByteAxis("FORWARD / BACK", results, x => x.FBScore, x => x.FBVar, x => x.FBRange);
        PrintByteAxis("YAW / ROTATION", results, x => x.YawScore, x => x.YawVar, x => x.YawRange);
    }

    static void PrintByteAxis(
        string name,
        List<ByteResult> results,
        Func<ByteResult, double> score,
        Func<ByteResult, double> moveVar,
        Func<ByteResult, int> moveRange)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name} ---");

        foreach (var r in results
            .OrderByDescending(score)
            .Take(10))
        {
            Console.WriteLine(
                $"BYTE {r.Offset,2} | " +
                $"score {score(r),10:F2} | " +
                $"stillVar {r.StillVar,8:F2} | " +
                $"moveVar {moveVar(r),8:F2} | " +
                $"stillRange {r.StillRange,3} | " +
                $"moveRange {moveRange(r),3}");
        }
    }

    // ------------------------------------------------------------
    // FLOAT ANALYSIS
    // ------------------------------------------------------------

    sealed class FloatResult
    {
        public int Offset;

        public double StillRange;
        public double LRRange;
        public double FBRange;
        public double YawRange;

        public double StillVar;
        public double LRVar;
        public double FBVar;
        public double YawVar;

        public double LRScore;
        public double FBScore;
        public double YawScore;

        public int StillValid;
        public int LRValid;
        public int FBValid;
        public int YawValid;
    }

    static void AnalyzeFloat32(
        Capture still,
        Capture lr,
        Capture fb,
        Capture yaw)
    {
        Header("TOP FLOAT32 AXIS CANDIDATES");

        var results = new List<FloatResult>();

        int maxLength = new[]
        {
            MaxPacketLength(still),
            MaxPacketLength(lr),
            MaxPacketLength(fb),
            MaxPacketLength(yaw)
        }.Min();

        int end = Math.Min(96, maxLength - 4);

        // Intentionally scan EVERY byte offset, not only multiples of four.
        // This catches unaligned Float32 fields.
        for (int off = ScanStart; off <= end; off++)
        {
            var s = FloatStats(still, off);
            var l = FloatStats(lr, off);
            var f = FloatStats(fb, off);
            var y = FloatStats(yaw, off);

            if (s.Valid < 20 ||
                l.Valid < 20 ||
                f.Valid < 20 ||
                y.Valid < 20)
                continue;

            double baseVar = s.Variance + 0.000001;

            results.Add(new FloatResult
            {
                Offset = off,

                StillRange = s.Range,
                LRRange = l.Range,
                FBRange = f.Range,
                YawRange = y.Range,

                StillVar = s.Variance,
                LRVar = l.Variance,
                FBVar = f.Variance,
                YawVar = y.Variance,

                StillValid = s.Valid,
                LRValid = l.Valid,
                FBValid = f.Valid,
                YawValid = y.Valid,

                LRScore = SafeScore(l.Variance, baseVar),
                FBScore = SafeScore(f.Variance, baseVar),
                YawScore = SafeScore(y.Variance, baseVar)
            });
        }

        PrintFloatAxis(
            "LEFT / RIGHT",
            results,
            x => x.LRScore,
            x => x.LRRange,
            x => x.LRVar);

        PrintFloatAxis(
            "FORWARD / BACK",
            results,
            x => x.FBScore,
            x => x.FBRange,
            x => x.FBVar);

        PrintFloatAxis(
            "YAW / ROTATION",
            results,
            x => x.YawScore,
            x => x.YawRange,
            x => x.YawVar);
    }

    static void PrintFloatAxis(
        string name,
        List<FloatResult> results,
        Func<FloatResult, double> score,
        Func<FloatResult, double> moveRange,
        Func<FloatResult, double> moveVar)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name} ---");

        foreach (var r in results
            .Where(x =>
                IsReasonable(x.StillRange) &&
                IsReasonable(moveRange(x)))
            .OrderByDescending(score)
            .Take(12))
        {
            Console.WriteLine(
                $"FLOAT @{r.Offset,2} | " +
                $"score {score(r),12:F2} | " +
                $"stillRange {r.StillRange,12:G6} | " +
                $"moveRange {moveRange(r),12:G6} | " +
                $"stillVar {r.StillVar,12:G6} | " +
                $"moveVar {moveVar(r),12:G6}");
        }
    }

    static bool IsReasonable(double x)
    {
        return !double.IsNaN(x) &&
               !double.IsInfinity(x) &&
               x >= 0 &&
               x < 1e9;
    }

    static double SafeScore(double move, double baseline)
    {
        double x = move / baseline;

        if (double.IsNaN(x))
            return 0;

        if (double.IsPositiveInfinity(x))
            return double.MaxValue;

        return x;
    }

    // ------------------------------------------------------------
    // LAST PACKET VIEW
    // ------------------------------------------------------------

    static void PrintLastPacketFloats(Capture c)
    {
        Header("LAST PACKET FLOAT32 VALUES");

        if (c.Packets.Count == 0)
            return;

        byte[] p = c.Packets[^1];

        for (int off = ScanStart; off <= Math.Min(96, p.Length - 4); off++)
        {
            float v = BitConverter.ToSingle(p, off);

            if (!float.IsFinite(v))
                continue;

            // Hide ridiculous interpretations.
            if (Math.Abs(v) > 1_000_000)
                continue;

            Console.WriteLine($"FLOAT @{off,2} = {v,14:F6}");
        }
    }

    // ------------------------------------------------------------
    // STATISTICS
    // ------------------------------------------------------------

    static int MaxPacketLength(Capture c)
    {
        if (c.Packets.Count == 0)
            return 0;

        return c.Packets.Min(x => x.Length);
    }

    static double ByteVariance(Capture c, int off)
    {
        var vals = c.Packets
            .Where(p => p.Length > off)
            .Select(p => (double)p[off])
            .ToArray();

        return Variance(vals);
    }

    static int ByteRange(Capture c, int off)
    {
        var vals = c.Packets
            .Where(p => p.Length > off)
            .Select(p => (int)p[off])
            .ToArray();

        if (vals.Length == 0)
            return 0;

        return vals.Max() - vals.Min();
    }

    readonly struct Stats
    {
        public readonly double Variance;
        public readonly double Range;
        public readonly int Valid;

        public Stats(double variance, double range, int valid)
        {
            Variance = variance;
            Range = range;
            Valid = valid;
        }
    }

    static Stats FloatStats(Capture c, int off)
    {
        var values = new List<double>();

        foreach (byte[] p in c.Packets)
        {
            if (off + 4 > p.Length)
                continue;

            float v = BitConverter.ToSingle(p, off);

            if (!float.IsFinite(v))
                continue;

            // Reject absurd interpretations caused by starting inside
            // another field. Keep a generous range for unknown units.
            if (Math.Abs(v) > 1_000_000)
                continue;

            values.Add(v);
        }

        if (values.Count < 2)
            return new Stats(0, 0, values.Count);

        double[] a = values.ToArray();

        return new Stats(
            Variance(a),
            a.Max() - a.Min(),
            a.Length);
    }

    static double Variance(double[] a)
    {
        if (a.Length < 2)
            return 0;

        double mean = a.Average();

        double sum = 0;

        foreach (double x in a)
        {
            double d = x - mean;
            sum += d * d;
        }

        return sum / a.Length;
    }

    // ------------------------------------------------------------
    // DSU
    // ------------------------------------------------------------

    static void Subscribe()
    {
        Send(MsgVersion, new byte[]
        {
            0xE9, 0x03
        });

        Send(MsgPorts, new byte[]
        {
            0x01, 0x00, 0x00, 0x00, 0x00
        });

        Send(MsgPad, new byte[8]);
    }

    static byte[] ReceivePad()
    {
        while (true)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);

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

    static void Send(uint type, byte[] payload)
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
}
