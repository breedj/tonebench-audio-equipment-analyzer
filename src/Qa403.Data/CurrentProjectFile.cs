namespace Qa403.Data;

/// <summary>
/// The path of the project file every repository currently opens — or <see langword="null"/> when no
/// project is open.
///
/// <para>This is the one seam that makes "one SQLite file per project" possible without touching a
/// repository: they are all stateless (every call opens its own connection through
/// <see cref="SqliteConnectionFactory.OpenAsync"/>), so switching projects is switching what that
/// call resolves. Written only by the project session, under its lock, after the previous file has
/// been checkpointed and its pooled connections released; read on every open. Any read with nothing
/// open throws <see cref="NoProjectOpenException"/>, which the host maps to a 409 with its message —
/// the alternative, a null path quietly creating an empty file somewhere, is exactly the failure this
/// type exists to rule out.</para>
/// </summary>
public sealed class CurrentProjectFile
{
    private string? _path;

    public string? Path
    {
        get => Volatile.Read(ref _path);
        set => Volatile.Write(ref _path, value);
    }

    /// <summary>A holder pinned to one file, for tests and for code that never switches.</summary>
    public static CurrentProjectFile At(string path) => new() { Path = path };
}
