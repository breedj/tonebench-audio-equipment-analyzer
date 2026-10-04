// SqliteConnection.ClearAllPools() is process-global, and most classes here call it — TestDb.Cleanup
// has to, because Microsoft.Data.Sqlite pools the native handle on Dispose and the OS file lock
// outlives the `using` block that wrote the database.
//
// That makes these classes unsafe to run against each other in parallel: clearing the pool out from
// under SqliteConnectionFactoryTests.Open_PooledReuse_ReappliesPragmas is precisely the thing that
// test is checking did not happen, so it failed intermittently depending on which other class
// happened to be tearing down at the same moment.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
