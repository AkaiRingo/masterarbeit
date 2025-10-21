using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
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

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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

using var scope = app.Services.CreateScope();
var services = scope.ServiceProvider;

var db = services.GetRequiredService<InventoryDbContext>();
var logger = services.GetRequiredService<ILogger<Program>>();

await db.Database.MigrateAsync();
await InventorySeeder.SeedAsync(db, logger);

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGet("/inventory", async (InventoryDbContext db, ILogger<Program> logger, CancellationToken ct) =>
    {
        logger.LogInformation("Anfrage für alle Produkte im Inventar");
        var items = await db.Items.ToListAsync(ct);

        return Results.Ok(items);
    })
    .WithName("GetAllInventory");

app.MapGet("/inventory/{product}", async (
        string product,
        InventoryDbContext db,
        ILogger<Program> logger,
        CancellationToken ct
    ) =>
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Product == product, ct);

        if (item is null)
        {
            logger.LogWarning("Produkt '{Product}' nicht gefunden.", product);

            return Results.NotFound($"Produkt '{product}' nicht gefunden.");
        }

        return Results.Ok(item);
    })
    .WithName("GetInventory");

app.MapPost("/inventory/reserve", async (
        ReserveRequest request,
        InventoryDbContext db,
        ILogger<Program> logger,
        CancellationToken ct
    ) =>
    {
        logger.LogInformation("Reservierung: {Product} x{Quantity}", request.Product, request.Quantity);

        var item = await db.Items.FirstOrDefaultAsync(i => i.Product == request.Product, ct);

        if (item is null)
        {
            return Results.NotFound($"Produkt '{request.Product}' nicht vorhanden.");
        }

        if (item.Quantity < request.Quantity)
        {
            return Results.BadRequest("Nicht genügend Bestand verfügbar.");
        }

        // item.Quantity -= request.Quantity;
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            Status = "Reserved",
            request.Product,
            request.Quantity
        });
    })
    .WithName("ReserveInventory");

app.MapPrometheusScrapingEndpoint();

app.Run();

public class InventoryItem
{
    public Guid Id { get; set; }
    public string Product { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

record ReserveRequest(
    string Product,
    int Quantity
);

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<InventoryItem> Items => Set<InventoryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.Entity<InventoryItem>().HasKey(i => i.Id);
        modelBuilder.Entity<InventoryItem>().HasIndex(i => i.Product).IsUnique();
    }
}

public static class InventorySeeder
{
    public static async Task SeedAsync(InventoryDbContext db, ILogger logger)
    {
        if (await db.Items.AnyAsync()) return;

        logger.LogInformation("Füge initiale Inventar-Einträge hinzu...");

        var items = new List<InventoryItem>
        {
            new InventoryItem
            {
                Id = Guid.NewGuid(),
                Product = "Widget A",
                Quantity = 10000,
                CreatedAt = DateTime.UtcNow
            },
            new InventoryItem
            {
                Id = Guid.NewGuid(),
                Product = "Widget B",
                Quantity = 5000,
                CreatedAt = DateTime.UtcNow
            },
            new InventoryItem
            {
                Id = Guid.NewGuid(),
                Product = "Widget C",
                Quantity = 2000,
                CreatedAt = DateTime.UtcNow
            }
        };

        await db.Items.AddRangeAsync(items);
        await db.SaveChangesAsync();
    }
}