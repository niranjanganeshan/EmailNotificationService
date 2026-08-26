using RabbitMQ.Client;

namespace EmailNotificationService.Worker.Messaging;

public interface IRabbitMqConnectionProvider
{
    IConnection Connection { get; }
}

public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private IConnection? _connection;

    public IConnection Connection =>
        _connection ?? throw new InvalidOperationException("RabbitMQ connection has not been established yet.");

    internal IConnection? RawConnection => _connection;

    internal void SetConnection(IConnection connection) => _connection = connection;
}
