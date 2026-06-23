using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ScreenBuddyCapture;

/// <summary>
/// Encodes raw BGRA frames into H.264 using Windows Media Foundation.
/// Hardware-accelerated (Intel QSV / NVIDIA NVENC / AMD) with software fallback.
///
/// Per-frame pipeline:
///   byte[] BGRA → NV12 conversion → IMFSample → H.264 MFT → byte[] H.264 NAL units
/// </summary>
public sealed class H264Encoder : IDisposable
{
    // MFT category for video encoders
    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // MF media subtypes
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFMediaType_Video  = new("73646976-0000-0010-8000-00aa00389b71");

    // MF attribute GUIDs (mfapi.h)
    private static readonly Guid MF_MT_MAJOR_TYPE              = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE                 = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE              = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE              = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_AVG_BITRATE             = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE          = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO      = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    // MFT friendly name attribute (for diagnostics)
    private static readonly Guid MFT_FRIENDLY_NAME = new("3aecb0cc-035b-4bcc-8185-2b8d551ef4af");

    private readonly IMFTransform _encoder;
    private readonly int  _width;
    private readonly int  _height;
    private readonly int  _fps;
    private long _sampleTime = 0;
    private bool _disposed   = false;

    private const long MF_TICKS_PER_SECOND = 10_000_000L;

    public int Width  => _width;
    public int Height => _height;

    public H264Encoder(int width, int height, int fps = 30, int bitrateBps = 2_000_000)
    {
        _width  = width;
        _height = height;
        _fps    = fps;

        // 1. Start Windows Media Foundation
        MediaFactory.MFStartup();

        // 2. Enumerate H.264 encoder MFTs (hardware first, then software fallback)
        var outTypeInfo = new RegisterTypeInfo
        {
            GuidMajorType = MFMediaType_Video,
            GuidSubtype   = MFVideoFormat_H264,
        };

        MediaFactory.MFTEnumEx(
            guidCategory: MFT_CATEGORY_VIDEO_ENCODER,
            flags:        0,
            inputType:    null,
            outputType:   outTypeInfo,
            out IntPtr activatePtrs,
            out uint   count
        );

        if (count == 0)
            throw new InvalidOperationException(
                "No H.264 encoder MFT found. Ensure Windows is up-to-date and GPU drivers are installed.");

        // 3. Activate the first (highest priority) MFT encoder
        var firstActivatePtr = Marshal.ReadIntPtr(activatePtrs);
        var activate = new IMFActivate(firstActivatePtr);

        // Print the encoder name for diagnostics
        try
        {
            string? name = activate.GetAllocatedString(MFT_FRIENDLY_NAME);
            Console.WriteLine($"[H264Encoder] Using encoder: {name}");
        }
        catch
        {
            Console.WriteLine("[H264Encoder] MFT encoder activated.");
        }

        // ActivateObject: get the IMFTransform (generic overload picks the right interface)
        _encoder = activate.ActivateObject<IMFTransform>();
        activate.Dispose();
        Marshal.FreeCoTaskMem(activatePtrs);

        // 4. Set OUTPUT type (H.264) — required before setting input type on encoders
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MF_MT_MAJOR_TYPE,         MFMediaType_Video);
        outType.Set(MF_MT_SUBTYPE,            MFVideoFormat_H264);
        outType.Set(MF_MT_FRAME_SIZE,         PackHiLo((uint)width, (uint)height));
        outType.Set(MF_MT_FRAME_RATE,         PackHiLo((uint)fps, 1u));
        outType.Set(MF_MT_AVG_BITRATE,        (uint)bitrateBps);
        outType.Set(MF_MT_INTERLACE_MODE,     2u);   // MFVideoInterlace_Progressive
        outType.Set(MF_MT_PIXEL_ASPECT_RATIO, PackHiLo(1u, 1u));
        _encoder.SetOutputType(0, outType, 0);
        outType.Dispose();

