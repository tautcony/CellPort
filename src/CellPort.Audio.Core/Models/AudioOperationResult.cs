namespace CellPort.Audio.Core.Models;

/// <summary>Immutable result of an operation that has no return value.</summary>
public sealed class AudioOperationResult
{
    private AudioOperationResult(bool succeeded, AudioOperationError? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }
    public AudioOperationError? Error { get; }

    public static AudioOperationResult Success() => new(true, null);

    public static AudioOperationResult Failure(AudioOperationError error) =>
        new(false, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Immutable result of an operation that returns a value.</summary>
public sealed class AudioOperationResult<T>
{
    private AudioOperationResult(bool succeeded, T? value, AudioOperationError? error)
    {
        Succeeded = succeeded;
        Value = value;
        Error = error;
    }

    public bool Succeeded { get; }
    public T? Value { get; }
    public AudioOperationError? Error { get; }

    public static AudioOperationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AudioOperationResult<T>(true, value, null);
    }

    public static AudioOperationResult<T> Failure(AudioOperationError error) =>
        new(false, default, error ?? throw new ArgumentNullException(nameof(error)));
}
