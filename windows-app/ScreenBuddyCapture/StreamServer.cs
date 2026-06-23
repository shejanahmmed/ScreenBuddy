using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenBuddyCapture;

/// <summary>
/// A TCP streaming server that listens for a connection, captures the screen using DXGI,
/// encodes it into H.264 via Media Foundation, and streams the raw NAL units over TCP.
/// It features UDP Auto-Discovery on port 7891 and 6-digit PIN handshake verification.
/// </summary>
public sealed class StreamServer : IDisposable
{
    private readonly int _port;
    private readonly int _displayIndex;
    private TcpListener? _listener;
    private UdpClient? _udpListener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private Task? _udpTask;
    private bool _disposed = false;

    // Protocol Message Types
    private const int MSG_VIDEO_FRAME = 1;
    private const int MSG_INPUT_EVENT = 2;
    private const int MSG_HANDSHAKE   = 3;
    private const int MSG_QUALITY_CHANGE = 4;
    private const int MSG_DISCONNECT  = 5;

    public string Pin { get; private set; }

    public StreamServer(int port = 7890, int displayIndex = 0)
    {
        _port = port;
        _displayIndex = displayIndex;
        // Generate a random 6-digit pairing PIN on instantiation
        var rand = new Random();
        Pin = rand.Next(100000, 999999).ToString();
    }

