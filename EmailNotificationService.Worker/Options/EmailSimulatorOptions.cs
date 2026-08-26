namespace EmailNotificationService.Worker.Options;

public sealed class EmailSimulatorOptions
{
    public const string SectionName = "EmailSimulator";

    public int FailureRatePercent { get; set; } = 30;
}
