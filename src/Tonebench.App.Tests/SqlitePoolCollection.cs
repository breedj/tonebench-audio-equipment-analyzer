namespace Tonebench.App.Tests;

/// <summary>
/// Classes that cannot run beside anything else, because <see cref="
/// Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools"/> is <b>process-wide</b> and about fifteen
/// classes in this assembly call it — directly in their cleanup, or through
/// <c>ProjectSessionService.CloseAsync</c> and <c>DatabaseLifecycleHostedService.StopAsync</c>,
/// which call it by design.
///
/// <para><b>The damage is always the same shape.</b> SQLite checkpoints and <i>removes</i> the
/// <c>-wal</c> and <c>-shm</c> when the last connection to a file closes, so a parallel class
/// clearing the pool closes this one's connection and takes its WAL with it. Any assertion about a
/// WAL existing, or about a pooled connection still being open, then fails — intermittently, more
/// often on a busy machine, and never when the class is run on its own. Two separate faults have
/// now presented exactly that way, three weeks apart.</para>
///
/// <para><b>Serialising the whole assembly is not the alternative.</b> It was measured at 9 m 05 s
/// against 1 m 55 s at eight threads (see <c>AssemblyInfo.cs</c>), because these tests spend their
/// time waiting on a real-time simulated device rather than computing. So the victims go in here
/// and the rest of the assembly keeps its parallelism; a class only needs to join if it
/// <i>asserts</i> on pool or WAL state, not merely because it clears pools itself.</para>
///
/// <para>A collection with <c>DisableParallelization</c> runs alone rather than merely internally
/// serially, which is the property this needs — the perpetrators are everywhere, so the victims
/// have to be serialised against the entire assembly.</para>
/// </summary>
[CollectionDefinition(nameof(SqlitePoolCollection), DisableParallelization = true)]
public sealed class SqlitePoolCollection;
