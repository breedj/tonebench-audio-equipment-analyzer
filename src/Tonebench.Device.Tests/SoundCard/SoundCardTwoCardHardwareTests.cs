using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.SoundCard;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.SoundCard;

/// <summary>
/// Two real cards streaming at once, each on a transfer thread of its own — the measurement that
/// gates building a two-card bench on top of <c>SplitAnalyzerDevice</c>.
///
/// <para><b>What only hardware can answer.</b> <see cref="SoundCardTwoCardStreamTests"/> pins the
/// routing — two devices, two dispatchers, both pumps making progress — against a fake that has no
/// apartment and no driver. What it cannot say is whether the native library tolerates a stream
/// being read and written from a second STA thread while another stream's loop occupies the first.
/// PortAudio is not documented thread-safe for concurrent library calls, which is why opening,
/// starting, stopping and closing all stay on the one thread that enumerated the devices; only
/// <c>Pa_ReadStream</c> and <c>Pa_WriteStream</c> move, and those are ring-buffer operations the
/// driver fills from its own callback thread. Plausible, and not proven until this passes.</para>
///
/// <para><b>One half may be ASIO, never both.</b> The ASIO SDK has no notion of more than one
/// driver open per process and PortAudio's backend carries that as a singleton, so an ASIO+ASIO
/// pair is refused however the threads are arranged. This looks for two cards that are not both
/// ASIO, which is also the only pairing a two-card bench can really have.</para>
///
/// <para><b>Read the counters, not the clock.</b> What says the mechanism works is that both pumps
/// delivered and neither reported a drop. A two-card bench's real failure — the ring slipping as
/// the two clocks diverge — takes about half an hour to appear at 100 ppm and 192 kHz, so a run
/// this short cannot see it and must not be read as having cleared it. That is the long bench run,
/// not this.</para>
/// </summary>
[Trait("Category", "Hardware")]
public class SoundCardTwoCardHardwareTests(ITestOutputHelper output)
{
    private const double RateHz = 48_000.0;
    private const int BlocksWanted = 200;

    [Fact]
    public async Task TwoCards_OnTheirOwnThreads_BothStreamAtOnce()
    {
        var pa = PortAudioNative.Shared;

        if (!TryFindAPair(pa, out SoundCardDeviceDescriptor? inputCard, out SoundCardDeviceDescriptor? outputCard))
            return;

        output.WriteLine(
            $"capturing on {inputCard!.HostApiName} / {inputCard.Name}, " +
            $"playing on {outputCard!.HostApiName} / {outputCard.Name} at {RateHz:0} Hz");

        SoundCardDevice capture = Build(pa, inputCard, outputCard: null);
        SoundCardDevice playback = Build(pa, inputCard: null, outputCard);

        try
        {
            await capture.ConnectAsync(CancellationToken.None);
            await playback.ConnectAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var config = new AcquisitionConfig(RateHz, 2);

            // The playing card runs for the length of the run. Output-only yields no blocks at all
            // — there is nothing to measure on a generator — so its evidence is that it held its
            // own thread and reported nothing wrong.
            Task playing = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (PcmBlock block in playback.StreamAsync(config, cts.Token))
                            block.Dispose();
                    }
                    catch (OperationCanceledException)
                    {
                        // The run is over; a playback-only stream only ever ends by being cancelled.
                    }
                },
                CancellationToken.None);

            int blocks = 0;
            var clock = Stopwatch.StartNew();

            await foreach (PcmBlock block in capture.StreamAsync(config, cts.Token))
            {
                block.Dispose();
                if (++blocks >= BlocksWanted)
                    break;
            }

            clock.Stop();
            await cts.CancelAsync();
            await playing;

            output.WriteLine(
                $"{blocks} blocks in {clock.Elapsed.TotalSeconds:0.00} s; " +
                $"capture dropped {capture.DroppedRxChunks} chunks / {capture.DroppedBlocks} blocks, " +
                $"playback dropped {playback.DroppedRxChunks} chunks / {playback.DroppedBlocks} blocks");

            // Both pumps ran: the capturing one delivered, and it was not starved behind the
            // playing one. Sharing a thread, the second device's loop is simply queued and this
            // produces nothing at all — no exception and no log line, which is why it is asserted
            // rather than eyeballed.
            Assert.Equal(BlocksWanted, blocks);
            Assert.Equal(0, capture.DroppedRxChunks);
            Assert.Equal(0, capture.DroppedBlocks);
        }
        finally
        {
            capture.Dispose();
            playback.Dispose();
        }
    }

    /// <summary>
    /// An input card and an output card that are not the same interface and not both ASIO, in the
    /// order the picker would prefer them. Skips quietly when this machine has no such pair, which
    /// is every machine with one interface.
    /// </summary>
    private bool TryFindAPair(
        IPortAudio pa,
        out SoundCardDeviceDescriptor? inputCard,
        out SoundCardDeviceDescriptor? outputCard)
    {
        var enumerator = new SoundCardEnumerator(pa);

        IEnumerable<SoundCardDeviceDescriptor> inputs = pa.Devices
            .Where(d => d.HasInput && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples)
            .OrderBy(d => SoundCardEnumerator.PreferenceOf(d.HostApiKind));

        foreach (SoundCardDeviceDescriptor candidateIn in inputs)
        {
            // Each side is probed on its own, which is the point of two one-direction streams: a
            // duplex probe across the two answers a question nothing is going to ask of them, and
            // for an ASIO pair it refuses outright.
            if (!enumerator.SupportedRatesHz(candidateIn, candidateIn, exclusive: true).Contains(RateHz))
                continue;

            SoundCardDeviceDescriptor? candidateOut = pa.Devices
                .Where(d => d.HasOutput
                    && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples
                    && !SameInterface(candidateIn, d)
                    && !(d.HostApiKind == SoundCardHostApiKind.Asio
                        && candidateIn.HostApiKind == SoundCardHostApiKind.Asio)
                    && enumerator.SupportedRatesHz(d, d, exclusive: true).Contains(RateHz))
                .OrderBy(d => SoundCardEnumerator.PreferenceOf(d.HostApiKind))
                .FirstOrDefault();

            if (candidateOut is null)
                continue;

            inputCard = candidateIn;
            outputCard = candidateOut;
            return true;
        }

        inputCard = null;
        outputCard = null;
        output.WriteLine(
            "no pair of trustworthy cards here that are different interfaces and not both ASIO — skipping");
        return false;
    }

    /// <summary>
    /// Whether the two are one physical interface, and therefore one clock — which is a one-card
    /// bench rather than the two-card one under test. <c>Unknown</c> counts as two: WDM-KS names
    /// carry no interface at all, so refusing it would rule out the host API this may need.
    /// </summary>
    private static bool SameInterface(SoundCardDeviceDescriptor input, SoundCardDeviceDescriptor output) =>
        SoundCardEnumerator.Pairing(input, output)
            is SoundCardPairing.FullDuplexDevice or SoundCardPairing.NamesMatch;

    private static SoundCardDevice Build(
        IPortAudio pa, SoundCardDeviceDescriptor? inputCard, SoundCardDeviceDescriptor? outputCard)
    {
        var settings = new SoundCardSettings
        {
            InputHostApi = inputCard?.HostApiName ?? string.Empty,
            InputDeviceName = inputCard?.Name ?? string.Empty,
            OutputHostApi = outputCard?.HostApiName ?? string.Empty,
            OutputDeviceName = outputCard?.Name ?? string.Empty,
        };

        // A transfer thread of its own: the thing under test.
        return new SoundCardDevice(
            settings,
            SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance,
            pa,
            ownStreamThread: true);
    }
}
