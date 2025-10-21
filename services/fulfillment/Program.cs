using System.Text;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddLogging();
builder.Services.AddHttpClient();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var config = builder.Configuration;

// Service-URLs
var orderUrl = config["ServiceUrls:Order"];
var rabbitmqHostName = config["ServiceUrls:RabbitMQ:HostName"];

bool resilienceEnabled = config.GetValue<bool>("Resilience:Enabled");

const string orderClientName = "order";

var orderClientBuilder = builder.Services
    .AddHttpClient(orderClientName, client => { client.BaseAddress = new Uri(orderUrl); });

if (resilienceEnabled)
{
    orderClientBuilder.AddStandardResilienceHandler();
}

var app = builder.Build();

app.UseCors("AllowAll");

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

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

var factory = new ConnectionFactory() { HostName = rabbitmqHostName };
var connection = await factory.CreateConnectionAsync();
var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync("orders-exchange", ExchangeType.Fanout);
await channel.QueueDeclareAsync("fulfillment-queue", durable: false, exclusive: false, autoDelete: false);
await channel.QueueBindAsync("fulfillment-queue", "orders-exchange", "");

var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += async (model, ea) =>
{
    var body = ea.Body.ToArray();
    var message = Encoding.UTF8.GetString(body);
    logger.LogInformation("Bestellung empfangen: {Message}", message);

    var orderId = ExtractOrderId(message);

    if (orderId == Guid.Empty)
    {
        logger.LogWarning("Bestellung {OrderId} nicht gefunden", orderId);
        return;
    }

    var client = httpClientFactory.CreateClient(orderClientName);
    var updateUrl = $"{orderUrl}/orders/{orderId}/status";

    var content = JsonContent.Create(new { Status = OrderStatus.Completed });
    var response = await client.PutAsync(updateUrl, content);

    if (response.IsSuccessStatusCode)
    {
        logger.LogInformation("Status aktualisiert für Bestellung {OrderId}", orderId);
    }
    else
    {
        logger.LogWarning("Fehler beim Aktualisieren der Bestellung {OrderId}: {StatusCode}",
            orderId, response.StatusCode);
    }

    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
};

await channel.BasicConsumeAsync(
    queue: "fulfillment-queue",
    autoAck: false,
    consumer: consumer);

app.MapPrometheusScrapingEndpoint();

app.Run();

static Guid ExtractOrderId(string message)
{
    return Guid.TryParse(message, out Guid orderId) ? orderId : Guid.Empty;
}

enum OrderStatus
{
    Pending,
    Completed,
    Cancelled
}