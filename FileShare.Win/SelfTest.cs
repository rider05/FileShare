using System.Net;
using System.Net.Sockets;

namespace FileShare.Win;

// Loopback test of the whole transfer protocol: clean send, then interrupted + auto-resumed send.
// Run: FileShare.exe --selftest
static class SelfTest
{
    public static int Run()
    {
        Tx.CheckpointEvery = 1L << 20; // 1 MB → fast resume granularity
        Tx.AckEvery = 1L << 20;

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fileshare_selftest_" + Guid.NewGuid().ToString("N")[..8]);
        var recvDir = System.IO.Path.Combine(dir, "recv");
        var recvDir2 = System.IO.Path.Combine(dir, "recv2");
        Directory.CreateDirectory(recvDir);
        Directory.CreateDirectory(recvDir2);

        try
        {
            // 0) rel-path sanitizing (pure logic, no sockets)
            Check(Receiver.SafeRel("../evil") == "evil", "SafeRel drops traversal");
            Check(Receiver.SafeRel("a\\b/c") == "a/b/c", "SafeRel keeps nested segments");
            Check(Receiver.SafeRel("C:/x") == "x", "SafeRel drops drive prefix");

            Console.WriteLine("selftest: receiver listening on 127.0.0.1");
            using var receiver = new Receiver(IPAddress.Loopback, recvDir);
            receiver.Start();

            // 16 MB random source
            var src = System.IO.Path.Combine(dir, "source.bin");
            var rnd = new Random(42);
            var data = new byte[1 << 20];
            using (var f = File.Create(src))
                for (int i = 0; i < 16; i++) { rnd.NextBytes(data); f.Write(data); }
            var expected = Sender.Sha256Async(src, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"selftest: source ready, {new FileInfo(src).Length:N0} bytes, sha {expected[..12]}…");

            // 1) clean transfer
            var item = new TransferItem { Path = src, Size = new FileInfo(src).Length };
            Sender.SendAsync(item, "127.0.0.1", Proto.TcpPort, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(item.State == TransferState.Done, "clean transfer completes");
            var got = System.IO.Path.Combine(recvDir, "source.bin");
            Check(File.Exists(got), "received file exists");
            Check(Sender.Sha256Async(got, CancellationToken.None).GetAwaiter().GetResult() == expected, "hash matches after clean send");

            // 2) interrupted then auto-resumed
            using var receiver2 = new Receiver(IPAddress.Loopback, recvDir2, Proto.TcpPort + 1);
            receiver2.Start();
            SimulatePartialSend(src, expected, "127.0.0.1", Proto.TcpPort + 1, bytesToSend: 5L << 20);
            Check(File.Exists(System.IO.Path.Combine(recvDir2, "source.bin.part")), "partial kept after hard disconnect");

            var item2 = new TransferItem { Path = src, Size = new FileInfo(src).Length };
            Sender.SendAsync(item2, "127.0.0.1", Proto.TcpPort + 1, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(receiver2.LastResumeOffset > 0, $"auto-resumed from byte {receiver2.LastResumeOffset:N0} (> 0)");
            Check(item2.State == TransferState.Done, "resumed transfer completes");
            Check(Sender.Sha256Async(System.IO.Path.Combine(recvDir2, "source.bin"), CancellationToken.None).GetAwaiter().GetResult() == expected, "hash matches after resume");
            Check(!File.Exists(System.IO.Path.Combine(recvDir2, "source.bin.part")), "no partial left behind");

            // 3) nested-folder transfer lands in a matching subfolder
            var item3 = new TransferItem { Path = src, Size = new FileInfo(src).Length, RelPath = "photos/trip" };
            Sender.SendAsync(item3, "127.0.0.1", Proto.TcpPort, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(item3.State == TransferState.Done, "nested folder transfer completes");
            var gotNested = System.IO.Path.Combine(recvDir, "photos", "trip", "source.bin");
            Check(File.Exists(gotNested), "nested file landed in subfolder");
            Check(Sender.Sha256Async(gotNested, CancellationToken.None).GetAwaiter().GetResult() == expected, "nested file hash matches");

            // 4) four files in flight at once to one receiver (parallel queue path)
            var parItems = new List<TransferItem>();
            for (int i = 0; i < 4; i++)
            {
                var p = System.IO.Path.Combine(dir, $"par{i}.bin");
                File.Copy(src, p);
                parItems.Add(new TransferItem { Path = p, Size = new FileInfo(p).Length });
            }
            var par = parItems.Select(it =>
                Sender.SendAsync(it, "127.0.0.1", Proto.TcpPort, null, CancellationToken.None)).ToArray();
            Task.WhenAll(par).GetAwaiter().GetResult();
            Check(parItems.All(i => i.State == TransferState.Done), "4 parallel transfers complete");
            Check(parItems.All(i => File.Exists(System.IO.Path.Combine(recvDir, i.Name))), "parallel files all arrived");

            Console.WriteLine("SELFTEST PASSED");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static void Check(bool cond, string msg)
    {
        if (!cond) throw new Exception("check failed: " + msg);
        Console.WriteLine("  ok: " + msg);
    }

    // Simulates a phone dying mid-transfer: header + N bytes, then an RST (no graceful close).
    static void SimulatePartialSend(string src, string sha, string host, int port, long bytesToSend)
    {
        using var client = new TcpClient();
        client.Connect(host, port);
        var s = client.GetStream();
        Proto.WriteJsonAsync(s, new { v = 2, name = System.IO.Path.GetFileName(src), size = new FileInfo(src).Length, sha }).GetAwaiter().GetResult();
        var first = Proto.ReadLineAsync(s).GetAwaiter().GetResult();
        if (!first.Contains("\"resume\":0")) throw new Exception("expected fresh resume:0, got: " + first);

        using var f = File.OpenRead(src);
        var buf = new byte[1 << 20];
        long left = bytesToSend;
        while (left > 0)
        {
            int n = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
            s.Write(buf, 0, n);
            left -= n;
        }
        s.Flush();
        client.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0); // RST
        client.Close();
    }
}
