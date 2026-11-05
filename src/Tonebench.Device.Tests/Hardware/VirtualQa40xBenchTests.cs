using System.Reflection;
using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// An audit rather than a case, in <see cref="UdevRulesTests"/>'s shape: it reads the real files under
/// <c>testbench/virtual-qa40x/</c> as text and fails on drift. <b>No <c>Category</c> trait — this runs
/// in the commit gate</b>, because it needs no device and the drift it catches is otherwise invisible.
/// </summary>
public class VirtualQa40xBenchTests
{
    private static DirectoryInfo BenchDirectory()
    {
        // Walk up from the test binary to the repository root (the directory holding Tonebench.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "testbench", "virtual-qa40x", "README.md")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return new DirectoryInfo(Path.Combine(dir!.FullName, "testbench", "virtual-qa40x"));
    }

    private static string ReadBenchFile(string name) =>
        File.ReadAllText(Path.Combine(BenchDirectory().FullName, name));

    /// <summary>
    /// The assertion that justifies this file. Add a register to the protocol path and the virtual
    /// bench quietly stops covering it: the emulator either does not answer — a 20 ms timeout, which
    /// is swallowed on any of the tier-2 paths — or returns its register-0 scratch value, which is a
    /// plausible number that is simply wrong. Neither shows up as a failure on the machine where the
    /// work happens. The reflection is the point: the table cannot be satisfied by remembering.
    /// </summary>
    [Fact]
    public void EveryRegisterThisAppTouches_IsInTheBenchReadmesCoverageTable()
    {
        string readme = ReadBenchFile("README.md");

        var registers = typeof(Qa40xRegisters)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .Select(f => (f.Name, Value: (byte)f.GetRawConstantValue()!))
            .ToArray();

        // If this ever reaches zero the reflection has stopped finding anything and the test would
        // pass vacuously, which is the one way an audit like this can rot silently.
        Assert.True(registers.Length >= 14, $"Expected the register map to have members; found {registers.Length}.");

        foreach (var (name, value) in registers)
        {
            string cell = $"`0x{value:x2}`";
            Assert.True(
                readme.Contains(cell, StringComparison.Ordinal),
                $"Qa40xRegisters.{name} (0x{value:x2}) has no row in testbench/virtual-qa40x/README.md's "
                    + "register-coverage table. Add one saying whether the emulator answers it — a register "
                    + "the emulator does not implement reads back as its scratch value, which is wrong "
                    + "without being obviously wrong.");
        }
    }

    /// <summary>
    /// The sentinel is a convention rather than a device property, so the two places that have to
    /// agree about it are pinned against each other. Edit <c>--serial</c> in the script alone and the
    /// virtual suite starts failing with "found a device that isn't the emulator", which reads as a
    /// hardware problem and is not one.
    /// </summary>
    [Fact]
    public void TheSentinelSerial_IsSingleSourced()
    {
        string bench = ReadBenchFile("bench.ps1");
        string readme = ReadBenchFile("README.md");

        Assert.Contains(VirtualQa40xBench.SentinelSerialArgument, bench, StringComparison.Ordinal);
        Assert.Contains(VirtualQa40xBench.SentinelSerialArgument, readme, StringComparison.Ordinal);

        // The decimal rendering is what ConnectAsync reports (register 0x1D as serialRaw.ToString()),
        // so the README must quote that too or the failure message sends the reader hunting for hex.
        Assert.Contains(VirtualQa40xBench.SentinelSerial, readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts the inverse of what might look natural: there must be <b>no</b> QA403 invocation.
    /// <c>Qa40xTransport.Open</c> probes <see cref="Qa40xModel.All"/> with the QA403 first, so running
    /// the emulator as a QA403 would make it indistinguishable from a real one and unreachable
    /// whenever one is plugged in — defeating the discriminator in <see cref="VirtualQa40xBench"/>.
    /// </summary>
    [Fact]
    public void TheBenchRunsTheEmulatorAsAQa402_NeverAsAQa403()
    {
        string bench = ReadBenchFile("bench.ps1");

        Assert.Contains("[string]$Model = \"qa402\"", bench, StringComparison.Ordinal);
        Assert.Equal("QA402", VirtualQa40xBench.ProductName);

        // Narrowly the model argument: the script mentions the QA403 freely and must, because
        // -Attach refuses when a real one is present. What it must never do is ask for one.
        Assert.DoesNotContain("--model qa403", bench, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Model qa403", bench, StringComparison.OrdinalIgnoreCase);
    }
}
