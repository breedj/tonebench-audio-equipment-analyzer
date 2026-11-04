using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// An audit rather than a case: it reads <c>packaging/udev/99-qa403.rules</c> as text and fails if a
/// model in <see cref="Qa40xModel.All"/> has no Linux access rule.
///
/// <para>This exists because the failure it catches is invisible on the machine most of this is
/// developed on. A missing rule costs nothing on Windows, which is driverless for these devices, and
/// on Linux it surfaces as "Disconnected" with a permission error buried in a log — on hardware the
/// author may well not own. The same technique as <c>numberField.test.ts</c> and
/// <c>libraries.test.ts</c>: read the real file, so the check cannot drift from what ships.</para>
/// </summary>
public class UdevRulesTests
{
    private static string RulesPath()
    {
        // Walk up from the test binary to the repository root (the directory holding Tonebench.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packaging", "udev", "99-qa403.rules")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "packaging", "udev", "99-qa403.rules");
    }

    [Fact]
    public void EveryModelThisAppOpens_HasALinuxAccessRule()
    {
        string rules = File.ReadAllText(RulesPath());

        foreach (var model in Qa40xModel.All)
        {
            // udev matches the attribute text, which is lower-case hex with no 0x prefix.
            string expected = $"ATTR{{idProduct}}==\"{model.ProductId:x4}\"";
            Assert.Contains(expected, rules);
        }
    }

    /// <summary>
    /// The vendor id must be matched alongside every product id and never on its own: 0x16c0 is a
    /// shared "free" VID block rather than QuantAsylum's, so a VID-only rule would hand group write
    /// access to unrelated hardware on the user's bench.
    /// </summary>
    [Fact]
    public void TheRules_MatchTheVendorIdTogetherWithAProductId_NeverAlone()
    {
        string[] lines = File.ReadAllLines(RulesPath())
            .Where(l => l.TrimStart().StartsWith("SUBSYSTEM", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(Qa40xModel.All.Count, lines.Length);
        foreach (string line in lines)
        {
            Assert.Contains($"ATTR{{idVendor}}==\"{Qa40xRegisters.Qa40xVendorId:x4}\"", line);
            Assert.Contains("ATTR{idProduct}==", line);
        }
    }
}
