using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The two rates for the rate-per-card test, four to one on purpose: a build that silently
    /// opened both streams at one rate cannot land inside the band the measured rate is asserted
    /// against, whichever of the two rates it picked.
    /// </summary>
    private const double CaptureRateHz = 192_000.0;

    /// <summary>The other one. See <see cref="CaptureRateHz"/>.</summary>
    private const double PlaybackRateHz = 48_000.0;

    /// <summary>Minutes to soak for. <b>Unset means the soak does not run at all</b> — see below.</summary>
    private const string MinutesVariable = "TONEBENCH_TWOCARD_SOAK_MINUTES";

    private const string InputVariable = "TONEBENCH_TWOCARD_INPUT";
    private const string OutputVariable = "TONEBENCH_TWOCARD_OUTPUT";
    private const string RateVariable = "TONEBENCH_TWOCARD_RATE";
    private const string LogVariable = "TONEBENCH_TWOCARD_SOAK_LOG";

    /// <summary>Tried highest first, because the drift to be caught accumulates in frames per second.</summary>
    private static readonly double[] SoakRatesHz = [192_000.0, 96_000.0, 48_000.0];

    [Fact]
    public async Task TwoCards_OnTheirOwnThreads_BothStreamAtOnce()
    {
        var pa = PortAudioNative.Shared;

        if (!TryFindAPair(pa, RateHz, out SoundCardDeviceDescriptor? inputCard, out SoundCardDeviceDescriptor? outputCard))
            return;

        output.WriteLine(
            $"capturing on {inputCard!.HostApiName} / {inputCard.Name}, " +
            $"playing on {outputCard!.HostApiName} / {outputCard.Name} at {RateHz:0} Hz");

        SoundCardDevice capture = Build(pa, inputCard, outputCard: null, NullLogger<SoundCardDevice>.Instance);
        SoundCardDevice playback = Build(pa, inputCard: null, outputCard, NullLogger<SoundCardDevice>.Instance);

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
    /// <b>One rate per card</b> — the capability the duplex shape cannot express at all, because a
    /// single <c>Pa_OpenStream</c> call takes one sample rate for both of its directions. Two
    /// streams take one each, so a 192 kHz converter can be paired with one that stops at 96, and a
    /// generator can run at the rate that suits it while the analysis runs at the rate the
    /// measurement wants.
    ///
    /// <para><b>The measured capture rate is the assertion that carries this test.</b> Both pumps
    /// delivering says nothing on its own — a build that quietly opened both streams at one rate
    /// would deliver just as happily, and the only visible difference would be a generator playing
    /// at the wrong pitch, which no counter reports. So the frame rate is measured off the blocks'
    /// own timestamps and checked against the rate <i>this</i> card was asked for.</para>
    ///
    /// <para>The pair is found at the capture rate, so the playing card here happens to support
    /// both and is simply told to use the other one. That is the same demonstration: what is shown
    /// is two streams holding different rates at the same time, not that either card is limited.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoCards_AtARatePerCard_BothStreamAtOnce()
    {
        var pa = PortAudioNative.Shared;

        if (!TryFindAPair(pa, CaptureRateHz, out SoundCardDeviceDescriptor? inputCard, out SoundCardDeviceDescriptor? outputCard))
            return;

        // Both out parameters are non-null on a true return; taken into locals once so the rest of
        // the test reads them without a null-forgiving operator at every use.
        SoundCardDeviceDescriptor inCard = inputCard!;
        SoundCardDeviceDescriptor outCard = outputCard!;

        var enumerator = new SoundCardEnumerator(pa);
        if (!enumerator.SupportedRatesHz(null, outCard, exclusive: true).Contains(PlaybackRateHz))
        {
            output.WriteLine(
                $"[{outCard.HostApiName}] {outCard.Name} does not offer {PlaybackRateHz:0} Hz — skipping");
            return;
        }

        output.WriteLine(
            $"capturing on {inCard.HostApiName} / {inCard.Name} at {CaptureRateHz:0} Hz, " +
            $"playing on {outCard.HostApiName} / {outCard.Name} at {PlaybackRateHz:0} Hz");

        SoundCardDevice capture = Build(pa, inCard, outputCard: null, NullLogger<SoundCardDevice>.Instance);
        SoundCardDevice playback = Build(pa, inputCard: null, outCard, NullLogger<SoundCardDevice>.Instance);

        try
        {
            await capture.ConnectAsync(CancellationToken.None);
            await playback.ConnectAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            Task playing = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (PcmBlock block in playback.StreamAsync(
                            new AcquisitionConfig(PlaybackRateHz, 2), cts.Token))
                        {
                            block.Dispose();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The run is over; a playback-only stream only ever ends by being cancelled.
                    }
                },
                CancellationToken.None);

            int blocks = 0;
            long framesAfterTheFirst = 0, firstTicks = 0, lastTicks = 0;

            await foreach (PcmBlock block in capture.StreamAsync(new AcquisitionConfig(CaptureRateHz, 2), cts.Token))
            {
                // Both ends taken from the blocks' own timestamps and the first block's frames left
                // out, so the fixed distance the consumer runs behind the driver cancels -- the same
                // correction the soak carries, and for the same reason.
                if (blocks == 0)
                    firstTicks = block.TimestampTicks;
                else
                    framesAfterTheFirst += block.SampleCount / block.ChannelCount;

                lastTicks = block.TimestampTicks;
                block.Dispose();

                if (++blocks >= BlocksWanted)
                    break;
            }

            await cts.CancelAsync();
            await playing;

            double spanS = (lastTicks - firstTicks) / (double)Stopwatch.Frequency;
            double measuredHz = spanS > 0.0 ? framesAfterTheFirst / spanS : 0.0;

            output.WriteLine(
                $"{blocks} blocks, {framesAfterTheFirst} frames over {spanS:0.000} s; "
                + $"capture clock {measuredHz:0} Hz against {CaptureRateHz:0} asked; "
                + $"capture dropped {capture.DroppedRxChunks}/{capture.DroppedBlocks}, "
                + $"playback dropped {playback.DroppedRxChunks}/{playback.DroppedBlocks}");

            Assert.Equal(BlocksWanted, blocks);
            Assert.Equal(0, capture.DroppedRxChunks);
            Assert.Equal(0, capture.DroppedBlocks);
            Assert.Equal(0, playback.DroppedRxChunks);
            Assert.Equal(0, playback.DroppedBlocks);

            // Wide on purpose: the two rates are four to one, so what this has to settle is which
            // of them the capture ran at, not how many ppm it was out by.
            Assert.InRange(measuredHz, CaptureRateHz * 0.9, CaptureRateHz * 1.1);
        }
        finally
        {
            capture.Dispose();
            playback.Dispose();
        }
    }

    /// <summary>
    /// An input card and an output card that are not the same interface and not both ASIO, in the
    /// order the picker would prefer them. Skips when this machine has no such pair, which is
    /// every machine with one interface — saying which candidate failed which test, because "no
    /// pair" on a bench that plainly has two cards is otherwise a dead end.
    /// </summary>
    private bool TryFindAPair(
        IPortAudio pa,
        double rateHz,
        out SoundCardDeviceDescriptor? inputCard,
        out SoundCardDeviceDescriptor? outputCard)
    {
        var enumerator = new SoundCardEnumerator(pa);

        // The names are a seam for the soak below, so a long run can be pointed at the two cards
        // whose clocks are worth watching rather than at whatever the preference order reaches
        // first -- which on this machine is a laptop microphone array.
        string? wantIn = Environment.GetEnvironmentVariable(InputVariable);
        string? wantOut = Environment.GetEnvironmentVariable(OutputVariable);

        IEnumerable<SoundCardDeviceDescriptor> inputs = pa.Devices
            .Where(d => d.HasInput && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples)
            .Where(d => !SoundCardEnumerator.IsRenderLoopback(d.Name))
            .Where(d => Wanted(d, wantIn))
            .OrderBy(d => SoundCardEnumerator.PreferenceOf(d.HostApiKind));

        foreach (SoundCardDeviceDescriptor candidateIn in inputs)
        {
            // **Each side is probed in the direction it will actually be opened in**, which is the
            // point of two one-direction streams: a duplex probe across the two answers a question
            // nothing is going to ask of them, and for an ASIO pair it refuses outright.
            //
            // Passing one descriptor as *both* sides -- which this did until 2026-11-15 -- is a
            // duplex probe of a device against itself, and `SupportedRatesHz` reads both sides
            // being non-null as exactly that. On Windows only ASIO exposes a full-duplex device,
            // so every WASAPI and WDM-KS card reported **no rates at all**, and the only output
            // that could ever pass was an ASIO one. It read as "the DAC is unavailable" -- a
            // Topping D50 III that in fact offered 44.1k through 768k in exclusive mode -- and it
            // cost a bench reboot before the probe itself was suspected.
            if (!enumerator.SupportedRatesHz(candidateIn, null, exclusive: true).Contains(rateHz))
            {
                output.WriteLine($"  in  [{candidateIn.HostApiName}] {candidateIn.Name}: no {rateHz:0} Hz");
                continue;
            }

            foreach (SoundCardDeviceDescriptor other in pa.Devices.Where(d => d.HasOutput && Wanted(d, wantOut)))
            {
                string? why = SoundCardEnumerator.Trust(other.HostApiKind) == SoundCardHostApiTrust.Resamples
                        ? "resamples"
                    : SameInterface(candidateIn, other) ? "same interface as the input"
                    : other.HostApiKind == SoundCardHostApiKind.Asio
                        && candidateIn.HostApiKind == SoundCardHostApiKind.Asio ? "both halves would be ASIO"
                    : !enumerator.SupportedRatesHz(null, other, exclusive: true).Contains(rateHz)
                        ? $"no {rateHz:0} Hz"
                    : null;

                output.WriteLine(
                    $"  out [{other.HostApiName}] {other.Name}: {why ?? "usable"}"
                    + $" (against in [{candidateIn.HostApiName}] {candidateIn.Name})");
            }

            SoundCardDeviceDescriptor? candidateOut = pa.Devices
                .Where(d => d.HasOutput
                    && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples
                    && Wanted(d, wantOut)
                    && !SameInterface(candidateIn, d)
                    && !(d.HostApiKind == SoundCardHostApiKind.Asio
                        && candidateIn.HostApiKind == SoundCardHostApiKind.Asio)
                    && enumerator.SupportedRatesHz(null, d, exclusive: true).Contains(rateHz))
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
            $"no pair of trustworthy cards at {rateHz:0} Hz that are different interfaces and not both ASIO — skipping");
        return false;
    }

    /// <summary>Whether a device answers a name filter; no filter wants every device.</summary>
    private static bool Wanted(SoundCardDeviceDescriptor device, string? nameFragment) =>
        string.IsNullOrWhiteSpace(nameFragment)
        || device.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the two are one physical interface, and therefore one clock — which is a one-card
    /// bench rather than the two-card one under test.
    ///
    /// <para><b><see cref="SoundCardEnumerator.Pairing"/> alone cannot answer this, and that is
    /// structural rather than a gap.</b> It returns <c>Unknown</c> the moment the two host API
    /// indices differ — which is precisely the cross-API pairing this whole feature exists for —
    /// so asking it on its own calls every such pair two interfaces. Measured: it put WASAPI
    /// "Loopback (MOTU M Series)" against ASIO "MOTU M Series", one card answering itself, which a
    /// soak would then find beautifully glitch-free for ninety minutes while measuring one clock
    /// against itself.</para>
    ///
    /// <para>So the stems are compared directly, falling back to the whole name for the ASIO
    /// convention of naming the interface and nothing else. It is a heuristic and it only has to
    /// be conservative in one direction: calling two interfaces one skips a usable pair, where the
    /// reverse produces a measurement that cannot fail.</para>
    /// </summary>
    private static bool SameInterface(SoundCardDeviceDescriptor input, SoundCardDeviceDescriptor output)
    {
        if (SoundCardEnumerator.Pairing(input, output)
            is SoundCardPairing.FullDuplexDevice or SoundCardPairing.NamesMatch)
            return true;

        return string.Equals(InterfaceOf(input), InterfaceOf(output), StringComparison.OrdinalIgnoreCase);

        // "In 1-2 (MOTU M Series)" names its interface in the brackets; ASIO's "MOTU M Series" is
        // the interface already and has none.
        static string InterfaceOf(SoundCardDeviceDescriptor device) =>
            SoundCardEnumerator.InterfaceStem(device.Name) is { Length: > 0 } stem ? stem : device.Name;
    }

    private static SoundCardDevice Build(
        IPortAudio pa,
        SoundCardDeviceDescriptor? inputCard,
        SoundCardDeviceDescriptor? outputCard,
        ILogger<SoundCardDevice> logger)
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
            logger,
            pa,
            ownStreamThread: true);
    }

    /// <summary>
    /// Two cards streaming for an hour and a half, which is the one measurement that says the
    /// mechanism is fixed rather than merely working.
    ///
    /// <para><b>Why this long.</b> What a two-card bench fails at is not starting — it is running.
    /// One duplex stream spanning two devices reads on one card's clock and writes on the other's,
    /// so their difference accumulates into the ring at the ppm rate: ~19.2 frames/s at 100 ppm and
    /// 192 kHz, which eats a 180 ms ring in about half an hour and then glitches on that schedule
    /// for ever. Two one-direction streams on two threads each free-run on their own clock and have
    /// nothing to accumulate <i>into</i>. A short run cannot tell those two apart — both are clean
    /// for the first twenty minutes — so ninety is the figure: past three of the old shape's
    /// glitches, and long enough that a clean result means something.</para>
    ///
    /// <para><b>Opt-in beyond the Hardware category</b>, because <c>--filter "Category=Hardware"</c>
    /// is a documented command and a ninety-minute member of that suite is a trap. Set
    /// <c>TONEBENCH_TWOCARD_SOAK_MINUTES</c> to run it, and that figure is the duration. Pin the
    /// pair with <c>TONEBENCH_TWOCARD_INPUT</c> / <c>_OUTPUT</c> (a fragment of the device name),
    /// the rate with <c>TONEBENCH_TWOCARD_RATE</c>, and the running log with
    /// <c>TONEBENCH_TWOCARD_SOAK_LOG</c>; without the two names the preference order picks, which
    /// on a laptop is likely to reach a built-in microphone array before anything worth soaking.
    /// </para>
    ///
    /// <para><b>What is evidence here.</b> The drop counters on both sides, and — the sharper
    /// signal — a block flagged <see cref="PcmBlock.StartsAfterDiscontinuity"/> after the stream
    /// has settled, which is how a read timeout, an input overflow or an output underflow reaches
    /// the analyzer. The playing card hands out no blocks at all, so its own glitches can only be
    /// counted through its log, which is why both halves get a real logger rather than a null one.
    /// The measured frame rate against the host clock is reported for each card but not asserted:
    /// it is the ppm figure that says how fast the <i>old</i> shape would have slipped.</para>
    /// </summary>
    [Fact]
    public async Task TwoCards_SoakedForAnHourAndAHalf_NeitherStreamGlitches()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(MinutesVariable), out int minutes) || minutes <= 0)
        {
            output.WriteLine($"{MinutesVariable} is not set to a positive number of minutes — skipping the soak");
            return;
        }

        var pa = PortAudioNative.Shared;
        ListDevices(pa);

        double[] rates = double.TryParse(Environment.GetEnvironmentVariable(RateVariable), out double pinned)
            ? [pinned]
            : SoakRatesHz;

        SoundCardDeviceDescriptor? inputCard = null, outputCard = null;
        double rateHz = 0.0;

        foreach (double candidate in rates)
        {
            if (TryFindAPair(pa, candidate, out inputCard, out outputCard))
            {
                rateHz = candidate;
                break;
            }
        }

        if (inputCard is null || outputCard is null)
            return;

        string logPath = Environment.GetEnvironmentVariable(LogVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Path.GetTempPath(), $"tonebench-twocard-soak-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        await using var file = new StreamWriter(logPath, append: true) { AutoFlush = true };
        var gate = new Lock();
        var captureLog = new SoakLogger("capture ", file, gate);
        var playbackLog = new SoakLogger("playback", file, gate);

        void Say(string line)
        {
            output.WriteLine(line);
            lock (gate)
                file.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        }

        Say($"soaking for {minutes} min at {rateHz:0} Hz");
        Say($"  capturing on {inputCard.HostApiName} / {inputCard.Name}");
        Say($"  playing on   {outputCard.HostApiName} / {outputCard.Name}");

        SoundCardDevice capture = Build(pa, inputCard, outputCard: null, captureLog);
        SoundCardDevice playback = Build(pa, inputCard: null, outputCard, playbackLog);

        try
        {
            await capture.ConnectAsync(CancellationToken.None);
            await playback.ConnectAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var config = new AcquisitionConfig(rateHz, 2);

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
                        // The soak is over; a playback-only stream only ever ends by being cancelled.
                    }
                },
                CancellationToken.None);

            var deadline = TimeSpan.FromMinutes(minutes);
            long blocks = 0, framesAfterTheFirst = 0, discontinuities = 0, sequenceGaps = 0;
            long previousSequence = -1;
            long firstTicks = 0, lastTicks = 0;
            TimeSpan nextReport = TimeSpan.FromMinutes(1);
            var wall = Stopwatch.StartNew();

            await foreach (PcmBlock block in capture.StreamAsync(config, CancellationToken.None))
            {
                // **Between the first block and the last, counting neither the first block's own
                // frames nor the wall clock.** The consumer runs a fixed distance behind the
                // driver -- one input ring, 0.68 s here -- and frames sitting in that ring when
                // the deadline fires are captured and never counted. Measured against the wall
                // clock that read -11694 ppm over a minute, which is 1.2 % and is the ring, not a
                // converter. Taking both ends from the blocks' own timestamps cancels it, because
                // the offset is the same at both.
                if (blocks == 0)
                    firstTicks = block.TimestampTicks;
                else
                    framesAfterTheFirst += block.SampleCount / block.ChannelCount;

                lastTicks = block.TimestampTicks;
                blocks++;

                // The first block after a settle is flagged by construction; any later one is a
                // hole, which is the whole question being asked here.
                if (block.StartsAfterDiscontinuity && blocks > 1)
                {
                    discontinuities++;
                    Say($"  discontinuity at block {block.SequenceNumber}, {wall.Elapsed.TotalMinutes:0.0} min in");
                }

                if (previousSequence >= 0 && block.SequenceNumber != previousSequence + 1)
                    sequenceGaps++;

                previousSequence = block.SequenceNumber;
                block.Dispose();

                if (wall.Elapsed >= nextReport)
                {
                    nextReport += TimeSpan.FromMinutes(1);
                    Say(
                        $"  {wall.Elapsed.TotalMinutes,5:0.0} min: {blocks} blocks, "
                        + $"capture dropped {capture.DroppedRxChunks}/{capture.DroppedBlocks}, "
                        + $"playback dropped {playback.DroppedRxChunks}/{playback.DroppedBlocks}, "
                        + $"glitches logged {captureLog.Glitches} / {playbackLog.Glitches}");
                }

                if (wall.Elapsed >= deadline)
                    break;
            }

            wall.Stop();
            await cts.CancelAsync();
            await playing;

            double spanS = (lastTicks - firstTicks) / (double)Stopwatch.Frequency;
            double measuredHz = spanS > 0.0 ? framesAfterTheFirst / spanS : 0.0;
            Say(
                $"done: {wall.Elapsed.TotalMinutes:0.0} min, {blocks} blocks, {framesAfterTheFirst} frames "
                + $"over {spanS:0.0} s; capture clock {measuredHz:0.00} Hz against {rateHz:0} asked "
                + $"({(measuredHz / rateHz - 1.0) * 1e6:+0;-0} ppm against this host's clock)");
            Say(
                $"capture dropped {capture.DroppedRxChunks} chunks / {capture.DroppedBlocks} blocks, "
                + $"playback dropped {playback.DroppedRxChunks} chunks / {playback.DroppedBlocks} blocks; "
                + $"glitches logged {captureLog.Glitches} capture / {playbackLog.Glitches} playback; "
                + $"{discontinuities} discontinuities, {sequenceGaps} sequence gaps");
            Say($"log: {logPath}");

            // It really did run: a stream that ended early, or stalled and was carried by the
            // deadline alone, must not read as a clean ninety minutes.
            Assert.True(
                wall.Elapsed >= deadline - TimeSpan.FromSeconds(30),
                $"the capture stream ended after {wall.Elapsed.TotalMinutes:0.0} min of {minutes}");
            Assert.True(
                measuredHz > rateHz * 0.98,
                $"the capturing card delivered {measuredHz:0} Hz against {rateHz:0} asked for");

            Assert.Equal(0, capture.DroppedRxChunks);
            Assert.Equal(0, capture.DroppedBlocks);
            Assert.Equal(0, playback.DroppedRxChunks);
            Assert.Equal(0, playback.DroppedBlocks);
            Assert.Equal(0, discontinuities);
            Assert.Equal(0, sequenceGaps);
            Assert.Equal(0, captureLog.Glitches + playbackLog.Glitches);
        }
        finally
        {
            capture.Dispose();
            playback.Dispose();
        }
    }

    /// <summary>Every device this machine offers, so a skip or a surprising pick explains itself.</summary>
    private void ListDevices(IPortAudio pa)
    {
        foreach (SoundCardDeviceDescriptor device in pa.Devices)
        {
            output.WriteLine(
                $"  [{device.HostApiName}] {device.Name} — in {(device.HasInput ? "yes" : "no")}, "
                + $"out {(device.HasOutput ? "yes" : "no")}, trust {SoundCardEnumerator.Trust(device.HostApiKind)}");
        }
    }

    /// <summary>
    /// Writes the device's log to a file as the soak runs, and counts glitch lines.
    ///
    /// <para>A null logger would throw away the only evidence the <i>playing</i> card can give:
    /// it hands out no blocks, so its read timeouts, overflows and underflows reach nobody except
    /// through here. Counted on the structured <c>Cause</c> property rather than by matching the
    /// message, and at every level, because a stream glitching steadily drops to Debug after the
    /// first few of each cause — which is exactly the run that most needs counting.</para>
    /// </summary>
    private sealed class SoakLogger(string label, TextWriter file, Lock gate) : ILogger<SoundCardDevice>
    {
        /// <summary>Past this many written out, a glitch is still counted but no longer printed.</summary>
        private const int LinesPerRun = 200;

        private int _glitches;
        private int _written;

        public int Glitches => Volatile.Read(ref _glitches);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            bool glitch = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                && pairs.Any(pair => pair.Key == "Cause");

            if (glitch && Interlocked.Increment(ref _glitches) > LinesPerRun)
                return;

            if (Interlocked.Increment(ref _written) > LinesPerRun * 4)
                return;

            lock (gate)
            {
                file.WriteLine($"{DateTime.Now:HH:mm:ss} {label} {logLevel}: {formatter(state, exception)}");
                if (exception is not null)
                    file.WriteLine(exception.ToString());
            }
        }
    }
}
