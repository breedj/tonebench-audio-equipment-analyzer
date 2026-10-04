using System.Numerics;

namespace Tonebench.Device.Simulation;

/// <summary>Which output impedance the simulated amplifier presents at its output terminals.</summary>
public enum SimulatedOutputStageModel
{
    /// <summary>An ideal voltage source: no output impedance, so the load makes no difference. The default, and bit-identical to the simulator before the stage existed.</summary>
    None = 0,

    /// <summary>A solid-state amplifier: 0.028 Ω in series with a 1.5 µH output coil — the tutorial's BC-1 (DF 286 at LF, its coil dominating by 20 kHz).</summary>
    SolidState = 1,

    /// <summary>A tube amplifier through its output transformer: 2 Ω in series with 0.3 mH of leakage inductance (DF 4 at LF).</summary>
    TubeAmp = 2,
}

/// <summary>What hangs on the simulated amplifier's output. <see cref="Open"/> is the default: nothing, so no divider.</summary>
public enum SimulatedLoad
{
    Open = 0,
    EightOhms = 1,
    FourOhms = 2,
}

/// <summary>
/// The simulator's amplifier output stage: a series output impedance (R + sL) with a selectable
/// resistive load hanging off it, forming a divider <c>H(s) = R_L / (R_L + R + sL)</c> on the DUT
/// channel. What the load-step output-impedance measurement measures against <c>--simulate</c>.
///
/// <para>Applied right after the DUT model, on the left channel — the amplifier's output node is
/// where the load connects — and before the crosstalk and the additive hum and noise, none of which
/// is a property of the output stage. Unity when the model is <see cref="SimulatedOutputStageModel.None"/>
/// or the load is <see cref="SimulatedLoad.Open"/>, which is what keeps every other simulated
/// measurement untouched: the stage only exists while both knobs say so.</para>
///
/// <para>The divider is a bilinear <see cref="Biquad"/> without pre-warping, like every other
/// filter in the simulator, so at 48 kHz the top of the band warps upward; the oracle a measurement
/// is checked against is therefore <see cref="ZoutAt(SimulatedOutputStageModel, double, double, double)"/>,
/// the impedance the <em>sampled</em> divider implies, not the analog figures the model was
/// designed to (<see cref="AnalogZoutAt"/>).</para>
/// </summary>
public sealed class SimulatedOutputStage
{
    /// <summary>The solid-state model's series resistance, ohms.</summary>
    public const double SolidStateOhms = 0.028;

    /// <summary>The solid-state model's output coil, henry.</summary>
    public const double SolidStateHenry = 1.5e-6;

    /// <summary>The tube model's series resistance, ohms.</summary>
    public const double TubeAmpOhms = 2.0;

    /// <summary>The tube model's leakage inductance, henry.</summary>
    public const double TubeAmpHenry = 0.3e-3;

    private readonly Biquad? _section;
    private double _s1;
    private double _s2;

    public SimulatedOutputStage(SimulatedOutputStageModel model, SimulatedLoad load, double sampleRateHz)
    {
        Model = model;
        Load = load;
        _section = Section(model, load, sampleRateHz);
    }

    public SimulatedOutputStageModel Model { get; }

    public SimulatedLoad Load { get; }

    /// <summary>True when the stage changes nothing — no model, or nothing connected.</summary>
    public bool IsUnity => _section is null;

    /// <summary>The load's resistance in ohms; infinity for <see cref="SimulatedLoad.Open"/>.</summary>
    public static double LoadOhms(SimulatedLoad load) => load switch
    {
        SimulatedLoad.Open => double.PositiveInfinity,
        SimulatedLoad.EightOhms => 8.0,
        SimulatedLoad.FourOhms => 4.0,
        _ => throw new ArgumentOutOfRangeException(nameof(load), load, "Unknown simulated load."),
    };

    /// <summary>The model's series (R, L); (0, 0) for <see cref="SimulatedOutputStageModel.None"/>.</summary>
    public static (double Ohms, double Henry) SeriesElements(SimulatedOutputStageModel model) => model switch
    {
        SimulatedOutputStageModel.None => (0.0, 0.0),
        SimulatedOutputStageModel.SolidState => (SolidStateOhms, SolidStateHenry),
        SimulatedOutputStageModel.TubeAmp => (TubeAmpOhms, TubeAmpHenry),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown simulated output stage."),
    };

    /// <summary>The impedance the model was designed to, R + jωL — the figure a manual quotes.</summary>
    public static Complex AnalogZoutAt(SimulatedOutputStageModel model, double freqHz)
    {
        var (r, l) = SeriesElements(model);
        return new Complex(r, 2.0 * Math.PI * freqHz * l);
    }

    /// <summary>
    /// The impedance the <em>sampled</em> divider implies at <paramref name="freqHz"/> into
    /// <paramref name="loadOhms"/>: <c>R_L · (1/H − 1)</c> with H the biquad's own response. This is
    /// the exact oracle for a load-step measurement against the simulator, at the sample rate the
    /// run used; it converges on <see cref="AnalogZoutAt"/> as the rate rises.
    /// </summary>
    public static Complex ZoutAt(SimulatedOutputStageModel model, double loadOhms, double freqHz, double sampleRateHz)
    {
        if (model == SimulatedOutputStageModel.None)
            return Complex.Zero;

        var section = Divider(model, loadOhms, sampleRateHz);
        var h = section.ResponseAt(2.0 * Math.PI * freqHz / sampleRateHz);
        return loadOhms * ((Complex.One / h) - Complex.One);
    }

    private static Biquad? Section(SimulatedOutputStageModel model, SimulatedLoad load, double sampleRateHz)
    {
        if (model == SimulatedOutputStageModel.None || load == SimulatedLoad.Open)
            return null;

        return Divider(model, LoadOhms(load), sampleRateHz);
    }

    private static Biquad Divider(SimulatedOutputStageModel model, double loadOhms, double sampleRateHz)
    {
        var (r, l) = SeriesElements(model);
        // H(s) = R_L / (R_L + R + sL): numerator (0·s² + 0·s + R_L), denominator (0·s² + L·s + (R + R_L)).
        return Biquad.FromAnalog(0.0, 0.0, loadOhms, 0.0, l, r + loadOhms, sampleRateHz);
    }

    /// <summary>The amplifier's terminal voltage for <paramref name="x"/>, its open-circuit output. Stateful; one call per frame.</summary>
    public double Process(double x)
    {
        if (_section is not { } s)
            return x;

        double y = (s.B0 * x) + _s1;
        _s1 = (s.B1 * x) - (s.A1 * y) + _s2;
        _s2 = (s.B2 * x) - (s.A2 * y);
        return y;
    }
}
