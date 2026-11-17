using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text.Json;

namespace FileShare.Win;

class MainForm : Form
{
    readonly ListBox _devices = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly ListBox _queueList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly TextBox _txtIp = new() { Width = 160, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox _txtDir = new() { Width = 260, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = Color.FromArgb(0x6B, 0x72, 0x80) };
    readonly FlatBar _bar = new() { Dock = DockStyle.Bottom, Height = 6, Margin = new Padding(16, 4, 16, 6) };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _partials = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(0x6B, 0x72, 0x80) };
    // live receive strip (incoming transfers) and upload-speed line
    readonly Panel _recvStrip = new() { Dock = DockStyle.Bottom, Height = 46, Visible = false, Padding = new Padding(16, 4, 16, 2) };
    readonly Label _recvInfo = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly FlatBar _recvBar = new() { Dock = DockStyle.Bottom, Height = 6, Margin = new Padding(16, 0, 16, 2) };
    readonly Label _sendSpeed = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(0x6B, 0x72, 0x80), Visible = false, Margin = new Padding(16, 2, 0, 0) };
    double _recvSpeedBps;
    long _lastRecvBytes;
    DateTime _lastRecvTick = DateTime.UtcNow;
    readonly Button _btnSend = MakeBtn("Send to selected device", 180, primary: true);
    readonly Button _btnPause = MakeBtn("Pause", 92);
    readonly Button _btnCancel = MakeBtn("Cancel", 92);

    Discovery _discovery;
    Receiver _receiver = null!;
    readonly List<TransferItem> _queue = new();
    readonly List<Peer> _manual = new();
    List<Peer> _shown = new();
    int _lastPeerCount;
    CancellationTokenSource? _sendCts;
    CancellationTokenSource? _pauseCts;
    bool _running, _paused;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    int _tickCount;

    // Fluent 2 light palette from ui-preview.html
    static readonly Color Ink = Color.FromArgb(0x1A, 0x1A, 0x1A);
    static readonly Color Dim = Color.FromArgb(0x6B, 0x72, 0x80);
    static readonly Color Accent = Color.FromArgb(0x00, 0x5F, 0xB8);
    static readonly Color AccentSel = Color.FromArgb(0xDC, 0xEB, 0xF9);
    static readonly Color SendC = Color.FromArgb(0x0F, 0x6C, 0xBD);
    static readonly Color WarnC = Color.FromArgb(0xB5, 0x47, 0x08);
    static readonly Color OkC = Color.FromArgb(0x06, 0x76, 0x47);
    static readonly Color BadC = Color.FromArgb(0xB4, 0x23, 0x18);

    public MainForm()
    {
        Text = "FileShare";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(0xF3, 0xF3, 0xF3);
        ForeColor = Ink;
        Size = new Size(920, 640);
        MinimumSize = new Size(700, 480);
        AllowDrop = true;
        DragEnter += (s, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files); };

        SetupList(_devices, DrawDevices);
        SetupList(_queueList, DrawQueue);
        _queueList.SelectionMode = SelectionMode.None;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var pDevices = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0) };
        var ipRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        ipRow.Controls.Add(new Label { Text = "No broadcast? Connect by IP:", AutoSize = true, Margin = new Padding(3, 8, 3, 0), ForeColor = Dim });
        ipRow.Controls.Add(_txtIp);
        var btnAddIp = MakeBtn("Add by IP", 90);
        btnAddIp.Click += (s, e) => AddByIp();
        ipRow.Controls.Add(btnAddIp);
        pDevices.Controls.Add(_devices);
        pDevices.Controls.Add(ipRow);
        pDevices.Controls.Add(SectionLabel("Devices (auto-discovered on WiFi)"));

        var pQueue = new Panel { Dock = DockStyle.Fill };
        pQueue.Controls.Add(_queueList);
        pQueue.Controls.Add(SectionLabel("Files and folders (drag and drop here, or use Add)"));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 38, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var btnAdd = MakeBtn("Add files…", 110);
        btnAdd.Click += (s, e) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Files to send" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        };
        buttons.Controls.Add(btnAdd);
        var btnFolder = MakeBtn("Add folder…", 112);
        btnFolder.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder to send" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFolder(dlg.SelectedPath);
        };
        buttons.Controls.Add(btnFolder);
        buttons.Controls.Add(_btnSend);
        buttons.Controls.Add(_btnPause);
        buttons.Controls.Add(_btnCancel);
        buttons.Controls.Add(new Label { Text = "Paused files resume from the receiver's exact offset.", AutoSize = true, Margin = new Padding(10, 10, 0, 0), ForeColor = Dim });
        _btnSend.Click += (s, e) => StartRun();
        _btnPause.Click += (s, e) =>
        {
            if (_running && !_paused)
            {
                _paused = true;
                _pauseCts?.Cancel();
                _status.Text = "Pausing…";
            }
            else if (_paused)
            {
                StartRun(); // resume: paused + pending items re-send, receiver's offset wins
            }
        };
        _btnCancel.Click += (s, e) =>
        {
            _sendCts?.Cancel();
            _status.Text = "Cancelled, partial files will resume automatically.";
        };

        var recvRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 38, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        recvRow.Controls.Add(new Label { Text = "Save received files to:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        recvRow.Controls.Add(_txtDir);
        var btnBrowse = MakeBtn("Change…", 86);
        btnBrowse.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                Settings.ReceiveDir = dlg.SelectedPath;
                Settings.Save();
                _txtDir.Text = Settings.ReceiveDir;
                RestartReceiver();
            }
        };
        recvRow.Controls.Add(btnBrowse);
        var btnOpen = MakeBtn("Open folder", 104);
        btnOpen.Click += (s, e) =>
        {
            try { Directory.CreateDirectory(Settings.ReceiveDir); Process.Start("explorer.exe", Settings.ReceiveDir); }
            catch (Exception ex) { _status.Text = "Cannot open folder: " + ex.Message; }
        };
        recvRow.Controls.Add(btnOpen);

        layout.Controls.Add(pDevices, 0, 0);
        layout.Controls.Add(pQueue, 1, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(recvRow, 0, 2);
        layout.SetColumnSpan(recvRow, 2);

        _recvStrip.Controls.Add(_recvInfo);
        _recvStrip.Controls.Add(_recvBar);
        Controls.Add(layout);
        Controls.Add(_recvStrip);
        Controls.Add(_sendSpeed);
        Controls.Add(_bar);
        Controls.Add(_partials);
        Controls.Add(_status);

        _txtDir.Text = Settings.ReceiveDir;

        _discovery = new Discovery();
        _discovery.Changed += () => { try { BeginInvoke((MethodInvoker)RefreshDevices); } catch (ObjectDisposedException) { } };
        StartReceiver();

        _timer.Tick += (s, e) => { _tickCount++; RefreshQueue(); UpdateReceive(); if (_tickCount % 4 == 0) RefreshPartials(); };
        _timer.Start();
        RefreshDevices();
        RefreshPartials();
    }

    void StartReceiver()
    {
        _receiver = new Receiver(null, Settings.ReceiveDir);
        _receiver.Received += p => { try { BeginInvoke((MethodInvoker)(() => _status.Text = "Received: " + p)); } catch (ObjectDisposedException) { } };
        _receiver.Start();
    }

    void RestartReceiver()
    {
        var old = _receiver;
        StartReceiver();
        old.Dispose();
    }

    void AddByIp()
    {
        var ip = _txtIp.Text.Trim();
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            _status.Text = "Enter a valid IPv4 address, e.g. 192.168.1.23";
            return;
        }
        if (!_manual.Any(p => p.Ip == ip)) _manual.Add(new Peer { Name = "manual " + ip, Platform = "manual", Ip = ip, TcpPort = Proto.TcpPort, LastSeen = DateTime.MaxValue });
        RefreshDevices();
        _status.Text = "Added " + ip + ", select it and send.";
    }

    void AddFiles(IEnumerable<string> paths, string relRoot = "")
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { AddFolder(p, relRoot); continue; }
            if (!File.Exists(p)) continue;
            AddOne(p, relRoot);
        }
        RefreshQueue();
    }

    // queue a whole folder; files keep their layout ("Photos/sub/x.jpg" on the receiver)
    void AddFolder(string dir, string relRoot = "")
    {
        var name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Length == 0) return;
        var root = relRoot.Length > 0 ? relRoot + "/" + name : name;
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(dir, "*", opt))
        {
            var rel = root + f[dir.Length..].Replace('\\', '/').TrimStart('/');
            AddOne(f, rel);
        }
        RefreshQueue();
    }

    void AddOne(string path, string rel)
    {
        var ex = _queue.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (ex != null)
        {
            if (ex.State is not (TransferState.Sending or TransferState.Reconnecting or TransferState.Hashing or TransferState.Connecting))
            {
                ex.State = TransferState.Pending;
                ex.Error = "";
            }
            return;
        }
        _queue.Add(new TransferItem { Path = path, Size = new FileInfo(path).Length, RelPath = rel });
    }

    void StartRun()
    {
        var ip = SelectedIp;
        if (ip == null) { _status.Text = "Select a device first."; return; }
        var peer = _shown.FirstOrDefault(p => p.Ip == ip);
        if (peer == null) { _status.Text = "Select a device first."; return; }
        var items = _queue.Where(i => i.State is TransferState.Pending or TransferState.Paused).ToList();
        if (items.Count == 0) { _status.Text = "No files waiting, add files first."; return; }

        _sendCts?.Cancel();
        _pauseCts?.Cancel(); // a stale pause flag from a previous run must not leak in
        _sendCts = new CancellationTokenSource();
        _pauseCts = new CancellationTokenSource();
        _running = true;
        _paused = false;
        var ct = _sendCts.Token;
        var pauseCts = _pauseCts;
        _btnSend.Enabled = false;
        _btnPause.Text = "Pause";
        _btnPause.Enabled = true;
        _btnCancel.Enabled = true;
        _status.Text = $"Sending {items.Count} file(s) to {peer.Name}…";
        Task.Run(async () =>
        {
            foreach (var it in items)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseCts.Token);
                try
                {
                    await Sender.SendAsync(it, ip, peer.TcpPort, null, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    if (pauseCts.IsCancellationRequested)
                    {
                        it.State = TransferState.Paused; // receiver's offset corrects any overshoot on resume
                        break;
                    }
                    it.State = TransferState.Cancelled;
                    break;
                }
                catch { /* item marked Failed inside Sender */ }
            }
            _running = false;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    var anyPaused = items.Any(i => i.State == TransferState.Paused);
                    _paused = anyPaused;
                    _btnSend.Enabled = true;
                    _btnPause.Text = anyPaused ? "Resume" : "Pause";
                    _btnPause.Enabled = anyPaused;
                    _btnCancel.Enabled = false;
                    _status.Text = anyPaused ? "Paused, Resume continues from the receiver's offset." : "Idle.";
                }));
            }
            catch (ObjectDisposedException) { }
        });
    }

    string? SelectedIp =>
        _devices.SelectedIndex >= 0 && _devices.SelectedIndex < _shown.Count ? _shown[_devices.SelectedIndex].Ip : null;

    void RefreshDevices()
    {
        var all = _manual.Concat(_discovery.Snapshot()).GroupBy(p => p.Ip).Select(g => g.First()).ToList();
        var sel = SelectedIp;
        _shown = all;
        _devices.BeginUpdate();
        _devices.Items.Clear();
        foreach (var p in all) _devices.Items.Add(p);
        _devices.EndUpdate();
        var idx = all.FindIndex(p => p.Ip == sel);
        if (idx >= 0) _devices.SelectedIndex = idx;

        if (_lastPeerCount == 0 && all.Count > 0 && _btnSend.Enabled)
            _status.Text = $"Device found: {all[0].Name} ({all[0].Ip})";
        _lastPeerCount = all.Count;
    }

    void RefreshQueue()
    {
        var now = DateTime.UtcNow;
        foreach (var i in _queue)
        {
            if (i.State != TransferState.Sending) { i.LastProgress = i.Progress; i.LastTick = now; continue; }
            var dt = (now - i.LastTick).TotalSeconds;
            if (dt >= 0.4)
            {
                var inst = (i.Progress - i.LastProgress) / dt;
                i.SpeedBps = (long)(i.SpeedBps == 0 ? inst : inst * 0.3 + i.SpeedBps * 0.7);
                i.LastProgress = i.Progress;
                i.LastTick = now;
            }
        }

        _queueList.BeginUpdate();
        _queueList.Items.Clear();
        foreach (var i in _queue) _queueList.Items.Add(i.DisplayName); // state rendered in DrawQueue
        _queueList.EndUpdate();

        var active = _queue.FirstOrDefault(i => i.State is TransferState.Sending or TransferState.Reconnecting or TransferState.Hashing or TransferState.Connecting);
        _bar.Fraction = active is { Size: > 0 } a ? Math.Clamp(a.Progress / (double)a.Size, 0, 1) : 0;

        if (active is { SpeedBps: > 0 } up)
        {
            var eta = up.Size > up.Progress ? Eta((long)((up.Size - up.Progress) / up.SpeedBps)) : "";
            _sendSpeed.Text = $"Up {Fmt(up.SpeedBps)}/s, {Pct(up)}{(eta.Length > 0 ? $", ETA {eta}" : "")}";
            _sendSpeed.Visible = true;
        }
        else _sendSpeed.Visible = false;
    }

    // live receive strip: aggregate bytes/speed over all in-flight incoming transfers
    void UpdateReceive()
    {
        var now = DateTime.UtcNow;
        var actives = _receiver.Active.Keys.ToArray();
        if (actives.Length == 0)
        {
            _recvStrip.Visible = false;
            _recvSpeedBps = 0;
            _lastRecvBytes = 0;
            _lastRecvTick = now;
            return;
        }

        long recv = actives.Sum(a => a.Received), size = actives.Sum(a => a.Size);
        if (recv < _lastRecvBytes) { _recvSpeedBps = 0; _lastRecvBytes = recv; _lastRecvTick = now; } // next file started
        var dt = (now - _lastRecvTick).TotalSeconds;
        if (dt >= 0.4)
        {
            var inst = Math.Max(0, recv - _lastRecvBytes) / dt;
            _recvSpeedBps = _recvSpeedBps == 0 ? inst : inst * 0.3 + _recvSpeedBps * 0.7;
            _lastRecvTick = now;
            _lastRecvBytes = recv;
        }

        var pct = size > 0 ? 100.0 * recv / size : 0;
        var what = actives.Length == 1 ? actives[0].Name : $"{actives.Length} files";
        var dev = actives.Length == 1 ? DeviceName(actives[0].Ip) : $"{actives.Length} devices";
        var txt = $"Receiving {what} from {dev}, {pct:F1}%, {Fmt((long)_recvSpeedBps)}/s";
        if (_recvSpeedBps > 1 && size > recv) txt += $", ETA {Eta((long)((size - recv) / _recvSpeedBps))}";
        _recvInfo.Text = txt;
        _recvBar.Fraction = size > 0 ? Math.Clamp(recv / (double)size, 0, 1) : 0;
        _recvStrip.Visible = true;
    }

    string DeviceName(string ip) => _shown.FirstOrDefault(p => p.Ip == ip)?.Name ?? ip;

    static string Eta(long sec)
    {
        if (sec < 0) sec = 0;
        if (sec < 60) return $"{sec}s";
        if (sec < 3600) return $"{sec / 60}m {sec % 60}s";
        return $"{sec / 3600}h {sec % 3600 / 60}m";
    }

    void RefreshPartials()
    {
        try
        {
            var n = Directory.EnumerateFiles(Settings.ReceiveDir, "*.part",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Count();
            _partials.Text = n == 0 ? "" : $"Partials waiting to auto-resume on re-send: {n}";
        }
        catch { }
    }

    static string Fmt(long b)
    {
        double v = b;
        if (v >= 1L << 40) return $"{v / (1L << 40):F2} TB";
        if (v >= 1L << 30) return $"{v / (1L << 30):F1} GB";
        if (v >= 1L << 20) return $"{v / (1L << 20):F1} MB";
        if (v >= 1L << 10) return $"{v / (1L << 10):F0} KB";
        return $"{b} B";
    }

    static string Pct(TransferItem i) => i.Size <= 0 ? "0%" : $"{100.0 * i.Progress / i.Size:F0}%";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _sendCts?.Cancel();
        _pauseCts?.Cancel();
        _discovery.Dispose();
        _receiver.Dispose();
        base.OnFormClosing(e);
    }

    // ---- Fluent 2 styling helpers (design source: ui-preview.html) --------

    static Label SectionLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 24, AutoSize = false,
        Font = new Font("Segoe UI", 9.75f, FontStyle.Bold), ForeColor = Ink,
        Padding = new Padding(0, 2, 0, 6), TextAlign = ContentAlignment.BottomLeft,
    };

    static Button MakeBtn(string text, int width, bool primary = false)
    {
        var b = new Button { Text = text, Width = width, Height = 30, FlatStyle = FlatStyle.Flat };
        var fa = b.FlatAppearance;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            fa.BorderSize = 0;
            fa.MouseOverBackColor = Color.FromArgb(0x0B, 0x6C, 0xC9);
            fa.MouseDownBackColor = Color.FromArgb(0x0B, 0x53, 0x9C);
        }
        else
        {
            b.BackColor = Color.FromArgb(0xFB, 0xFB, 0xFB);
            b.ForeColor = Ink;
            fa.BorderSize = 1;
            fa.BorderColor = Color.FromArgb(0xD1, 0xD1, 0xD1);
            fa.MouseOverBackColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
            fa.MouseDownBackColor = Color.FromArgb(0xE4, 0xE4, 0xE4);
        }
        return b;
    }

    void SetupList(ListBox lb, DrawItemEventHandler draw)
    {
        lb.DrawMode = DrawMode.OwnerDrawFixed;
        lb.ItemHeight = 22;
        lb.BorderStyle = BorderStyle.FixedSingle;
        lb.DrawItem += draw;
        typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(lb, true); // the queue repaints twice a second; kill flicker
    }

    void DrawDevices(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var p = _shown[e.Index];
        var manual = p.Platform == "manual";
        DrawRow(e, $"{p.Name}  ({p.Platform})",
            manual ? Dim : Ink, p.Ip, Dim, (e.State & DrawItemState.Selected) != 0);
    }

    void DrawQueue(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _queue.Count) return;
        var i = _queue[e.Index];
        var (right, rightColor, nameColor) = i.State switch
        {
            TransferState.Sending => ($"Sending, {Pct(i)}, {Fmt(i.SpeedBps)}/s", SendC, Ink),
            TransferState.Reconnecting => ($"Reconnecting, resumes at {Fmt(i.Progress)}", WarnC, Ink),
            TransferState.Hashing => ("Hashing…", Dim, Ink),
            TransferState.Connecting => ("Connecting…", Dim, Ink),
            TransferState.Paused => ("Paused", WarnC, Ink),
            TransferState.Done => ("Done", OkC, Ink),
            TransferState.Failed => ($"Failed, {i.Error}", BadC, Dim),
            TransferState.Cancelled => ("Cancelled, will resume", WarnC, Dim),
            TransferState.Pending => (Fmt(i.Size), Dim, Ink),
            _ => ("", Dim, Ink),
        };
        DrawRow(e, i.DisplayName, nameColor, right, rightColor, false);
    }

    // one list row: left label, right-aligned secondary text, tinted when selected
    void DrawRow(DrawItemEventArgs e, string left, Color leftColor, string right, Color rightColor, bool selected)
    {
        if (selected)
            using (var bg = new SolidBrush(AccentSel)) e.Graphics.FillRectangle(bg, e.Bounds);
        var f = e.Font!;
        using var sfL = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var sfR = new StringFormat { LineAlignment = StringAlignment.Center };
        using var bl = new SolidBrush(leftColor);
        using var br = new SolidBrush(rightColor);
        var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
        var rw = right.Length == 0 ? 0 : (int)e.Graphics.MeasureString(right, f, r.Width).Width;
        if (rw > 0)
            e.Graphics.DrawString(right, f, br, new Rectangle(r.Right - rw, r.Y, rw, r.Height), sfR);
        e.Graphics.DrawString(left, f, bl, new Rectangle(r.X, r.Y, Math.Max(0, r.Width - rw - 12), r.Height), sfL);
    }
}

// Slim rounded progress bar in the app accent; WinForms ProgressBar can't be recolored.
sealed class FlatBar : Control
{
    double _frac;
    public double Fraction
    {
        get => _frac;
        set { _frac = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    public FlatBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(0xF3, 0xF3, 0xF3);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, (Height - 4) / 2f, Width, 4);
        using (var bt = new SolidBrush(Color.FromArgb(0xE6, 0xE6, 0xE6)))
        using (var pt = Capsule(track))
            g.FillPath(bt, pt);
        if (_frac > 0.001)
        {
            var fill = track with { Width = Math.Max(4f, (float)(track.Width * _frac)) };
            using var bf = new SolidBrush(Color.FromArgb(0x00, 0x5F, 0xB8));
            using var pf = Capsule(fill);
            g.FillPath(bf, pf);
        }
    }

    static GraphicsPath Capsule(RectangleF r)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }
}
