namespace Qa403.App.Services;

/// <summary>
/// The user asked for a file that cannot be used as a project: missing, not SQLite, not a QA403
/// database, or a legacy <c>qa403.db</c> holding several projects. A 400 with the message, as
/// opposed to <see cref="InvalidOperationException"/>, which is the session refusing to act right
/// now (a run in progress, a target that already exists) and is a 409.
/// </summary>
public sealed class ProjectFileException(string message) : Exception(message);
