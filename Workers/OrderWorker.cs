using Amazon.SQS;
using Amazon.SQS.Model;
using EcsApp.Data;
using EcsApp.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EcsApp.Workers
{
    public class OrderWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAmazonSQS _sqs;
        private readonly ILogger<OrderWorker> _logger;
        private readonly string _queueUrl;

        public OrderWorker(
            IServiceScopeFactory scopeFactory,
            IAmazonSQS sqs,
            ILogger<OrderWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _sqs          = sqs;
            _logger       = logger;
            _queueUrl     = Environment.GetEnvironmentVariable("SQS_QUEUE_URL") ?? "";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("OrderWorker started. Listening on: {Queue}", _queueUrl);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Read up to 10 messages at once
                    var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl            = _queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds     = 5    // long polling — waits 5s for messages
                    }, stoppingToken);

                    if (response.Messages.Count == 0)
                    {
                        _logger.LogInformation("No messages in queue. Waiting...");
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }

                    _logger.LogInformation("Received {Count} messages", response.Messages.Count);

                    foreach (var message in response.Messages)
                    {
                        await ProcessMessage(message, stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error processing SQS messages. Retrying in 5s...");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task ProcessMessage(Message message, CancellationToken ct)
        {
            try
            {
                // Deserialize order from SQS message
                var orderData = JsonSerializer.Deserialize<OrderMessage>(
                    message.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (orderData is null)
                {
                    _logger.LogWarning("Failed to deserialize message: {Body}", message.Body);
                    return;
                }

                // Save order to RDS
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var order = new Order
                {
                    ProductId   = orderData.ProductId,
                    ProductName = orderData.ProductName,
                    Quantity    = orderData.Quantity,
                    TotalPrice  = orderData.TotalPrice,
                    Status      = "Processed",
                    CreatedAt   = orderData.CreatedAt,
                    ProcessedAt = DateTime.UtcNow
                };

                db.Orders.Add(order);
                await db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Order processed: {Product} x{Qty} = ${Price}",
                    order.ProductName, order.Quantity, order.TotalPrice);

                // Delete message from SQS — only after successful DB save
                await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, ct);

                _logger.LogInformation("Message deleted from SQS: {Id}", message.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process message: {Id}", message.MessageId);
                // Don't delete — message will reappear after visibility timeout
            }
        }
    }

    // Matches the JSON we send from checkout endpoint
    public record OrderMessage(
        int      ProductId,
        string   ProductName,
        int      Quantity,
        decimal  TotalPrice,
        DateTime CreatedAt);
}