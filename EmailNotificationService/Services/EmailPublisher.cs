using System.Text.Json;
using EmailNotificationService.Contracts.Messages;
using EmailNotificationService.Contracts.Messaging;
using EmailNotificationService.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Serilog.Context;

namespace EmailNotificationService.Services;

public sealed class EmailPublisher(
    IRabbitMqConnectionProvider connectionProvider,
    IOptions<RabbitMqOptions> options,
    ILogger<EmailPublisher> logger) : IEmailPublisher
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    public async Task PublishAsync(SendEmailMessage message, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        using (LogContext.PushProperty("MessageId", message.MessageId))
        {
            // Publisher confirms: ask the broker to explicitly ack/nack each message rather than
            // firing-and-forgetting, so we can tell the caller whether the message actually made it
            // onto the broker before returning 202.
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null,
                consumerDispatchConcurrency: null);

            await using var channel = await connectionProvider.Connection.CreateChannelAsync(channelOptions, cancellationToken);

            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.BasicAcksAsync += (_, _) =>
            {
                confirmation.TrySetResult(true);
                return Task.CompletedTask;
            };
            channel.BasicNacksAsync += (_, _) =>
            {
                confirmation.TrySetResult(false);
                return Task.CompletedTask;
            };

            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var properties = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.MessageId.ToString(),
                CorrelationId = message.MessageId.ToString(),
            };

            await channel.BasicPublishAsync(
                exchange: opts.ExchangeName,
                routingKey: opts.RoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);

            bool confirmed;
            try
            {
                confirmed = await confirmation.Task.WaitAsync(ConfirmTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogError("Timed out waiting for RabbitMQ publisher confirm for message to {To}", message.To);
                throw new InvalidOperationException($"Timed out waiting for publisher confirm for message {message.MessageId}.");
            }

            if (!confirmed)
            {
                logger.LogError("RabbitMQ broker nacked publish of email message to {To}", message.To);
                throw new InvalidOperationException($"RabbitMQ broker nacked publish of message {message.MessageId}.");
            }

            logger.LogInformation("Published email message for {To} with subject {Subject}", message.To, message.Subject);
        }
    }
}
