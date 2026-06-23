using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Vortice.DXGI;

namespace ScreenBuddyTray;

/// <summary>
/// The ScreenBuddy control panel popup window.
/// Dark-themed, custom-drawn — no designer file required.
/// </summary>
public sealed class MainForm : Form
{
    // ── Colour palette ─────────────────────────────────────────────────────────
    private static readonly Color C_Bg        = Color.FromArgb(10,  15,  30);   // #0A0F1E
    private static readonly Color C_Surface   = Color.FromArgb(15,  23,  42);   // #0F172A
    private static readonly Color C_Surface2  = Color.FromArgb(22,  33,  57);   // #162139
    private static readonly Color C_Border    = Color.FromArgb(30,  41,  59);   // #1E293B
    private static readonly Color C_Accent    = Color.FromArgb(99,  102, 241);  // indigo
    private static readonly Color C_AccentHov = Color.FromArgb(129, 140, 248);
    private static readonly Color C_Green     = Color.FromArgb(52,  211, 153);  // emerald
    private static readonly Color C_Red       = Color.FromArgb(239, 68,  68);   // red-500
    private static readonly Color C_Text      = Color.FromArgb(226, 232, 240);  // slate-200
    private static readonly Color C_TextMuted = Color.FromArgb(100, 116, 139);  // slate-500

    // ── Fonts ──────────────────────────────────────────────────────────────────
    private readonly Font _fontTitle  = new("Segoe UI", 16f, FontStyle.Bold);
    private readonly Font _fontSub    = new("Segoe UI", 9f,  FontStyle.Regular);
    private readonly Font _fontLabel  = new("Segoe UI", 9f,  FontStyle.Bold);
    private readonly Font _fontNormal = new("Segoe UI", 9f,  FontStyle.Regular);
    private readonly Font _fontPin    = new("Consolas", 28f, FontStyle.Bold);
    private readonly Font _fontBig    = new("Segoe UI", 11f, FontStyle.Bold);

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly StreamManager _stream;
    private int _activeTab = 0;   // 0 = Displays, 1 = Stream, 2 = Settings

    // ── Displays tab controls ──────────────────────────────────────────────────
    private Panel       _displaysPanel = null!;
    private Panel       _displayListPanel = null!;
    private Label       _noDisplaysLabel = null!;
    private ComboBox    _resolutionCombo = null!;
    private DarkButton  _addDisplayBtn = null!;
    private DarkButton  _installDriverBtn = null!;
    private Label       _driverStatusLabel = null!;

    // ── Stream tab controls ────────────────────────────────────────────────────
    private Panel       _streamPanel = null!;
    private Label       _statusLabel = null!;
    private Label       _pinLabel = null!;
    private Label       _pinValueLabel = null!;
    private Label       _deviceLabel = null!;
    private DarkButton  _startBtn = null!;
    private DarkButton  _stopBtn = null!;
    private ComboBox    _displayCombo = null!;
    private Label       _displayComboLabel = null!;
    private Panel       _pinCard = null!;

    // ── Settings tab controls ──────────────────────────────────────────────────
    private Panel       _settingsPanel = null!;
    private CheckBox    _startupCheck = null!;
    private Panel       _contentContainer = null!;

    // ── Tab bar ────────────────────────────────────────────────────────────────
    private DarkButton[] _tabButtons = null!;

    // ── Constructor ────────────────────────────────────────────────────────────

    public MainForm(StreamManager stream)
    {
        _stream = stream;
        _stream.StatusChanged += OnStreamStatusChanged;

        InitializeWindow();
        BuildLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshDisplaysTab();
        RefreshStreamTab();
        RefreshAllDisplayCombos();
    }

    // ── Window setup ───────────────────────────────────────────────────────────

