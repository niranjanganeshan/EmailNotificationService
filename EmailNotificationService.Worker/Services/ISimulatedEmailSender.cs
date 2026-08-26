using EmailNotificationService.Contracts.Messages;

namespace EmailNotificationService.Worker.Services;

public interface ISimulatedEmailSender
{
    Task SendAsync(SendEmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class SimulatedSendFailureException(string message) : Exception(message);
