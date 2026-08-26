using EmailNotificationService.Contracts.Messages;

namespace EmailNotificationService.Services;

public interface IEmailPublisher
{
    Task PublishAsync(SendEmailMessage message, CancellationToken cancellationToken = default);
}