    private void InitializeWindow()
    {
        Text            = "ScreenBuddy";
        Size            = new Size(520, 620);
        MinimumSize     = new Size(520, 620);
        MaximumSize     = new Size(520, 620);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = C_Bg;
        ForeColor       = C_Text;
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered  = true;
        ShowInTaskbar   = true;
        AutoScaleMode   = AutoScaleMode.Dpi;

        // Custom border via Paint
        Paint += (_, e) =>
        {
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var pen = new Pen(C_Border, 1);
            e.Graphics.DrawRectangle(pen, r);
        };

        // Allow dragging by the header area
        _dragEnabled = true;
    }

    private int ScaleX(int x) => (int)(x * (DeviceDpi / 96.0f));
    private int ScaleY(int y) => (int)(y * (DeviceDpi / 96.0f));

    // ── Drag-to-move (borderless form) ────────────────────────────────────────

    private bool   _dragEnabled;
    private bool   _dragging;
    private Point  _dragStart;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_dragEnabled && e.Y < 70 && e.Button == MouseButtons.Left)
        {
            _dragging  = true;
            _dragStart = e.Location;
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragStart.X,
                                 Location.Y + e.Y - _dragStart.Y);
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    // ── Layout builder ─────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        SuspendLayout();

        // ── Header ────────────────────────────────────────────────────────────
        var header = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = ScaleY(80),
            Width     = Width, // Explicitly set width so child anchoring works correctly
            BackColor = C_Surface,
        };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.FillRectangle(new SolidBrush(C_Surface), e.ClipRectangle);

            // Accent left bar
            using var accentBrush = new SolidBrush(C_Accent);
            g.FillRectangle(accentBrush, 0, 0, ScaleX(4), header.Height);

            // Scale logo coordinates
            int iconX = ScaleX(18);
            int iconY = ScaleY(18);
            int iconSize = ScaleX(44);
            int titleX = ScaleX(76);
            int titleY = ScaleY(14);
            int subX = ScaleX(78);
            int subY = ScaleY(46);

            // Logo icon (monitor)
            DrawMonitorIcon(g, new Point(iconX, iconY), iconSize, C_Accent);

            // Title & Subtitle (No overlap)
            using var textBrush = new SolidBrush(C_Text);
            using var mutedBrush = new SolidBrush(C_TextMuted);
            g.DrawString("ScreenBuddy", _fontTitle, textBrush, titleX, titleY);
            g.DrawString("Virtual Display & Stream Manager", _fontSub, mutedBrush, subX, subY);
        };

        // Close button (Anchored correctly relative to header width)
        var closeBtn = new Button
        {
            Text      = "✕",
            Size      = new Size(32, 32),
            Location  = new Point(header.Width - 44, 24),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Hand,
            Font      = new Font("Segoe UI", 11f)
        };
        closeBtn.FlatAppearance.BorderSize     = 0;
        closeBtn.FlatAppearance.MouseOverBackColor  = C_Surface2;
        closeBtn.FlatAppearance.MouseDownBackColor  = C_Border;
        closeBtn.Click += (_, _) => Close();
        header.Controls.Add(closeBtn);

        // ── Tab bar ───────────────────────────────────────────────────────────
        var tabBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 46,
            BackColor = C_Surface,
        };
        tabBar.Paint += (_, e) =>
        {
            using var pen = new Pen(C_Border, 1);
            e.Graphics.DrawLine(pen, 0, tabBar.Height - 1, tabBar.Width, tabBar.Height - 1);
        };

        // Clean, modern text titles without emoji fallback squares
        string[] tabNames  = { "Displays", "Stream", "Settings" };
        _tabButtons = new DarkButton[tabNames.Length];
        int tx = 8;
        for (int i = 0; i < tabNames.Length; i++)
        {
            int idx = i;
            var btn = new DarkButton
            {
                Text      = tabNames[idx],
                Size      = new Size(130, 34),
                Location  = new Point(tx, 6),
                Font      = _fontLabel,
                IsTab     = true,
                IsActive  = idx == 0,
            };
            btn.Click += (_, _) => SwitchTab(idx);
            _tabButtons[idx] = btn;
            tabBar.Controls.Add(btn);
            tx += 136;
        }

        // ── Content container ──────────────────────────────────────────────────
        _contentContainer = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = C_Bg,
        };

        // ── Content area ──────────────────────────────────────────────────────
        BuildDisplaysTab();
        BuildStreamTab();
        BuildSettingsTab();

        Controls.Add(_contentContainer);
        Controls.Add(tabBar);
        Controls.Add(header);

        // Set explicit Z-order layout (front-most to back-most):
        // index 0 = contentContainer (Fill)
        // index 1 = tabBar (Dock.Top, docks below header)
        // index 2 = header (Dock.Top, docks at the very top of form)
        Controls.SetChildIndex(_contentContainer, 0);
        Controls.SetChildIndex(tabBar, 1);
        Controls.SetChildIndex(header, 2);

        ResumeLayout(true);
    }

    private void SwitchTab(int index)
    {
        _activeTab = index;
        for (int i = 0; i < _tabButtons.Length; i++)
            _tabButtons[i].IsActive = i == index;

        _displaysPanel.Visible = index == 0;
        _streamPanel.Visible   = index == 1;
        _settingsPanel.Visible = index == 2;

        if (index == 0) { _displaysPanel.BringToFront(); RefreshDisplaysTab(); }
        if (index == 1) { _streamPanel.BringToFront(); RefreshAllDisplayCombos(); }
        if (index == 2) { _settingsPanel.BringToFront(); }
    }

    // ── Displays tab ──────────────────────────────────────────────────────────

    private void BuildDisplaysTab()
    {
        _displaysPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = C_Bg,
            Padding   = new Padding(16),
        };

        // Driver status bar
        var driverBar = new Panel
        {
            Height    = 52,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Padding   = new Padding(12, 10, 12, 10),
        };
        _driverStatusLabel = new Label
        {
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = _fontNormal,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };
        _installDriverBtn = new DarkButton
        {
            Text     = "Install Driver",
            Size     = new Size(110, 28),
            Dock     = DockStyle.Right,
            IsSmall  = true,
        };
        _installDriverBtn.Click += OnInstallDriver;
        driverBar.Controls.Add(_driverStatusLabel);
        driverBar.Controls.Add(_installDriverBtn);
        _displaysPanel.Controls.Add(driverBar);

        // Spacer
        _displaysPanel.Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top, BackColor = C_Bg });

        // "Add display" row
        var addRow = new Panel
        {
            Height    = 48,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Padding   = new Padding(12, 8, 8, 8),
        };
        _resolutionCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 180,
            Dock          = DockStyle.Left,
            BackColor     = C_Surface2,
            ForeColor     = C_Text,
            FlatStyle     = FlatStyle.Flat,
            Font          = _fontNormal,
        };
        _resolutionCombo.Items.AddRange(new object[]
        {
            "720p  — 1280×720",
            "1080p — 1920×1080",
            "1440p — 2560×1440",
            "Portrait 9:16 — 1080×1920",
            "Portrait 9:16 — 720×1280",
            "Square — 1080×1080",
        });
        _resolutionCombo.SelectedIndex = 1; // default 1080p

        _addDisplayBtn = new DarkButton
        {
            Text    = "+ Add Virtual Display",
            Size    = new Size(160, 32),
            Dock    = DockStyle.Right,
            IsGreen = false,
        };
        _addDisplayBtn.Click += OnAddVirtualDisplay;
        addRow.Controls.Add(_resolutionCombo);
        addRow.Controls.Add(_addDisplayBtn);
        _displaysPanel.Controls.Add(addRow);

        // Spacer
        _displaysPanel.Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top, BackColor = C_Bg });

        // Section label
        var sectionLabel = new Label
        {
            Text      = "ACTIVE VIRTUAL DISPLAYS",
            Dock      = DockStyle.Top,
            Height    = 24,
            ForeColor = C_TextMuted,
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Regular),
            TextAlign = ContentAlignment.BottomLeft,
            Padding   = new Padding(2, 0, 0, 4),
        };
        _displaysPanel.Controls.Add(sectionLabel);

        // Virtual display list (Standard Panel + AutoScroll for robust Top-docked layout)
        _displayListPanel = new Panel
        {
            Dock       = DockStyle.Fill,
            AutoScroll = true,
            BackColor  = C_Bg,
            Padding    = new Padding(0, 4, 0, 0),
        };
        _noDisplaysLabel = new Label
        {
            Text      = "No virtual displays active.\nClick '+ Add Virtual Display' to create one.",
            Dock      = DockStyle.Fill,
            ForeColor = C_TextMuted,
            Font      = _fontNormal,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        _displayListPanel.Controls.Add(_noDisplaysLabel);
        _displaysPanel.Controls.Add(_displayListPanel);

        _contentContainer.Controls.Add(_displaysPanel);
        UpdateDriverStatusLabel();
    }

    private void RefreshDisplaysTab()
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { Invoke(RefreshDisplaysTab); return; }

        UpdateDriverStatusLabel();
        var virtuals = VddManager.GetVirtualDisplays();

        _displayListPanel.SuspendLayout();
        _displayListPanel.Controls.Clear();

        if (virtuals.Count == 0)
        {
            _displayListPanel.Controls.Add(_noDisplaysLabel);
        }
        else
        {
            foreach (var vd in virtuals)
            {
                var card = BuildDisplayCard(vd);
                _displayListPanel.Controls.Add(card);
                card.SendToBack(); // stacks cleanly in Top dock order
            }
        }
        _displayListPanel.ResumeLayout(true);
    }

    private Panel BuildDisplayCard(VirtualDisplayInfo vd)
    {
        var wrapper = new Panel
        {
            Height = 72,
            Dock   = DockStyle.Top,
            Width  = 488,
            BackColor = C_Bg,
            Padding = new Padding(0, 0, 0, 8),
        };

        var card = new Panel
        {
            Dock      = DockStyle.Fill,
            Width  = 488,
            BackColor = C_Surface,
        };

        card.Paint += (_, e) =>
        {
            using var pen = new Pen(C_Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            using var accentBrush = new SolidBrush(C_Accent);
            e.Graphics.FillRectangle(accentBrush, 0, 0, 4, card.Height);
        };
        card.Resize += (_, _) => card.Invalidate();

        var nameLabel = new Label
        {
            Text      = vd.Name,
            Location  = new Point(16, 12),
            AutoSize  = true,
            Font      = _fontBig,
            ForeColor = C_Text,
            BackColor = Color.Transparent,
        };
        var specLabel = new Label
        {
            Text      = $"{vd.Width}×{vd.Height}  @{vd.RefreshRate}Hz",
            Location  = new Point(16, 36),
            AutoSize  = true,
            Font      = _fontNormal,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };
        var removeBtn = new DarkButton
        {
            Text     = "Remove",
            Size     = new Size(84, 28),
            Location = new Point(360, 18),
            IsSmall  = true,
            IsDanger = true,
            Anchor   = AnchorStyles.Right | AnchorStyles.Top,
        };
        int capturedId = vd.Id;
        removeBtn.Click += (_, _) =>
        {
            if (MessageBox.Show($"Remove {vd.Name}?", "ScreenBuddy",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                VddManager.RemoveVirtualDisplay(capturedId);
                RefreshDisplaysTab();
                RefreshAllDisplayCombos();
            }
        };

        card.Controls.Add(nameLabel);
        card.Controls.Add(specLabel);
        card.Controls.Add(removeBtn);
        wrapper.Controls.Add(card);
        return wrapper;
    }

    private void UpdateDriverStatusLabel()
    {
        bool installed = VddManager.IsDriverInstalled();
        _driverStatusLabel.Text      = installed
            ? "✓  Virtual Display Driver installed"
            : "⚠  Virtual Display Driver not installed";
        _driverStatusLabel.ForeColor = installed ? C_Green : Color.FromArgb(251, 191, 36);
        _installDriverBtn.Visible    = !installed;
    }

    private async void OnInstallDriver(object? sender, EventArgs e)
    {
        _installDriverBtn.Enabled = false;
        _installDriverBtn.Text    = "Installing…";
        bool ok = await VddManager.EnsureDriverInstalledAsync();
        _installDriverBtn.Text    = ok ? "Installed ✓" : "Install Driver";
        _installDriverBtn.Enabled = !ok;
        UpdateDriverStatusLabel();
    }

    private void OnAddVirtualDisplay(object? sender, EventArgs e)
    {
        var (w, h) = _resolutionCombo.SelectedIndex switch
        {
            0 => (1280, 720),
            1 => (1920, 1080),
            2 => (2560, 1440),
            3 => (1080, 1920),
            4 => (720,  1280),
            5 => (1080, 1080),
            _ => (1920, 1080)
        };

        if (!VddManager.IsDriverInstalled())
        {
            MessageBox.Show("Please install the Virtual Display Driver first.",
                "ScreenBuddy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _addDisplayBtn.Enabled = false;
        _addDisplayBtn.Text    = "Adding…";
        bool ok = VddManager.AddVirtualDisplay(w, h);
        _addDisplayBtn.Enabled = true;
        _addDisplayBtn.Text    = "+ Add Virtual Display";

        if (ok)
        {
            RefreshDisplaysTab();
            RefreshAllDisplayCombos();
        }
    }

    // ── Stream tab ─────────────────────────────────────────────────────────────

    private void BuildStreamTab()
    {
        _streamPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = C_Bg,
            Visible   = false,
        };

        // Status card at top
        var statusCard = new Panel
        {
            Height    = 80,
            Dock      = DockStyle.Top,
            Width     = Width,
            BackColor = C_Surface,
            Padding   = new Padding(16, 12, 16, 12),
        };
        statusCard.Paint += (_, e) =>
        {
            using var pen = new Pen(C_Border);
            e.Graphics.DrawLine(pen, 0, statusCard.Height - 1,
                                statusCard.Width, statusCard.Height - 1);
        };
        statusCard.Resize += (_, _) => statusCard.Invalidate();

        _statusLabel = new Label
        {
            Text      = "● Not Streaming",
            Location  = new Point(16, 14),
            AutoSize  = true,
            Font      = _fontBig,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };
        _deviceLabel = new Label
        {
            Text     = "",
            Location = new Point(16, 38),
            AutoSize = true,
            Font     = _fontNormal,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };

        // Start/Stop buttons
        _startBtn = new DarkButton
        {
            Text     = "▶  Start Streaming",
            Size     = new Size(160, 38),
            Location = new Point(320, 21),
            Anchor   = AnchorStyles.Top | AnchorStyles.Right,
            IsGreen  = true,
        };
        _startBtn.Click += OnStartBtn;

        _stopBtn = new DarkButton
        {
            Text     = "■  Stop",
            Size     = new Size(100, 38),
            Location = new Point(380, 21),
            Anchor   = AnchorStyles.Top | AnchorStyles.Right,
            IsDanger = true,
            Visible  = false,
        };
        _stopBtn.Click += (_, _) => _stream.Stop();

        statusCard.Controls.AddRange(new Control[]
            { _statusLabel, _deviceLabel, _startBtn, _stopBtn });
        _streamPanel.Controls.Add(statusCard);

        // Spacer
        _streamPanel.Controls.Add(new Panel
            { Height = 8, Dock = DockStyle.Top, BackColor = C_Bg });

        // Display selector row
        var displayRow = new Panel
        {
            Height    = 52,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Padding   = new Padding(16, 10, 16, 10),
        };
        _displayComboLabel = new Label
        {
            Text      = "Stream display:",
            AutoSize  = false,
            Width     = 110,
            Dock      = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = _fontLabel,
            ForeColor = C_Text,
            BackColor = Color.Transparent,
        };
        _displayCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock          = DockStyle.Fill,
            BackColor     = C_Surface2,
            ForeColor     = C_Text,
            FlatStyle     = FlatStyle.Flat,
            Font          = _fontNormal,
        };
        displayRow.Controls.Add(_displayCombo);
        displayRow.Controls.Add(_displayComboLabel);
        _streamPanel.Controls.Add(displayRow);

        // Spacer
        _streamPanel.Controls.Add(new Panel
            { Height = 8, Dock = DockStyle.Top, BackColor = C_Bg });

        // PIN card (Docks children to top with centering to prevent horizontal misalignments)
        _pinCard = new Panel
        {
            Height    = 120,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Visible   = false,
        };
        _pinCard.Paint += DrawPinCard;
        _pinCard.Resize += (_, _) => _pinCard.Invalidate();

        _pinLabel = new Label
        {
            Text      = "PAIRING PIN",
            Dock      = DockStyle.Top,
            Height    = 24,
            TextAlign = ContentAlignment.BottomCenter,
            Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };
        _pinValueLabel = new Label
        {
            Text      = "------",
            Dock      = DockStyle.Top,
            Height    = 64,
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = _fontPin,
            ForeColor = C_Accent,
            BackColor = Color.Transparent,
        };
        var pinHint = new Label
        {
            Text      = "Enter this PIN in the ScreenBuddy app on your phone",
            Dock      = DockStyle.Top,
            Height    = 28,
            TextAlign = ContentAlignment.TopCenter,
            Font      = _fontNormal,
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
        };
        _pinCard.Controls.AddRange(new Control[] { pinHint, _pinValueLabel, _pinLabel }); // reverse order so they dock correctly from top to bottom
        _streamPanel.Controls.Add(_pinCard);

        // Instructions
        var hints = new Label
        {
            Text      = "1. Click Start Streaming\n2. Open ScreenBuddy on your Android phone\n" +
                        "3. Enter the PIN shown above or tap 'Auto-Discover'",
            Dock      = DockStyle.Bottom,
            Height    = 64,
            Padding   = new Padding(16, 0, 16, 0),
            Font      = _fontNormal,
            ForeColor = C_TextMuted,
            BackColor = C_Bg,
        };
        _streamPanel.Controls.Add(hints);

        _contentContainer.Controls.Add(_streamPanel);
    }

    private void DrawPinCard(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(C_Surface);
        using var pen = new Pen(C_Border);
        g.DrawLine(pen, 0, 0, _pinCard.Width, 0);

        // Glow behind PIN digits
        using var glowBrush = new SolidBrush(Color.FromArgb(20, C_Accent));
        g.FillRectangle(glowBrush, new RectangleF(0, 40, _pinCard.Width, 64));
    }

    private void RefreshStreamTab()
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { Invoke(RefreshStreamTab); return; }

        var s = _stream.Status;
        bool streaming  = s is StreamStatus.Running or StreamStatus.Connected or StreamStatus.Starting;
        bool connected  = s == StreamStatus.Connected;

        _statusLabel.Text = s switch
        {
            StreamStatus.Idle      => "● Not Streaming",
            StreamStatus.Starting  => "◌ Starting…",
            StreamStatus.Running   => "● Waiting for phone…",
            StreamStatus.Connected => "● Connected",
            StreamStatus.Error     => "● Error — see console",
            _                      => "● Unknown"
        };
        _statusLabel.ForeColor = s switch
        {
            StreamStatus.Running   => Color.FromArgb(251, 191, 36),
            StreamStatus.Connected => C_Green,
            StreamStatus.Error     => C_Red,
            _                      => C_TextMuted
        };

        _deviceLabel.Text      = connected ? $"Device: {_stream.ConnectedDevice}" : "";
        _pinValueLabel.Text    = _stream.Pin;
        _pinCard.Visible       = streaming;
        _startBtn.Visible      = !streaming;
        _stopBtn.Visible       = streaming;
    }

    private void RefreshAllDisplayCombos()
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { Invoke(RefreshAllDisplayCombos); return; }

        var displays = GetAllDisplayNames();
        _displayCombo.Items.Clear();
        foreach (var d in displays) _displayCombo.Items.Add(d);
        if (_displayCombo.Items.Count > 0) _displayCombo.SelectedIndex = 0;
    }

    private static List<string> GetAllDisplayNames()
    {
        var list = new List<string>();
        try
        {
            DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).CheckError();
            factory!.EnumAdapters(0, out var adapter).CheckError();
            uint i = 0;
            while (true)
            {
                var result = adapter!.EnumOutputs(i, out var output);
                if (result.Failure) break;
                var desc = output!.Description;
                int w    = desc.DesktopCoordinates.Right  - desc.DesktopCoordinates.Left;
                int h    = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                list.Add($"Display {i}: {w}×{h}{(i == 0 ? " (Primary)" : "")}");
                output.Dispose();
                i++;
            }
            adapter.Dispose();
            factory.Dispose();
        }
        catch
        {
            list.Clear();
            list.Add("Display 0 (Primary)");
        }
        return list;
    }

    public async void StartStreaming()
    {
        int displayIndex = _displayCombo.SelectedIndex < 0 ? 0 : _displayCombo.SelectedIndex;
        await _stream.StartAsync(displayIndex);
    }

    private void OnStartBtn(object? sender, EventArgs e) => StartStreaming();

    // ── Settings tab ──────────────────────────────────────────────────────────

    private void BuildSettingsTab()
    {
        _settingsPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = C_Bg,
            Visible   = false,
            Padding   = new Padding(16),
        };

        var card = new Panel
        {
            Height    = 56,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Padding   = new Padding(16, 0, 16, 0),
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(C_Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        card.Resize += (_, _) => card.Invalidate();

        _startupCheck = new CheckBox
        {
            Text      = "Launch ScreenBuddy when Windows starts",
            Dock      = DockStyle.Fill,
            Font      = _fontNormal,
            ForeColor = C_Text,
            BackColor = Color.Transparent,
            Checked   = VddManager.IsStartupWithWindowsEnabled(),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _startupCheck.CheckedChanged += (_, _) =>
            VddManager.SetStartupWithWindows(_startupCheck.Checked);

        card.Controls.Add(_startupCheck);
        _settingsPanel.Controls.Add(card);

        // Spacer
        _settingsPanel.Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top, BackColor = C_Bg });

        // Info card
        var info = new Panel
        {
            Height    = 100,
            Dock      = DockStyle.Top,
            BackColor = C_Surface,
            Margin    = new Padding(0, 8, 0, 0),
        };
        info.Paint += (_, e) =>
        {
            e.Graphics.Clear(C_Surface);
            using var pen = new Pen(C_Border);
            e.Graphics.DrawRectangle(pen, 0, 0, info.Width - 1, info.Height - 1);
            using var accentBrush = new SolidBrush(C_Accent);
            e.Graphics.FillRectangle(accentBrush, 0, 0, 3, info.Height);

            var text =
                "TCP stream port: 7890\n" +
                "UDP auto-discover port: 7891\n" +
                "Protocol: H.264 over raw TCP with 6-digit PIN auth";
            TextRenderer.DrawText(e.Graphics, text, _fontNormal,
                new Rectangle(14, 12, info.Width - 20, info.Height - 12), C_TextMuted,
                TextFormatFlags.WordBreak);
        };
        info.Resize += (_, _) => info.Invalidate();
        _settingsPanel.Controls.Add(info);

        _contentContainer.Controls.Add(_settingsPanel);
    }

    // ── Stream status handler ─────────────────────────────────────────────────

    private void OnStreamStatusChanged(StreamStatus status)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired)
        {
            Invoke(() => OnStreamStatusChanged(status));
            return;
        }
        RefreshStreamTab();
    }

    // ── Monitor icon drawing helper ────────────────────────────────────────────

    private static void DrawMonitorIcon(Graphics g, Point origin, int size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int s = size;
        using var pen   = new Pen(color, 2);
        using var brush = new SolidBrush(Color.FromArgb(40, color));

        var monitorRect = new Rectangle(origin.X, origin.Y, s, (int)(s * 0.65f));
        using var path  = GraphicsEx.RoundedRect(monitorRect, 3);
        g.DrawPath(pen, path);
        g.FillPath(brush, path);

        // Stand
        int standX = origin.X + s / 2 - 3;
        g.DrawLine(pen, standX, origin.Y + monitorRect.Height,
                   standX, origin.Y + monitorRect.Height + 6);
        g.DrawLine(pen, origin.X + s / 2 - 8,
                   origin.Y + monitorRect.Height + 6,
                   origin.X + s / 2 + 8,
                   origin.Y + monitorRect.Height + 6);
    }

    // ── Dispose ────────────────────────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _stream.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.StatusChanged -= OnStreamStatusChanged;
            _fontTitle.Dispose(); _fontSub.Dispose();
            _fontLabel.Dispose(); _fontNormal.Dispose();
            _fontPin.Dispose();   _fontBig.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ── Custom dark-themed button ──────────────────────────────────────────────────

