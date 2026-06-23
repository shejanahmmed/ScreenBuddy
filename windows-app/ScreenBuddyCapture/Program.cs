using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenBuddyCapture;
using Vortice.DXGI;

// Ensure console can display UTF8 characters properly
Console.OutputEncoding = System.Text.Encoding.UTF8;

while (true)
{
    SafeClearConsole();
    Console.WriteLine("=================================================");
    Console.WriteLine(" ScreenBuddy — Windows Companion App");
    Console.WriteLine("=================================================");
    Console.WriteLine(" 1. Run DXGI Screen Capture Test (Step 1)");
    Console.WriteLine(" 2. Run H.264 Encoder Test (Step 2)");
    Console.WriteLine(" 3. Start TCP Stream Server (Step 3)");
    Console.WriteLine(" 4. Start Local Loopback Test Client (Step 3)");
    Console.WriteLine(" 5. Exit");
    Console.WriteLine("=================================================");
    Console.Write(" Select an option (1-5): ");

    var choice = Console.ReadLine();
    if (choice == "5")
    {
        break;
    }

    switch (choice)
    {
        case "1":
            RunStep1Test();
            break;
        case "2":
            RunStep2Test();
            break;
        case "3":
            RunStreamServer();
            break;
        case "4":
            RunStreamClient();
            break;
        default:
            Console.WriteLine("\nInvalid option. Press any key to try again...");
            Console.ReadKey();
            break;
    }
}

Console.WriteLine("\nGoodbye!");

// ─────────────────────────────────────────────────────────────────────────────
// Test and Server/Client Helper Methods
// ─────────────────────────────────────────────────────────────────────────────

static void SafeClearConsole()
{
    try
    {
        Console.Clear();
    }
    catch (IOException)
    {
        // Ignore exception when console output is redirected
    }
}

static void RunStep1Test()
{
    SafeClearConsole();
    Console.WriteLine("=================================================");
    Console.WriteLine(" DXGI Capture Test (Step 1)");
    Console.WriteLine("=================================================");
    
    var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
    Directory.CreateDirectory(outputDir);
    Console.WriteLine($"Output folder: {outputDir}");
    Console.WriteLine();

    try
    {
        using var capture = new ScreenCapture(displayIndex: 0);
        Console.WriteLine($"Monitor: {capture.Width} x {capture.Height}");
        Console.WriteLine();

        int saved = 0, attempts = 0;
        while (saved < 5 && attempts < 300)
        {
            attempts++;
            using var frame = capture.CaptureFrame(timeoutMs: 200);
            if (frame == null) continue;

            var path = Path.Combine(outputDir, $"frame_{saved:D3}.png");
            frame.Save(path, ImageFormat.Png);
            Console.WriteLine($"  Saved frame_{saved:D3}.png ({frame.Width}x{frame.Height})");
            saved++;
        }

        Console.WriteLine(saved == 5 
            ? "\n✓ Step 1 PASSED — 5 frames saved to output folder!" 
            : $"\n⚠ Only captured {saved} frames.");
    }
    catch (Exception ex)
    {
        PrintException(ex);
    }

    Console.WriteLine("\nPress any key to return to menu...");
    Console.ReadKey();
}

static void RunStep2Test()
{
    SafeClearConsole();
    Console.WriteLine("=================================================");
    Console.WriteLine(" H.264 Encoder Test (Step 2)");
    Console.WriteLine("=================================================");

    try
    {
        using var capture = new ScreenCapture(displayIndex: 0);
        Console.WriteLine($"Monitor: {capture.Width} x {capture.Height}");
        Console.WriteLine();

        Console.WriteLine("Initializing H.264 encoder (Windows Media Foundation)...");
        using var encoder = new H264Encoder(
            width:      capture.Width,
            height:     capture.Height,
            fps:        30,
            bitrateBps: 2_000_000 // 2 Mbps
        );

        Console.WriteLine("Encoding 60 frames (move your mouse/windows to generate frame updates)...");
        Console.WriteLine();

        int  framesEncoded  = 0;
        int  framesAttempted = 0;
        long totalBytes     = 0;
        var  encodeStart    = DateTime.UtcNow;

        while (framesAttempted < 150 && framesEncoded < 60)
        {
            framesAttempted++;

            using var bitmap = capture.CaptureFrame(timeoutMs: 50);
            if (bitmap == null) continue;

            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb
            );
            var bgra = new byte[bitmap.Width * bitmap.Height * 4];
            Marshal.Copy(bitmapData.Scan0, bgra, 0, bgra.Length);
            bitmap.UnlockBits(bitmapData);

            var h264Data = encoder.EncodeFrame(bgra);
            framesEncoded++;

            if (h264Data != null)
            {
                totalBytes += h264Data.Length;
                Console.Write($"\r  Encoded: {framesEncoded}/60 frames | Bytes out: {totalBytes:N0}     ");
            }
            else
            {
                Console.Write($"\r  Encoded: {framesEncoded}/60 frames | Buffering...                ");
            }
        }

        var flushData = encoder.Flush();
        if (flushData != null) totalBytes += flushData.Length;

        double encodeElapsed = (DateTime.UtcNow - encodeStart).TotalSeconds;
        double encodeFps     = framesEncoded / encodeElapsed;

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"  Frames encoded  : {framesEncoded}");
        Console.WriteLine($"  Total H.264 data: {totalBytes:N0} bytes ({totalBytes / 1024.0:F1} KB)");
        Console.WriteLine($"  Encode time     : {encodeElapsed:F2}s");
        Console.WriteLine($"  Encode FPS      : {encodeFps:F1}");
        Console.WriteLine($"  Avg frame size  : {(framesEncoded > 0 ? totalBytes / framesEncoded : 0):N0} bytes");
        Console.WriteLine();

        if (totalBytes > 0)
            Console.WriteLine("✓ Step 2 PASSED — H.264 encoder is producing output!");
        else
            Console.WriteLine("⚠ Step 2 WARNING — encoder produced no output yet.");
    }
    catch (Exception ex)
    {
        PrintException(ex);
    }

    Console.WriteLine("\nPress any key to return to menu...");
    Console.ReadKey();
}

