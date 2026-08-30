using System.Runtime.InteropServices;
using System.Text;

namespace FxDeck.Tray;

/// <summary>FxDeck is a WinExe; for <c>--send</c> / <c>--console</c> we attach to the terminal that started us.</summary>
internal static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    /// <summary>Attaches to the parent's console, or allocates a new one when there is none (e.g. started from Explorer).</summary>
    public static bool TryAttach(bool allocateIfNone)
    {
        var attached = AttachConsole(AttachParentProcess) || (allocateIfNone && AllocConsole());
        if (attached)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine();
        }

        return attached;
    }
}
