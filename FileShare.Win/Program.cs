using System.Runtime.InteropServices;

namespace FileShare.Win;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(); // WinExe has no console; attach to the parent's
            try
            {
                return SelfTest.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELFTEST FAILED: " + ex);
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Settings.Load();
        Application.Run(new MainForm());
        return 0;
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    public static void AttachConsole() => AttachConsole(-1); // ATTACH_PARENT_PROCESS
}
