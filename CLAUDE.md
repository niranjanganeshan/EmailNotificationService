# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

A portfolio piece demonstrating RabbitMQ messaging-queue skills: an HTTP API publishes "send email" requests onto a RabbitMQ queue, and a separate Worker process consumes them and simulates sending. The two are independently deployable processes, deliberately decoupled by the broker rather than an in-process background task — that decoupling is the point of the demo. Consumer failures are simulated (no real SMTP), with retry and dead-letter-queue behavior demonstrated end-to-end.

## Commands

Run all commands from the repository root (where `EmailNotificationService.slnx` lives).

- Start the broker and log viewer: `docker compose up -d` (RabbitMQ with the management plugin, UI at http://localhost:15672, credentials `appuser` / `apppassword123!`, vhost `/notifications`; and Seq, UI at http://localhost:8341)
- Build the whole solution: `dotnet build EmailNotificationService.slnx`
- Run the Api (producer): `dotnet run --project EmailNotificationService`
- Run the Worker (consumer), in a separate terminal: `dotnet run --project EmailNotificationService.Worker`
- Restore packages: `dotnet restore`

There is no test project. Manual API requests can be made using `EmailNotificationService/EmailNotificationService.http` (POST `/api/emails`).

Both the Api and Worker must be running, and RabbitMQ must be up, for a message to flow end-to-end. Either process can be started before the other — each declares the RabbitMQ topology idempotently on startup.

## Architecture

Three projects, referenced from `EmailNotificationService.slnx`:

- **`EmailNotificationService.Contracts`** (class library) — shared between the other two: the `SendEmailMessage` record (the wire contract published to the queue), `RabbitMqOptions`, `RabbitMqConnectionFactory` (builds a `ConnectionFactory` with automatic recovery enabled), and `RabbitMqTopology.DeclareAsync` (idempotently declares the exchange/queues/bindings — see below).
- **`EmailNotificationService`** (project file `EmailAPI.csproj`, producer) — ASP.NET Core Web API. `Controllers/EmailsController.cs` exposes `POST /api/emails`, validates the request (`Models/SendEmailRequest.cs`), and publishes via `Services/EmailPublisher.cs`. `Messaging/RabbitMqTopologyInitializer.cs` is an `IHostedService` that opens the shared RabbitMQ connection (with a startup retry loop, since the broker may not be up yet) and declares the topology before the app starts serving.
- **`EmailNotificationService.Worker`** (consumer) — a .NET Generic Host `BackgroundService`. `Services/EmailConsumerService.cs` consumes from the queue with manual ack/nack and an explicit prefetch count, and `Services/SimulatedEmailSender.cs` simulates sending with a configurable random failure rate (`EmailSimulator:FailureRatePercent` in `appsettings.json`). Has its own copy of `Messaging/RabbitMqTopologyInitializer.cs` (same pattern as the Api's).

### RabbitMQ topology

```
Exchange:  notifications.topic  (topic, durable)
Queue:     email.send.queue     (durable; x-dead-letter-exchange=notifications.dlx,
                                          x-dead-letter-routing-key=email.send.dead)
Binding:   email.send.queue <-- notifications.topic  routing key "email.send"

DLX:       notifications.dlx    (direct, durable)
DLQ:       email.send.dlq       (durable)
Binding:   email.send.dlq <-- notifications.dlx  routing key "email.send.dead"
```

A topic exchange is used (rather than direct) so future message types could bind onto the same exchange without a topology redesign, even though only one routing key exists today.

**Retry/DLQ behavior**: on a simulated send failure, `EmailConsumerService` reads the `x-retry-count` header off the delivery (default 0), and if under `RabbitMq:MaxRetryAttempts` (default 3), republishes the same payload with the header incremented plus a short backoff delay, then acks the original delivery. Once attempts are exhausted, it `nack`s with `requeue:false` — RabbitMQ dead-letters the message into `email.send.dlq` automatically via the queue's `x-dead-letter-exchange` argument; no code manually publishes to the DLQ.

**Reliability details implemented**: publisher confirms (`EmailPublisher` opens the channel with `PublisherConfirmationsEnabled`, publishes, and awaits the broker's ack/nack event via a `TaskCompletionSource` before returning — RabbitMQ.Client 7.x replaced the old synchronous `ConfirmSelect`/`WaitForConfirms` API with this ack/nack-event model), manual ack/nack with `BasicQosAsync(prefetchCount: 10)` on the consumer, `AutomaticRecoveryEnabled`/`TopologyRecoveryEnabled` on the connection factory, idempotent topology declaration in both processes, and graceful shutdown (`EmailConsumerService.StopAsync` closes its channel; `RabbitMqTopologyInitializer.StopAsync` closes the shared connection).

### Structured logging (Serilog)

Both processes use Serilog (bootstrap logger in `Program.cs`, then `ReadFrom.Configuration`), writing to three sinks: console, a rolling daily file (`logs/api-*.log` / `logs/worker-*.log`, gitignored, 14-day retention), and Seq (`Seq:ServerUrl` in each process's `appsettings.json`, defaulting to `http://localhost:5341`, the ingestion endpoint of the `seq` service in `docker-compose.yml`). Each process also enriches every event with a constant `Application` property (`"Api"` / `"Worker"`, set via `.Enrich.WithProperty(...)` in `Program.cs`), so Seq queries can isolate one process with `Application = 'Api'` or `Application = 'Worker'` — cleaner than filtering on `SourceContext` namespaces. The `SendEmailMessage.MessageId` is pushed onto Serilog's `LogContext` at publish time and again at consume time (read back from the AMQP message's `CorrelationId` property), so every log line for a given email — across both processes — carries the same `MessageId` property. Seq's UI (http://localhost:8341) is the easiest way to browse and filter this: search `MessageId = '<guid>'` there to trace one email end-to-end across the Api and Worker without grepping log files by hand. The file logs remain as a secondary, always-available record; Seq only shows events written after it was wired in — it doesn't ingest history from the existing `.log` files.

### Configuration

`RabbitMqOptions` is bound from the `RabbitMq` section in each process's own `appsettings.json` (intentionally duplicated — they're independently deployable processes). Defaults match `docker-compose.yml`'s credentials, so a fresh clone works with zero config changes.

## Out of scope (not built, natural next steps)

Automated tests, CI, real SMTP/email-provider integration (currently simulated), and containerizing the Api/Worker themselves (only the RabbitMQ broker is in `docker-compose.yml` today).
