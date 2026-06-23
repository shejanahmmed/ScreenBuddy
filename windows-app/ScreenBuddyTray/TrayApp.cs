using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenBuddyTray;

/// <summary>
/// Application context that owns the system tray icon, context menu,
/// and the single StreamManager instance shared with the MainForm.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon    _trayIcon;
    private readonly StreamManager _streamManager;
    private          MainForm?     _mainForm;

    // Context menu items we need to toggle enabled state on
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;

    public TrayApp()
    {
        _streamManager = new StreamManager();
        _streamManager.StatusChanged += OnStreamStatusChanged;

        // ── Context menu ──────────────────────────────────────────────────────
        _startItem = new ToolStripMenuItem("▶  Start Streaming", null, OnStartStreaming);
        _stopItem  = new ToolStripMenuItem("■  Stop Streaming",  null, OnStopStreaming)
                     { Enabled = false };

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer()
        };
        menu.Items.Add(new ToolStripMenuItem("⬡  Open ScreenBuddy", null, OnOpen)
                       { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Launch on Startup", null, OnToggleStartup));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("✕  Exit", null, OnExit));

        // ── Tray icon ─────────────────────────────────────────────────────────
        _trayIcon = new NotifyIcon
        {
            Icon             = BuildTrayIcon(streaming: false),
            ContextMenuStrip = menu,
            Text             = "ScreenBuddy — Idle",
            Visible          = true
        };

        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnOpen(s, e);
        };

        // Show a welcome balloon the first time
        _trayIcon.BalloonTipTitle = "ScreenBuddy is running";
        _trayIcon.BalloonTipText  = "Right-click or double-click the tray icon to open the control panel.";
        _trayIcon.ShowBalloonTip(3000);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnOpen(object? sender, EventArgs e)
    {
        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_streamManager);
            _mainForm.FormClosed += (_, _) => _mainForm = null;
        }
        _mainForm.Show();
        _mainForm.BringToFront();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    private async void OnStartStreaming(object? sender, EventArgs e)
    {
        // If the main form is open, delegate to it so the display selector is used.
        // Otherwise start on display 0.
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
            _mainForm.StartStreaming();
        }
        else
        {
            await _streamManager.StartAsync(displayIndex: 0);
        }
    }

    private void OnStopStreaming(object? sender, EventArgs e)
    {
        _streamManager.Stop();
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        bool nowEnabled = !VddManager.IsStartupWithWindowsEnabled();
        VddManager.SetStartupWithWindows(nowEnabled);

        if (sender is ToolStripMenuItem item)
            item.Checked = nowEnabled;

        _trayIcon.ShowBalloonTip(2000,
            "ScreenBuddy",
            nowEnabled ? "Will now launch on Windows startup." : "Removed from startup.",
            ToolTipIcon.Info);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _streamManager.Stop();
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // ── Stream status handler ─────────────────────────────────────────────────

    private void OnStreamStatusChanged(StreamStatus status)
    {
        // Marshal back to UI thread
        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _trayIcon.ContextMenuStrip.Invoke(() => OnStreamStatusChanged(status));
            return;
        }

        _trayIcon.Icon = BuildTrayIcon(streaming: status != StreamStatus.Idle);

        _trayIcon.Text = status switch
        {
            StreamStatus.Idle      => "ScreenBuddy — Idle",
            StreamStatus.Starting  => "ScreenBuddy — Starting…",
            StreamStatus.Running   => $"ScreenBuddy — Waiting (PIN: {_streamManager.Pin})",
            StreamStatus.Connected => $"ScreenBuddy — Connected ✓",
            StreamStatus.Error     => "ScreenBuddy — Error",
            _                      => "ScreenBuddy"
        };

        _startItem.Enabled = status == StreamStatus.Idle || status == StreamStatus.Error;
        _stopItem.Enabled  = status is StreamStatus.Running or StreamStatus.Connected or StreamStatus.Starting;

        if (status == StreamStatus.Connected)
        {
            _trayIcon.ShowBalloonTip(2000, "ScreenBuddy",
                $"Device connected: {_streamManager.ConnectedDevice}", ToolTipIcon.Info);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Programmatically draws the tray icon so we need no .ico file.</summary>
    private static Icon BuildTrayIcon(bool streaming)
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Monitor body
        Color accent = streaming ? Color.FromArgb(52, 211, 153) : Color.FromArgb(99, 102, 241);
        using var bodyBrush = new SolidBrush(accent);
        g.FillRoundedRect(bodyBrush, new Rectangle(1, 3, 30, 20), 3);

        // Screen interior
        using var screenBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        g.FillRectangle(screenBrush, 3, 5, 26, 16);

        // Stand
        using var standBrush = new SolidBrush(accent);
        g.FillRectangle(standBrush, 12, 23, 8, 3);
        g.FillRectangle(standBrush, 8,  26, 16, 3);

        // Dot (streaming indicator)
        if (streaming)
        {
            using var dotBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
            g.FillEllipse(dotBrush, 12, 8, 8, 8);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mainForm?.Dispose();
            _trayIcon.Dispose();
            _streamManager.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ── Dark-themed context menu renderer ─────────────────────────────────────────

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color BgColor     = Color.FromArgb(15, 23, 42);
    private static readonly Color HoverColor  = Color.FromArgb(30, 41, 59);
    private static readonly Color BorderColor = Color.FromArgb(51, 65, 85);
    private static readonly Color TextColor   = Color.FromArgb(226, 232, 240);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(BgColor);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected)
            e.Graphics.FillRectangle(new SolidBrush(HoverColor), new Rectangle(Point.Empty, e.Item.Size));
        else
            e.Graphics.FillRectangle(new SolidBrush(BgColor), new Rectangle(Point.Empty, e.Item.Size));
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : Color.FromArgb(100, 116, 139);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(new Pen(BorderColor), 4, y, e.Item.Width - 4, y);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(new Pen(BorderColor), rect);
    }
}

// ── Graphics extension ────────────────────────────────────────────────────────

internal static class GraphicsEx
{
    public static void FillRoundedRect(this Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = RoundedRect(rect, radius);
        g.FillPath(brush, path);
    }

    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