    /// <summary>
    /// Starts the server and UDP auto-discovery listener on background threads.
    /// </summary>
    public void Start()
    {
        if (_serverTask != null)
            return;

        _cts = new CancellationTokenSource();
        
        // Start TCP listener
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        // Start UDP listener for local auto-discovery broadcasts on port 7891
        try
        {
            _udpListener = new UdpClient();
            // Allow multiple bindings to port 7891 just in case, and listen on Any
            _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, 7891));
            _udpTask = Task.Run(() => ListenUdpBroadcastsAsync(_cts.Token));
            Console.WriteLine($"[StreamServer] Auto-Discovery active on UDP port 7891. PIN: {Pin}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamServer] Warning: Failed to start UDP auto-discovery: {ex.Message}");
        }

        Console.WriteLine($"[StreamServer] TCP Stream Server started. Listening on port {_port}...");

        _serverTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Stops the server, terminates listeners, and cleans up resources.
    /// </summary>
    public void Stop()
    {
        if (_serverTask == null)
            return;

        _cts?.Cancel();
        _listener?.Stop();
        
        try
        {
            _udpListener?.Close();
        }
        catch { }

        try
        {
            Task.WaitAll(new[] { _serverTask, _udpTask! }, 500);
        }
        catch
        {
            // Expected cancellation exceptions
        }

        _serverTask = null;
        _udpTask = null;
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _udpListener = null;

        Console.WriteLine("[StreamServer] Server stopped.");
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                // Wait for a client connection
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                Console.WriteLine($"[StreamServer] Client connected from: {client.Client.RemoteEndPoint}");

                // Handle the client stream in a synchronous loop on this thread/task
                RunStreamingSession(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Console.WriteLine($"[StreamServer] Error accepting client: {ex.Message}");
                client?.Dispose();
                // Brief pause before trying to accept again
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task ListenUdpBroadcastsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpListener!.ReceiveAsync(token);
                string message = System.Text.Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith("SCREENBUDDY_DISCOVER|"))
                {
                    string clientPin = message.Substring("SCREENBUDDY_DISCOVER|".Length).Trim();
                    if (clientPin == Pin)
                    {
                        // Reply with SCREENBUDDY_OFFER|[TCP port]
                        byte[] replyBytes = System.Text.Encoding.UTF8.GetBytes($"SCREENBUDDY_OFFER|{_port}");
                        await _udpListener.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                        Console.WriteLine($"[StreamServer] Replied to Auto-Discovery request from {result.RemoteEndPoint}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested)
                    break;
                await Task.Delay(1000, token);
            }
        }
    }

    private void RunStreamingSession(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            ScreenCapture? capture = null;
            H264Encoder? encoder = null;
            Task? inputReaderTask = null;

            try
            {
                // ─────────────────────────────────────────────────────────────
                // 1. SECURE TCP HANDSHAKE
                //    Read first packet from client and verify PIN.
                // ─────────────────────────────────────────────────────────────
                byte[] handshakeHeader = new byte[8];
                if (!ReadExact(stream, handshakeHeader, 8))
                {
                    Console.WriteLine("[StreamServer] Client disconnected during handshake header.");
                    return;
                }

                int msgType = (handshakeHeader[0] << 24) |
                              (handshakeHeader[1] << 16) |
                              (handshakeHeader[2] << 8)  |
                              handshakeHeader[3];

                int payloadLen = (handshakeHeader[4] << 24) |
                                 (handshakeHeader[5] << 16) |
                                 (handshakeHeader[6] << 8)  |
                                 handshakeHeader[7];

                if (msgType != MSG_HANDSHAKE || payloadLen != 6)
                {
                    Console.WriteLine($"[StreamServer] Security handshake failed: Invalid packet type ({msgType}) or length ({payloadLen}).");
                    SendPacket(stream, MSG_DISCONNECT, Array.Empty<byte>());
                    return;
                }

                byte[] pinBuffer = new byte[6];
                if (!ReadExact(stream, pinBuffer, 6))
                {
                    Console.WriteLine("[StreamServer] Client disconnected during handshake payload.");
                    return;
                }

                string clientPin = System.Text.Encoding.UTF8.GetString(pinBuffer);
                if (clientPin != Pin)
                {
                    Console.WriteLine($"[StreamServer] Security handshake failed: Client PIN '{clientPin}' is incorrect.");
                    SendPacket(stream, MSG_DISCONNECT, Array.Empty<byte>());
                    return;
                }

                Console.WriteLine("[StreamServer] Client authenticated successfully.");

                // ─────────────────────────────────────────────────────────────
                // 2. RESOURCE INITIALIZATION
                //    D3D device and Media Foundation Encoder allocated ONLY
                //    after successful client authentication.
                // ─────────────────────────────────────────────────────────────
                capture = new ScreenCapture(displayIndex: _displayIndex);
                
                var sessionState = new SessionState();

                encoder = new H264Encoder(
                    width: capture.Width,
                    height: capture.Height,
                    fps: 30,
                    bitrateBps: sessionState.BitrateBps
                );

                // 3. Spawn client touch reader task
                var clientSocket = client;
                inputReaderTask = Task.Run(() => ReadFromClientLoop(clientSocket, stream, sessionState));

                Console.WriteLine("[StreamServer] Streaming active screen mirror & receiving touch inputs...");

                // Buffer for frame capture data
                byte[]? bgraBuffer = null;
                int currentBitrateBps = sessionState.BitrateBps;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Check if client disconnected or shut down connection
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                    {
                        Console.WriteLine("[StreamServer] Client closed the connection.");
                        break;
                    }

                    // A. Check if bitrate needs updating dynamically
                    int targetBitrate = sessionState.BitrateBps;
                    if (targetBitrate != currentBitrateBps)
                    {
                        Console.WriteLine($"[StreamServer] Updating encoder bitrate dynamically to: {targetBitrate / 1000} kbps");
                        encoder?.Dispose();
                        encoder = new H264Encoder(
                            width: capture.Width,
                            height: capture.Height,
                            fps: 30,
                            bitrateBps: targetBitrate
                        );
                        currentBitrateBps = targetBitrate;
                    }

                    // B. Capture a new screen frame
                    using var bitmap = capture.CaptureFrame(timeoutMs: 33);
                    if (bitmap == null)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    // B. Lock bits and copy BGRA pixel data into our buffer
                    int pixelCount = bitmap.Width * bitmap.Height;
                    int bytesNeeded = pixelCount * 4;

                    if (bgraBuffer == null || bgraBuffer.Length != bytesNeeded)
                    {
                        bgraBuffer = new byte[bytesNeeded];
                    }

                    var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                    var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        Marshal.Copy(bitmapData.Scan0, bgraBuffer, 0, bytesNeeded);
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }

                    // C. Feed BGRA bytes to the H.264 Encoder
                    var h264Data = encoder.EncodeFrame(bgraBuffer);
                    if (h264Data != null && h264Data.Length > 0)
                    {
                        // D. Send H.264 frame packet over TCP
                        SendPacket(stream, MSG_VIDEO_FRAME, h264Data);
                    }
                }

                // Drain remaining encoder buffers
                var flushData = encoder.Flush();
                if (flushData != null && flushData.Length > 0)
                {
                    SendPacket(stream, MSG_VIDEO_FRAME, flushData);
                }

                // Send disconnect packet
                try
                {
                    SendPacket(stream, MSG_DISCONNECT, Array.Empty<byte>());
                }
                catch
                {
                    // Ignore error on disconnect notification
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamServer] Session ended with error: {ex.Message}");
            }
            finally
            {
                // Safely clean up input reader task
                if (inputReaderTask != null)
                {
                    try
                    {
                        inputReaderTask.Wait(300);
                    }
                    catch { }
                }

                // Safely clean up D3D and MF resources for this streaming session
                encoder?.Dispose();
                capture?.Dispose();
                Console.WriteLine("[StreamServer] Session cleanup finished. Ready for next client.");
            }
        }
    }

    /// <summary>
    /// Reads incoming control packets (e.g., touch/mouse coordinates) from the client asynchronously.
    /// </summary>
    private static void ReadFromClientLoop(TcpClient client, NetworkStream stream, SessionState sessionState)
    {
        var header = new byte[8];
        var payload = new byte[1024];

        try
        {
            while (client.Connected)
            {
                // 1. Read header (8 bytes)
                if (!ReadExact(stream, header, 8))
                    break;

                int msgType = (header[0] << 24) |
                              (header[1] << 16) |
                              (header[2] << 8)  |
                              header[3];

                int payloadLen = (header[4] << 24) |
                                 (header[5] << 16) |
                                 (header[6] << 8)  |
                                 header[7];

                if (payloadLen < 0 || payloadLen > 65536)
                    break;

                if (payload.Length < payloadLen)
                {
                    payload = new byte[payloadLen];
                }

                // 2. Read payload
                if (payloadLen > 0)
                {
                    if (!ReadExact(stream, payload, payloadLen))
                        break;
                }

                // 3. Process MSG_INPUT_EVENT
                if (msgType == MSG_INPUT_EVENT && payloadLen >= 9)
                {
                    int action = payload[0];

                    // Convert big-endian 4-byte float to single
                    int xBits = (payload[1] << 24) |
                                (payload[2] << 16) |
                                (payload[3] << 8)  |
                                payload[4];
                    float normX = BitConverter.Int32BitsToSingle(xBits);

                    int yBits = (payload[5] << 24) |
                                (payload[6] << 16) |
                                (payload[7] << 8)  |
                                payload[8];
                    float normY = BitConverter.Int32BitsToSingle(yBits);

                    // Forward to Win32 Simulation API
                    InputSimulator.SimulateTouchEvent(action, normX, normY);
                }
                else if (msgType == MSG_QUALITY_CHANGE && payloadLen >= 4)
                {
                    int bitrate = (payload[0] << 24) |
                                  (payload[1] << 16) |
                                  (payload[2] << 8)  |
                                  payload[3];

                    Console.WriteLine($"[StreamServer] Client requested quality/bitrate change: {bitrate / 1000} kbps");
                    sessionState.BitrateBps = bitrate;
                }
                else if (msgType == MSG_DISCONNECT)
                {
                    Console.WriteLine("[StreamServer] Client initiated disconnect.");
                    break;
                }
            }
        }
        catch
        {
            // Socket errors caught and handled on session shutdown
        }
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int bytesRead = 0;
        while (bytesRead < count)
        {
            int read = stream.Read(buffer, bytesRead, count - bytesRead);
            if (read <= 0)
                return false; // Closed socket

            bytesRead += read;
        }
        return true;
    }

    /// <summary>
    /// Writes a framed packet to the stream.
    /// Format: [4-byte Big Endian MsgType] [4-byte Big Endian PayloadLength] [Payload bytes]
    /// </summary>
    private static void SendPacket(NetworkStream stream, int msgType, byte[] payload)
    {
        byte[] header = new byte[8];

        // 1. Pack message type (big endian)
        header[0] = (byte)((msgType >> 24) & 0xFF);
        header[1] = (byte)((msgType >> 16) & 0xFF);
        header[2] = (byte)((msgType >> 8) & 0xFF);
        header[3] = (byte)(msgType & 0xFF);

        // 2. Pack payload length (big endian)
        int length = payload.Length;
        header[4] = (byte)((length >> 24) & 0xFF);
        header[5] = (byte)((length >> 16) & 0xFF);
        header[6] = (byte)((length >> 8) & 0xFF);
        header[7] = (byte)(length & 0xFF);

        // 3. Write header and payload to socket
        stream.Write(header, 0, 8);
        if (length > 0)
        {
            stream.Write(payload, 0, length);
        }
        stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class SessionState
    {
        public volatile int BitrateBps = 2_000_000;
    }
}
