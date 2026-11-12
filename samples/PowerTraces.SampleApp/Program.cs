using System.Diagnostics;
using Microsoft.Data.SqlClient;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PowerTraces;
using PowerTraces.Dashboard;
using PowerTraces.MSSQL;

var builder = WebApplication.CreateBuilder(args);

// This is the whole point of the library: point it at a SQL Server connection string and every
// span/log the app emits gets written straight there — no collector, no EF Core, no migrations.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:Default. Set it in appsettings.Development.json or via " +
        "`dotnet user-secrets set ConnectionStrings:Default \"...\"`.");

var resourceBuilder = ResourceBuilder.CreateDefault().AddService("powertraces-exporter-sample-app");

const string dashboardPrefix = "/otel";

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // Lets you see for yourself that the exporter's own schema-init/writer traffic never
        // shows up in the dashboard — even with every SQL statement this app issues instrumented,
        // including the ones PowerTraces.MSSQL makes to its own tables.
        .AddSqlClientInstrumentation(o => o.RecordException = true)
        .AddSource(SampleActivitySource.Name)
        .AddPowerTracesMsSqlExporter(connectionString, o =>
        {
            // Otherwise every request the dashboard itself makes (its assets, its own query API)
            // shows up as noise in the trace list you're using it to browse.
            o.ExcludePathPrefixes.Add(dashboardPrefix);
        }))
    .WithLogging(logging => logging
        .SetResourceBuilder(resourceBuilder)
        .AddPowerTracesMsSqlExporter(connectionString));

// Registers a SQL Server-backed IDashboardStore under PowerTracesDatabaseProvider.SqlServer — the
// dashboard package itself has no idea SQL Server exists, it just resolves whatever's keyed under
// the provider you pass to MapPowerTracesDashboard below.
builder.Services.AddPowerTracesMsSqlDashboard(connectionString);

var dashboardUsername = builder.Configuration["Dashboard:Username"];
var dashboardPassword = builder.Configuration["Dashboard:Password"];

var app = builder.Build();

app.MapGet("/", () => Results.Redirect(dashboardPrefix));

// A few endpoints that generate varied spans/logs, purely so there's something to look at in the
// dashboard right after startup.
app.MapGet("/hello/{name}", (string name, ILogger<Program> logger) =>
{
    using var activity = SampleActivitySource.Source.StartActivity("greet");
    activity?.SetTag("greet.name", name);

    logger.LogInformation("Greeting {Name}", name);
    return Results.Ok(new { message = $"Hello, {name}!" });
});

app.MapGet("/work", async (ILogger<Program> logger) =>
{
    using var activity = SampleActivitySource.Source.StartActivity("do-work");
    logger.LogInformation("Starting simulated work");

    await SimulateStepAsync("step-1", logger);
    // A genuine DB call the HOST app makes — deliberately against no particular table (just
    // @@VERSION), so with AddSqlClientInstrumentation() enabled above you get a real, visible SQL
    // span here to contrast against PowerTraces.MSSQL's own internal writes, which never show up.
    await QueryDatabaseAsync(connectionString, logger);
    await SimulateStepAsync("step-2", logger);

    logger.LogInformation("Work complete");
    return Results.Ok(new { status = "done" });
});

app.MapGet("/fail", (ILogger<Program> logger) =>
{
    using var activity = SampleActivitySource.Source.StartActivity("fail-on-purpose");
    try
    {
        throw new InvalidOperationException("Simulated failure — this is what an error trace looks like.");
    }
    catch (Exception ex)
    {
        // Records the exception with its stack and the code around each line of this app that it
        // passed through (sources come from the PDB — see <EmbedAllSources> in the .csproj).
        activity.RecordExceptionWithSource(ex);
        logger.LogError(ex, "Something went wrong handling the request");
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// The dashboard: traces/logs UI + its query API, mounted at /otel, backed by whichever store is
// registered under PowerTracesDatabaseProvider.SqlServer above. Creates its own tables if they don't
// exist yet. Basic-auth protects both the UI and the API in one shot when Dashboard:Username/
// Password are configured (unset -> no auth, e.g. when the host already puts this behind its own
// auth/network boundary).
app.MapPowerTracesDashboard(dashboardPrefix, PowerTracesDatabaseProvider.SqlServer, dashboardUsername, dashboardPassword, (o) => o.Title = "Test");

app.Run();

static async Task SimulateStepAsync(string stepName, ILogger logger)
{
    using var activity = SampleActivitySource.Source.StartActivity(stepName);
    logger.LogDebug("Running {Step}", stepName);
    await Task.Delay(Random.Shared.Next(20, 150));
}

static async Task QueryDatabaseAsync(string connectionString, ILogger logger)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT @@VERSION;";
    var version = (string)(await command.ExecuteScalarAsync())!;

    logger.LogDebug("SQL Server version: {Version}", version);
}

internal static class SampleActivitySource
{
    public const string Name = "PowerTraces.SampleApp";
    public static readonly ActivitySource Source = new(Name);
}
