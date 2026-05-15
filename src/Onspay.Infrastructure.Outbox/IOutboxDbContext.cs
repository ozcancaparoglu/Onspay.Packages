using Microsoft.EntityFrameworkCore;

namespace Onspay.Infrastructure.Outbox;

/// <summary>
/// Minimal EF Core interface for writing to the outbox_messages table.
/// Implement this alongside your service's IApplicationDbContext.
/// </summary>
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
