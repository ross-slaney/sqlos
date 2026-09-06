using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SqlOS.Database;

internal static class SqlOSDatabaseErrors
{
    public static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => IsUniqueConstraintViolation((Exception)exception);

    public static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is SqlException { Number: 1205 })
            {
                return true;
            }

            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }
}
