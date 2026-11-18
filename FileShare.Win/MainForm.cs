using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FileShare.Win;

class MainForm : Form
{
    readonly ListBox _devices = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly ListBox _queueList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly TextBox _txtIp = new() { Width = 130, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(0x16, 0x23, 0x3A), ForeColor = Color.FromArgb(0xEE, 0xF3, 0xFB) };
    readonly TextBox _txtDir = new() { Width = 200, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(0x16, 0x23, 0x3A), ForeColor = Color.FromArgb(0x8C, 0xA3, 0xC7) };
    readonly FlatBar _bar = new() { Dock = DockStyle.Bottom, Height = 6, Margin = new Padding(16, 4, 16, 6) };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
    readonly Label _partials = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(0x6B, 0x72, 0x80), BackColor = Color.Transparent };
    // live receive strip (incoming transfers) and upload-speed line
    readonly Panel _recvStrip = new() { Dock = DockStyle.Bottom, Height = 46, Visible = false, Padding = new Padding(16, 4, 16, 2), BackColor = Color.Transparent };
    readonly Label _recvInfo = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
    readonly FlatBar _recvBar = new() { Dock = DockStyle.Bottom, Height = 6, Margin = new Padding(16, 0, 16, 2) };
    readonly Label _sendSpeed = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(0x6B, 0x72, 0x80), Visible = false, Margin = new Padding(16, 2, 0, 0), BackColor = Color.Transparent };
    readonly Panel _divider = new() { Dock = DockStyle.Bottom, Height = 1, BackColor = Line }; // above the status/progress stack
    double _recvSpeedBps;
    long _lastRecvBytes;
    DateTime _lastRecvTick = DateTime.UtcNow;
    readonly Button _btnSend = MakeBtn("Send to selected device", 180, primary: true);
    readonly Button _btnPause = MakeBtn("Pause", 92);
    readonly Button _btnCancel = MakeBtn("Cancel", 92);

    readonly List<Button> _metricBtns = new(); // buttons whose height follows the font, applied post-scale
    FlowLayoutPanel _ipRow = null!, _btnRow = null!, _recvRow = null!;
    Panel _bottomRows = null!;
    Label _devLabel = null!;

    Discovery _discovery;
    Receiver _receiver = null!;
    readonly List<TransferItem> _queue = new();
    readonly List<Peer> _manual = new();
    List<Peer> _shown = new();
    int _lastPeerCount;
    string _lastQueueSig = "", _lastDevSig = "";
    List<RecvRow> _recvRows = new();
    readonly Dictionary<RecvInfo, RecvSpeed> _recvSpeeds = new();
    CancellationTokenSource? _sendCts;
    readonly Rectangle _placeWa = Screen.PrimaryScreen!.WorkingArea;
    CancellationTokenSource? _pauseCts;
    bool _running, _paused;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    FileSystemWatcher? _partWatcher;
    int _partialCount;

    // ShareIt-style dark palette
    static readonly Color Ink = Color.FromArgb(0xEE, 0xF3, 0xFB);
    static readonly Color Dim = Color.FromArgb(0x8C, 0xA3, 0xC7);
    static readonly Color Accent = Color.FromArgb(0x00, 0xA6, 0xFF);
    static readonly Color AccentSel = Color.FromArgb(0x11, 0x3B, 0x66);
    static readonly Color SendC = Color.FromArgb(0x00, 0xA6, 0xFF);
    static readonly Color WarnC = Color.FromArgb(0xF5, 0x9E, 0x0B);
    static readonly Color OkC = Color.FromArgb(0x22, 0xC5, 0x5E);
    static readonly Color BadC = Color.FromArgb(0xEF, 0x44, 0x44);
    static readonly Color Bg = Color.FromArgb(0x0E, 0x17, 0x26);
    static readonly Color Card = Color.FromArgb(0x16, 0x23, 0x3A);
    static readonly Color Edge = Color.FromArgb(0x2A, 0x3E, 0x5F);
    static readonly Color Line = Color.FromArgb(0x1E, 0x2F, 0x49); // hairline dividers (design statusbar border)
    // avatar circle colors, picked per device name
    static readonly Color[] AvatarCols = {
        Color.FromArgb(0x00, 0xA6, 0xFF), Color.FromArgb(0x7C, 0x5C, 0xFF), Color.FromArgb(0x00, 0xC9, 0xA7),
        Color.FromArgb(0xFF, 0x8A, 0x3D), Color.FromArgb(0xE5, 0x4D, 0x8C),
    };

    public MainForm()
    {
        Text = "FileShare";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.None; // the built-in pass never fires for a runtime-built form (no design baseline);
                                            // we scale the layout ourselves once at the end of the ctor
        BackColor = Bg;
        ForeColor = Ink;
        Size = new Size(920, 640);
        MinimumSize = new Size(700, 480);
        WireDrop(this);
        _btnSend.Height = _btnPause.Height = _btnCancel.Height = BtnH;
        _recvStrip.Height = Font.Height + 26;
        _status.Height = _sendSpeed.Height = _partials.Height = Font.Height + 5;

        SetupList(_devices, DrawDevices);
        SetupList(_queueList, DrawQueue);
        _queueList.SelectionMode = SelectionMode.None;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12), BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pDevices = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0), BackColor = Color.Transparent };
        _ipRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        _ipRow.Controls.Add(new Label { Text = "Connect by IP:", AutoSize = true, Margin = new Padding(0, 8, 8, 0), ForeColor = Dim });
        _ipRow.Controls.Add(_txtIp);
        var btnAddIp = MakeBtn("Add by IP", 90);
        btnAddIp.Height = BtnH;
        btnAddIp.Click += (s, e) => AddByIp();
        _ipRow.Controls.Add(btnAddIp);
        pDevices.Controls.Add(_devices);
        pDevices.Controls.Add(_ipRow);
        _devLabel = SectionLabel("Devices (auto-discovered on WiFi)");
        pDevices.Controls.Add(_devLabel);

        var pQueue = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        pQueue.Controls.Add(_queueList);

        // ShareIt-style drop zone: click to browse, drop files or folders
        var drop = new GlassCard { Dock = DockStyle.Top, Height = 92, BackColor = Card, Padding = new Padding(0, 0, 0, 2) };
        var dropMain = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Drag files or folders here", ForeColor = Ink, Font = new Font("Segoe UI", 11.5f) };
        var dropSub = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleCenter, Text = "or click to browse", ForeColor = Dim };
        drop.Controls.Add(dropMain);
        drop.Controls.Add(dropSub);
        Action browse = () =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Files to send" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        };
        foreach (var c in new Control[] { drop, dropMain, dropSub })
        {
            WireDrop(c);
            c.Cursor = Cursors.Hand;
            c.Click += (s, e) => browse();
        }
        pQueue.Controls.Add(drop);

        // lists get the same rounded card look; their hairline is painted on the parent like the fields
        Round(_devices);
        Round(_queueList);
        PaintField(pDevices, _devices);
        PaintField(pQueue, _queueList);

        _btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = RowH, WrapContents = false, BackColor = Color.Transparent };
        var btnAdd = MakeBtn("Add files…", 110);
        btnAdd.Height = BtnH;
        btnAdd.Click += (s, e) => browse();
        _btnRow.Controls.Add(btnAdd);
        var btnFolder = MakeBtn("Add folder…", 112);
        btnFolder.Height = BtnH;
        btnFolder.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder to send" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFolder(dlg.SelectedPath);
        };
        _btnRow.Controls.Add(btnFolder);
        _btnRow.Controls.Add(_btnSend);
        _btnRow.Controls.Add(_btnPause);
        _btnRow.Controls.Add(_btnCancel);
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

        _recvRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = RowH, WrapContents = false, BackColor = Color.Transparent };
        _recvRow.Controls.Add(new Label { Text = "Save received files to:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _recvRow.Controls.Add(_txtDir);
        var btnBrowse = MakeBtn("Change…", 86);
        btnBrowse.Height = BtnH;
        btnBrowse.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                Settings.ReceiveDir = dlg.SelectedPath;
                Settings.Save();
                _txtDir.Text = Settings.ReceiveDir;
                RestartReceiver();
                ResetPartialsTracking();
            }
        };
        _recvRow.Controls.Add(btnBrowse);
        var btnOpen = MakeBtn("Open folder", 104);
        btnOpen.Height = BtnH;
        btnOpen.Click += (s, e) =>
        {
            try { Directory.CreateDirectory(Settings.ReceiveDir); Process.Start("explorer.exe", Settings.ReceiveDir); }
            catch (Exception ex) { _status.Text = "Cannot open folder: " + ex.Message; }
        };
        _recvRow.Controls.Add(btnOpen);

        // fixed-height bottom rows in one panel; explicit heights, no margins (docked controls ignore margins)
        _bottomRows = new Panel { Dock = DockStyle.Bottom, Height = RowH * 2 + 31, Padding = new Padding(0, 6, 0, 12) };
        var rowDiv = new Panel { Dock = DockStyle.Top, Height = 13, BackColor = Color.Transparent }; // 12px glass gap + hairline: send actions vs receive row
        rowDiv.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Line });
        _bottomRows.Controls.Add(_recvRow);  // processed last: sits at the bottom, below the divider
        _bottomRows.Controls.Add(rowDiv);
        _bottomRows.Controls.Add(_btnRow);

        _metricBtns.AddRange(new[] { _btnSend, _btnPause, _btnCancel, btnAddIp, btnAdd, btnFolder, btnBrowse, btnOpen });
        foreach (var b in _metricBtns) Round(b); // rounded corners on every button
        Round(drop); // rounded drop card
        Round(_txtIp);
        Round(_txtDir);
        PaintField(_ipRow, _txtIp); // rounded border ring around the borderless fields
        PaintField(_recvRow, _txtDir);

        layout.Controls.Add(pDevices, 0, 0);
        layout.Controls.Add(pQueue, 1, 0);

        _recvStrip.Controls.Add(_recvInfo);
        _recvStrip.Controls.Add(_recvBar);
        Controls.Add(layout);
        Controls.Add(_bottomRows);
        Controls.Add(_divider); // design statusbar border-top: separates content from the status zone
        Controls.Add(_recvStrip);
        Controls.Add(_sendSpeed);
        Controls.Add(_bar);
        Controls.Add(_partials);
        Controls.Add(_status);

        _txtDir.Text = Settings.ReceiveDir;

        _discovery = new Discovery();
        _discovery.Changed += () => { try { BeginInvoke((MethodInvoker)RefreshDevices); } catch (ObjectDisposedException) { } };
        StartReceiver();

        _timer.Tick += (s, e) => { RefreshQueue(); UpdateReceive(); };
        _timer.Start();
        RefreshDevices();
        SetupPartialsWatcher();
        Task.Run(InitialPartialsScan);

        // fonts render at DeviceDpi; the layout was designed at 96 dpi, so scale it once to match
        // (PMv2 process: the form is born at the monitor's DPI, no WM_DPICHANGED rescale ever arrives)
        var s = DeviceDpi / 96f;
        Scale(new SizeF(s, s));
        MinimumSize = new Size((int)(700 * s), (int)(480 * s));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (Width > wa.Width || Height > wa.Height) Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
        ApplyMetrics();
        DoubleBuffered = true;
        // open offscreen and jump onscreen once painted: freshly created child surfaces are
        // unpainted, and the visible first frame showed garbage through them (see OnShown)
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
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
        // rebuild the list only when the peer set actually changed, not on every 2s discovery ping
        var sig = string.Join(";", all.Select(p => $"{p.Name}|{p.Ip}|{p.TcpPort}"));
        var sel = SelectedIp;
        if (sig != _lastDevSig)
        {
            _lastDevSig = sig;
            _shown = all;
            _devices.BeginUpdate();
            _devices.Items.Clear();
            foreach (var p in all) _devices.Items.Add(p);
            _devices.EndUpdate();
            var idx = all.FindIndex(p => p.Ip == sel);
            if (idx >= 0) _devices.SelectedIndex = idx;
        }

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

        // incoming transfers: snapshot one row each + per-file speed smoothing (same model as sends)
        var actives = _receiver.Active.Keys;
        foreach (var info in actives)
        {
            if (!_recvSpeeds.TryGetValue(info, out var prev))
            {
                prev = new RecvSpeed { Bytes = info.Received, T = now };
                _recvSpeeds[info] = prev; // store the first sample, or dt stays 0 forever
            }
            var dt = (now - prev.T).TotalSeconds;
            if (dt >= 0.4)
            {
                var inst = Math.Max(0, info.Received - prev.Bytes) / dt;
                prev.Speed = prev.Speed == 0 ? inst : inst * 0.3 + prev.Speed * 0.7;
                prev.Bytes = info.Received;
                prev.T = now;
            }
        }
        _recvRows = actives.Select(k => new RecvRow
        {
            Name = k.Name, Ip = k.Ip, Size = k.Size, Received = k.Received,
            SpeedBps = _recvSpeeds.TryGetValue(k, out var sv) ? sv.Speed : 0,
        }).ToList();
        var gone = _recvSpeeds.Keys.Where(k => !actives.Contains(k)).ToList();
        foreach (var k in gone) _recvSpeeds.Remove(k);

        // rebuild the list only when something visible changed; idle ticks stay free
        var sig = string.Join("|", _queue.Select(i => $"{i.State}:{i.Progress}:{(long)i.SpeedBps}:{i.Error}:{i.Size}:{i.DisplayName}"))
            + "#" + string.Join("|", _recvRows.Select(r => $"{r.Name}:{r.Received}:{r.Size}:{(long)r.SpeedBps}"));
        if (sig != _lastQueueSig)
        {
            _lastQueueSig = sig;
            _queueList.BeginUpdate();
            _queueList.Items.Clear();
            foreach (var i in _queue) _queueList.Items.Add(i.DisplayName); // state rendered in DrawQueue
            foreach (var r in _recvRows) _queueList.Items.Add(r.Name);     // incoming, rendered from _recvRows
            _queueList.EndUpdate();
        }

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

    // Partial count is a hint label; the old version re-enumerated the whole receive tree every 2s,
    // which burns CPU when the receive dir is large (users can point it at a whole drive).
    // Count once at startup in the background, then track changes with a FileSystemWatcher.
    void SetupPartialsWatcher()
    {
        try
        {
            var w = new FileSystemWatcher(Settings.ReceiveDir, "*.part")
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            w.Created += (s, e) => { try { BeginInvoke((MethodInvoker)(() => { _partialCount++; RefreshPartialsLabel(); })); } catch (ObjectDisposedException) { } };
            w.Deleted += (s, e) => { try { BeginInvoke((MethodInvoker)(() => { _partialCount--; RefreshPartialsLabel(); })); } catch (ObjectDisposedException) { } };
            w.Renamed += (s, e) => { try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (e.OldName?.EndsWith(".part", StringComparison.OrdinalIgnoreCase) == true) _partialCount--;
                    if (e.Name?.EndsWith(".part", StringComparison.OrdinalIgnoreCase) == true) _partialCount++;
                    RefreshPartialsLabel();
                }));
            } catch (ObjectDisposedException) { } };
            w.Error += (s, e) => Task.Run(InitialPartialsScan); // buffer overflow → recount once
            w.EnableRaisingEvents = true;
            _partWatcher = w;
        }
        catch { /* dir missing or unwritable → label just stays empty */ }
    }

    void InitialPartialsScan()
    {
        try
        {
            var n = Directory.EnumerateFiles(Settings.ReceiveDir, "*.part",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Count();
            try { BeginInvoke((MethodInvoker)(() => { _partialCount = n; RefreshPartialsLabel(); })); }
            catch (ObjectDisposedException) { }
        }
        catch { /* dir missing */ }
    }

    void ResetPartialsTracking()
    {
        try { _partWatcher?.Dispose(); } catch { }
        _partWatcher = null;
        _partialCount = 0;
        RefreshPartialsLabel();
        SetupPartialsWatcher();
        Task.Run(InitialPartialsScan);
    }

    void RefreshPartialsLabel() =>
        _partials.Text = _partialCount == 0 ? "" : $"Partials waiting to auto-resume on re-send: {_partialCount}";

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar (Win10 20H1+)
        var round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND: rounded window corners
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
        // 26H2 ignores the dark-mode flag for the ACTIVE caption and draws a warm gray material,
        // so force the caption, border and caption text colors to the app palette
        var caption = ToColorRef(Bg);
        DwmSetWindowAttribute(Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR
        var border = ToColorRef(Edge);
        DwmSetWindowAttribute(Handle, 34, ref border, 4); // DWMWA_BORDER_COLOR
        var text = ToColorRef(Ink);
        DwmSetWindowAttribute(Handle, 36, ref text, 4); // DWMWA_TEXT_COLOR
    }

    static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    // frosted "liquid glass" backdrop: a soft vertical ice gradient. Opaque by design: the
    // system acrylic path composites per-pixel alpha that classic GDI controls never write,
    // which ghosted the whole UI. Cards add their own sheen so the glass feel lives in them.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var g = new LinearGradientBrush(e.ClipRectangle, Color.FromArgb(0x11, 0x1C, 0x30), Color.FromArgb(0x0C, 0x15, 0x22), 90f);
        e.Graphics.FillRectangle(g, e.ClipRectangle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // DWM never schedules a paint for a fully offscreen window, so nothing would ever
        // paint out there; force every surface to paint now, then jump onscreen complete
        RefreshTree(this);
        BeginInvoke(new Action(() =>
            Location = new Point(_placeWa.Left + (_placeWa.Width - Width) / 2,
                                 _placeWa.Top + Math.Max(0, (_placeWa.Height - Height) / 2))));
    }

    static void RefreshTree(Control c)
    {
        c.Refresh(); // synchronous paint of this surface
        foreach (Control k in c.Controls) RefreshTree(k);
    }

    // Font-relative metrics must be applied AFTER WinForms' one-time DPI auto-scale:
    // Font.Height is already in physical pixels, and the auto-scale pass would multiply
    // these bounds again (double-scaling at 125/150%). Assigning here lands them exactly once.
    void ApplyMetrics()
    {
        var fh = Font.Height;
        foreach (var b in _metricBtns) { b.Height = BtnH; b.Margin = new Padding(0, (RowH - BtnH) / 2, fh / 2, 0); } // uniform 8px-class gaps, vertically centered
        _ipRow.Height = _btnRow.Height = _recvRow.Height = RowH;
        _bottomRows.Height = RowH * 2 + 31; // two rows + 13px divider band + 6/12 padding
        _recvStrip.Height = fh + 26;
        _status.Height = _sendSpeed.Height = _partials.Height = fh + 5;
        _devLabel.Height = fh + 12; // bold 9.75pt + padding needs more than the old fixed 24px
        _devices.ItemHeight = _queueList.ItemHeight = fh + 8;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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
        var b = new Button { Text = text, Width = width, FlatStyle = FlatStyle.Flat };
        var fa = b.FlatAppearance;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            fa.BorderSize = 0;
            fa.MouseOverBackColor = Color.FromArgb(0x2F, 0xBB, 0xFF);
            fa.MouseDownBackColor = Color.FromArgb(0x00, 0x86, 0xCC);
        }
        else
        {
            b.BackColor = Card;
            b.ForeColor = Ink;
            fa.BorderSize = 1;
            fa.BorderColor = Edge;
            fa.MouseOverBackColor = Color.FromArgb(0x1D, 0x2E, 0x4B);
            fa.MouseDownBackColor = Color.FromArgb(0x24, 0x38, 0x57);
        }
        return b;
    }

    // font-relative metrics: row and control heights always match the rendered text at any DPI
    int BtnH => Font.Height + 12;
    int RowH => Font.Height + 20;

    // rounded window-region for buttons, cards and fields; reapplied on resize (docked controls resize with the form)
    void Round(Control c)
    {
        var rad = Math.Max(6, Font.Height / 2);
        Action apply = () =>
        {
            var old = c.Region;
            c.Region = c.Width > 4 && c.Height > 4 ? new Region(RoundedPath(new Rectangle(0, 0, c.Width, c.Height), rad)) : null;
            old?.Dispose();
        };
        c.Resize += (s, e) => apply();
        apply();
    }

    // hairline ring around a borderless field, drawn on the parent row so it follows the field's rounded region
    void PaintField(Control row, Control f)
    {
        var rad = Math.Max(6, Font.Height / 2);
        row.Paint += (s, e) =>
        {
            var b = f.Bounds; b.Inflate(1, 1);
            using var path = RoundedPath(b, rad + 1); // +1 keeps the ring concentric with the control's rounded region
            using var pen = new Pen(Edge);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        };
        f.LocationChanged += (s, e) => row.Invalidate();
    }

    static internal GraphicsPath RoundedPath(Rectangle r, int rad)
    {
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        var d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void SetupList(ListBox lb, DrawItemEventHandler draw)
    {
        lb.DrawMode = DrawMode.OwnerDrawFixed;
        lb.ItemHeight = Font.Height + 8;
        lb.BorderStyle = BorderStyle.None; // default Fixed3D draws a white system border outside the rounded region
        lb.BackColor = Card;
        lb.ForeColor = Ink;
        lb.DrawItem += draw;
        typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(lb, true); // the queue repaints twice a second; kill flicker
    }

    // children don't inherit drag-drop; wire the form, lists and drop card so dropping works anywhere
    void WireDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (s, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        c.DragDrop += (s, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files); };
    }

    void DrawDevices(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var p = _shown[e.Index];
        var manual = p.Platform == "manual";
        var av = manual ? Edge : AvatarCols[(p.Name.GetHashCode() & 0x7FFFFFFF) % AvatarCols.Length];
        DrawRow(e, $"{p.Name}  ({p.Platform})",
            Ink, p.Ip, Dim, (e.State & DrawItemState.Selected) != 0, p.Name[0], av);
    }

    void DrawQueue(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        if (e.Index >= _queue.Count)
        {
            // incoming files share the list, drawn from the live receiver snapshot
            var ri = e.Index - _queue.Count;
            if (ri >= _recvRows.Count) return;
            var r = _recvRows[ri];
            var pct = r.Size > 0 ? 100.0 * r.Received / r.Size : 0;
            var speed = r.SpeedBps > 0 ? $", {Fmt((long)r.SpeedBps)}/s" : "";
            DrawRow(e, r.Name, Ink, $"Receiving, {pct:F0}%{speed}", SendC, false);
            return;
        }
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

    // one list row: optional letter avatar, left label, right-aligned secondary text, tinted when selected
    void DrawRow(DrawItemEventArgs e, string left, Color leftColor, string right, Color rightColor, bool selected,
        char avatar = '\0', Color avatarBg = default)
    {
        if (selected)
            using (var bg = new SolidBrush(AccentSel)) e.Graphics.FillRectangle(bg, e.Bounds);
        var f = e.Font!;
        var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
        var leftX = r.X;
        if (avatar != '\0')
        {
            var d = Math.Min(18, r.Height - 6);
            var c = new Rectangle(r.X + 2, r.Y + (r.Height - d) / 2, d, d);
            using var ba = new SolidBrush(avatarBg);
            e.Graphics.FillEllipse(ba, c);
            using var sa = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            e.Graphics.DrawString(char.ToUpperInvariant(avatar).ToString(), f, Brushes.White, c, sa);
            leftX = c.Right + 8;
        }
        // TextRenderer (GDI) pair with SingleLine: GDI+ DrawString wraps inside a rect sized by its own
        // MeasureString, which dropped long right labels onto a second line and crowded out the name
        const TextFormatFlags rf = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        var rw = right.Length == 0 ? 0 : TextRenderer.MeasureText(right, f, Size.Empty, rf).Width;
        rw = Math.Min(rw, Math.Max(0, r.Width - 80)); // the name always keeps some room
        if (rw > 0)
            TextRenderer.DrawText(e.Graphics, right, f, new Rectangle(r.Right - rw, r.Y, rw + 4, r.Height), rightColor, rf);
        var lw = Math.Max(0, r.Right - rw - 12 - leftX);
        TextRenderer.DrawText(e.Graphics, left, f, new Rectangle(leftX, r.Y, lw, r.Height), leftColor, rf | TextFormatFlags.EndEllipsis);
    }
}

