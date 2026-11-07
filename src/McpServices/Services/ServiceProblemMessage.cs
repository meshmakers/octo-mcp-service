using Meshmakers.Octo.Sdk.ServiceClient;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Turns an exception of an SDK call into the error text and problem code of a tool response (AB#8346). An RFC 9457
///     problem answer becomes one readable line (<see cref="ServiceProblem.ToDisplayText" />) plus its <c>code</c>; any
///     other exception keeps its message. Only what the service answered is reported — never the tool's arguments.
/// </summary>
internal static class ServiceProblemMessage
{
    /// <summary>Error text, problem code (or <c>null</c>) and the wait time of a rate-limited answer.</summary>
    internal readonly record struct Result(string Message, string? Code, int? RetryAfterSeconds);

    /// <summary>Describes <paramref name="exception" />.</summary>
    public static Result From(Exception exception)
    {
        if (exception is ServiceClientResultException resultException &&
            ServiceProblem.TryParse(resultException, out var problem))
        {
            return new Result(problem.ToDisplayText(), problem.Code, problem.RetryAfterSeconds);
        }

        return new Result(exception.Message, null, null);
    }
}
