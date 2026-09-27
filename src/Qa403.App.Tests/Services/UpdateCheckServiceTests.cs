using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The passive update check: ask GitHub Releases what the latest tag is, compare it with this
/// build, and say nothing at all if any part of that fails. Two things make it worth pinning.
/// Its silent-failure contract is deliberate — an offline bench machine must never see an error
/// over this — which also means a *broken* check is indistinguishable from a healthy one by
/// looking at the app: every path returns "no update available" and nothing is logged above
/// Warning. And the version it compares against is the running assembly's own, complete with the
/// SDK's "+&lt;git-sha&gt;" metadata, so the check can only work if that string still parses.
///
/// The service reads no clock and no configuration, so these tests are pure: a stub handler in
/// place of github.com, and tags computed relative to whatever version this build carries.
/// </summary>
public sealed class UpdateCheckServiceTests
{
    /// <summary>The name Program.cs configures the BaseAddress, user agent and 5 s timeout on.</summary>
    private const string ClientName = "GitHubReleases";

    private static SemVer RunningVersion
    {
        get
        {
            Assert.True(SemVer.TryParse(UpdateCheckService.CurrentVersion, out var version),
                $"this build's own version ('{UpdateCheckService.CurrentVersion}') must parse, or every check silently returns no update");
            return version!.Value;
        }
    }

    /// <summary>A release tag one patch above this build — the "an update exists" case.</summary>
    private static string HigherTag() => $"v{RunningVersion.Major}.{RunningVersion.Minor}.{RunningVersion.Patch + 1}";

    /// <summary>
    /// A release tag strictly below this build, whatever version it carries: decrement the
    /// rightmost non-zero component. 0.0.0 has no release below it, only its own prereleases.
    /// </summary>
    private static string LowerTag()
    {
        var v = RunningVersion;
        if (v.Patch > 0) return $"v{v.Major}.{v.Minor}.{v.Patch - 1}";
        if (v.Minor > 0) return $"v{v.Major}.{v.Minor - 1}.0";
        if (v.Major > 0) return $"v{v.Major - 1}.0.0";
        return "v0.0.0-alpha";
    }

    private static string CurrentTag() => "v" + RunningVersion;

    private static string ReleaseJson(string tag, string url = "https://github.com/breedj/qa40x-audio-analyzer-cp/releases/tag/x")
        => $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","name":"whatever","draft":false}""";

    private static UpdateCheckService Create(StubHandler handler)
        => new(new StubFactory(handler), NullLogger<UpdateCheckService>.Instance);

    private static Task<UpdateCheckResult> CheckAsync(StubHandler handler)
        => Create(handler).GetStatusAsync(CancellationToken.None);

    [Fact]
    public async Task ANewerTag_IsReportedAsAnUpdate()
    {
        var handler = StubHandler.Returning(ReleaseJson(HigherTag(), "https://github.com/breedj/qa40x-audio-analyzer-cp/releases/tag/" + HigherTag()));

        var result = await CheckAsync(handler);

        Assert.True(result.UpdateAvailable);
        // Normalised, not the raw tag: this string is what the banner prints.
        Assert.Equal(HigherTag()[1..], result.LatestVersion);
        Assert.Equal("https://github.com/breedj/qa40x-audio-analyzer-cp/releases/tag/" + HigherTag(), result.LatestUrl);
        Assert.Equal(UpdateCheckService.CurrentVersion, result.CurrentVersion);
    }

    [Fact]
    public async Task TheSameVersionAsThisBuild_IsNotAnUpdate()
    {
        var result = await CheckAsync(StubHandler.Returning(ReleaseJson(CurrentTag())));

        Assert.False(result.UpdateAvailable);
        Assert.Equal(RunningVersion.ToString(), result.LatestVersion);
    }

    [Fact]
    public async Task AnOlderTag_IsNotAnUpdate_ButIsStillReported()
    {
        var result = await CheckAsync(StubHandler.Returning(ReleaseJson(LowerTag())));

        Assert.False(result.UpdateAvailable);
        Assert.Equal(LowerTag()[1..], result.LatestVersion);
    }

    // A build that carries the SDK's "+<sha>" metadata must compare equal to the tag it was made
    // from, or every release announces itself as an update to the very build cut from it.
    [Fact]
    public async Task ABuildWithGitMetadata_DoesNotOfferItselfAnUpdate()
    {
        Assert.False((await CheckAsync(StubHandler.Returning(ReleaseJson(CurrentTag())))).UpdateAvailable);
        Assert.Contains('.', UpdateCheckService.CurrentVersion);
    }

