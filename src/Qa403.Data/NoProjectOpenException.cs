namespace Qa403.Data;

/// <summary>
/// Thrown by <see cref="SqliteConnectionFactory.OpenAsync"/> when <see cref="CurrentProjectFile.Path"/>
/// is <see langword="null"/>. An <see cref="InvalidOperationException"/> so the endpoints that already
/// translate that type to a 409 keep doing so; the host's exception handler catches the rest.
/// </summary>
public sealed class NoProjectOpenException()
    : InvalidOperationException("No project is open. Open or create a project first.");
