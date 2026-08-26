using System.ComponentModel.DataAnnotations;

namespace EmailNotificationService.Models;

public sealed class SendEmailRequest
{
    [Required, EmailAddress]
    public string To { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required, MaxLength(10000)]
    public string Body { get; set; } = string.Empty;
}