    [Fact]
    public async Task ItAsksTheReleasesApiOfThisAppsOwnRepository()
    {
        var handler = StubHandler.Returning(ReleaseJson(CurrentTag()));

        await CheckAsync(handler);

        Assert.Equal(ClientName, Assert.Single(handler.ClientNames));
        Assert.Equal(
            "https://api.github.com/repos/breedj/qa40x-audio-analyzer-cp/releases/latest",
            handler.LastRequestUri?.ToString());
    }

    // The situation on this machine today: the repository is private, so an unauthenticated
    // request 404s. Nothing is shown and nothing is thrown.
    [Fact]
    public async Task ANonSuccessResponse_IsNoUpdateRatherThanAnError()
    {
        var result = await CheckAsync(StubHandler.WithStatus(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        AssertQuietlyNoUpdate(result);
    }

    [Fact]
    public async Task AnUnreachableGitHub_IsNoUpdateRatherThanAnError()
    {
        var result = await CheckAsync(StubHandler.Throwing(new HttpRequestException("no such host")));

        AssertQuietlyNoUpdate(result);
    }

    // What HttpClient raises when Program.cs's 5 s timeout expires.
    [Fact]
    public async Task ATimedOutRequest_IsNoUpdateRatherThanAnError()
    {
        var result = await CheckAsync(StubHandler.Throwing(new TaskCanceledException("timed out")));

        AssertQuietlyNoUpdate(result);
    }

    [Fact]
    public async Task AnUnparsableBody_IsNoUpdateRatherThanAnError()
    {
        var result = await CheckAsync(StubHandler.Returning("<html>we are having trouble</html>"));

        AssertQuietlyNoUpdate(result);
    }

    [Theory]
    [InlineData("""{"html_url":"https://github.com/x"}""")]      // no tag at all
    [InlineData("""{"tag_name":null}""")]
    [InlineData("""{"tag_name":"nightly"}""")]                    // not a version
    [InlineData("""{"tag_name":"2026-09-25"}""")]
    public async Task AReleaseWithNoUsableTag_IsNoUpdate(string body)
    {
        AssertQuietlyNoUpdate(await CheckAsync(StubHandler.Returning(body)));
    }

    // A release published without html_url is not a reason to withhold the notice — the banner
    // simply drops its "View release" link (UpdateBanner.vue guards on latestUrl).
    [Fact]
    public async Task AReleaseWithNoUrl_IsStillOffered()
    {
        var result = await CheckAsync(StubHandler.Returning($$"""{"tag_name":"{{HigherTag()}}"}"""));

        Assert.True(result.UpdateAvailable);
        Assert.Null(result.LatestUrl);
    }

    // The client polls every 6 hours per browser tab (useUpdateCheck.ts) and every tab polls
    // independently, so the hour of caching here is what keeps a day of bench work down to a
    // couple of requests — GitHub rate-limits unauthenticated callers by IP.
    [Fact]
    public async Task TheResultIsCached_SoRepeatedPollsDoNotReachGitHubAgain()
    {
        var handler = StubHandler.Returning(ReleaseJson(HigherTag()));
        var service = Create(handler);

        var first = await service.GetStatusAsync(CancellationToken.None);
        var second = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(first, second);
    }

    // Failures are cached too, deliberately: an offline machine stops asking for an hour rather
    // than spending a 5 s timeout on every poll.
    [Fact]
    public async Task AFailedCheckIsCachedToo_SoAnOfflineMachineStopsAsking()
    {
        var handler = StubHandler.Throwing(new HttpRequestException("no such host"));
        var service = Create(handler);

        await service.GetStatusAsync(CancellationToken.None);
        await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    private static void AssertQuietlyNoUpdate(UpdateCheckResult result)
    {
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
        Assert.Null(result.LatestUrl);
        Assert.Equal(UpdateCheckService.CurrentVersion, result.CurrentVersion);
    }

    /// <summary>
    /// Stands in for Program.cs's named client, and configures the BaseAddress under that name
    /// only — so a service asking for a different name gets an unconfigured client and fails
    /// loudly on the relative URL, the same way it would in the app.
    /// </summary>
    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            handler.ClientNames.Add(name);
            var client = new HttpClient(handler, disposeHandler: false);
            if (name == ClientName)
                client.BaseAddress = new Uri("https://api.github.com/");
            return client;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        private StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public List<string> ClientNames { get; } = [];
        public int Calls { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        public static StubHandler Returning(string body) => WithStatus(HttpStatusCode.OK, body);

        public static StubHandler WithStatus(HttpStatusCode status, string body) => new(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        public static StubHandler Throwing(Exception exception) => new(() => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_respond());
        }
    }
}
