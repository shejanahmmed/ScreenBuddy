using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenBuddyCapture;

/// <summary>
/// Captures the Windows desktop using the DXGI Desktop Duplication API.
/// Same mechanism used by OBS, Discord, etc. Hardware-accelerated, low CPU overhead.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private ID3D11Device?         _device;
    private ID3D11DeviceContext?  _context;
    private IDXGIOutputDuplication? _duplication;
    private bool _disposed = false;

    public int Width  { get; private set; }
    public int Height { get; private set; }

    public ScreenCapture(int displayIndex = 0)
    {
        // ─────────────────────────────────────────────────────────────────
        // Step 1: Create a DXGI factory to enumerate GPUs
        // ─────────────────────────────────────────────────────────────────
        DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).CheckError();

        // ─────────────────────────────────────────────────────────────────
        // Step 2: Get adapter (GPU) 0 using the raw out-param API
        // ─────────────────────────────────────────────────────────────────
        factory!.EnumAdapters(0, out var adapter).CheckError();

        // ─────────────────────────────────────────────────────────────────
        // Step 3: Get the target output (monitor) using its index
        // ─────────────────────────────────────────────────────────────────
        adapter!.EnumOutputs((uint)displayIndex, out var output).CheckError();
        var output1 = output!.QueryInterface<IDXGIOutput1>();

        // Read the monitor's pixel resolution from its description
        var desc = output!.Description;
        Width  = desc.DesktopCoordinates.Right  - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        // ─────────────────────────────────────────────────────────────────
        // Step 4: Create a D3D11 device on the SAME adapter as this output
        //         (required — DuplicateOutput fails if devices are mismatched)
        // ─────────────────────────────────────────────────────────────────
        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,           // must be Unknown when adapter is provided
            DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out _device!,
            out _context!
        ).CheckError();

        // ─────────────────────────────────────────────────────────────────
        // Step 5: Open the Desktop Duplication session
        //         AcquireNextFrame() will give us raw GPU frames
        // ─────────────────────────────────────────────────────────────────
        _duplication = output1.DuplicateOutput(_device);

        Console.WriteLine($"[ScreenCapture] Ready. Monitor {displayIndex}: {Width}x{Height}");

        // Cleanup intermediate DXGI objects (we only need _device, _context, _duplication)
        output1.Dispose();
        output.Dispose();
        adapter.Dispose();
        factory.Dispose();
    }

    /// <summary>
    /// Captures the current desktop frame as a Bitmap (BGRA, full resolution).
    /// Returns null if no new frame arrived within the timeout
    /// (screen has not changed — totally normal).
    /// </summary>
    public Bitmap? CaptureFrame(int timeoutMs = 100)
    {
        var result = _duplication!.AcquireNextFrame(
            timeoutInMilliseconds: (uint)timeoutMs,
            frameInfo: out _,
            desktopResource: out var desktopResource
        );

        // DXGI_ERROR_WAIT_TIMEOUT → screen unchanged → not an error, return null
        if (result.Failure)
        {
            desktopResource?.Dispose();
            return null;
        }

        try
        {
            using var desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();

            // ─────────────────────────────────────────────────────────────
            // Create a CPU-readable staging texture to copy GPU data into
            // ─────────────────────────────────────────────────────────────
            var stagingDesc = new Texture2DDescription
            {
                Width             = (uint)Width,
                Height            = (uint)Height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage             = ResourceUsage.Staging,
                BindFlags         = BindFlags.None,
                MiscFlags         = ResourceOptionFlags.None,
                CPUAccessFlags    = CpuAccessFlags.Read,
            };

            using var staging = _device!.CreateTexture2D(stagingDesc);

            // GPU texture → staging texture
            _context!.CopyResource(staging, desktopTexture);

            // Map staging texture into CPU-accessible memory
            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            // Copy pixel data row-by-row into a Bitmap
            // (GPU rows may have pitch padding, so we can't do a single memcpy)
            var bitmap     = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, Width, Height),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb
            );

            int rowBytes = Width * 4;
            for (int row = 0; row < Height; row++)
            {
                var src = mapped.DataPointer + row * (int)mapped.RowPitch;
                var dst = bitmapData.Scan0   + row * bitmapData.Stride;
                unsafe
                {
                    Buffer.MemoryCopy(src.ToPointer(), dst.ToPointer(), rowBytes, rowBytes);
                }
            }

            bitmap.UnlockBits(bitmapData);
            _context.Unmap(staging, 0);

            return bitmap;
        }
        finally
        {
            desktopResource?.Dispose();
            // ALWAYS release the frame, or future AcquireNextFrame calls will fail
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
