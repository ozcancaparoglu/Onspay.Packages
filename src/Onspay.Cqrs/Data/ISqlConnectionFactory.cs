using System.Data;

namespace Onspay.Cqrs.Data;

public interface ISqlConnectionFactory
{
    IDbConnection CreateConnection();
}
