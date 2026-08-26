using EmailNotificationService.Contracts.Messages;
using EmailNotificationService.Models;
using EmailNotificationService.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmailNotificationService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EmailsController(IEmailPublisher emailPublisher, ILogger<EmailsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] SendEmailRequest request, CancellationToken cancellationToken)
    {
        var message = new SendEmailMessage(
            Guid.NewGuid(),
            request.To,
            request.Subject,
            request.Body,
            DateTimeOffset.UtcNow);

        await emailPublisher.PublishAsync(message, cancellationToken);

        logger.LogInformation("Accepted email request {MessageId} for {To}", message.MessageId, message.To);

        return Accepted(new { messageId = message.MessageId });
    }
}
