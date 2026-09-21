using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DS4WindowsMonitor;

internal static class NativeMethods
{
    public const uint WM_NULL = 0x0000;
    public const uint SMTO_BLOCK = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [Flags]
    public enum ProcessAccess : uint
    {
        PROCESS_VM_READ = 0x0010,
        PROCESS_QUERY_INFORMATION = 0x0400,
    }

    [Flags]
    public enum MiniDumpType : uint
    {
        MiniDumpNormal = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithUnloadedModules = 0x00000020,
        MiniDumpWithProcessThreadData = 0x00000100,
        MiniDumpWithFullMemoryInfo = 0x00000800,
        MiniDumpWithThreadInfo = 0x00001000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void SetLastError(uint dwErrCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        ProcessAccess processAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        MiniDumpType dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    public static string Win32ErrorMessage(int error)
    {
        try
        {
            return new Win32Exception(error).Message;
        }
        catch
        {
            return $"Win32 error {error}";
        }
    }
}
