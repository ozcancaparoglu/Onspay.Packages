# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build entire solution
dotnet build

# Build specific project
dotnet build src/Onspay.SharedKernel/Onspay.SharedKernel.csproj

# Create NuGet packages
dotnet pack
```

## Architecture Overview

This is a shared NuGet package library for DDD/CQRS-based .NET applications. Target framework is .NET 10 with nullable reference types and warnings-as-errors enabled.

### Package Dependency Graph

```
Onspay.SharedKernel (base primitives)
    ↑
Onspay.Cqrs (CQRS interfaces)
    ↑
├── Onspay.Cqrs.Behaviors (decorators, DI registration)
├── Onspay.Infrastructure.DomainEvents (EF Core dispatch)
├── Onspay.Infrastructure.Inbox (Quartz job processing)
└── Onspay.Infrastructure.Outbox (Quartz job publishing)

Onspay.AspNetCore (depends on SharedKernel only)
```

### SharedKernel Primitives

- **Result/Result\<T\>**: Railway-oriented error handling. All handlers return `Result` or `Result<T>`, never throw for business errors
- **Error**: Immutable error record with Code, Description, and ErrorType. Factory methods: `Error.Failure()`, `Error.NotFound()`, `Error.Conflict()`
- **Entity**: Base class with domain event collection. Use `entity.Raise(domainEvent)` to queue events

### CQRS Pattern

Commands and queries are marker interfaces. Handlers return Result types:

```csharp
public record CreateOrderCommand(string CustomerId) : ICommand<Guid>;

public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, Guid>
{
    public Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken ct) { ... }
}
```

Register handlers with `services.AddCqrs(typeof(SomeHandler).Assembly)`. This auto-registers:
- `ICommandHandler<>` and `ICommandHandler<,>` implementations
- `IQueryHandler<,>` implementations
- `IDomainEventHandler<>` implementations
- `IInboxMessageHandler<>` implementations
- FluentValidation validators
- Applies ValidationDecorator then LoggingDecorator to handlers via Scrutor

### Domain Events

Inherit DbContext from `DomainEventDbContextBase` to auto-dispatch domain events after `SaveChangesAsync`. Events are collected from all tracked entities, cleared, then dispatched sequentially.

### Inbox Pattern

The inbox provides idempotent message processing with retry logic:
- Messages stored in PostgreSQL table `message.inbox_messages`
- `ProcessInboxMessagesJob` (Quartz) polls for unprocessed messages with `FOR UPDATE SKIP LOCKED`
- Exponential backoff retry with configurable max attempts
- Implement `IInboxMessageHandler<T>` and register via `AddCqrs()`

Configuration via `appsettings.json`:
```json
{
  "Inbox": {
    "IntervalInSeconds": 10,
    "BatchSize": 100,
    "MaxRetryCount": 5,
    "RetryDelayInSeconds": 30
  }
}
```

### Outbox Pattern

The outbox provides reliable message publishing with retry logic:
- Messages stored in PostgreSQL table `message.outbox_messages`
- `ProcessOutboxMessagesJob` (Quartz) polls for unprocessed messages with `FOR UPDATE SKIP LOCKED`
- Publishes via `IRabbitMqPublisher` with exponential backoff retry
- Register with `services.AddOutboxProcessing(configuration)`

Writing to the outbox (in same transaction as domain changes):
```csharp
var message = OutboxMessage.Create("OrderCreated", jsonPayload, routingKey: "orders.created");
dbContext.OutboxMessages.Add(message);
await dbContext.SaveChangesAsync();
```

Configuration via `appsettings.json`:
```json
{
  "Outbox": {
    "IntervalInSeconds": 10,
    "BatchSize": 100,
    "MaxRetryCount": 5,
    "RetryDelayInSeconds": 30
  }
}
```

### ASP.NET Core Integration

- **IEndpoint**: Minimal API endpoint pattern. Implement and register with `app.MapEndpoints()`
- **CustomResults.Problem()**: Converts `Result` failures to RFC 7807 ProblemDetails responses
- **ResultExtensions.Match()**: Functional pattern matching for Result types in endpoints
