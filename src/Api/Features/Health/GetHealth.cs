using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Features.Health;

/// <summary>What `GET /health` returns when every dependency answers.</summary>
/// <param name="Status">Always "healthy" in a 200.</param>
public sealed record HealthResponse(string Status);

public static class GetHealth
{
    /// <summary>Reports whether the API can reach its database.</summary>
    /// <remarks>
    /// A named handler over the native HealthCheckService rather than MapHealthChecks,
    /// because the OpenAPI generator does not describe MapHealthChecks endpoints and
    /// the contract must list all three routes (AC 23). The check itself is unchanged.
    /// </remarks>
    public static async Task<IResult> HandleAsync(HealthCheckService health, CancellationToken cancellationToken)
    {
        var report = await health.CheckHealthAsync(cancellationToken);

        if (report.Status == HealthStatus.Healthy)
        {
            return Results.Ok(new HealthResponse("healthy"));
        }

        // Which dependency failed, and why, stays in the server's log: an
        // unauthenticated route says only that the service is not ready.
        return Results.Problem(
            title: "Service unavailable.",
            detail: "A dependency the service needs is not reachable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
