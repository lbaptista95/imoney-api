using Api.Features;
using Api.Features.Accounts;
using Api.Features.Health;
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

// snake_case on the wire, because the plan's Surface names the fields that way
// (`next_cursor`, `account_id`, `occurred_at`) and the app is generated from it.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

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
//
// If the database cannot be reached here, the API refuses to start (AC 5a): it
// would otherwise serve with no schema applied, which AC 2 forbids. Surviving a
// database failure (AC 5) is about failures after this point.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        // Through the redactor, for the same reason as the seed: the exception that
        // says it could not connect is the text most likely to quote the string.
        var safe = ConnectionSecretRedactor.Redact(ex.Message, db.Database.GetConnectionString());
        app.Logger.LogCritical("startup: could not reach the database to apply migrations: {Error}", safe);
        await Console.Error.WriteLineAsync($"startup: could not reach the database to apply migrations: {safe}");
        return 1;
    }
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
app.MapGet("/health", GetHealth.HandleAsync)
    .WithName("GetHealth")
    .AllowAnonymous()
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// One group, so a route added later is guarded by default rather than by remembering.
// Every status below is the one the plan's Surface lists for that route, and every
// error is problem+json: the contract and the runtime have to say the same thing.
var v1 = app.MapGroup("/v1").RequireAuthorization();

v1.MapGet("/accounts", GetAccounts.HandleAsync)
    .WithName("GetAccounts")
    .Produces<List<AccountResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status500InternalServerError);

v1.MapGet("/transactions", GetTransactions.HandleAsync)
    .WithName("GetTransactions")
    .Produces<TransactionPage>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status500InternalServerError);

app.Run();

return 0;

/// <summary>Exposed so the integration tests assemble the real application.</summary>
public partial class Program;
