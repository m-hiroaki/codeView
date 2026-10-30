using System.ComponentModel;
using System.Runtime.InteropServices;
using CodeView.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CodeView.Files;

internal interface ISourceAccess
{
    OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct);
    SourceStamp GetStamp(SourcePath path);
}

/// <summary>Read-only I/O boundary. No write/save capability is exposed.</summary>
internal sealed class SourceAccess : ISourceAccess
{
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
            var status = new byte[256];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? MacFStatInode64(fd, status)
                : MacFStat(fd, status);
            if (result != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
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

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(int fd, [Out] byte[] status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFStatInode64(int fd, [Out] byte[] status);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint WindowsGetFileType(SafeFileHandle handle);
}
