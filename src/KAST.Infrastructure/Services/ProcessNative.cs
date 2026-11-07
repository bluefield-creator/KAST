using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace KAST.Infrastructure.Services;

/// <summary>
/// Thin, best-effort OS helpers for process hunting: reading another process's
/// command line / executable path and sending a polite termination signal.
/// Every method swallows OS errors and returns null/false — callers treat
/// "unknown" conservatively.
/// </summary>
internal static partial class ProcessNative
{
    // ── Instance tag ─────────────────────────────────────────────────────────

    // KAST launches every server with -name=server_{id} and every headless
    // client with -name=server_{id}_hc{n}; the tag survives a panel restart.
    [GeneratedRegex(@"(?:^|[\s""])-name=server_(\d+)(?:_hc(\d+))?(?=$|[\s""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstanceTagRegex();

    internal static bool TryParseInstanceTag(string? commandLine, out int instanceId, out int? headlessIndex)
    {
        instanceId = 0;
        headlessIndex = null;
        if (string.IsNullOrEmpty(commandLine))
            return false;

        var match = InstanceTagRegex().Match(commandLine);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out instanceId))
            return false;

        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var hc))
            headlessIndex = hc;
        return true;
    }

    // ── Command line / executable ────────────────────────────────────────────

    internal static string? TryGetCommandLine(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var path = $"/proc/{pid}/cmdline";
                if (!File.Exists(path))
                    return null;
                var args = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                return args.Length == 0 ? null : string.Join(' ', args);
            }

            if (OperatingSystem.IsWindows())
                return TryGetWindowsCommandLine(pid);
        }
        catch
        {
            // Best effort: the process may have exited or be inaccessible.
        }

        return null;
    }

    internal static string? TryGetExecutablePath(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.FullName;

            if (OperatingSystem.IsWindows())
                return TryGetWindowsImagePath(pid);
        }
        catch
        {
            // Best effort.
        }

        return null;
    }

    // ── Polite termination ───────────────────────────────────────────────────

    /// <summary>Sends SIGTERM on Unix. Returns false when it could not be delivered.</summary>
    internal static bool TrySendTerminate(int pid)
    {
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            const int SIGTERM = 15;
            return SysKill(pid, SIGTERM) == 0;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int sig);

    // ── Windows ──────────────────────────────────────────────────────────────

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle handle, int infoClass, IntPtr buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle handle, int flags, IntPtr buffer, ref int size);

    private static string? TryGetWindowsCommandLine(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;

        var length = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out var needed);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow)
                {
                    length = Math.Max(needed, length * 2);
                    continue;
                }

                if (status < 0)
                    return null;

                // Buffer starts with a UNICODE_STRING { ushort Length; ushort Max; PWSTR Buffer }.
                var byteLength = (ushort)Marshal.ReadInt16(buffer);
                var textPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return byteLength == 0 || textPtr == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(textPtr, byteLength / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static string? TryGetWindowsImagePath(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;

        var size = 32_768;
        var buffer = Marshal.AllocHGlobal(size * 2);
        try
        {
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Marshal.PtrToStringUni(buffer, size)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
