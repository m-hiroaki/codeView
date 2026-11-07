namespace CodeView.Contracts;

internal enum ErrorCode
{
    NotFound, InvalidPath, AccessDenied, NotRegularFile, BrokenLink, Binary, DecodeFailed,
    ChangedDuringRead, InvalidPattern, RegexTimedOut, UnsupportedSize, QueueFull,
    InvalidSettings, IoFailure, Unexpected, UnsupportedDeclaration
}

internal sealed record OperationError(ErrorCode Code, string? Path = null, bool CanRetry = false);

internal enum OperationStatus { Success, Cancelled, Failure }

internal sealed class OperationResult<T>
{
    private readonly T? _value;
    public OperationStatus Status { get; }
    public OperationError? Error { get; }
    public T Value => Status == OperationStatus.Success
        ? _value!
        : throw new InvalidOperationException("成功していない処理の値は参照できません。");

    private OperationResult(OperationStatus status, T? value, OperationError? error)
        => (Status, _value, Error) = (status, value, error);

    public static OperationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(OperationStatus.Success, value, null);
    }

    public static OperationResult<T> Cancelled() => new(OperationStatus.Cancelled, default, null);

    public static OperationResult<T> Failure(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(OperationStatus.Failure, default, error);
    }
}
