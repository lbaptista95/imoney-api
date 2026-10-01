using Api.Features;
using Api.Features.Accounts;
using Api.Features.Transactions;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The connection string is resolved from the service provider, not read here. Two
// reasons, and both are load-bearing: a test host adds its configuration after this
// file runs, so an eager read would silently ignore it; and the OpenAPI generator
// builds this application at build time with no database configured at all.
builder.Services.AddDbContext<AppDbContext>((services, options) =>
    options.UseNpgsql(DatabaseConfiguration.ConnectionStringFrom(
        services.GetRequiredService<IConfiguration>())));

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: services =>
            DatabaseConfiguration.ConnectionStringFrom(services.GetRequiredService<IConfiguration>()),
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy);

builder.Services.AddOptions<SharedSecretOptions>()
    .Bind(builder.Configuration.GetSection(SharedSecretOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SharedSecretOptions>, SharedSecretOptionsValidator>();

builder.Services.AddAuthentication(SharedSecretAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SharedSecretAuthenticationHandler>(
        SharedSecretAuthenticationHandler.SchemeName,
        configureOptions: null);
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// The seed runs in-process rather than as a separate project so it shares this
// DbContext configuration: a second configuration is a second thing to keep true.
if (args.Contains("seed"))
{
    return await SeedCommand.RunAsync(app.Services, app.Logger);
}

// Migrations apply automatically only here. Anywhere else they are applied by an
// explicit `dotnet ef database update`, so a deploy cannot migrate by accident.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Turns an unhandled failure into problem+json with nothing internal in it. The
// diagnosis is logged by the framework; the body says only that it failed.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    ExceptionHandler = async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Api.UnhandledException")
            .LogError(feature?.Error, "unhandled failure serving {Path}", context.Request.Path);

        await Problems.InternalError().ExecuteAsync(context);
    },
});

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

// Outside the /v1 group on purpose: health exposes no data, so it needs no token.
app.MapHealthChecks("/health").AllowAnonymous();

// One group, so a route added later is guarded by default rather than by remembering.
var v1 = app.MapGroup("/v1").RequireAuthorization();
v1.MapGet("/accounts", GetAccounts.HandleAsync);
v1.MapGet("/transactions", GetTransactions.HandleAsync);

app.Run();

return 0;

/// <summary>Exposed so the integration tests assemble the real application.</summary>
public partial class Program;
