using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Filter.ByExcluding(c =>
    {
        if (!c.Properties.TryGetValue("RequestPath", out var property))
            return false;

        if (property is Serilog.Events.ScalarValue { Value: string path })
        {
            return path.StartsWith("/health") || path.StartsWith("/metrics");
        }

        return false;
    })
    .CreateLogger();

builder.Host.UseSerilog();

var otel = builder.Services.AddOpenTelemetry();

otel.ConfigureResource(resource => resource
    .AddService(serviceName: builder.Environment.ApplicationName));

otel.WithMetrics(metrics => metrics
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddPrometheusExporter());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapPost("/payments", (PaymentRequest request, ILogger<Program> logger) =>
    {
        if (request.Amount <= 0)
        {
            return Results.BadRequest("Ungültiger Betrag.");
        }

        var confirmation = new PaymentConfirmation(
            Guid.NewGuid(),
            request.OrderId,
            request.Amount,
            DateTime.UtcNow,
            "Success"
        );

        logger.LogInformation("Zahlung erfolgreich: {Confirmation}", confirmation);

        return Results.Ok(confirmation);
    })
    .WithName("ProcessPayment");

app.MapPrometheusScrapingEndpoint();

app.Run();

record PaymentRequest(
    Guid OrderId,
    decimal Amount
);

record PaymentConfirmation(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    DateTime Timestamp,
    string Status
);