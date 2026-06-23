using System;
using System.Threading;
using System.Threading.Tasks;
using ScreenBuddyCapture;

namespace ScreenBuddyTray;

public enum StreamStatus { Idle, Starting, Running, Connected, Error }

/// <summary>
/// Wraps the existing StreamServer on a background thread and exposes
/// simple Start/Stop methods with status events for the UI layer.
/// </summary>
public sealed class StreamManager : IDisposable
{
    private StreamServer? _server;
    private Thread?       _thread;
    private bool          _disposed;

    public StreamStatus Status         { get; private set; } = StreamStatus.Idle;
    public string       Pin            => _server?.Pin ?? "------";
    public string       ConnectedDevice { get; private set; } = "";
    public int          Port           => 7890;

    /// <summary>Raised on the thread-pool whenever the stream status changes.</summary>
    public event Action<StreamStatus>? StatusChanged;

    // ── Public API ─────────────────────────────────────────────────────────────

    public Task<bool> StartAsync(int displayIndex)
    {
        if (Status is StreamStatus.Running or StreamStatus.Connected or StreamStatus.Starting)
            return Task.FromResult(false);

        SetStatus(StreamStatus.Starting);

        try
        {
            _server = new StreamServer(Port, displayIndex);

            _thread = new Thread(() => RunServer(displayIndex))
            {
                IsBackground = true,
                Name         = "ScreenBuddy-StreamServer"
            };
            _thread.Start();

            SetStatus(StreamStatus.Running);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamManager] Start failed: {ex.Message}");
            SetStatus(StreamStatus.Error);
            return Task.FromResult(false);
        }
    }

    public void Stop()
    {
        try
        {
            _server?.Stop();
            _server = null;
            _thread = null;
        }
        catch { /* ignore shutdown errors */ }

        ConnectedDevice = "";
        SetStatus(StreamStatus.Idle);
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private void RunServer(int displayIndex)
    {
        try
        {
            _server!.Start();
            // Start() blocks until the server stops internally
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamManager] Server error: {ex.Message}");
            if (Status != StreamStatus.Idle)
                SetStatus(StreamStatus.Error);
        }
    }

    private void SetStatus(StreamStatus status)
    {
        Status = status;
        // Fire on thread-pool so callers don't need to worry about cross-thread marshalling here;
        // UI controls must use Invoke() themselves.
        Task.Run(() => StatusChanged?.Invoke(status));
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
