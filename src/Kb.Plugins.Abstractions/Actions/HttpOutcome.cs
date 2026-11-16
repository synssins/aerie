using Microsoft.AspNetCore.Http;

namespace Kb.Plugins.Actions;

/// <summary>
/// Turns an <see cref="Outcome{T}"/> into the HTTP answer. One place, so every route, the core's and a plugin's, answers a problem the same way.
/// </summary>
public static class HttpOutcome
{
    public static IResult ToHttp<T>(this Outcome<T> outcome) => outcome.ToHttp(value => value switch
    {
        Unit => Results.NoContent(),
        FileOut file => new FileOutResult(file),
        _ => Results.Ok(value),
    });

    public static IResult ToHttp<T>(this Outcome<T> outcome, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(ok);
        return outcome.Problem is { } problem ? ToHttp(problem) : ok(outcome.Value!);
    }

    public static async Task<IResult> ToHttp<T>(this Task<Outcome<T>> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return (await outcome).ToHttp();
    }

    public static async Task<IResult> ToHttp<T>(this Task<Outcome<T>> outcome, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return (await outcome).ToHttp(ok);
    }

    /// <summary>
    /// 400 with fields is a validation problem. 401, 403 and 404 answer bare, as the routes always did.
    /// 409 answers its body when it has one. Everything else that carries a message answers problem details whose "detail" is the message.
    /// </summary>
    public static IResult ToHttp(Problem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem switch
        {
            { Status: 400, Fields: { } fields } => Results.ValidationProblem(fields.ToDictionary(f => f.Key, f => f.Value)),
            { Status: 401 } => Results.Unauthorized(),
            { Status: 404 } => Results.NotFound(),
            { Status: 409, Body: { } body } => Results.Conflict(body),
            { Status: 429, Body: RetryAfter wait } => new RetryAfterResult(Results.Problem(detail: problem.Message, statusCode: 429), wait.Seconds),
            { Message: { } message } => Results.Problem(detail: message, statusCode: problem.Status),
            _ => Results.StatusCode(problem.Status),
        };
    }

    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>Streams a file with the headers the action gave it. A file name makes it a download.</summary>
    private sealed class FileOutResult(FileOut file) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            foreach (var (name, value) in file.Headers) httpContext.Response.Headers[name] = value;
            var stream = await file.Open(httpContext.RequestAborted);
            await Results.Stream(stream, file.ContentType, file.FileName).ExecuteAsync(httpContext);
        }
    }
}
