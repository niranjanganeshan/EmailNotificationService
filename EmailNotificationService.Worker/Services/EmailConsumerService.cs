using System.Text;
using System.Text.Json;
using EmailNotificationService.Contracts.Messages;
using EmailNotificationService.Contracts.Messaging;
using EmailNotificationService.Worker.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog.Context;

namespace EmailNotificationService.Worker.Services;

/// <summary>
/// Consumes "send email" messages from the queue and simulates sending them. On a simulated
/// failure, republishes the message with an incremented x-retry-count header (up to
/// RabbitMqOptions.MaxRetryAttempts). Once attempts are exhausted, nacks without requeue so
/// the broker dead-letters the message into the DLQ via the queue's own topology arguments —
/// no manual DLQ publish needed.
/// </summary>
public sealed class EmailConsumerService(
    IRabbitMqConnectionProvider connectionProvider,
    ISimulatedEmailSender emailSender,
    IOptions<RabbitMqOptions> options,
    ILogger<EmailConsumerService> logger) : BackgroundService
{
    private const string RetryCountHeader = "x-retry-count";

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        _channel = await connectionProvider.Connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: opts.PrefetchCount, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, ea) => HandleMessageAsync(_channel, opts, ea, stoppingToken);

        await _channel.BasicConsumeAsync(
            queue: opts.QueueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation("Consuming from queue {Queue} (prefetch {Prefetch})", opts.QueueName, opts.PrefetchCount);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_channel is not null)
        {
            await _channel.CloseAsync(CancellationToken.None);
            await _channel.DisposeAsync();
        }

        logger.LogInformation("Email consumer stopped gracefully");
    }

    private async Task HandleMessageAsync(IChannel channel, RabbitMqOptions opts, BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        var retryCount = GetRetryCount(ea.BasicProperties);

        SendEmailMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SendEmailMessage>(ea.Body.Span);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize queued message; routing straight to dead-letter queue");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, stoppingToken);
            return;
        }

        if (message is null)
        {
            logger.LogError("Deserialized message was null; routing to dead-letter queue");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, stoppingToken);
            return;
        }

        using (LogContext.PushProperty("MessageId", message.MessageId))
        using (LogContext.PushProperty("RetryCount", retryCount))
        {
            try
            {
                await emailSender.SendAsync(message, stoppingToken);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                logger.LogInformation("Email to {To} sent successfully", message.To);
            }
            catch (SimulatedSendFailureException ex)
            {
                if (retryCount < opts.MaxRetryAttempts)
                {
                    var nextRetryCount = retryCount + 1;
                    logger.LogWarning(
                        ex,
                        "Simulated send failure for {To} (attempt {Attempt}/{MaxAttempts}); retrying with backoff",
                        message.To, nextRetryCount, opts.MaxRetryAttempts);

                    await Task.Delay(TimeSpan.FromMilliseconds(500 * nextRetryCount), stoppingToken);
                    await RepublishWithRetryCountAsync(channel, opts, ea, nextRetryCount, stoppingToken);
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                }
                else
                {
                    logger.LogError(
                        ex,
                        "Exceeded max retry attempts ({MaxAttempts}) for {To}; routing to dead-letter queue",
                        opts.MaxRetryAttempts, message.To);
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                }
            }
        }
    }

    private async Task RepublishWithRetryCountAsync(
        IChannel channel, RabbitMqOptions opts, BasicDeliverEventArgs ea, int retryCount, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            ContentType = ea.BasicProperties.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = ea.BasicProperties.MessageId,
            CorrelationId = ea.BasicProperties.CorrelationId,
            Headers = new Dictionary<string, object?> { [RetryCountHeader] = retryCount },
        };

        await channel.BasicPublishAsync(
            exchange: opts.ExchangeName,
            routingKey: opts.RoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: ea.Body,
            cancellationToken: cancellationToken);
    }

    private static int GetRetryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not { } headers || !headers.TryGetValue(RetryCountHeader, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            byte[] bytes => int.Parse(Encoding.UTF8.GetString(bytes)),
            _ => 0,
        };
    }
}
