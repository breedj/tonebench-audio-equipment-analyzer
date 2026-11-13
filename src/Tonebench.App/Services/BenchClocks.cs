using Tonebench.Data;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Services;

/// <summary>
/// A stable identity for one half of a bench, which is what a measured clock difference is stored
/// against.
///
/// <para><b>A pure function of the configuration, never of a connected device.</b> The response
/// screens ask <c>GET /api/device/ranges</c> while nothing is connected, so the answer has to exist
/// before anything opens — which is exactly why the sound card's own stamp stored the configured
/// device names rather than an identity read back from the hardware.</para>
/// </summary>
public static class BenchClockKey
{
    /// <summary>The key for the half of this bench that captures (<paramref name="asInput"/>) or plays.</summary>
    /// <remarks>
    /// <para><b>A card is keyed by its card, not its endpoint</b>, through
    /// <see cref="SoundCardCatalog.CardOf"/> — the same stem rule the picker groups by and the one
    /// <c>ClockDriftCorrection.UsesTwoCards</c> compares. One interface shows up as several
    /// endpoints with different names (<c>In 3-4 (MOTU M Series)</c> against
    /// <c>Out 3-4 (MOTU M Series)</c>), and a crystal belongs to the whole device: keying on the
    /// endpoint would throw a figure away every time a channel pair changed. That is a deliberate
    /// <i>fix</i> to the four-field stamp this replaces, whose own remarks said a crystal belongs
    /// to a device while its comparison used endpoint names.</para>
    ///
    /// <para><b>A QA40x carries no serial</b>, although one would be more precise, because two of
    /// them is not an openable bench — <c>Qa40xTransport.Open</c> claims the first match by product
    /// id — and a serial is not available until something has connected, which this must not
    /// require.</para>
    /// </remarks>
    public static string For(DeviceKind kind, SoundCardConfig card, bool asInput) => kind switch
    {
        DeviceKind.SoundCard => CardKey(card, asInput),
        DeviceKind.Simulated => "sim",
        _ => "qa40x",
    };

    /// <summary>What to call that half on screen. Display only — never compared.</summary>
    public static string LabelFor(DeviceKind kind, SoundCardConfig card, bool asInput) => kind switch
    {
        DeviceKind.SoundCard => Named(asInput ? card.InputDeviceName : card.OutputDeviceName, asInput ? "the input card" : "the output card"),
        DeviceKind.Simulated => "the simulator",
        _ => "the QA40x",
    };

    private static string CardKey(SoundCardConfig card, bool asInput)
    {
        string hostApi = asInput ? card.InputHostApi : card.OutputHostApi;
        string device = asInput ? card.InputDeviceName : card.OutputDeviceName;
        return $"card:{hostApi}/{SoundCardCatalog.CardOf(device)}";
    }

    private static string Named(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}

/// <summary>
/// The two clocks a bench is running on, or null where it has one.
///
/// <para>This is the question every part of the clock-drift feature actually asks — whether there
/// are two crystals, and which two — and it is asked in three places that have to agree: what the
/// response screens are offered, what the drift test stores, and what the dialog says it measured
/// between.</para>
/// </summary>
public sealed record BenchClocks(string OutputKey, string InputKey, string OutputLabel, string InputLabel)
{
    /// <summary>
    /// The pairing this bench is, or null when one device both plays and captures.
    ///
    /// <para>Null for a QA40x or the simulator alone, for a sound card whose two sides are one card
    /// (<see cref="ClockDriftCorrection.UsesTwoCards"/>, unchanged), and for a split whose two
    /// halves resolve to the same device — two simulators being the case that catches, where the
    /// difference measured would be a model's against itself.</para>
    /// </summary>
    public static BenchClocks? For(DeviceKind kind, SoundCardConfig card, SplitDeviceSetting split)
    {
        if (kind == DeviceKind.Split)
            return Between(split.OutputKind, split.InputKind, card);

        if (kind != DeviceKind.SoundCard || !ClockDriftCorrection.UsesTwoCards(card))
            return null;

        return Between(DeviceKind.SoundCard, DeviceKind.SoundCard, card);
    }

    private static BenchClocks? Between(DeviceKind outputKind, DeviceKind inputKind, SoundCardConfig card)
    {
        string outputKey = BenchClockKey.For(outputKind, card, asInput: false);
        string inputKey = BenchClockKey.For(inputKind, card, asInput: true);

        if (string.Equals(outputKey, inputKey, StringComparison.OrdinalIgnoreCase))
            return null;

        return new BenchClocks(
            outputKey,
            inputKey,
            BenchClockKey.LabelFor(outputKind, card, asInput: false),
            BenchClockKey.LabelFor(inputKind, card, asInput: true));
    }
}

/// <summary>
/// The measured clock differences, as the app holds them in memory: the live copy of
/// <c>device.clockDrift</c>.
///
/// <para>Not order-sensitive in <see cref="StartupSettingsLoader"/> — nothing builds a device from
/// it — but loaded in the same block for the reason that method's own remarks give: the rule there
/// is an order rather than a case-by-case judgement.</para>
/// </summary>
public sealed class ClockDriftSetting
{
    public ClockDriftConfig Config { get; set; } = ClockDriftConfig.Default;
}
