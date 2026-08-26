# EmailNotificationService

A portfolio project demonstrating **asynchronous, message-driven architecture with RabbitMQ** in .NET: an HTTP API accepts "send email" requests and publishes them onto a RabbitMQ queue; a completely separate Worker process consumes that queue and simulates sending the emails. The two processes are deliberately decoupled by the broker — rather than an in-process background task — and can be built, deployed, scaled, and restarted independently. Consumer failures are simulated (no real SMTP), with retry and dead-letter-queue behavior demonstrated end-to-end, and every log line is traceable across both processes via a shared correlation ID and a Seq log-search UI.

## Table of contents

- [Scope](#scope)
- [Architecture](#architecture)
- [Tech stack](#tech-stack)
- [Ports](#ports)
- [Getting started](#getting-started)
- [API reference](#api-reference)
- [RabbitMQ topology](#rabbitmq-topology)
- [Reliability details](#reliability-details)
- [Structured logging & Seq](#structured-logging--seq)
- [Project structure](#project-structure)
- [Configuration](#configuration)
- [Out of scope](#out-of-scope)

## Scope

**In scope — built and working:**
- An HTTP API (`EmailNotificationService`) that validates and accepts "send email" requests, publishing them onto RabbitMQ with **publisher confirms** (the API doesn't respond `202` until the broker has actually acknowledged the message).
- A standalone Worker (`EmailNotificationService.Worker`) that consumes those messages with manual ack/nack and a bounded prefetch count.
- A **simulated** email sender with a configurable random failure rate, standing in for a real SMTP/provider integration.
- **Retry with backoff**: failed sends are retried automatically, up to a configurable limit.
- **Dead-letter queue (DLQ)**: messages that exhaust their retries are routed to a DLQ by RabbitMQ itself — no application code publishes to it directly.
- **Structured logging** (Serilog) to console, rolling files, and [Seq](https://datalust.co/seq), correlated end-to-end by a per-message GUID across both processes.
- A `docker-compose.yml` that stands up RabbitMQ and Seq for local development with zero configuration.

**Explicitly out of scope** — see [Out of scope](#out-of-scope) below.

## Architecture

Three .NET projects, referenced from `EmailNotificationService.slnx`:

```
                    POST /api/emails
                          │
                          ▼
              ┌───────────────────────┐
              │  EmailNotificationService  (Api)  │
              │  ASP.NET Core Web API             │
              │  - validates the request          │
              │  - publishes with publisher        │
              │    confirms                        │
              └───────────────┬────────────────────┘
                               │ publish (routing key: email.send)
                               ▼
                 ┌─────────────────────────────┐
                 │   notifications.topic        │  (topic exchange)
                 └───────────────┬───────────────┘
                                  ▼
                     ┌─────────────────────┐
                     │  email.send.queue    │
                     └──────────┬───────────┘
                                 │ consume (manual ack/nack, prefetch 10)
                                 ▼
              ┌────────────────────────────────────┐
              │  EmailNotificationService.Worker    │
              │  .NET Generic Host BackgroundService│
              │  - SimulatedEmailSender             │
              │    (configurable random failures)   │
              └───────────────┬──────────────────────┘
                               │
                 success ──────┤────── failure, retries remain
                   (ack)       │        → republish with
                               │          x-retry-count++,
                               │          backoff delay, ack original
                               │
                               │  failure, retries exhausted
                               │  → nack (requeue:false)
                               ▼
                   ┌─────────────────────────┐
                   │ notifications.dlx        │  (direct exchange)
                   └────────────┬──────────────┘
                                 ▼
                     ┌─────────────────────┐
                     │  email.send.dlq       │
                     └─────────────────────┘

  Both processes also write structured logs (console + rolling file + Seq),
  tagged with the message's MessageId, so one email's whole lifecycle can
  be traced across both processes.
```

- **`EmailNotificationService.Contracts`** (class library) — shared between the Api and Worker: the `SendEmailMessage` record (the wire contract published to the queue), `RabbitMqOptions`, `RabbitMqConnectionFactory`, and `RabbitMqTopology.DeclareAsync`, which idempotently declares the exchange/queue/binding topology so either process can be the first to start.
- **`EmailNotificationService`** (project file `EmailAPI.csproj`, the **producer**) — ASP.NET Core Web API. `Controllers/EmailsController.cs` exposes `POST /api/emails`, and `Services/EmailPublisher.cs` publishes to RabbitMQ using publisher confirms.
- **`EmailNotificationService.Worker`** (the **consumer**) — a .NET Generic Host `BackgroundService`. `Services/EmailConsumerService.cs` handles consuming, retrying, and dead-lettering; `Services/SimulatedEmailSender.cs` simulates the actual send.

Either process can be started before the other — both declare the same RabbitMQ topology on startup, idempotently.

## Tech stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 |
| API | ASP.NET Core Web API (minimal hosting model) |
| Worker | .NET Generic Host (`BackgroundService`) |
| Messaging broker | RabbitMQ 4 (management plugin), via `RabbitMQ.Client` 7.x |
| Logging | Serilog → Console, rolling file, [Seq](https://datalust.co/seq) |
| Local infrastructure | Docker Compose (RabbitMQ + Seq containers) |

## Ports

Everything runs on `localhost`. No ports need to be changed for a fresh clone to work.

| Service | Port(s) | Purpose |
|---|---|---|
| Api (HTTP) | `5242` | `POST /api/emails` |
| Api (HTTPS, `https` launch profile) | `7277` | Same API over TLS |
| RabbitMQ (AMQP) | `5672` | Broker protocol — used by the Api and Worker |
| RabbitMQ management UI | `15672` | Browse exchanges/queues/messages — [http://localhost:15672](http://localhost:15672) (`appuser` / `apppassword123!`) |
| Seq ingestion | `5341` | Where Serilog ships log events to |
| Seq web UI | `8341` | Browse/search/filter logs — [http://localhost:8341](http://localhost:8341) (no login required, local dev only) |

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Docker Desktop](https://www.docker.com/products/docker-desktop/).

Run all commands from the repository root (where `EmailNotificationService.slnx` lives).

```bash
# 1. Start RabbitMQ and Seq
docker compose up -d

# 2. Build the whole solution
dotnet build EmailNotificationService.slnx

# 3. Run the Api (producer) — in one terminal
dotnet run --project EmailNotificationService

# 4. Run the Worker (consumer) — in a separate terminal
dotnet run --project EmailNotificationService.Worker
```

Either process can be started first; each declares the RabbitMQ topology idempotently on startup, and both will keep retrying their connection until RabbitMQ is reachable.

Send a test request with the included `.http` file (`EmailNotificationService/EmailNotificationService.http`, works with the VS Code REST Client extension or Visual Studio's built-in HTTP support), or with `curl`:

```bash
curl -X POST http://localhost:5242/api/emails \
  -H "Content-Type: application/json" \
  -d '{
    "to": "test@example.com",
    "subject": "Hello from RabbitMQ",
    "body": "This email was queued via RabbitMQ and will be consumed by the Worker."
  }'
```

Watch it flow through:
- The **RabbitMQ management UI** ([http://localhost:15672](http://localhost:15672)) to see the message land on `email.send.queue`, and to watch `email.send.dlq` fill up if you let enough simulated failures exhaust the retry limit.
- The **Seq UI** ([http://localhost:8341](http://localhost:8341)) to see structured logs from both processes, correlated by `MessageId`.

There is no test project. Restore packages with `dotnet restore` if needed.

## API reference

### `POST /api/emails`

Accepts a send-email request, publishes it to RabbitMQ, and returns immediately — the actual "sending" happens asynchronously in the Worker.

**Request body:**

```json
{
  "to": "user@example.com",
  "subject": "string, max 200 chars",
  "body": "string, max 10000 chars"
}
```

| Field | Type | Validation |
|---|---|---|
| `to` | string | required, must be a valid email address |
| `subject` | string | required, max 200 characters |
| `body` | string | required, max 10000 characters |

**Responses:**

| Status | Meaning |
|---|---|
| `202 Accepted` | Message published and confirmed by the RabbitMQ broker. Body: `{ "messageId": "<guid>" }` |
| `400 Bad Request` | Request failed validation |
| `500 Internal Server Error` | The broker nacked the publish, or the publisher-confirm wait timed out (10s) |

The returned `messageId` is the same GUID that tags every log line for this email in both processes' logs — see [Structured logging & Seq](#structured-logging--seq).

## RabbitMQ topology

Declared idempotently by both processes on startup (`RabbitMqTopology.DeclareAsync`):

```
Exchange:  notifications.topic  (topic, durable)
Queue:     email.send.queue     (durable; x-dead-letter-exchange=notifications.dlx,
                                          x-dead-letter-routing-key=email.send.dead)
Binding:   email.send.queue <-- notifications.topic  routing key "email.send"

DLX:       notifications.dlx    (direct, durable)
DLQ:       email.send.dlq       (durable)
Binding:   email.send.dlq <-- notifications.dlx  routing key "email.send.dead"
```

A **topic** exchange is used (rather than direct) so future message types could bind onto the same exchange without a topology redesign, even though only one routing key exists today.

**Retry / DLQ behavior:** on a simulated send failure, `EmailConsumerService` reads the `x-retry-count` header off the delivery (default `0`). If under `RabbitMq:MaxRetryAttempts` (default `3`), it republishes the same payload with the header incremented, waits a short backoff (`500ms × attempt number`), then acks the original delivery. Once attempts are exhausted, it `nack`s with `requeue:false` — RabbitMQ automatically dead-letters the message into `email.send.dlq` via the queue's `x-dead-letter-exchange` argument; no application code manually publishes to the DLQ.

## Reliability details

- **Publisher confirms** — `EmailPublisher` opens its channel with confirmations enabled, publishes, and awaits the broker's ack/nack event via a `TaskCompletionSource` before the API responds. (RabbitMQ.Client 7.x replaced the older synchronous `ConfirmSelect`/`WaitForConfirms` API with this ack/nack-event model.)
- **Manual ack/nack** with an explicit `BasicQos(prefetchCount: 10)` on the consumer, so one Worker instance never has more than 10 unacknowledged messages in flight.
- **Automatic connection recovery** (`AutomaticRecoveryEnabled` / `TopologyRecoveryEnabled`) on the shared RabbitMQ connection in both processes.
- **Idempotent topology declaration** in both processes — order of startup doesn't matter.
- **Graceful shutdown** — `EmailConsumerService.StopAsync` closes its channel; the shared connection is closed by the hosted service that owns it.

## Structured logging & Seq

Both processes log via [Serilog](https://serilog.net/) to three sinks:

1. **Console** — human-readable, for local dev.
2. **Rolling daily file** — `logs/api-*.log` / `logs/worker-*.log` (gitignored, 14-day retention). Always available even if Seq isn't running.
3. **[Seq](https://datalust.co/seq)** — a self-hosted structured-log server with a searchable web UI, at [http://localhost:8341](http://localhost:8341).

Two properties make cross-process tracing possible:

- **`MessageId`** — the same GUID returned by `POST /api/emails`, pushed onto Serilog's `LogContext` at publish time and again at consume time (read back from the AMQP message's `CorrelationId` property). Every log line for a given email, in both processes, carries this property.
- **`Application`** — a constant `"EmailApi"` or `"EmailWorker"` tag set on every event, so a single process's logs can be isolated cleanly.

In the Seq UI, search:

```
MessageId = '<guid-from-the-202-response>'
```

to see one email's whole lifecycle — accepted by the Api, published, consumed, retried, and eventually sent (or dead-lettered) — across both processes in one timeline. Or filter to one process with:

```
Application = 'EmailApi'
Application = 'EmailWorker'
```

Seq only shows events from the moment it was wired in; it does not retroactively ingest the existing plain-text `.log` files.

## Project structure

```
EmailNotificationService.slnx
docker-compose.yml

EmailNotificationService.Contracts/       Shared library
├── Messages/SendEmailMessage.cs          Wire contract published to the queue
└── Messaging/
    ├── RabbitMqOptions.cs                Bound from "RabbitMq" config section
    ├── RabbitMqConnectionFactory.cs       Builds a ConnectionFactory with auto-recovery
    └── RabbitMqTopology.cs               Idempotent exchange/queue/binding declaration

EmailNotificationService/                 Api (producer) — EmailAPI.csproj
├── Controllers/EmailsController.cs       POST /api/emails
├── Models/SendEmailRequest.cs            Request DTO + validation
├── Services/EmailPublisher.cs            Publishes with publisher confirms
├── Messaging/RabbitMqTopologyInitializer.cs   IHostedService: opens connection, declares topology
└── Program.cs

EmailNotificationService.Worker/          Worker (consumer)
├── Services/EmailConsumerService.cs      Consume loop, retry/backoff, dead-lettering
├── Services/SimulatedEmailSender.cs      Simulated send with configurable failure rate
├── Options/EmailSimulatorOptions.cs      Bound from "EmailSimulator" config section
├── Messaging/RabbitMqTopologyInitializer.cs   Same pattern as the Api's
└── Program.cs
```

## Configuration

Each process has its own `appsettings.json` — intentionally duplicated, since they're independently deployable processes.

**`RabbitMq` section** (both processes):

| Key | Default | Description |
|---|---|---|
| `HostName` | `localhost` | RabbitMQ host |
| `Port` | `5672` | AMQP port |
| `UserName` / `Password` | `appuser` / `apppassword123!` | Matches `docker-compose.yml` |
| `VirtualHost` | `/notifications` | RabbitMQ vhost |
| `ExchangeName` | `notifications.topic` | Main topic exchange |
| `QueueName` | `email.send.queue` | Main queue |
| `RoutingKey` | `email.send` | Binding key |
| `DeadLetterExchangeName` | `notifications.dlx` | DLX |
| `DeadLetterQueueName` | `email.send.dlq` | DLQ |
| `DeadLetterRoutingKey` | `email.send.dead` | DLX → DLQ binding key |
| `PrefetchCount` | `10` | Consumer QoS prefetch |
| `MaxRetryAttempts` | `3` | Retries before dead-lettering |

**`EmailSimulator` section** (Worker only):

| Key | Default | Description |
|---|---|---|
| `FailureRatePercent` | `30` | Chance (0–100) that a simulated send fails |

**`Seq` section** (both processes):

| Key | Default | Description |
|---|---|---|
| `ServerUrl` | `http://localhost:5341` | Seq ingestion endpoint |

Defaults match `docker-compose.yml`, so a fresh clone works with zero config changes.

## Out of scope

Not built, and not planned as part of this demo — natural next steps for anyone extending it:

- Automated tests and CI
- Real SMTP / email-provider integration (currently fully simulated)
- Containerizing the Api and Worker themselves (only the RabbitMQ and Seq infrastructure is in `docker-compose.yml` today)
- Authentication/authorization on the API
