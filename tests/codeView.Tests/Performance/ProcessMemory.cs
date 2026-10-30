using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeView.Tests.Performance;

/// <summary>Test-only OS measurement. Values from different OS metrics are not interchangeable.</summary>
internal sealed class ProcessMemory : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly byte[] usage = new byte[96];

    public string Metric => OperatingSystem.IsMacOS() ? "macOS.phys_footprint" : "PrivateMemorySize64";

    public long ReadBytes()
    {
        long value;
        if (OperatingSystem.IsMacOS())
        {
            // SDK sys/resource.h: rusage_info_v0, ri_phys_footprint at byte 72.
            if (ProcPidRusage(Environment.ProcessId, 0, usage) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            value = checked((long)BitConverter.ToUInt64(usage, 72));
        }
        else
        {
            process.Refresh();
            value = process.PrivateMemorySize64;
        }
        return Validate(value);
    }

    internal static long Validate(long value) => value > 0
        ? value : throw new InvalidOperationException("メモリ指標を取得できません。0を合格値として扱いません。");

    public void Dispose() => process.Dispose();

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage", SetLastError = true)]
    private static extern int ProcPidRusage(int pid, int flavor, [Out] byte[] buffer);
}
