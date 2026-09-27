using Qa403.App.Services;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The version parser and comparer behind the update check. Everything here is silent when it is
/// wrong: a tag that fails to parse, or an ordering that comes out backwards, makes
/// <see cref="UpdateCheckService"/> return "no update available" — exactly what it returns on an
/// offline machine — so nothing anywhere reports a fault and the banner simply never appears.
/// </summary>
public sealed class SemVerTests
{
    private static SemVer Parse(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version), $"'{text}' should parse");
        return version!.Value;
    }

    [Fact]
    public void TryParse_ReadsAPlainRelease()
    {
        var version = Parse("1.4.7");

        Assert.Equal(1, version.Major);
        Assert.Equal(4, version.Minor);
        Assert.Equal(7, version.Patch);
        Assert.Null(version.Prerelease);
    }

    // Release tags in this repo are vX.Y.Z (.github/workflows/release.yml triggers on "v*"), while
    // the assembly version they are compared against is bare — so the leading v has to go or every
    // release compares as unparsable.
    [Theory]
    [InlineData("v1.4.7")]
    [InlineData("V1.4.7")]
    [InlineData("  v1.4.7  ")]
    public void TryParse_StripsTheLeadingVAndSurroundingSpace(string tag)
    {
        Assert.Equal(new SemVer(1, 4, 7, null), Parse(tag));
    }

    // The .NET SDK appends "+<git-sha>" to AssemblyInformationalVersion for a build made inside a
    // git repo, so the *running* version always carries build metadata even though the tags do not.
    // Dropping it is what lets a build compare equal to the tag it was made from.
    [Fact]
    public void TryParse_IgnoresBuildMetadata()
    {
        Assert.Equal(new SemVer(0, 2, 0, null), Parse("0.2.0+9f3c1ab"));
        Assert.Equal(new SemVer(1, 0, 0, "beta.1"), Parse("1.0.0-beta.1+9f3c1ab"));
    }

    [Fact]
    public void TryParse_ReadsAPrerelease()
    {
        Assert.Equal(new SemVer(1, 0, 0, "rc.2"), Parse("v1.0.0-rc.2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.x")]
    [InlineData("nightly")]
    [InlineData("release-2026-09")]
    public void TryParse_RejectsWhatIsNotThreeNumbers(string text)
    {
        Assert.False(SemVer.TryParse(text, out var version));
        Assert.Null(version);
    }

    // The case the parser exists for at all: ordinal string comparison puts "1.9.0" after
    // "1.10.0", so a tenth minor release would never be offered to anyone on 1.9.x.
    [Fact]
    public void Compare_OrdersNumericallyAndNotAsText()
    {
        Assert.True(Parse("v1.10.0").CompareTo(Parse("v1.9.0")) > 0);
        Assert.True(Parse("v1.0.10").CompareTo(Parse("v1.0.9")) > 0);
        Assert.True(Parse("v10.0.0").CompareTo(Parse("v9.0.0")) > 0);
    }

    [Fact]
    public void Compare_TakesMajorThenMinorThenPatch()
    {
        Assert.True(Parse("2.0.0").CompareTo(Parse("1.99.99")) > 0);
        Assert.True(Parse("1.2.0").CompareTo(Parse("1.1.99")) > 0);
        Assert.True(Parse("1.1.2").CompareTo(Parse("1.1.1")) > 0);
        Assert.Equal(0, Parse("1.1.1").CompareTo(Parse("v1.1.1")));
    }

    [Fact]
    public void Compare_PutsAPrereleaseBeforeItsOwnRelease()
    {
        Assert.True(Parse("1.0.0-rc.1").CompareTo(Parse("1.0.0")) < 0);
        Assert.True(Parse("1.0.0").CompareTo(Parse("1.0.0-rc.1")) > 0);

        // And a prerelease of the *next* version still beats the current release, which is what
        // makes a prerelease tag offerable at all.
        Assert.True(Parse("1.1.0-rc.1").CompareTo(Parse("1.0.0")) > 0);
    }

    // Deliberately ordinal — the class doc says the suffix is not parsed further, because release
    // tags here are plain vX.Y.Z. It orders rc.1 before rc.2, and (wrongly, by semver §11, but
    // harmlessly for this repo) rc.10 before rc.2.
    [Fact]
    public void Compare_OrdersTwoPrereleasesOrdinally()
    {
        Assert.True(Parse("1.0.0-rc.2").CompareTo(Parse("1.0.0-rc.1")) > 0);
        Assert.True(Parse("1.0.0-rc.10").CompareTo(Parse("1.0.0-rc.2")) < 0);
    }

    [Fact]
    public void Compare_IgnoresBuildMetadata()
    {
        Assert.Equal(0, Parse("1.2.3+aaaaaaa").CompareTo(Parse("1.2.3+bbbbbbb")));
        Assert.Equal(0, Parse("1.2.3").CompareTo(Parse("1.2.3+aaaaaaa")));
    }

    // UpdateCheckResult.LatestVersion is this string, and it is what the banner shows — so it is
    // the normalised form, never the raw tag.
    [Fact]
    public void ToString_DropsTheVAndTheMetadataAndRoundTrips()
    {
        Assert.Equal("1.2.3", Parse("v1.2.3+9f3c1ab").ToString());
        Assert.Equal("1.2.3-rc.1", Parse("v1.2.3-rc.1").ToString());
        Assert.Equal(Parse("v1.2.3-rc.1"), Parse(Parse("v1.2.3-rc.1").ToString()));
    }
}
