namespace EmailNotificationService.Contracts.Messages;

public sealed record SendEmailMessage(
    Guid MessageId,
    string To,
    string Subject,
    string Body,
    DateTimeOffset RequestedAtUtc);
