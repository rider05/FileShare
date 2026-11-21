using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileShare.Win;

sealed class HashMismatchException : IOException
{
    public HashMismatchException() : base("receiver reported corruption") { }
}

// Tunables are internal static so SelfTest can shrink them for fast resume testing.
static class Tx
{
    internal static long CheckpointEvery = 64L << 20; // 64 MB fsync + meta checkpoint
    internal static long AckEvery = 8L << 20;         // 8 MB cumulative ack
    internal const int ChunkSize = 1 << 20;           // 1 MB streaming buffer
    internal const int MaxParallel = 4;               // files in flight at once; each file stays one TCP stream
    internal const int MaxAttempts = 6;
}

enum TransferState { Pending, Hashing, Connecting, Sending, Reconnecting, Paused, Done, Failed, Cancelled }

class TransferItem
{
    public string Path = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string RelPath = "";   // relative path inside a transferred folder, '/'-separated ("" = flat)
    public string DisplayName => RelPath.Length > 0 ? RelPath : Name;
    public long Size;
    public string Sha = "";
    public TransferState State = TransferState.Pending;
    public long Progress;      // bytes the receiver has (resume-aware)
    public long SpeedBps;
    public string Error = "";
    internal long LastProgress;
    internal DateTime LastTick = DateTime.UtcNow;
}

static class Sender
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, System.IO.FileShare.Read, Tx.ChunkSize, useAsync: true);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[Tx.ChunkSize];
        int n;
        while ((n = await f.ReadAsync(buf, ct)) > 0) hash.AppendData(buf, 0, n);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Retries with backoff; every reconnect re-sends the header and the receiver's resume offset wins.
    public static async Task SendAsync(TransferItem item, string host, int port,
        Action<TransferItem>? onTick, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.Sha))
        {
            item.State = TransferState.Hashing;
            onTick?.Invoke(item);
            item.Sha = await Sha256Async(item.Path, ct); // pre-pass hash; cached for resumes/re-sends
        }

        Exception? last = null;
        for (int attempt = 1; attempt <= Tx.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                item.State = attempt == 1 ? TransferState.Connecting : TransferState.Reconnecting;
                onTick?.Invoke(item);

                using var client = new TcpClient { SendTimeout = 30_000, ReceiveTimeout = 30_000, NoDelay = true, SendBufferSize = 1 << 20 };
                await client.ConnectAsync(host, port, ct);
                await using var stream = client.GetStream();

                await Proto.WriteJsonAsync(stream, new { v = 2, name = item.Name, size = item.Size, sha = item.Sha, path = string.IsNullOrEmpty(item.RelPath) ? null : item.RelPath }, ct);
                using var resumeDoc = JsonDocument.Parse(await Proto.ReadLineAsync(stream, ct));
                long resume = resumeDoc.RootElement.TryGetProperty("resume", out var r) ? r.GetInt64() : 0;
                if (resume < 0) throw new IOException("receiver busy with this file");
                if (resume > item.Size) throw new IOException("receiver ahead of sender");

                item.State = TransferState.Sending;
                item.Progress = resume;
                onTick?.Invoke(item);

                await using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, System.IO.FileShare.Read, Tx.ChunkSize, useAsync: true);
                file.Seek(resume, SeekOrigin.Begin);
                var buf = new byte[Tx.ChunkSize];
                long sent = resume;
                int n;
                while ((n = await file.ReadAsync(buf, ct)) > 0)
                {
                    await stream.WriteAsync(buf.AsMemory(0, n), ct);
                    sent += n;
                    item.Progress = sent;
                    onTick?.Invoke(item);
                }

                // drain replies; 8 MB acks interleave with the final ok/false
                while (true)
                {
                    var line = await Proto.ReadLineAsync(stream, ct);
                    if (line.Contains("\"ok\""))
                    {
                        using var okDoc = JsonDocument.Parse(line);
                        if (!okDoc.RootElement.GetProperty("ok").GetBoolean())
                            throw new HashMismatchException();
                        break;
                    }
                }

                item.State = TransferState.Done;
                item.Progress = item.Size;
                onTick?.Invoke(item);
                return;
            }
            catch (OperationCanceledException)
            {
                item.State = TransferState.Cancelled;
                onTick?.Invoke(item);
                throw;
            }
            catch (HashMismatchException)
            {
                item.State = TransferState.Failed;
                item.Error = "corrupted, resending will start over";
                onTick?.Invoke(item);
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                last = ex;
                item.Error = ex.Message;
                if (attempt < Tx.MaxAttempts)
                {
                    item.State = TransferState.Reconnecting;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt - 1)), ct);
                }
            }
        }
        item.State = TransferState.Failed;
        onTick?.Invoke(item);
        throw last!;
    }
}

// One live incoming transfer, shown in the main window while it runs.
public sealed class RecvInfo
{
    public string Ip = "";
    public string Name = "";
    public long Size;
    public long Received;
}

sealed class Receiver : IDisposable
{
    readonly string _dir;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    public event Action<string>? Received;
    public readonly ConcurrentDictionary<RecvInfo, byte> Active = new(); // live incoming transfers for the UI

    // test hook: offset the last resumed transfer started from
    public long LastResumeOffset { get; private set; }

