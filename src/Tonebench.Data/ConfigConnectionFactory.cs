using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// Opens connections to <c>tonebench-config.db</c>, with exactly the same pragmas
/// <see cref="SqliteConnectionFactory"/> applies to the measurement database.
///
/// <para>Its own type rather than a second registration of that class, because the two databases
/// are not interchangeable and a repository must not be able to receive the wrong one. Keyed DI
/// would express it too, at the cost of the mistake being a runtime surprise instead of a compile
/// error — a <c>ConfigRepository</c> handed the measurement database would happily create an
/// <c>app_setting</c> table inside it.</para>
///
/// <para>The pragma work itself is shared, not copied: see
/// <see cref="SqliteConnectionFactory.OpenAtAsync"/>. Two divergent sets of pragmas across two
/// databases is precisely the kind of drift that produces "SQLite is slow" on one of them only.</para>
/// </summary>
public sealed class ConfigConnectionFactory(TonebenchConfigDatabaseOptions options)
{
    public Task<SqliteConnection> OpenAsync(CancellationToken ct) =>
        SqliteConnectionFactory.OpenAtAsync(options.Path, create: true, ct);
}
