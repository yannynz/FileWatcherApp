using System.Runtime.InteropServices;

static class ConsoleHelper
{
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public static void DisableQuickEdit()
    {
        IntPtr hStdin = GetStdHandle(STD_INPUT_HANDLE);
        if (hStdin == IntPtr.Zero) return;

        if (!GetConsoleMode(hStdin, out uint mode)) return;

        // habilita EXTENDED_FLAGS e remove QUICK_EDIT
        mode |= ENABLE_EXTENDED_FLAGS;
        mode &= ~ENABLE_QUICK_EDIT_MODE;

        SetConsoleMode(hStdin, mode);
    }
}

