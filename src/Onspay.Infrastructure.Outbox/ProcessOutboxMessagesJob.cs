using System.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Onspay.Cqrs.Data;
using Onspay.Cqrs.Messaging;
using Onspay.SharedKernel;
using Quartz;

namespace Onspay.Infrastructure.Outbox;

[DisallowConcurrentExecution]
public sealed class ProcessOutboxMessagesJob(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<OutboxOptions> options,
    IDateTimeProvider dateTimeProvider,
    ILogger<ProcessOutboxMessagesJob> logger)
    : IJob
{
    private readonly OutboxOptions _options = options.Value;

    private const string SelectSql = $"""
        SELECT
            id AS Id,
            message_type AS MessageType,
            payload AS Payload,
            routing_key AS RoutingKey,
            occurred_on_utc AS OccurredOnUtc,
            processed_on_utc AS ProcessedOnUtc,
            retry_count AS RetryCount,
            error AS Error,
            next_retry_utc AS NextRetryUtc
        FROM {OutboxMessageConfiguration.Schema}.{OutboxMessageConfiguration.Table}
        WHERE processed_on_utc IS NULL
          AND (next_retry_utc IS NULL OR next_retry_utc <= @UtcNow)
        ORDER BY occurred_on_utc
        LIMIT @BatchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string SuccessSql = $"""
        UPDATE {OutboxMessageConfiguration.Schema}.{OutboxMessageConfiguration.Table}
        SET processed_on_utc = @ProcessedOnUtc,
            error = NULL,
            next_retry_utc = NULL
        WHERE id = @Id
        """;

    private const string FailureSql = $"""
        UPDATE {OutboxMessageConfiguration.Schema}.{OutboxMessageConfiguration.Table}
        SET retry_count = @RetryCount,
            error = @Error,
            processed_on_utc = @ProcessedOnUtc,
            next_retry_utc = @NextRetryUtc
        WHERE id = @Id
        """;

    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogDebug("Processing outbox messages...");

        using var scope = serviceScopeFactory.CreateScope();
        var sqlConnectionFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
        var publisher = scope.ServiceProvider.GetRequiredService<IRabbitMqPublisher>();

        using var connection = sqlConnectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            var messages = (await connection.QueryAsync<OutboxMessageDto>(
                SelectSql,
                new { dateTimeProvider.UtcNow, _options.BatchSize },
                transaction)).ToList();

            if (messages.Count == 0)
            {
                logger.LogDebug("No outbox messages to process");
                transaction.Commit();
                return;
            }

            logger.LogInformation("Processing {Count} outbox messages", messages.Count);

            foreach (var message in messages)
                await ProcessMessageAsync(message, publisher, transaction, context.CancellationToken);

            transaction.Commit();
            logger.LogInformation("Finished processing outbox messages batch");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing outbox messages batch, rolling back transaction");
            transaction.Rollback();
            throw;
        }
    }

    private async Task ProcessMessageAsync(
        OutboxMessageDto message,
        IRabbitMqPublisher publisher,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Publishing message {MessageId} of type {MessageType}",
                message.Id, message.MessageType);

            await publisher.PublishAsync(
                message.Payload ?? string.Empty,
                message.MessageType,
                message.Id.ToString(),
                routingKey: message.RoutingKey,
                cancellationToken: cancellationToken);

            await MarkSuccessAsync(transaction, message.Id);
            logger.LogInformation("Successfully published message {MessageId} of type {MessageType}",
                message.Id, message.MessageType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception while publishing message {MessageId} of type {MessageType}",
                message.Id, message.MessageType);
            await MarkFailedAsync(transaction, message.Id, ex.Message, message.RetryCount);
        }
    }

    private async Task MarkSuccessAsync(IDbTransaction transaction, Guid id) =>
        await transaction.Connection!.ExecuteAsync(
            SuccessSql,
            new { Id = id, ProcessedOnUtc = dateTimeProvider.UtcNow },
            transaction);

    private async Task MarkFailedAsync(
        IDbTransaction transaction,
        Guid id,
        string error,
        int currentRetryCount)
    {
        var newRetryCount = currentRetryCount + 1;
        DateTime? processedOnUtc = null;
        DateTime? nextRetryUtc = null;

        if (newRetryCount >= _options.MaxRetryCount)
        {
            processedOnUtc = dateTimeProvider.UtcNow;
        }
        else
        {
            var delay = TimeSpan.FromSeconds(_options.RetryDelayInSeconds * Math.Pow(2, newRetryCount - 1));
            nextRetryUtc = dateTimeProvider.UtcNow.Add(delay);
        }

        await transaction.Connection!.ExecuteAsync(
            FailureSql,
            new { Id = id, RetryCount = newRetryCount, Error = error, ProcessedOnUtc = processedOnUtc, NextRetryUtc = nextRetryUtc },
            transaction);
    }
}
