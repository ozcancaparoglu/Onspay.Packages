namespace Onspay.Infrastructure.Inbox;

internal sealed record InboxMessageDto(
    Guid Id,
    string MessageId,
    string MessageType,
    string? Payload,
    DateTime OccurredOnUtc,
    DateTime? ProcessedOnUtc,
    int RetryCount,
    string? Error,
    DateTime? NextRetryUtc);
