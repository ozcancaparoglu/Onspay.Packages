namespace Onspay.SharedKernel;

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
