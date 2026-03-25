using Microsoft.EntityFrameworkCore;
using Amazon.SQS;
using Amazon.SQS.Model;
using EcsApp.Data;
using EcsApp.Models;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Connection string from env var
var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

// SQS queue URL from env var
var sqsQueueUrl = Environment.GetEnvironmentVariable("SQS_QUEUE_URL") ?? "";

// Register EF Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// Register SQS client
builder.Services.AddAWSService<IAmazonSQS>();
builder.Services.AddHostedService<EcsApp.Workers.OrderWorker>();

var app = builder.Build();

// Auto-run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    var retries = 10;
    while (retries > 0)
    {
        try
        {
            logger.LogInformation("Attempting DB migration...");
            db.Database.Migrate();
            logger.LogInformation("DB migration successful");
            break;
        }
        catch (Exception ex)
        {
            retries--;
            logger.LogWarning("DB not ready. Retrying in 10s... ({Retries} attempts left). Error: {Error}", 
                retries, ex.Message);
            Thread.Sleep(10000);
        }
    }
}

// ─── Existing endpoints ───────────────────────────────────────────

app.MapGet("/", () => "Hello from .NET Core on EKS with RDS!");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/stress", () =>
{
    var result = 0;
    for (int i = 0; i < 1000000; i++) result += i;
    return Results.Ok(new { result });
});

// ─── Product endpoints ────────────────────────────────────────────

app.MapGet("/products", async (AppDbContext db) =>
    await db.Products.ToListAsync());

app.MapPost("/products", async (AppDbContext db, Product product) =>
{
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/products/{product.Id}", product);
});

app.MapGet("/products/{id}", async (AppDbContext db, int id) =>
{
    var product = await db.Products.FindAsync(id);
    return product is null ? Results.NotFound() : Results.Ok(product);
});

// ─── Order endpoints ──────────────────────────────────────────────

// POST /orders/checkout — reduce stock + send to SQS
app.MapPost("/orders/checkout", async (
    AppDbContext db,
    IAmazonSQS sqs,
    CheckoutRequest request) =>
{
    // Find product
    var product = await db.Products.FindAsync(request.ProductId);
    if (product is null)
        return Results.NotFound(new { error = "Product not found" });

    // Check stock
    if (product.Stock < request.Quantity)
        return Results.BadRequest(new { error = "Insufficient stock" });

    // Reduce stock
    product.Stock -= request.Quantity;
    await db.SaveChangesAsync();

    // Build order message
    var orderMessage = new
    {
        ProductId   = product.Id,
        ProductName = product.Name,
        Quantity    = request.Quantity,
        TotalPrice  = product.Price * request.Quantity,
        CreatedAt   = DateTime.UtcNow
    };

    // Send to SQS
    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl    = sqsQueueUrl,
        MessageBody = JsonSerializer.Serialize(orderMessage)
    });

    return Results.Ok(new
    {
        message     = "Order placed successfully",
        productName = product.Name,
        quantity    = request.Quantity,
        totalPrice  = orderMessage.TotalPrice
    });
});

// GET /orders — read all orders from RDS
app.MapGet("/orders", async (AppDbContext db) =>
    await db.Orders.OrderByDescending(o => o.CreatedAt).ToListAsync());

app.Run();

// ─── Request model ────────────────────────────────────────────────
record CheckoutRequest(int ProductId, int Quantity);