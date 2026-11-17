using System.Text;
using System.Text.Json;

namespace FileShare.Win;

// Shared wire format: JSON line + raw bytes. Same spec implemented in Transfer.kt.
static class Proto
{
    public const int UdpPort = 53515;
    public const int TcpPort = 53516;
    public const string AppTag = "fileshare";

    public static string DeviceName => Environment.MachineName;

    public static string BroadcastJson(string name, string platform) =>
        JsonSerializer.Serialize(new { app = AppTag, name, platform, tcp = TcpPort });

    public static async Task<string> ReadLineAsync(Stream s, CancellationToken ct = default)
    {
        var buf = new byte[1];
        using var ms = new MemoryStream();
        while (true)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n == 0) throw new EndOfStreamException();
            if (buf[0] == (byte)'\n') break;
            ms.WriteByte(buf[0]);
            if (ms.Length > 4096) throw new InvalidDataException("header too long");
        }
        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
    }

    public static async Task WriteJsonAsync(Stream s, object o, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o) + "\n");
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }
}
