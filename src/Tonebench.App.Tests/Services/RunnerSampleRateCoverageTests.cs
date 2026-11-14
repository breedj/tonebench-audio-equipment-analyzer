using System.Text.RegularExpressions;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Every runner that streams at a rate taken from its request refuses an unreachable one
/// <b>in <c>StartAsync</c></b>, where the caller is still listening.
///
/// <para><b>Why an audit and not a behavioural test per runner.</b> The fault is an <i>absence</i>:
/// a runner that simply does not call <c>RequireSupportedSampleRate</c> behaves perfectly on every
/// bench whose analyzer has the rate, which is every bench the test suite builds by default. It
/// surfaces only as a <c>200 OK</c> followed by a run that never produces a point — measured
/// 2026-11-14 on <c>POST /api/frequency-response/start</c> at a span the analyzer could not reach.
/// Covering it behaviourally would mean a valid request record for each of eight runners, and the
/// thing being covered is one line in each; the real risk is a ninth runner arriving without it,
/// which is what this catches. <c>FrequencyResponseRunnerTests</c> holds the behavioural half.</para>
///
/// <para>The same technique as <c>UdevRulesTests</c>, <c>numberField.test.ts</c> and
/// <c>supportedSpans.test.ts</c>: read the real source, so the check cannot drift from what ships.</para>
/// </summary>
public class RunnerSampleRateCoverageTests
{
    /// <summary>
    /// The two <c>*Runner</c> files that are not measurement runners taking a rate from a request.
    /// Named rather than skipped by a <c>continue</c>, so a real runner that lost its
    /// <c>StartAsync</c> fails here instead of quietly dropping out of the audit.
    ///
    /// <para><c>ClockDriftRunner</c> takes its rate from <c>session.Capabilities.SampleRatesHz</c>
    /// rather than from a request, so it cannot name one the analyzer does not have.
    /// <c>SoundCardCalibrationRunner</c> is not a measurement at all — it has no <c>StartAsync</c>,
    /// and pins its own record length to <c>CalibrationFftSize</c> rather than taking a rate.</para>
    /// </summary>
    private static readonly string[] Exempt = ["ClockDriftRunner", "SoundCardCalibrationRunner"];

    private static string ServicesPath()
    {
        // Walk up from the test binary to the repository root (the directory holding Tonebench.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tonebench.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Tonebench.App", "Services");
    }

    private static IEnumerable<string> RunnerFiles() =>
        Directory.EnumerateFiles(ServicesPath(), "*Runner.cs")
            .Where(path => !Exempt.Contains(Path.GetFileNameWithoutExtension(path)));

    [Fact]
    public void EveryRunner_ChecksItsRateWhereTheCallerCanSeeTheRefusal()
    {
        var missing = new List<string>();

        foreach (string path in RunnerFiles())
        {
            string source = File.ReadAllText(path);

            // The body of StartAsync, up to the next method-level declaration. Enough to tell
            // "checks it before spawning the run" from "checks it somewhere else in the file".
            int startAsync = source.IndexOf("> StartAsync(", StringComparison.Ordinal);
            Assert.True(startAsync > 0, $"{Path.GetFileName(path)} has no StartAsync to audit");

            string body = source[startAsync..];
            int end = body.IndexOf("\n    public ", StringComparison.Ordinal);
            if (end > 0)
                body = body[..end];

            if (!body.Contains("session.RequireSupportedSampleRate(", StringComparison.Ordinal))
                missing.Add(Path.GetFileNameWithoutExtension(path));
        }

        Assert.True(
            missing.Count == 0,
            $"these runners start without checking the rate against the analyzer: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// And it checks the rate it will actually stream at. Each runner builds exactly one
    /// <c>AcquisitionConfig</c>, so the expression there and the one handed to the check have to be
    /// the same — a runner that validated <c>SampleRateHz</c> and then streamed at
    /// <c>EffectiveSampleRateHz</c> would pass the test above and still be wrong.
    /// </summary>
    [Fact]
    public void EveryRunner_ChecksTheRateItActuallyStreamsAt()
    {
        var wrong = new List<string>();

        foreach (string path in RunnerFiles())
        {
            string source = File.ReadAllText(path);

            Match streamed = Regex.Match(source, @"new AcquisitionConfig\(\s*(request\.\w+)");
            Match checkedRate = Regex.Match(source, @"session\.RequireSupportedSampleRate\(\s*(request\.\w+)");

            if (!streamed.Success || !checkedRate.Success)
                continue;

            if (streamed.Groups[1].Value != checkedRate.Groups[1].Value)
            {
                wrong.Add(
                    $"{Path.GetFileNameWithoutExtension(path)} streams at {streamed.Groups[1].Value} " +
                    $"but checks {checkedRate.Groups[1].Value}");
            }
        }

        Assert.Empty(wrong);
    }

    /// <summary>
    /// The audits above pass vacuously if they find nothing to read, so this says what "nothing"
    /// would look like. Eight runners carry the check today; a ninth is the case the first test
    /// exists for and will raise this number with it.
    /// </summary>
    [Fact]
    public void TheAuditFindsTheRunnersItIsAuditing()
    {
        Assert.True(RunnerFiles().Count() >= 8, "expected at least the eight runners that take a rate from their request");
        Assert.Contains(
            RunnerFiles(),
            path => Path.GetFileNameWithoutExtension(path) == "FrequencyResponseRunner");
    }
}
