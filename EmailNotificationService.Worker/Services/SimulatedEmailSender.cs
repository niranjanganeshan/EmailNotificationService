using EmailNotificationService.Contracts.Messages;
using EmailNotificationService.Worker.Options;
using Microsoft.Extensions.Options;

namespace EmailNotificationService.Worker.Services;

/// <summary>
/// Stands in for a real SMTP/email-provider integration. Simulates network latency and a
/// configurable failure rate so the queue's retry and dead-letter behavior can be exercised
/// without needing real email credentials.
/// </summary>
public sealed class SimulatedEmailSender(
    IOptions<EmailSimulatorOptions> options,
    ILogger<SimulatedEmailSender> logger) : ISimulatedEmailSender
{
    public async Task SendAsync(SendEmailMessage message, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Random.Shared.Next(100, 500), cancellationToken);

        if (Random.Shared.Next(0, 100) < options.Value.FailureRatePercent)
        {
            throw new SimulatedSendFailureException($"Simulated failure sending email to {message.To}.");
        }

        logger.LogInformation("Simulated send of email to {To} with subject {Subject}", message.To, message.Subject);
    }
}
