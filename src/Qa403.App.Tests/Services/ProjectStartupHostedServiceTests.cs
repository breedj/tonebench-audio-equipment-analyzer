using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qa403.App.Json;
using Qa403.App.Services;
using Qa403.Data;

namespace Qa403.App.Tests.Services;

/// <summary>
/// What is open when the host comes up: nothing (the welcome screen), the last project (welcome
/// switched off), or the file named on the command line. Never a failed startup.
/// </summary>
public class ProjectStartupHostedServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-startup-{Guid.NewGuid():N}");
    private readonly CurrentProjectFile _current = new();
    private readonly ConfigRepository _config;
    private readonly ProjectSessionService _session;

    private sealed class IdleRun : IMeasurementRun
    {
        public bool Running => false;
    }

    public ProjectStartupHostedServiceTests()
    {
        Directory.CreateDirectory(_dir);
        string configPath = Path.Combine(_dir, "config.db");
        _config = new ConfigRepository(new ConfigConnectionFactory(new Qa403ConfigDatabaseOptions { Path = configPath }));

        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
        _session = new ProjectSessionService(
            _current, _config, new DatabaseHealth(), [new IdleRun()], Options.Create(jsonOptions),
            NullLogger<ProjectSessionService>.Instance);

        using var connection = SqliteConnectionFactory.OpenAtAsync(configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ProjectStartupHostedService Service(string? argument = null) =>
        new(_session, new StartupProjectArgument(argument), NullLogger<ProjectStartupHostedService>.Instance);

    private async Task<string> CreateAndCloseAsync(string name)
    {
        var info = await _session.CreateAsync(Path.Combine(_dir, name), name, null, CancellationToken.None);
        await _session.CloseAsync(CancellationToken.None);
        return info.Path;
    }

    private Task SetWelcomeAsync(bool show) =>
        _config.SetAsync(ConfigRepository.StartupKey, $$"""{"showWelcomeOnStartup":{{(show ? "true" : "false")}}}""", CancellationToken.None);

    [Fact]
    public async Task WithTheWelcomeScreenOn_OpensNothing()
    {
        await CreateAndCloseAsync("last");

        await Service().StartAsync(CancellationToken.None);

        Assert.Null(_session.Current);
    }

    [Fact]
    public async Task WithTheWelcomeScreenOff_ReopensTheLastProject()
    {
        string path = await CreateAndCloseAsync("last");
        await SetWelcomeAsync(false);

        await Service().StartAsync(CancellationToken.None);

        Assert.Equal(path, _session.Current?.Path);
    }

    [Fact]
    public async Task WithTheWelcomeScreenOff_AndTheLastFileGone_OpensNothingAndDoesNotThrow()
    {
        string path = await CreateAndCloseAsync("last");
        await SetWelcomeAsync(false);
        File.Delete(path);

        await Service().StartAsync(CancellationToken.None);

        Assert.Null(_session.Current);
    }

    [Fact]
    public async Task ACommandLineProject_WinsOverThePreference()
    {
        await CreateAndCloseAsync("last");
        string named = await CreateAndCloseAsync("named");
        await SetWelcomeAsync(true);

        await Service(named).StartAsync(CancellationToken.None);

        Assert.Equal(named, _session.Current?.Path);
    }

    [Fact]
    public async Task ACommandLineProjectThatCannotBeOpened_IsLoggedNotThrown()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "bad.qa403"), "not sqlite");

        await Service(Path.Combine(_dir, "bad.qa403")).StartAsync(CancellationToken.None);

        Assert.Null(_session.Current);
    }

    [Fact]
    public void Parse_AcceptsBothSpellings_LastOneWins()
    {
        Assert.Null(StartupProjectArgument.Parse(["--simulate"]).Path);
        Assert.Equal(@"C:\a.qa403", StartupProjectArgument.Parse(["--project", @"C:\a.qa403"]).Path);
        Assert.Equal("/home/j/b.qa403", StartupProjectArgument.Parse(["--project=/home/j/b.qa403"]).Path);
        Assert.Equal("second", StartupProjectArgument.Parse(["--project", "first", "--project=second"]).Path);
        Assert.Null(StartupProjectArgument.Parse(["--project"]).Path);
    }
}
