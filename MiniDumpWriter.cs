using System.Diagnostics;

namespace DS4WindowsMonitor;

internal static class MiniDumpWriter
{
    public static (bool Success, string Message) Write(Process process, string path, uint flags)
    {
        if (process.HasExited)
            return (false, "Process already exited before dump capture.");

        IntPtr processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessAccess.PROCESS_QUERY_INFORMATION |
            NativeMethods.ProcessAccess.PROCESS_VM_READ,
            false,
            checked((uint)process.Id));

        if (processHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            return (false, $"OpenProcess failed: {error} ({NativeMethods.Win32ErrorMessage(error)})");
        }

        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            bool ok = NativeMethods.MiniDumpWriteDump(
                processHandle,
                checked((uint)process.Id),
                stream.SafeFileHandle.DangerousGetHandle(),
                (NativeMethods.MiniDumpType)flags,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                return (false, $"MiniDumpWriteDump failed: {error} ({NativeMethods.Win32ErrorMessage(error)})");
            }

            return (true, $"Dump written: {path}");
        }
        catch (Exception ex)
        {
            return (false, $"Dump capture exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }
}
