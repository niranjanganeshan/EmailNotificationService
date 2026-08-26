namespace EmailNotificationService.Contracts.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "appuser";
    public string Password { get; set; } = "apppassword123!";
    public string VirtualHost { get; set; } = "/notifications";

    public string ExchangeName { get; set; } = "notifications.topic";
    public string QueueName { get; set; } = "email.send.queue";
    public string RoutingKey { get; set; } = "email.send";

    public string DeadLetterExchangeName { get; set; } = "notifications.dlx";
    public string DeadLetterQueueName { get; set; } = "email.send.dlq";
    public string DeadLetterRoutingKey { get; set; } = "email.send.dead";

    public ushort PrefetchCount { get; set; } = 10;
    public int MaxRetryAttempts { get; set; } = 3;
}
