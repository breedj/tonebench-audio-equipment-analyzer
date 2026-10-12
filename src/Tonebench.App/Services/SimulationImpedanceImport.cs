using System.Numerics;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>Which way round the current trace's reference direction is taken.</summary>
public enum AcImpedanceCurrentDirection
{
    /// <summary>
    /// Decide from the data, which is almost always what is wanted — see
    /// <see cref="SimulationResponseImportService.ImportImpedanceAsync"/>'s remarks on the sign.
    /// </summary>
    Auto = 0,

    /// <summary>Divide by the current exactly as the simulator stored it.</summary>
    AsStored = 1,

    /// <summary>Negate the current first.</summary>
    Inverted = 2,
}

/// <summary>
/// What to import from an <c>.ac</c> run as an impedance curve.
///
/// <para>Both traces are required, and that is what removes every assumption: with a 1 A source the
/// current column is a constant 1 and dividing by it costs nothing, so the "drive it with 1 A and
/// read the voltage as ohms" shortcut needs no special case — picking the source's own current
/// trace <i>is</i> that shortcut.</para>
/// </summary>
public sealed record AcImpedanceImportRequest(
    string Path,
    int VoltageTraceIndex,
    int CurrentTraceIndex,
    AcImpedanceCurrentDirection CurrentDirection,
    string? Name,
    string? Notes,
    IReadOnlyList<int>? SweepIndices = null);

/// <summary>
/// What a re-analysis of an imported impedance may change, which is the current's direction and
/// nothing else — there is no band, no corner search and no reference level on an impedance curve,
/// and which two traces were divided is settled at import because the other columns were never
/// read. Null leaves the direction as it is.
/// </summary>
public sealed record AcImpedanceReanalyzeRequest(AcImpedanceCurrentDirection? CurrentDirection = null);
