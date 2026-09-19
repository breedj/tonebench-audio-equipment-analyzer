namespace Qa403.Data;

/// <summary>
/// A fixed path for a measurement database. Kept for the tests, which build their databases by
/// opening a fresh temp path and migrating it (<see cref="SqliteConnectionFactory(Qa403DatabaseOptions)"/>);
/// the app itself has no fixed measurement database any more — a project is one file, and which one
/// is open is <see cref="CurrentProjectFile"/>'s. The old default, <c>%LOCALAPPDATA%\Qa403\qa403.db</c>,
/// is left where it is and can be opened as a project when it holds a single project row.
/// </summary>
public sealed record Qa403DatabaseOptions
{
    public required string Path { get; init; }
}