        // 5. Set INPUT type (NV12 — the format H.264 HW encoders require)
        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MF_MT_MAJOR_TYPE,              MFMediaType_Video);
        inType.Set(MF_MT_SUBTYPE,                 MFVideoFormat_NV12);
        inType.Set(MF_MT_FRAME_SIZE,              PackHiLo((uint)width, (uint)height));
        inType.Set(MF_MT_FRAME_RATE,              PackHiLo((uint)fps, 1u));
        inType.Set(MF_MT_INTERLACE_MODE,          2u);
        inType.Set(MF_MT_PIXEL_ASPECT_RATIO,      PackHiLo(1u, 1u));
        inType.Set(MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
        _encoder.SetInputType(0, inType, 0);
        inType.Dispose();

        // 6. Signal streaming start
        _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream,  UIntPtr.Zero);

        Console.WriteLine($"[H264Encoder] Ready: {width}x{height} @ {fps}fps, {bitrateBps / 1000}kbps");
    }

    /// <summary>
    /// Encodes one BGRA frame to H.264.
    /// Returns encoded bytes, or null if the encoder is still buffering
    /// (normal for the first few frames of a GOP).
    /// </summary>
    public byte[]? EncodeFrame(byte[] bgraData)
    {
        if (bgraData.Length != _width * _height * 4)
            throw new ArgumentException($"Expected {_width * _height * 4} bytes, got {bgraData.Length}");

        // A. Convert BGRA → NV12 (BT.601 luma/chroma, software, ~5ms for 1080p)
        byte[] nv12 = BgraToNv12(bgraData, _width, _height);

        // B. Create IMFMediaBuffer from the NV12 bytes
        var mfBuf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        mfBuf.Lock(out IntPtr bufPtr, out _, out _);
        Marshal.Copy(nv12, 0, bufPtr, nv12.Length);
        mfBuf.Unlock();
        mfBuf.CurrentLength = nv12.Length;  // use the property, not method

        // C. Wrap in an IMFSample with PTS/duration
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(mfBuf);
        mfBuf.Dispose();

        long frameDur = MF_TICKS_PER_SECOND / _fps;
        sample.SampleTime     = _sampleTime;  // use property (not SetSampleTime method)
        sample.SampleDuration = frameDur;      // use property (not SetSampleDuration method)
        _sampleTime += frameDur;

        // D. Submit sample to encoder (ProcessInput returns void)
        _encoder.ProcessInput(0, sample, 0);
        sample.Dispose();

        // E. Pull encoded H.264 output
        return DrainOutput();
    }

    /// <summary>Drain remaining frames on shutdown.</summary>
    public byte[]? Flush()
    {
        _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageCommandDrain,      UIntPtr.Zero);
        return DrainOutput();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────

    private byte[]? DrainOutput()
    {
        var streamInfo   = _encoder.GetOutputStreamInfo(0);
        // Flags field is int in Vortice — cast the enum for comparison
        bool mftProvides = (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

        IMFSample? outSample = null;
        if (!mftProvides)
        {
            int bufSize = streamInfo.Size > 0 ? streamInfo.Size : _width * _height;
            var outBuf  = MediaFactory.MFCreateMemoryBuffer(bufSize);
            outSample   = MediaFactory.MFCreateSample();
            outSample.AddBuffer(outBuf);
            outBuf.Dispose();
        }

        var dataBuffer = new OutputDataBuffer
        {
            StreamID = 0,
            Sample   = outSample,
            Status   = 0,
            Events   = null,
        };

        var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out _);

        if (result.Failure)
        {
            outSample?.Dispose();
            return null;  // MF_E_TRANSFORM_NEED_MORE_INPUT — normal while buffering
        }

        var producedSample = dataBuffer.Sample;
        if (producedSample == null) return null;

        var contiguous = producedSample.ConvertToContiguousBuffer();
        contiguous.Lock(out IntPtr dataPtr, out _, out int currentLen);

        var bytes = new byte[currentLen];
        Marshal.Copy(dataPtr, bytes, 0, currentLen);

        contiguous.Unlock();
        contiguous.Dispose();
        producedSample.Dispose();

        return bytes.Length > 0 ? bytes : null;
    }

    /// <summary>
    /// Software BGRA → NV12 conversion (BT.601).
    /// NV12: Y plane (W×H bytes) + interleaved UV plane (W×H/2 bytes).
    /// </summary>
    private static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        int frameSize = width * height;
        var nv12      = new byte[frameSize + frameSize / 2];

        // Y plane (luma, one per pixel)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int si = (y * width + x) * 4;
                byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2];
                nv12[y * width + x] = (byte)Math.Clamp(16 + (66 * r + 129 * g + 25 * b) / 256, 16, 235);
            }
        }

        // UV plane (chroma, one Cb/Cr pair per 2×2 block)
        int uvBase = frameSize;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int si = (y * width + x) * 4;
                byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2];
                nv12[uvBase + (y / 2) * width + x]     = (byte)Math.Clamp(128 + (-38 * r - 74 * g + 112 * b) / 256, 16, 240);
                nv12[uvBase + (y / 2) * width + x + 1] = (byte)Math.Clamp(128 + (112 * r - 94 * g - 18 * b) / 256, 16, 240);
            }
        }

        return nv12;
    }

    /// <summary>Packs two uint32 into a uint64 for MF packed attributes (hi | lo).</summary>
    private static ulong PackHiLo(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _encoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
