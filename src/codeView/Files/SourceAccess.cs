using System.ComponentModel;
using System.Runtime.InteropServices;
using CodeView.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CodeView.Files;

internal interface ISourceAccess
{
    OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct);
    SourceStamp GetStamp(SourcePath path);
    OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct);
    IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct);
    OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct);
}

/// <summary>Read-only I/O boundary. No write/save capability is exposed.</summary>
internal sealed class SourceAccess : ISourceAccess
{
    private static readonly EnumerationOptions ChildOptions = new()
    {
        RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        IgnoreInaccessible = false, AttributesToSkip = 0, MatchType = MatchType.Simple
    };

    public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(path.FullPath);
            var kind = (attributes & FileAttributes.Directory) != 0 ? SourceEntryKind.Directory
                : (attributes & FileAttributes.Device) != 0 ? SourceEntryKind.Other : SourceEntryKind.FileCandidate;
            ct.ThrowIfCancellationRequested();
            return OperationResult<SourceEntry>.Success(new(path, Path.GetFileName(path.FullPath), kind,
                (attributes & FileAttributes.ReparsePoint) != 0));
        }
        catch (FileNotFoundException) { return Fail(ErrorCode.NotFound); }
        catch (DirectoryNotFoundException) { return Fail(ErrorCode.NotFound); }
        catch (UnauthorizedAccessException) { return Fail(ErrorCode.AccessDenied); }
        catch (IOException) { return Fail(ErrorCode.IoFailure); }

        OperationResult<SourceEntry> Fail(ErrorCode code) => OperationResult<SourceEntry>.Failure(new(code, path.DisplayPath, true));
    }

    public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        var isLink = false;
        try
        {
            var info = new DirectoryInfo(path.FullPath);
            isLink = info.LinkTarget is not null;
            var resolved = isLink ? info.ResolveLinkTarget(true)?.FullName : path.FullPath;
            if (resolved is null)
                return OperationResult<SourcePath>.Failure(new(ErrorCode.BrokenLink, path.DisplayPath));
            var attributes = File.GetAttributes(resolved);
            if ((attributes & FileAttributes.Directory) == 0)
                return OperationResult<SourcePath>.Failure(new(ErrorCode.NotRegularFile, path.DisplayPath));
            ct.ThrowIfCancellationRequested();
            return OperationResult<SourcePath>.Success(SourcePath.Create(resolved, path.FullPath));
        }
        catch (FileNotFoundException) { return Fail(isLink ? ErrorCode.BrokenLink : ErrorCode.NotFound); }
        catch (DirectoryNotFoundException) { return Fail(isLink ? ErrorCode.BrokenLink : ErrorCode.NotFound); }
        catch (UnauthorizedAccessException) { return Fail(ErrorCode.AccessDenied); }
        catch (IOException) { return Fail(isLink ? ErrorCode.BrokenLink : ErrorCode.IoFailure); }

        OperationResult<SourcePath> Fail(ErrorCode code) => OperationResult<SourcePath>.Failure(new(code, path.DisplayPath, true));
    }

    public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        // The root must be explicitly resolved first. A child changed to a link is not entered.
        var attributes = File.GetAttributes(path.FullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンクフォルダは暗黙に列挙できません。");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("フォルダではありません。");
        foreach (var info in new DirectoryInfo(path.FullPath).EnumerateFileSystemInfos("*", ChildOptions))
        {
            ct.ThrowIfCancellationRequested();
            var entryAttributes = info.Attributes;
            var kind = (entryAttributes & FileAttributes.Directory) != 0 ? SourceEntryKind.Directory
                : (entryAttributes & FileAttributes.Device) != 0 ? SourceEntryKind.Other : SourceEntryKind.FileCandidate;
            yield return new(SourcePath.Create(info.FullName, path.FullPath), info.Name, kind,
                (entryAttributes & FileAttributes.ReparsePoint) != 0);
        }
    }

    public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        var isLink = false;
        try
        {
            var attributes = File.GetAttributes(path.FullPath);
            isLink = (attributes & FileAttributes.ReparsePoint) != 0;
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                return OperationResult<FileStream>.Failure(new(ErrorCode.NotRegularFile, path.DisplayPath));

            var handle = OperatingSystem.IsMacOS()
                ? OpenMacRegularFile(path.FullPath)
                : OpenWindowsRegularFile(path.FullPath);
            try
            {
                ct.ThrowIfCancellationRequested();
                var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, false);
                return OperationResult<FileStream>.Success(stream);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException)
        {
            return OperationResult<FileStream>.Failure(new(isLink ? ErrorCode.BrokenLink : ErrorCode.NotFound, path.DisplayPath));
        }
        catch (DirectoryNotFoundException)
        {
            return OperationResult<FileStream>.Failure(new(ErrorCode.NotFound, path.DisplayPath));
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<FileStream>.Failure(new(ErrorCode.AccessDenied, path.DisplayPath));
        }
        catch (NotRegularFileException)
        {
            return OperationResult<FileStream>.Failure(new(ErrorCode.NotRegularFile, path.DisplayPath));
        }
        catch (Win32Exception error)
        {
            var code = OperatingSystem.IsMacOS()
                ? error.NativeErrorCode switch
                {
                    2 => isLink ? ErrorCode.BrokenLink : ErrorCode.NotFound,
                    1 or 13 => ErrorCode.AccessDenied,
                    62 => ErrorCode.BrokenLink,
                    _ => ErrorCode.IoFailure
                }
                : error.NativeErrorCode switch
                {
                    2 or 3 => isLink ? ErrorCode.BrokenLink : ErrorCode.NotFound,
                    5 => ErrorCode.AccessDenied,
                    _ => ErrorCode.IoFailure
                };
            return OperationResult<FileStream>.Failure(new(code, path.DisplayPath));
        }
        catch (IOException)
        {
            return OperationResult<FileStream>.Failure(new(ErrorCode.IoFailure, path.DisplayPath));
        }
    }

    public SourceStamp GetStamp(SourcePath path)
    {
        var info = new FileInfo(path.FullPath);
        if (info.LinkTarget is not null)
        {
            info = info.ResolveLinkTarget(true) as FileInfo
                ?? throw new FileNotFoundException("リンク先の通常ファイルが見つかりません。", path.FullPath);
        }
        return new(info.Length, info.LastWriteTimeUtc);
    }

    private static SafeFileHandle OpenMacRegularFile(string path)
    {
        // Darwin O_RDONLY | O_NONBLOCK | O_CLOEXEC. FIFO open must not wait for a writer.
        var fd = MacOpen(path, 0x00000004 | 0x01000000);
        if (fd < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle(fd, true);
        try
        {
            // Darwin stat64: st_mode is a 16-bit field at offset 4. Reserve the full native structure.
            var status = GetMacFileStatus(handle);
            var mode = BitConverter.ToUInt16(status, 4);
            if ((mode & 0xf000) != 0x8000)
                throw new NotRegularFileException();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenWindowsRegularFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsとmacOSを対象としています。");
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        try
        {
            if (WindowsGetFileType(handle) != 1) // FILE_TYPE_DISK
                throw new NotRegularFileException();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class NotRegularFileException : Exception;

    internal static bool HasSingleLink(SafeFileHandle handle)
    {
        if (OperatingSystem.IsMacOS())
            return BitConverter.ToUInt16(GetMacFileStatus(handle), 6) == 1; // Darwin stat64 st_nlink.
        return OperatingSystem.IsWindows() &&
            GetFileInformationByHandle(handle, out var information) && information.NumberOfLinks == 1;
    }

    private static byte[] GetMacFileStatus(SafeFileHandle handle)
    {
        var status = new byte[256];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacFStatInode64(handle, status)
            : MacFStat(handle, status);
        if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return status;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(SafeFileHandle handle, [Out] byte[] status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFStatInode64(SafeFileHandle handle, [Out] byte[] status);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint WindowsGetFileType(SafeFileHandle handle);
}
