using System.Runtime.InteropServices;
using System.Text;

namespace StartManager;

/// <summary>Connect a Windows GUI executable to its caller without allocating a console window.</summary>
internal static class CliConsole
{
    public static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            var output = GetStdHandle(-11); var error = GetStdHandle(-12);
            var keepOutput = Valid(output); var keepError = Valid(error);
            // AttachConsole can reset redirected handles, so preserve pipes/files from the caller.
            if (AttachConsole(unchecked((uint)-1)))
            {
                if (keepOutput) SetStdHandle(-11, output);
                if (keepError) SetStdHandle(-12, error);
            }
        }
        var utf8 = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.IsOutputRedirected ? utf8 : Console.OutputEncoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.IsErrorRedirected ? utf8 : Console.OutputEncoding) { AutoFlush = true });
    }

    private static bool Valid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int name, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
}
