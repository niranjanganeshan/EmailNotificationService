using EmailNotificationService.Contracts.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EmailNotificationService.Messaging;

/// <summary>
/// Establishes the shared RabbitMQ connection and declares the exchange/queue/DLQ topology
/// on startup. Runs with a small connection retry loop since the broker (typically started via
/// docker compose) may not be ready the instant this process starts.
/// </summary>
public sealed class RabbitMqTopologyInitializer(
    RabbitMqConnectionProvider connectionProvider,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqTopologyInitializer> logger) : IHostedService
{
    private const int MaxConnectionAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = RabbitMqConnectionFactory.Create(options.Value);
        var connection = await ConnectWithRetryAsync(factory, cancellationToken);
        connectionProvider.SetConnection(connection);

        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, options.Value, cancellationToken);

        logger.LogInformation(
            "RabbitMQ topology ready: exchange {Exchange}, queue {Queue}, dead-letter queue {DeadLetterQueue}",
            options.Value.ExchangeName, options.Value.QueueName, options.Value.DeadLetterQueueName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (connectionProvider.RawConnection is { } connection)
        {
            await connection.CloseAsync(CancellationToken.None);
            await connection.DisposeAsync();
        }
    }

    private async Task<IConnection> ConnectWithRetryAsync(ConnectionFactory factory, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxConnectionAttempts; attempt++)
        {
            try
            {
                return await factory.CreateConnectionAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxConnectionAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Failed to connect to RabbitMQ (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}...",
                    attempt, MaxConnectionAttempts, RetryDelay);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        return await factory.CreateConnectionAsync(cancellationToken);
    }
}