// One in-flight incoming file, shown as a row in the transfers list.
sealed class RecvRow
{
    public string Name = "", Ip = "";
    public long Size, Received;
    public double SpeedBps;
}

// per-file receive-speed smoothing state
sealed class RecvSpeed
{
    public long Bytes;
    public DateTime T;
    public double Speed;
}

// Glass card: vertical ice fill, a soft top sheen and a hairline rounded border, drawn in the
// background pass so transparent child labels composite straight over the finish.
sealed class GlassCard : Panel
{
    static readonly Color Edge = Color.FromArgb(0x2A, 0x3E, 0x5F); // same hairline as the app chrome

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var lg = new LinearGradientBrush(e.ClipRectangle, Color.FromArgb(0x1C, 0x2C, 0x48), Color.FromArgb(0x13, 0x20, 0x36), 90f))
            g.FillRectangle(lg, e.ClipRectangle);
        // children repaint fill only; sheen and border belong to the card's own full-size pass
        if (e.ClipRectangle.Location != Point.Empty || e.ClipRectangle.Size != ClientSize || Width < 10 || Height < 10) return;
        using (var sheen = new LinearGradientBrush(new Rectangle(0, 0, Width, Height / 2), Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90f))
            g.FillRectangle(sheen, new Rectangle(0, 0, Width, Height / 2));
        using var path = MainForm.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(6, Font.Height / 2));
        using var pen = new Pen(Edge);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(pen, path);
    }
}

// Slim rounded progress bar in the app accent; WinForms ProgressBar can't be recolored.
sealed class FlatBar : Control
{
    double _frac;
    public double Fraction
    {
        get => _frac;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (v == _frac) return; // no repaint when the fraction didn't move
            _frac = v;
            Invalidate();
        }
    }

    public FlatBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(0x0E, 0x17, 0x26);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, (Height - 4) / 2f, Width, 4);
        using (var bt = new SolidBrush(Color.FromArgb(0x23, 0x36, 0x50)))
        using (var pt = Capsule(track))
            g.FillPath(bt, pt);
        if (_frac > 0.001)
        {
            var fill = track with { Width = Math.Max(4f, (float)(track.Width * _frac)) };
            using var bf = new SolidBrush(Color.FromArgb(0x00, 0xA6, 0xFF));
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