static void RunStreamServer()
{
    SafeClearConsole();
    Console.WriteLine("=================================================");
    Console.WriteLine(" TCP Socket Stream Server (Step 3)");
    Console.WriteLine("=================================================");

    var displays = GetDisplayNames();
    int selectedDisplay = 0;

    if (displays.Count > 1)
    {
        Console.WriteLine("\nMultiple displays detected:");
        for (int i = 0; i < displays.Count; i++)
        {
            Console.WriteLine($"  [{i}] {displays[i]}");
        }
        Console.WriteLine();
        Console.Write($"Select display to stream (0-{displays.Count - 1}, default 0): ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out int choice) && choice >= 0 && choice < displays.Count)
        {
            selectedDisplay = choice;
        }
        Console.WriteLine($"Selected: {displays[selectedDisplay]}\n");
    }

    using var server = new StreamServer(port: 7890, displayIndex: selectedDisplay);
    try
    {
        server.Start();
        Console.WriteLine("\nServer is running.");
        Console.WriteLine($"Pairing PIN: {server.Pin}");
        Console.WriteLine("Press 'q' and then [Enter] to shut down the server...");
        
        while (true)
        {
            var line = Console.ReadLine();
            if (line != null && line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[Server Error]: {ex.Message}");
    }
    finally
    {
        Console.WriteLine("Shutting down stream server...");
        server.Stop();
    }

    Console.WriteLine("\nPress any key to return to menu...");
    Console.ReadKey();
}

static List<string> GetDisplayNames()
{
    var list = new List<string>();
    try
    {
        DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).CheckError();
        factory!.EnumAdapters(0, out var adapter).CheckError();
        
        uint outputIndex = 0;
        while (true)
        {
            var result = adapter!.EnumOutputs(outputIndex, out var output);
            if (result.Failure) break;
            
            var desc = output!.Description;
            int width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            int height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            list.Add($"Display {outputIndex}: {width}x{height}");
            
            output.Dispose();
            outputIndex++;
        }
        
        adapter.Dispose();
        factory.Dispose();
    }
    catch
    {
        list.Clear();
        list.Add("Display 0 (Default)");
    }
    return list;
}

static void RunStreamClient()
{
    SafeClearConsole();
    Console.WriteLine("=================================================");
    Console.WriteLine(" TCP Socket Stream Client (Step 3)");
    Console.WriteLine("=================================================");

    Console.Write("Enter the 6-digit pairing PIN shown on the server: ");
    string? pin = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(pin) || pin.Length != 6)
    {
        Console.WriteLine("\nInvalid PIN. Must be exactly 6 digits.");
        Console.WriteLine("\nPress any key to return to menu...");
        Console.ReadKey();
        return;
    }

    var client = new StreamClient(host: "127.0.0.1", port: 7890);
    client.Run(pin);

    Console.WriteLine("Press any key to return to menu...");
    Console.ReadKey();
}

static void PrintException(Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"ERROR: {ex.GetType().Name}");
    Console.WriteLine($"  {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Common causes:");
    Console.WriteLine("  • Running inside RDP session (DXGI blocked)");
    Console.WriteLine("  • No H.264 encoder available (very old hardware/OS)");
    Console.WriteLine();
    Console.WriteLine(ex.StackTrace);
}
