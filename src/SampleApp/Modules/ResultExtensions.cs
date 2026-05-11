using System.ComponentModel.DataAnnotations;

namespace SampleApp.Modules;

public static class ResultExtensions
{
    public static IResult ToHttpResult(
        this Result result,
        Func<IResult> onSuccess)
    {
        return result switch
        {
            { IsSuccess: true } => onSuccess(),
            { Exception: ValidationException } => Results.Problem(result.Exception?.Message, statusCode: 400),
            _ => Results.Problem(result.Exception?.Message, statusCode: 500),
        };
    }
}






