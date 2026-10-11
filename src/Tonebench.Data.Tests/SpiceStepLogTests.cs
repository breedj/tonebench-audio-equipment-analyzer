using Tonebench.Data;

namespace Tonebench.Data.Tests;

public sealed class SpiceStepLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tonebench-steplog-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// LTspice's own wording, copied out of a generated run's log: one <c>.step param=value</c> line
    /// per step, in the order they ran, among a page of unrelated output.
    /// </summary>
    [Fact]
    public void ReadLabels_TakesTheStepLinesInOrder()
    {
        string raw = Write("run.raw", "x");
        File.WriteAllText(
            Path.Combine(_dir, "run.log"),
            """
            LTspice 24.1.10 for Windows
            Circuit: C:\somewhere\run.net
            Options: plotwinsize=0  numdgt=7
            Gmin stepping succeeded in finding the operating point.

            .step freq=1000
            .step freq=2000
            .step freq=3000
            Total elapsed time: 1.389 seconds.
            """);

        Assert.Equal(["freq=1000", "freq=2000", "freq=3000"], SpiceStepLog.ReadLabels(raw));
    }

    /// <summary>
    /// A QSpice run has no log at all — it prints its step lines to the console — and that costs a
    /// name, never an import. Same for a log that cannot be read.
    /// </summary>
    [Fact]
    public void ReadLabels_WithNoLog_IsEmpty()
    {
        Assert.Empty(SpiceStepLog.ReadLabels(Write("alone.qraw", "x")));
        Assert.Empty(SpiceStepLog.ReadLabels(Path.Combine(_dir, "missing.raw")));
    }

    /// <summary>
    /// The <i>directive</i> is not a label. <c>.step param freq 1k 3k 1k</c> is what the schematic
    /// asked for, where <c>.step freq=1000</c> is one run of it — and labelling three sweeps with
    /// the range they came from would be worse than numbering them.
    /// </summary>
    [Fact]
    public void ReadLabels_IgnoresTheDirectiveItself()
    {
        string raw = Write("directive.raw", "x");
        File.WriteAllText(
            Path.Combine(_dir, "directive.log"),
            ".step param freq 1k 3k 1k\n.step freq=1000\n.step freq=2000\n");

        Assert.Equal(["freq=1000", "freq=2000"], SpiceStepLog.ReadLabels(raw));
    }

    [Fact]
    public void LogPathFor_IsTheSiblingLog()
    {
        Assert.EndsWith("amp.log", SpiceStepLog.LogPathFor(Path.Combine(_dir, "amp.raw")), StringComparison.Ordinal);
        Assert.EndsWith("amp.log", SpiceStepLog.LogPathFor(Path.Combine(_dir, "amp.qraw")), StringComparison.Ordinal);
    }

    private string Write(string name, string contents)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
