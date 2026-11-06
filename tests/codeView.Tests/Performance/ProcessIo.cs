using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodeView.Tests.Performance;

/// <summary>Test-only process accounting. OS counters are not interchangeable or file-specific.</summary>
internal sealed class ProcessIo
{
    private readonly byte[] _macUsage = new byte[160];
    public string Metric => OperatingSystem.IsMacOS() ? "macOS.rusage_v2.diskio_bytes" :
        OperatingSystem.IsWindows() ? "Windows.IO_COUNTERS.transfer_bytes" : throw new PlatformNotSupportedException();

    public ProcessIoSample Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (ProcPidRusage(Environment.ProcessId, 2, _macUsage) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return ParseMac(_macUsage);
        }
        if (OperatingSystem.IsWindows())
        {
            if (!GetProcessIoCounters(new IntPtr(-1), out var counters))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new(Metric, counters.ReadTransferCount, counters.WriteTransferCount);
        }
        throw new PlatformNotSupportedException();
    }

    internal static ProcessIoSample ParseMac(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 160) throw new ArgumentException("rusage_info_v2 must be 160 bytes.", nameof(bytes));
        // Adopted SDK sys/resource.h: v0=96, v1=144, then two uint64_t disk counters.
        return new("macOS.rusage_v2.diskio_bytes", BinaryPrimitives.ReadUInt64LittleEndian(bytes[144..152]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[152..160]));
    }

    internal static ProcessIoSample Difference(ProcessIoSample before, ProcessIoSample after)
    {
        if (before.Metric != after.Metric || after.ReadBytes < before.ReadBytes || after.WrittenBytes < before.WrittenBytes)
            throw new InvalidOperationException("Process I/O counters changed identity or decreased.");
        return new(after.Metric, after.ReadBytes - before.ReadBytes, after.WrittenBytes - before.WrittenBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsIoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage", SetLastError = true)]
    private static extern int ProcPidRusage(int pid, int flavor, [Out] byte[] buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out WindowsIoCounters counters);
}

internal readonly record struct ProcessIoSample(string Metric, ulong ReadBytes, ulong WrittenBytes);