    public Receiver(IPAddress? bind, string dir, int port = Proto.TcpPort)
    {
        _dir = dir;
        _listener = new TcpListener(bind ?? IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client, _cts.Token));
        }
    }

    async Task Handle(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 30_000;
                client.SendTimeout = 30_000;
                client.NoDelay = true;
                client.ReceiveBufferSize = 1 << 20;
                client.SendBufferSize = 1 << 20;
                var stream = client.GetStream();

                using var header = JsonDocument.Parse(await Proto.ReadLineAsync(stream, ct));
                var root = header.RootElement;
                long size = root.GetProperty("size").GetInt64();
                string sha = root.GetProperty("sha").GetString() ?? "";
                string name = Sanitize(root.GetProperty("name").GetString() ?? "file");
                string rel = root.TryGetProperty("path", out var rp) ? SafeRel(rp.GetString() ?? "") : "";
                if (size < 0) return;

                // files from a transferred folder land in a matching subfolder; guard the canonical result
                var subDir = _dir;
                if (rel.Length > 0)
                {
                    subDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(_dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!subDir.StartsWith(System.IO.Path.GetFullPath(_dir) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        subDir = _dir;
                }
                Directory.CreateDirectory(subDir);

                var partPath = System.IO.Path.Combine(subDir, name + ".part");
                var metaPath = partPath + ".meta";

                // one writer per file; key includes the subfolder so same-named files in different folders don't collide
                var gate = _fileLocks.GetOrAdd((rel.Length > 0 ? rel + "/" : "") + name.ToLowerInvariant(), _ => new SemaphoreSlim(1, 1));
                if (!await gate.WaitAsync(0, ct))
                {
                    await Proto.WriteJsonAsync(stream, new { resume = -1 }, ct);
                    return;
                }
                var info = new RecvInfo
                {
                    Ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "",
                    Name = rel.Length > 0 ? rel + "/" + name : name,
                    Size = size,
                };
                Active[info] = 1;
                try
                {
                    long resume = 0;
                    if (File.Exists(partPath) && File.Exists(metaPath))
                    {
                        try
                        {
                            using var m = JsonDocument.Parse(File.ReadAllText(metaPath));
                            var mr = m.RootElement;
                            if (mr.GetProperty("size").GetInt64() == size &&
                                mr.GetProperty("sha").GetString() == sha)
                            {
                                var done = mr.GetProperty("done").GetInt64();
                                if (done >= 0 && done <= size && new FileInfo(partPath).Length >= done)
                                    resume = done;
                            }
                        }
                        catch { /* corrupt meta → fresh start */ }
                    }
                    LastResumeOffset = resume;
                    info.Received = resume;
                    await Proto.WriteJsonAsync(stream, new { resume }, ct);

                    var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long received;
                    await using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, System.IO.FileShare.None, Tx.ChunkSize, useAsync: true))
                    {
                        if (file.Length < size) file.SetLength(size); // preallocate
                        file.Seek(resume, SeekOrigin.Begin);

                        // hash the trusted prefix already on disk, then keep hashing incoming bytes
                        if (resume > 0) await HashPrefixAsync(file, hash, resume, ct);

                        var buf = new byte[Tx.ChunkSize];
                        received = resume;
                        long lastCp = resume, lastAck = resume;
                        int n;
                        while (received < size && (n = await stream.ReadAsync(buf, ct)) > 0)
                        {
                            if (received + n > size) throw new InvalidDataException("oversized transfer");
                            await file.WriteAsync(buf.AsMemory(0, n), ct);
                            hash.AppendData(buf, 0, n);
                            received += n;
                            info.Received = received;
                            if (received - lastCp >= Tx.CheckpointEvery)
                            {
                                await file.FlushAsync(ct);
                                file.Flush(true); // fsync so checkpoints survive a crash
                                WriteMeta(metaPath, size, sha, received);
                                lastCp = received;
                            }
                            if (received - lastAck >= Tx.AckEvery)
                            {
                                await Proto.WriteJsonAsync(stream, new { ack = received }, ct);
                                lastAck = received;
                            }
                        }
                        await file.FlushAsync(ct);
                        file.Flush(true);
                    }

                    if (received != size) throw new IOException("connection lost — partial kept for resume");

                    WriteMeta(metaPath, size, sha, received);
                    var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    if (digest != sha.ToLowerInvariant())
                    {
                        File.Delete(partPath);
                        File.Delete(metaPath);
                        await Proto.WriteJsonAsync(stream, new { ok = false, reason = "hash" }, ct);
                        return;
                    }

                    var target = UniquePath(System.IO.Path.Combine(subDir, name));
                    File.Move(partPath, target);
                    File.Delete(metaPath);
                    await Proto.WriteJsonAsync(stream, new { ok = true }, ct);
                    Received?.Invoke(target);
                }
                finally { Active.TryRemove(info, out _); gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* drop → .part stays for resume */ }
    }

    static string Sanitize(string name)
    {
        name = System.IO.Path.GetFileName(name);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    // sanitize a relative folder path from the wire: keep clean segments, drop traversal/drive parts
    internal static string SafeRel(string rel)
    {
        var segs = rel.Split('/', '\\')
            .Select(s => System.IO.Path.GetFileName(s.Trim()))
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .Select(s => { foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; });
        return string.Join("/", segs);
    }

    static async Task HashPrefixAsync(FileStream file, IncrementalHash hash, long len, CancellationToken ct)
    {
        file.Seek(0, SeekOrigin.Begin);
        var buf = new byte[Tx.ChunkSize];
        long left = len;
        while (left > 0)
        {
            int n = await file.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
            if (n == 0) throw new EndOfStreamException();
            hash.AppendData(buf, 0, n);
            left -= n;
        }
        file.Seek(len, SeekOrigin.Begin);
    }

    static void WriteMeta(string metaPath, long size, string sha, long done)
    {
        var tmp = metaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { size, sha, done }));
        File.Move(tmp, metaPath, overwrite: true);
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
