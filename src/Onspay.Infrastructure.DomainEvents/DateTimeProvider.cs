using Onspay.SharedKernel;

namespace Onspay.Infrastructure.DomainEvents;

public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
