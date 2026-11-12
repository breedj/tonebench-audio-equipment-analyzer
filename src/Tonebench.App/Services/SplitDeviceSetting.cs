using Tonebench.Data;
using Tonebench.Device.Split;

namespace Tonebench.App.Services;

/// <summary>
/// Which two kinds a stored split names, once they have been checked — and the one place that
/// checks them.
///
/// <para>One function because the answer is needed in three places that have to agree: the setting
/// that falls back on an unusable document, the endpoint that refuses one with a message, and the
/// factory that builds the thing. Each refusal is a sentence a user can act on, which is the reason
/// it returns a problem rather than a bool.</para>
/// </summary>
public sealed record SplitPairing(DeviceKind Input, DeviceKind Output)
{
    /// <summary>
    /// The pairing a document names, or null with <paramref name="problem"/> set.
    ///
    /// <para>Three pairings are refused and the reasons are unrelated. A <b>composite inside a
    /// composite</b> is meaningless. <b>Two sound cards</b> cannot both be pumped: everything
    /// PortAudio does runs on one thread, which <c>SoundCardDevice</c> occupies for a stream's
    /// whole life, so the second card's transfer loop would never be scheduled — and that bench
    /// already has a home under Sound card, which opens both directions in one stream. <b>Two
    /// QA40x analyzers</b> are one device opened twice: the transport claims the first match by
    /// product id, so the second would fail at claim time, which reads as hardware that needs a
    /// power cycle.</para>
    ///
    /// <para>Two simulators are deliberately allowed. It is the one pairing that exercises the whole
    /// composite — both stream lifecycles, the rate conversion, the withheld settle — at a desk with
    /// nothing plugged in.</para>
    /// </summary>
    public static SplitPairing? Resolve(SplitDeviceConfig config, out string? problem)
    {
        problem = null;

        if (config is null || !config.IsValid)
        {
            problem = "Those split-device settings are out of range.";
            return null;
        }

        if (ParseKind(config.InputKind) is not { } input)
        {
            problem = $"\"{config.InputKind}\" is not an analyzer this build knows.";
            return null;
        }

        if (ParseKind(config.OutputKind) is not { } output)
        {
            problem = $"\"{config.OutputKind}\" is not an analyzer this build knows.";
            return null;
        }

        if (input == DeviceKind.Split || output == DeviceKind.Split)
        {
            problem = "A split analyzer's halves have to be a QA40x, a sound card or the simulator.";
            return null;
        }

        if (input == DeviceKind.SoundCard && output == DeviceKind.SoundCard)
        {
            problem =
                "A split cannot have a sound card on both sides: everything PortAudio does runs on one "
                    + "thread, and the second card's transfer loop would never be scheduled. Choose Sound "
                    + "card instead, which opens both directions of a two-card bench in one stream.";
            return null;
        }

        if (input == DeviceKind.Hardware && output == DeviceKind.Hardware)
        {
            problem =
                "A split cannot have a QA40x on both sides: that is one device opened twice, and the "
                    + "second claim would fail. A QA40x already generates and captures on its own clock.";
            return null;
        }

        return new SplitPairing(input, output);
    }

    /// <summary>
    /// A stored kind name, or null. The name has to spell itself back, because <c>Enum.TryParse</c>
    /// also accepts a <i>number</i> — "1" parses happily as <see cref="DeviceKind.Simulated"/> —
    /// and storing names rather than numbers is the whole point: a document holding a number is not
    /// one this build wrote, so its number means nothing. The same rule
    /// <c>ConfigEndpoints.ReadAnalyzerKindAsync</c> applies to <c>device.analyzer</c>.
    /// </summary>
    private static DeviceKind? ParseKind(string name) =>
        Enum.TryParse(name, ignoreCase: true, out DeviceKind kind)
        && Enum.IsDefined(kind)
        && string.Equals(kind.ToString(), name, StringComparison.OrdinalIgnoreCase)
            ? kind
            : null;
}

/// <summary>
/// Which two devices a composite analyzer is made of, as stored in <c>device.split</c>: the live
/// copy <see cref="AnalyzerDeviceFactory"/> builds a split from.
///
/// <para>A mutable live copy for the reason <see cref="SoundCardSetting"/> and
/// <see cref="AnalyzerKindSetting"/> are: the factory is resolved lazily, after the startup block
/// has read the configuration database, so it can read this without the registration doing database
/// work. And like those two it has to be loaded <i>before</i> anything resolves
/// <see cref="DeviceSessionService"/> — see <see cref="StartupSettingsLoader"/>.</para>
/// </summary>
public sealed class SplitDeviceSetting
{
    private SplitDeviceConfig _config = SplitDeviceConfig.Default;
    private SplitPairing _pairing = SplitPairing.Resolve(SplitDeviceConfig.Default, out _)!;

    /// <summary>
    /// The stored document. One whose pairing this build cannot make falls back to the default
    /// rather than throwing, which is the rule every setting here follows — and here it is load
    /// bearing twice over: the session builds its first device in a field initializer, so a refusal
    /// would be a crash at startup rather than a message on the status bar.
    /// </summary>
    public SplitDeviceConfig Config
    {
        get => _config;
        set
        {
            if (SplitPairing.Resolve(value, out _) is { } pairing)
            {
                _config = value;
                _pairing = pairing;
            }
            else
            {
                _config = SplitDeviceConfig.Default;
                _pairing = SplitPairing.Resolve(SplitDeviceConfig.Default, out _)!;
            }
        }
    }

    /// <summary>The capturing half's kind.</summary>
    public DeviceKind InputKind => _pairing.Input;

    /// <summary>The generating half's kind.</summary>
    public DeviceKind OutputKind => _pairing.Output;

    /// <summary>The generator's rate, or 0 for "follow the analysis rate".</summary>
    public double GeneratorSampleRateHz => _config.GeneratorSampleRateHz;

    /// <summary>The device-layer settings for the composite, with each half named for a log line or a message.</summary>
    public SplitDeviceSettings DeviceSettings(string inputLabel, string outputLabel) => new()
    {
        GeneratorSampleRateHz = _config.GeneratorSampleRateHz,
        InputLabel = inputLabel,
        OutputLabel = outputLabel,
    };
}
