using System.Diagnostics.Metrics;
using System.Text;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using RabbitMQ.Client;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Filter.ByExcluding(c =>
    {
        if (!c.Properties.TryGetValue("RequestPath", out var property))
            return false;

        if (property is Serilog.Events.ScalarValue { Value: string path })
            return path.StartsWith("/health") || path.StartsWith("/metrics");

        return false;
    })
    .CreateLogger();

builder.Host.UseSerilog();

var otel = builder.Services.AddOpenTelemetry();
var meter = new Meter("OrderService", "1.0.0");
var orderRequestedCounter =
    meter.CreateCounter<long>("orders_requested_total", "number", "Anzahl angefragter Bestellungen");
var orderCompletedCounter =
    meter.CreateCounter<long>("orders_completed_total", "number", "Anzahl abgeschlossener Bestellungen");

otel.ConfigureResource(resource => resource
    .AddService(serviceName: builder.Environment.ApplicationName));

otel.WithMetrics(metrics => metrics
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddMeter("OrderService")
    .AddPrometheusExporter());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionString: builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "postgres",
        tags:
        [
            "db",
            "postgres",
            "ready"
        ]);

var config = builder.Configuration;

var inventoryUrl = config["ServiceUrls:Inventory"];
var paymentUrl = config["ServiceUrls:Payment"];
var rabbitmqHostName = config["ServiceUrls:RabbitMQ:HostName"];

bool resilienceEnabled = config.GetValue<bool>("Resilience:Enabled");

const string inventoryClientName = "inventory";
const string paymentClientName = "payment";

var inventoryClientBuilder = builder.Services
    .AddHttpClient(inventoryClientName, client =>
    {
        client.BaseAddress = new Uri(inventoryUrl);
        client.Timeout = TimeSpan.FromSeconds(5);
    });

if (resilienceEnabled)
{
    inventoryClientBuilder.AddStandardResilienceHandler();
}

var paymentClientBuilder = builder.Services
    .AddHttpClient(paymentClientName, client =>
    {
        client.BaseAddress = new Uri(paymentUrl);
        client.Timeout = TimeSpan.FromSeconds(5);
    });

if (resilienceEnabled)
{
    paymentClientBuilder.AddStandardResilienceHandler();
}

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

using var scope = app.Services.CreateScope();
var services = scope.ServiceProvider;

var db = services.GetRequiredService<OrderDbContext>();
var logger = services.GetRequiredService<ILogger<Program>>();

await db.Database.MigrateAsync();
await OrderSeeder.SeedAsync(db, logger);

var factory = new ConnectionFactory
{
    HostName = rabbitmqHostName
};
await using var connection = await factory.CreateConnectionAsync();
var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync("orders-exchange", ExchangeType.Fanout);

app.MapPost("/orders", async (
        OrderRequest request,
        IHttpClientFactory httpClientFactory,
        OrderDbContext db,
        ILogger<Program> logger,
        CancellationToken ct
    ) =>
    {
        logger.LogInformation("Anfrage zum Erstellen einer Bestellung für {Product} mit Menge {Quantity}",
            request.Product, request.Quantity);
        orderRequestedCounter.Add(1);

        // 1. Inventar reservieren
        var orderId = Guid.NewGuid();

        using var inventoryClient = httpClientFactory.CreateClient(inventoryClientName);
        var reserveResponse = await inventoryClient.PostAsJsonAsync("/inventory/reserve", new
        {
            Product = request.Product,
            Quantity = request.Quantity,
        }, ct);

        if (!reserveResponse.IsSuccessStatusCode)
        {
            var error = await reserveResponse.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Reservierung fehlgeschlagen: {Error}", error);

            return Results.BadRequest($"Fehler bei Reservierung: {error}");
        }

        // 2. Zahlung durchführen
        var amount = request.Quantity * 10;
        using var paymentClient = httpClientFactory.CreateClient(paymentClientName);
        var paymentResponse = await paymentClient.PostAsJsonAsync("/payments", new
        {
            OrderId = orderId,
            Amount = amount
        });

        if (!paymentResponse.IsSuccessStatusCode)
        {
            var error = await paymentResponse.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Zahlung fehlgeschlagen: {Error}", error);

            return Results.Problem($"Zahlung fehlgeschlagen: {error}", statusCode: 500);
        }

        // 3. Bestellung speichern
        var order = new Order
        {
            Id = orderId,
            Product = request.Product,
            Quantity = request.Quantity,
            CreatedAt = DateTime.UtcNow,
            Status = OrderStatus.Pending
        };

        await db.Orders.AddAsync(order, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Bestellung für {OrderId} mit {Product} erfolgreich in Auftrag gegeben", orderId,
            request.Product);

        // 4. Nachricht an RabbitMQ senden
        var message = $"{orderId}";
        var body = Encoding.UTF8.GetBytes(message);

        await channel.BasicPublishAsync(
            exchange: "orders-exchange",
            routingKey: "",
            body: body,
            ct
        );

        return Results.Created($"/orders/{order.Id}", order);
    })
    .WithName("CreateOrder");

