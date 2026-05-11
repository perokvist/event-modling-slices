using System.ComponentModel.DataAnnotations;

namespace SampleApp.Modules;

public static class Validation
{
    public static Result<T> Validate<T>(this T command) where T : Command
    {
        var context = new ValidationContext(command);
        var results = new List<ValidationResult>();

        bool isValid = Validator.TryValidateObject(command, context, results, validateAllProperties: true);

        if (isValid)
            return Result<T>.Success(command);

        var errorMessages = results.Select(r => r.ErrorMessage ?? "Unknown error").ToList();
        var exception = new ValidationException(string.Join("; ", errorMessages));

        return Result<T>.Failure(exception);
    }
}

public sealed record Result<T>(bool IsSuccess, T? Value, Exception? Exception) : Result(IsSuccess, Exception)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static new Result<T> Success() => new(true, default, null); // for unit
    public static new Result<T> Failure(Exception exception) => new(false, default, exception);
}

public record Result(bool IsSuccess, Exception? Exception)
{
    public static Result Success() => new(true, default);
    public static Result Failure(Exception exception) => new(false, exception);
}

public static class TaskExtensions
{
    public static async Task<Result<T>> ToResultAsync<T>(this Task<T> task)
    {
        try
        {
            var value = await task;
            return Result<T>.Success(value);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(ex);
        }
    }

    public static async Task<Result> ToResultAsync(this Task task)
    {
        try
        {
            await task;
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex);
        }
    }
}
