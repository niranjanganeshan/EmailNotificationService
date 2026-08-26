using EmailNotificationService.Contracts.Messaging;
using EmailNotificationService.Worker.Messaging;
using EmailNotificationService.Worker.Options;
using EmailNotificationService.Worker.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "EmailWorker")
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}")
        .WriteTo.File("logs/worker-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
        .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

    builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
    builder.Services.Configure<EmailSimulatorOptions>(builder.Configuration.GetSection(EmailSimulatorOptions.SectionName));

    builder.Services.AddSingleton<RabbitMqConnectionProvider>();
    builder.Services.AddSingleton<IRabbitMqConnectionProvider>(sp => sp.GetRequiredService<RabbitMqConnectionProvider>());
    builder.Services.AddHostedService<RabbitMqTopologyInitializer>();

    builder.Services.AddSingleton<ISimulatedEmailSender, SimulatedEmailSender>();
    builder.Services.AddHostedService<EmailConsumerService>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "EmailNotificationService.Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
