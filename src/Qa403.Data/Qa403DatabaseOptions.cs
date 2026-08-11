namespace Qa403.Data;

/// <summary>
/// Where <c>qa403.db</c> lives. Defaults under LocalApplicationData rather than next to the
/// binary (the pattern the app's Serilog file sink uses) because measurement history is user
/// data — a `dotnet clean` or a rebuild must not silently destroy it. Override with the
/// <c>Database:Path</c> configuration key.
/// </summary>
public sealed record Qa403DatabaseOptions
{
    public required string Path { get; init; }

    public static Qa403DatabaseOptions Default
    {
        get
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qa403");
            Directory.CreateDirectory(directory);
            return new Qa403DatabaseOptions { Path = System.IO.Path.Combine(directory, "qa403.db") };
        }
    }
}