internal sealed class DarkButton : Control
{
    private static readonly Color BgNormal  = Color.FromArgb(30,  41, 59);
    private static readonly Color BgHover   = Color.FromArgb(51,  65, 85);
    private static readonly Color BgPress   = Color.FromArgb(15,  23, 42);
    private static readonly Color BgGreen   = Color.FromArgb(5,   150, 105);
    private static readonly Color BgGreenH  = Color.FromArgb(16,  185, 129);
    private static readonly Color BgDanger  = Color.FromArgb(127, 29,  29);
    private static readonly Color BgDangerH = Color.FromArgb(185, 28,  28);
    private static readonly Color BgTab     = Color.FromArgb(22,  33, 57);
    private static readonly Color AccentCol = Color.FromArgb(99, 102, 241);

    public bool IsGreen  { get; set; }
    public bool IsDanger { get; set; }
    public bool IsTab    { get; set; }
    public bool IsSmall  { get; set; }

    private bool _active;
    public bool IsActive
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }

    private bool _hover, _pressed;

    public DarkButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        Font   = new Font("Segoe UI", 9f, FontStyle.Regular);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover   = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover   = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)  { _pressed = true;  Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)    { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g   = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

        Color bg;
        if (IsTab)
        {
            bg = _active ? BgTab : Color.Transparent;
        }
        else if (IsGreen)
        {
            bg = _hover || _pressed ? BgGreenH : BgGreen;
        }
        else if (IsDanger)
        {
            bg = _hover || _pressed ? BgDangerH : BgDanger;
        }
        else
        {
            bg = _hover || _pressed ? BgHover : BgNormal;
        }

        int radius = IsSmall ? 4 : 6;
        var rect   = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = GraphicsEx.RoundedRect(rect, radius);

        // Fill
        if (bg != Color.Transparent)
        {
            using var bgBrush = new SolidBrush(bg);
            g.FillPath(bgBrush, path);
        }

        // Tab active accent underline
        if (IsTab && _active)
        {
            using var accentPen = new Pen(AccentCol, 2);
            g.DrawLine(accentPen, 6, Height - 2, Width - 6, Height - 2);
        }

        // Border (non-tab)
        if (!IsTab)
        {
            var borderColor = IsGreen ? BgGreenH : IsDanger ? BgDangerH :
                              _hover  ? Color.FromArgb(99, 102, 241) : Color.FromArgb(30, 41, 59);
            using var pen = new Pen(borderColor, 1);
            g.DrawPath(pen, path);
        }

        // Text
        var textColor = !Enabled
            ? Color.FromArgb(71, 85, 105)
            : IsTab && !_active
            ? Color.FromArgb(100, 116, 139)
            : Color.FromArgb(226, 232, 240);

        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(Text, Font, new SolidBrush(textColor),
                     new RectangleF(0, 0, Width, Height), sf);
    }
}
