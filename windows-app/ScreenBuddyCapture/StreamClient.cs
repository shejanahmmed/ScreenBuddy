using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace ScreenBuddyCapture;

/// <summary>
/// A test client that connects to the StreamServer over TCP loopback,
/// receives H.264 packets, validates the protocol header, and counts stats.
/// </summary>
public sealed class StreamClient
{
    private readonly string _host;
    private readonly int _port;

    // Protocol Message Types
    private const int MSG_VIDEO_FRAME = 1;
    private const int MSG_HANDSHAKE   = 3;
    private const int MSG_DISCONNECT  = 5;

    public StreamClient(string host = "127.0.0.1", int port = 7890)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Connects to the server, authenticates with the PIN, and consumes the stream.
    /// </summary>
    public void Run(string pin)
    {
        Console.WriteLine($"[StreamClient] Connecting to {_host}:{_port}...");

        using var client = new TcpClient();
        try
        {
            client.Connect(_host, _port);
            Console.WriteLine("[StreamClient] Connected! Sending handshake...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamClient] Connection failed: {ex.Message}");
            return;
        }

        using var stream = client.GetStream();

        // 1. Send pairing PIN handshake
        try
        {
            byte[] pinBytes = System.Text.Encoding.UTF8.GetBytes(pin);
            SendPacket(stream, MSG_HANDSHAKE, pinBytes);
            Console.WriteLine("[StreamClient] Handshake sent. Listening for H.264 stream...");
            Console.WriteLine("Press any key to disconnect the client.");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamClient] Failed to send handshake: {ex.Message}");
            return;
        }

        // Separate thread/task to wait for user keystroke to exit client
        bool shouldExit = false;
        var cancelThread = new Thread(() =>
        {
            try
            {
                Console.ReadKey(true);
                shouldExit = true;
            }
            catch (InvalidOperationException)
            {
                // Console input is redirected
                Thread.Sleep(Timeout.Infinite);
            }
        }) { IsBackground = true };
        cancelThread.Start();

        var headerBuffer = new byte[8];
        var payloadBuffer = new byte[1024 * 1024]; // 1MB reusable buffer

        long totalBytesReceived = 0;
        int  totalFrames = 0;
        var  startTime = Stopwatch.StartNew();
        var  lastStatsTime = Stopwatch.StartNew();

        int  intervalFrames = 0;
        long intervalBytes  = 0;

        try
        {
            while (!shouldExit)
            {
                if (client.Client.Poll(10000, SelectMode.SelectRead) && client.Client.Available == 0)
                {
                    Console.WriteLine("\n[StreamClient] Connection closed by remote host.");
                    break;
                }

                // Read 8-byte header
                if (!ReadExact(stream, headerBuffer, 8))
                {
                    Console.WriteLine("\n[StreamClient] Failed to read packet header.");
                    break;
                }

                int msgType = (headerBuffer[0] << 24) |
                              (headerBuffer[1] << 16) |
                              (headerBuffer[2] << 8)  |
                              headerBuffer[3];

                int payloadLen = (headerBuffer[4] << 24) |
                                 (headerBuffer[5] << 16) |
                                 (headerBuffer[6] << 8)  |
                                 headerBuffer[7];

                if (payloadLen < 0 || payloadLen > payloadBuffer.Length)
                {
                    int newSize = Math.Max(payloadBuffer.Length * 2, payloadLen);
                    Array.Resize(ref payloadBuffer, newSize);
                }

                // Read payload
                if (payloadLen > 0)
                {
                    if (!ReadExact(stream, payloadBuffer, payloadLen))
                    {
                        Console.WriteLine("\n[StreamClient] Failed to read packet payload.");
                        break;
                    }
                }

                if (msgType == MSG_VIDEO_FRAME)
                {
                    totalFrames++;
                    totalBytesReceived += payloadLen;
                    intervalFrames++;
                    intervalBytes += payloadLen;
                }
                else if (msgType == MSG_DISCONNECT)
                {
                    Console.WriteLine("\n[StreamClient] Server sent disconnect signal.");
                    break;
                }

                // Periodically print stats
                if (lastStatsTime.ElapsedMilliseconds >= 1000)
                {
                    double seconds = lastStatsTime.Elapsed.TotalSeconds;
                    double fps = intervalFrames / seconds;
                    double mbps = (intervalBytes * 8.0) / (1024.0 * 1024.0) / seconds;

                    Console.Write($"\r[StreamClient] Received: {totalFrames} frames | {totalBytesReceived / 1024.0:F1} KB | Speed: {fps:F1} FPS, {mbps:F2} Mbps");

                    intervalFrames = 0;
                    intervalBytes = 0;
                    lastStatsTime.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[StreamClient] Error: {ex.Message}");
        }
        finally
        {
            startTime.Stop();
            double totalSeconds = startTime.Elapsed.TotalSeconds;
            double avgFps = totalFrames / totalSeconds;
            double avgKb = (totalBytesReceived / 1024.0) / totalSeconds;

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine(" Stream Client Summary");
            Console.WriteLine("=================================================");
            Console.WriteLine($"  Total duration: {totalSeconds:F2}s");
            Console.WriteLine($"  Total frames  : {totalFrames}");
            Console.WriteLine($"  Total received: {totalBytesReceived:N0} bytes ({totalBytesReceived / (1024.0 * 1024.0):F2} MB)");
            Console.WriteLine($"  Average FPS   : {avgFps:F1}");
            Console.WriteLine($"  Average Speed : {avgKb * 8.0 / 1024.0:F2} Mbps ({avgKb:F1} KB/s)");
            Console.WriteLine("=================================================");
            Console.WriteLine();
        }
    }

    private static void SendPacket(NetworkStream stream, int msgType, byte[] payload)
    {
        byte[] header = new byte[8];
        header[0] = (byte)((msgType >> 24) & 0xFF);
        header[1] = (byte)((msgType >> 16) & 0xFF);
        header[2] = (byte)((msgType >> 8) & 0xFF);
        header[3] = (byte)(msgType & 0xFF);

        int length = payload.Length;
        header[4] = (byte)((length >> 24) & 0xFF);
        header[5] = (byte)((length >> 16) & 0xFF);
        header[6] = (byte)((length >> 8) & 0xFF);
        header[7] = (byte)(length & 0xFF);

        stream.Write(header, 0, 8);
        if (length > 0)
        {
            stream.Write(payload, 0, length);
        }
        stream.Flush();
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int bytesRead = 0;
        while (bytesRead < count)
        {
            int read = stream.Read(buffer, bytesRead, count - bytesRead);
            if (read <= 0)
                return false;

            bytesRead += read;
        }
        return true;
    }
}
