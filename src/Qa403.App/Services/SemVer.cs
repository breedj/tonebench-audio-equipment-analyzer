using System.Diagnostics.CodeAnalysis;

namespace Qa403.App.Services;

/// <summary>
/// Minimal major.minor.patch[-prerelease] parser and comparer for the update-check feature
/// (docs/qa403-app-stack-spec.md §8 calls for "proper semver parsing, not string comparison" —
/// string comparison would put "1.9.0" after "1.10.0"). A build with any prerelease suffix always
/// compares lower than the same major.minor.patch without one, matching semver precedence; beyond
/// that the suffix itself is not parsed further since release tags here are plain vX.Y.Z.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemVer>
{
    public static bool TryParse(string value, [NotNullWhen(true)] out SemVer? result)
    {
        result = null;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        // Semver build metadata (semver.org §10) — the .NET SDK appends "+<git-commit-sha>" to
        // AssemblyInformationalVersion automatically when building inside a git repo, so the
        // running assembly's own version always carries one of these. Ignored for precedence.
        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
            text = text[..plusIndex];

        var dashIndex = text.IndexOf('-');
        string? prerelease = null;
        if (dashIndex >= 0)
        {
            prerelease = text[(dashIndex + 1)..];
            text = text[..dashIndex];
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
            return false;

        result = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;

        var byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0) return byMinor;

        var byPatch = Patch.CompareTo(other.Patch);
        if (byPatch != 0) return byPatch;

        // Semver precedence: a prerelease sorts before the same version without one.
        var hasPrerelease = Prerelease is not null;
        var otherHasPrerelease = other.Prerelease is not null;
        if (hasPrerelease && !otherHasPrerelease) return -1;
        if (!hasPrerelease && otherHasPrerelease) return 1;
        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