app.MapPut("/orders/{id:guid}/status",
        async (Guid id, HttpContext context, OrderDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            logger.LogInformation("Anfrage zum Aktualisieren des Status der Bestellung {Id}", id);

            var data = await context.Request.ReadFromJsonAsync<StatusUpdateDto>(ct);
            if (data is null)
            {
                logger.LogWarning("Fehlender oder ungültiger Status im Body.");

                return Results.BadRequest("Kein Status angegeben oder ungültiges Format.");
            }

            var order = await db.Orders.FindAsync(id, ct);
            if (order == null)
            {
                logger.LogWarning("Order {Id} nicht gefunden.", id);

                return Results.NotFound();
            }

            order.Status = data.Status;
            order.UpdatedAt = DateTime.UtcNow;

            db.Orders.Update(order);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Updated Status der Order {Id} zu {Status}", order.Id, order.Status);
            orderCompletedCounter.Add(1);

            return Results.Ok(order);
        })
    .WithName("UpdateOrder");

app.MapGet("/orders", async (
        OrderDbContext db,
        ILogger<Program> logger,
        CancellationToken ct,
        int page = 1,
        int pageSize = 10
    ) =>
    {
        logger.LogInformation("Anfrage für alle Bestellungen (Page: {Page}, PageSize: {PageSize})", page, pageSize);

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;

        var totalOrders = await db.Orders.CountAsync(ct);
        var orders = await db.Orders
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var result = new
        {
            Page = page,
            PageSize = pageSize,
            Total = totalOrders,
            Data = orders
        };

        return Results.Ok(result);
    })
    .WithName("GetAllOrders");

app.MapGet("/orders/status/{status}", async (
        OrderDbContext db,
        ILogger<Program> logger,
        OrderStatus status,
        CancellationToken ct,
        int page = 1,
        int pageSize = 10
    ) =>
    {
        logger.LogInformation("Anfrage für alle Bestellungen mit Status {Status}", status);

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;

        var parsedStatus = Enum.Parse<OrderStatus>(status.ToString());

        var totalOrders = await db.Orders
            .Where(o => o.Status == parsedStatus)
            .CountAsync(ct);
        var orders = await db.Orders
            .Where(o => o.Status == parsedStatus)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var result = new
        {
            Page = page,
            PageSize = pageSize,
            Total = totalOrders,
            Data = orders
        };

        return Results.Ok(result);
    })
    .WithName("GetOrdersByStatus");

app.MapGet("/orders/{id:guid}", async (Guid id, OrderDbContext db, ILogger<Program> logger, CancellationToken ct) =>
    {
        logger.LogInformation("Anfrage für Bestellung {Id}", id);

        var order = await db.Orders.FindAsync(id, ct);
        if (order == null)
        {
            logger.LogWarning("Bestellung {Id} nicht gefunden.", id);

            return Results.NotFound();
        }

        return Results.Ok(order);
    })
    .WithName("GetOrderById");

app.MapPrometheusScrapingEndpoint();

app.Run();

public enum OrderStatus
{
    Pending,
    Completed,
    Cancelled
}

record StatusUpdateDto(
    OrderStatus Status
);

public class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Product { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
}

record OrderRequest(
    string Product,
    int Quantity
);

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("order");
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
    }
}

public static class OrderSeeder
{
    public static async Task SeedAsync(OrderDbContext context, ILogger logger)
    {
        if (await context.Orders.AnyAsync())
        {
            logger.LogInformation("Datenbank enthält bereits Bestellungen – kein Seeding notwendig.");

            return;
        }

        logger.LogInformation("Füge initiale Bestellungen hinzu...");

        var initialOrders = new List<Order>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Product = "Initial A",
                Quantity = 10,
                CreatedAt = DateTime.UtcNow,
                Status = OrderStatus.Pending
            },
            new()
            {
                Id = Guid.NewGuid(),
                Product = "Initial B",
                Quantity = 20,
                CreatedAt = DateTime.UtcNow,
                Status = OrderStatus.Pending
            },
            new()
            {
                Id = Guid.NewGuid(),
                Product = "Initial C",
                Quantity = 15,
                CreatedAt = DateTime.UtcNow,
                Status = OrderStatus.Pending
            }
        };

        await context.Orders.AddRangeAsync(initialOrders);
        await context.SaveChangesAsync();

        logger.LogInformation("Seeding abgeschlossen.");
    }
}