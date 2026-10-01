namespace Api.Features;

/// <summary>
/// The error bodies, as application/problem+json. Every message here is written by
/// hand: an exception's own text can carry a connection string or a stack trace, and
/// neither belongs in a response body.
/// </summary>
public static class Problems
{
    public static IResult BadRequest(string field, string detail) =>
        Results.Problem(
            title: "Invalid request.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["field"] = field });

    /// <summary>
    /// The body for a failure we do not want to describe: no exception message, no
    /// type name, no connection string. The detail that diagnoses it belongs in the
    /// server's log, not in the client's response.
    /// </summary>
    public static IResult InternalError() =>
        Results.Problem(
            title: "The request could not be completed.",
            detail: "The service could not reach its data store. Try again shortly.",
            statusCode: StatusCodes.Status500InternalServerError);
}
