
using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Program
{
    const int PORT = 26760;
    const uint VERSION = 1001;
    const uint MSG_VERSION = 0x00100000;
    const uint MSG_PORTS   = 0x00100001;
    const uint MSG_PAD     = 0x00100002;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }
    [DllImport("user32.dll", SetLastError=true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;

    static volatile float gx, gy, gz;
    static volatile bool haveData = false;
    static volatile bool running = true;

    // Easy tuning: raise for faster mouse, lower for slower.
    static double sensitivity = 0.22;
    static double deadzoneDps = 0.35;

    static uint Crc32(byte[] data, int offset, int count)
    {
        uint crc = 0xffffffff;
        for (int i = offset; i < offset + count; i++) {
            crc ^= data[i];
            for (int j=0; j<8; j++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    static byte[] MakeRequest(uint msgType, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        byte[] p = new byte[20 + payload.Length];
        Encoding.ASCII.GetBytes("DSUC").CopyTo(p, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4,2), (ushort)VERSION);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6,2), (ushort)(payload.Length + 4));
        // CRC at 8..11 remains zero during CRC calculation
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12,4), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16,4), msgType);
        payload.CopyTo(p,20);
        uint crc = Crc32(p, 0, p.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8,4), crc);
        return p;
    }

    static float F32(byte[] p, int o)
    {
        int bits = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o,4));
        return BitConverter.Int32BitsToSingle(bits);
    }

    static void Receiver()
    {
        using UdpClient udp = new UdpClient(PORT);
        udp.Client.ReceiveTimeout = 1000;
        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);

        DateTime lastSubscribe = DateTime.MinValue;
        IPEndPoint android = null;

        Console.WriteLine("Listening for AndroidDSU on UDP 26760...");
        Console.WriteLine("Open AndroidDSU and keep its DSU server enabled.");

        while (running)
        {
            try {
                byte[] p = udp.Receive(ref any);

                // Remember sender so we can refresh subscription.
                android = new IPEndPoint(any.Address, any.Port);

                if (p.Length >= 100 &&
                    p[0]=='D' && p[1]=='S' && p[2]=='U' && p[3]=='S' &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16,4)) == MSG_PAD &&
                    p[21] == 2)
                {
                    // AndroidDSU / DSUS standard layout used by our prior bridge:
                    // accel 76/80/84, gyro 88/92/96.
                    gx = F32(p,88);
                    gy = F32(p,92);
                    gz = F32(p,96);
                    haveData = true;
                }

                if (android != null && (DateTime.UtcNow-lastSubscribe).TotalMilliseconds > 900)
                {
                    // Subscribe to all pads.
                    byte[] req = MakeRequest(MSG_PAD, new byte[8]);
                    udp.Send(req, req.Length, android);
                    lastSubscribe = DateTime.UtcNow;
                }
            }
            catch (SocketException) { }
            catch (Exception ex) { Console.WriteLine("RX: " + ex.Message); }
        }
    }

    static void MoveMouse(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        INPUT[] inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].mi.dx = dx;
        inp[0].mi.dy = dy;
        inp[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    static void Main(string[] args)
    {
        Console.Title = "Odin Gyro -> Windows Mouse";
        Console.WriteLine("ODIN GYRO -> WINDOWS MOUSE TEST");
        Console.WriteLine("--------------------------------");
        Console.WriteLine("No Steam Input / no ViGEm required.");
        Console.WriteLine("ESC = quit   +/- = sensitivity");
        Console.WriteLine();

        Thread rx = new Thread(Receiver) { IsBackground = true };
        rx.Start();

        double remX=0, remY=0;
        long lastPrint = Environment.TickCount64;

        while (running)
        {
            // For handheld yaw, Z commonly maps to horizontal; X maps to vertical.
            // If axes feel wrong, this test still proves direct mouse output.
            double hz = Math.Abs(gz) < deadzoneDps ? 0 : gz;
            double vx = Math.Abs(gx) < deadzoneDps ? 0 : gx;

            remX += hz * sensitivity;
            remY += vx * sensitivity;

            int dx = (int)Math.Truncate(remX);
            int dy = (int)Math.Truncate(remY);
            remX -= dx; remY -= dy;
            MoveMouse(dx, dy);

            if (Environment.TickCount64 - lastPrint > 250)
            {
                Console.Write($"\rGYRO X={gx,7:F2} Y={gy,7:F2} Z={gz,7:F2}  mouse=({dx,3},{dy,3})  sens={sensitivity:F2}   ");
                lastPrint = Environment.TickCount64;
            }

            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Escape) running=false;
                else if (k.KeyChar == '+' || k.Key == ConsoleKey.Add) sensitivity *= 1.25;
                else if (k.KeyChar == '-' || k.Key == ConsoleKey.Subtract) sensitivity /= 1.25;
            }
            Thread.Sleep(10);
        }
    }
}
