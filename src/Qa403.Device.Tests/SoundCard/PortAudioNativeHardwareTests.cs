using Qa403.Device.SoundCard;
using Xunit.Abstractions;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// The real library, through the real interop. Tagged Hardware and excluded by default — it needs
/// a machine with a sound card, and `dotnet test --filter "Category!=Hardware"` must stay green on
/// one without.
///
/// <para>These exist because the marshalling is the part no fake can check. Two details in
/// <see cref="PortAudioInterop"/> are invisible until a real call is made: <c>PaSampleFormat</c>
/// and <c>PaStreamFlags</c> are C <c>unsigned long</c>, which is four bytes on Windows and eight on
/// Linux (so <c>CULong</c>, not <c>uint</c>), and <c>PaWasapiStreamInfo</c> has to be exactly the
/// 72 bytes PortAudio checks.</para>
///
/// <para>Every test here takes <see cref="PortAudioNative.Shared"/> rather than building its own.
/// That is not tidiness: with ASIO compiled in, a second <c>Pa_Initialize</c> after a
/// <c>Pa_Terminate</c> faults with an access violation and takes the test host with it, so a
/// per-test instance turns the whole class into one aborted run.</para>
/// </summary>
[Trait("Category", "Hardware")]
public class PortAudioNativeHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void Initialise_LoadsTheVendoredLibrary_AndReportsItsVersion()
    {
        var pa = PortAudioNative.Shared;

        output.WriteLine(pa.VersionText);
        Assert.StartsWith("PortAudio", pa.VersionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enumerate_FindsHostApisAndDevices()
    {
        var pa = PortAudioNative.Shared;

        Assert.NotEmpty(pa.HostApis);
        Assert.NotEmpty(pa.Devices);

        foreach (var api in pa.HostApis)
            output.WriteLine($"[{api.Index}] {api.Name} ({api.Kind}, trust {SoundCardEnumerator.Trust(api.Kind)})");

        foreach (var d in pa.Devices)
            output.WriteLine($"  [{d.Index,2}] {d.HostApiName,-20} {d.Name,-45} in {d.MaxInputChannels} out {d.MaxOutputChannels}");
    }

    /// <summary>
    /// The finding the pairing design rests on. This is an observation, not a requirement — a
    /// machine with an ASIO driver installed would have one — so it only reports, and asserts the
    /// weaker thing that matters: both directions are reachable somehow.
    /// </summary>
    [Fact]
    public void OnThisMachine_BothDirectionsExist_EvenIfNoSingleDeviceCarriesBoth()
    {
        var pa = PortAudioNative.Shared;
        var enumerator = new SoundCardEnumerator(pa);

        var duplex = pa.Devices.Where(d => d.IsFullDuplex).ToArray();
        output.WriteLine($"full-duplex devices: {duplex.Length}");
        foreach (var d in duplex)
            output.WriteLine($"  {d.HostApiName} / {d.Name}");

        Assert.NotEmpty(enumerator.InputDevices);
        Assert.NotEmpty(enumerator.OutputDevices);
    }

    /// <summary>
    /// The measured WASAPI difference, as a test: shared mode is pinned to the Windows mixer's rate
    /// while exclusive reaches the converter's own. Skipped rather than failed when the machine has
    /// no WASAPI pair to try, so this stays runnable on Linux.
    /// </summary>
    [Fact]
    public void UnderWasapi_ExclusiveModeReachesRatesSharedModeRefuses()
    {
        var pa = PortAudioNative.Shared;
        var enumerator = new SoundCardEnumerator(pa);

        var inputDevice = pa.Devices.FirstOrDefault(d => d.HostApiKind == SoundCardHostApiKind.Wasapi && d.HasInput);
        var outputDevice = pa.Devices.FirstOrDefault(d => d.HostApiKind == SoundCardHostApiKind.Wasapi && d.HasOutput);
        if (inputDevice is null || outputDevice is null)
        {
            output.WriteLine("no WASAPI pair on this machine");
            return;
        }

        var shared = enumerator.SupportedRatesHz(inputDevice, outputDevice, exclusive: false);
        var exclusive = enumerator.SupportedRatesHz(inputDevice, outputDevice, exclusive: true);

        output.WriteLine($"input  : {inputDevice.Name}");
        output.WriteLine($"output : {outputDevice.Name}");
        output.WriteLine($"shared   : {string.Join(", ", shared)}");
        output.WriteLine($"exclusive: {string.Join(", ", exclusive)}");

        // Exclusive can only ever offer more: it is the same hardware without the mixer in front.
        Assert.True(
            shared.All(exclusive.Contains) || exclusive.Count >= shared.Count,
            "Exclusive mode offered fewer rates than shared, which would mean the descriptor is being ignored.");
    }

    /// <summary>
    /// Opens a real duplex stream across two endpoints of one interface and reads its reported
    /// latency — the figure <c>StimulusLatencyFrames</c> is derived from, and the one the project's
    /// own history says must be measured rather than believed.
    /// </summary>
    [Fact]
    public void ADuplexStreamAcrossOneInterface_OpensAndReportsItsLatency()
    {
        var pa = PortAudioNative.Shared;
        var enumerator = new SoundCardEnumerator(pa);

        var pair = (from i in enumerator.InputDevices
                    from o in enumerator.OutputDevices
                    where SoundCardEnumerator.Pairing(i, o) != SoundCardPairing.Unknown
                       && SoundCardEnumerator.Trust(i.HostApiKind) != SoundCardHostApiTrust.Resamples
                    select (Input: i, Output: o)).FirstOrDefault();

        if (pair.Input is null || pair.Output is null)
        {
            output.WriteLine("no trustworthy same-interface pair on this machine");
            return;
        }

        var rates = enumerator.SupportedRatesHz(pair.Input, pair.Output, exclusive: true);
        if (rates.Count == 0)
        {
            output.WriteLine($"{pair.Input.Name} + {pair.Output.Name}: none of 48/96/192 kHz");
            return;
        }

        var request = new SoundCardStreamRequest(
            pair.Input.Index, 2, pair.Output.Index, 2, rates[0], Exclusive: true);

        using var stream = pa.OpenStream(request, framesPerBuffer: 1024);

        output.WriteLine($"{pair.Input.Name} + {pair.Output.Name} @ {stream.SampleRateHz:0} Hz");
        output.WriteLine($"  input latency  {stream.InputLatencyS * 1000:0.0} ms");
        output.WriteLine($"  output latency {stream.OutputLatencyS * 1000:0.0} ms");

        Assert.Equal(rates[0], stream.SampleRateHz);
        Assert.True(stream.OutputLatencyS > 0.0, "PortAudio reported no output latency, which StimulusLatencyFrames depends on.");
    }
}
