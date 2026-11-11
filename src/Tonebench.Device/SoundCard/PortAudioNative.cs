using System.Runtime.InteropServices;

namespace Tonebench.Device.SoundCard;

/// <summary>Thrown for any PortAudio error that is not one of the two non-fatal transfer conditions.</summary>
public sealed class PortAudioException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// <see cref="IPortAudio"/> over the real library. Owns the one <c>Pa_Initialize</c>/
/// <c>Pa_Terminate</c> pair, and - the part that is not optional - makes every native call on a
/// single STA thread. See <see cref="StaDispatcher"/> for what happens without one: no ASIO
/// devices at all, and an access violation on the second initialise.
/// </summary>
internal sealed class PortAudioNative : IPortAudio
{
    private readonly StaDispatcher _dispatcher = new("PortAudio");
    private SoundCardHostApi[] _hostApis = [];
    private SoundCardDeviceDescriptor[] _devices = [];

    private static readonly Lock SharedGate = new();
    private static PortAudioNative? _shared;

    /// <summary>
    /// The one instance for the process, created on first use and never torn down.
    ///
    /// <para><b>PortAudio is initialised exactly once here, deliberately.</b> Measured 2026-09-30
    /// with ASIO compiled in: a <c>Pa_Terminate</c> followed by another <c>Pa_Initialize</c> faults
    /// with an access violation that takes the whole process down, on a fresh STA thread as much as
    /// on the original one. Real ASIO hosts initialise once for the same reason, and the app has no
    /// need to do otherwise: <c>DeviceSessionService</c> builds and disposes a device on every
    /// analyzer switch, and without this a QA403 to sound card and back would be a crash.</para>
    /// </summary>
    public static PortAudioNative Shared
    {
        get
        {
            lock (SharedGate)
            {
                return _shared ??= new PortAudioNative();
            }
        }
    }

    private PortAudioNative()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                Initialise();
                ReadTopology();
            });
        }
        catch
        {
            _dispatcher.Dispose();
            throw;
        }
    }

    /// <summary>The thread every native call runs on, so a stream can run its loop there too.</summary>
    public StaDispatcher Dispatcher => _dispatcher;

    public string VersionText { get; private set; } = string.Empty;

    public IReadOnlyList<SoundCardHostApi> HostApis => _hostApis;

    public IReadOnlyList<SoundCardDeviceDescriptor> Devices => _devices;

    /// <summary>
    /// PortAudio reads the device list once, at initialise, so a card plugged in afterwards is
    /// invisible until the library is torn down and brought back up. That is what the dialog's
    /// Refresh does.
    /// </summary>
    /// <summary>
    /// Re-reads the device table. <b>It cannot pick up a card plugged in since start-up</b>:
    /// PortAudio caches the list at <c>Pa_Initialize</c>, and the only way to rebuild it -
    /// terminate and initialise again - crashes the process once ASIO is compiled in (see
    /// <see cref="Shared"/>). WASAPI offers its own rescan and that is used where it exists; for
    /// ASIO the app has to be restarted, which is what every other ASIO host asks for too.
    /// </summary>
    public void Refresh() => _dispatcher.Invoke(() =>
    {
        if (PortAudioInterop.Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId.Wasapi) >= 0)
            PortAudioInterop.PaWasapi_UpdateDeviceList();

        ReadTopology();
    });

    public bool IsFormatSupported(SoundCardStreamRequest request) => _dispatcher.Invoke(() =>
    {
        IntPtr inputExtra = IntPtr.Zero, outputExtra = IntPtr.Zero;
        IntPtr inputBlock = IntPtr.Zero, outputBlock = IntPtr.Zero;

        try
        {
            inputBlock = AllocParameters(request, output: false, out inputExtra);
            outputBlock = AllocParameters(request, output: true, out outputExtra);

            return PortAudioInterop.Pa_IsFormatSupported(inputBlock, outputBlock, request.SampleRateHz)
                == PaError.NoError;
        }
        finally
        {
            Free(inputBlock);
            Free(outputBlock);
            Free(inputExtra);
            Free(outputExtra);
        }
    });

    public IPortAudioStream OpenStream(SoundCardStreamRequest request, int framesPerBuffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerBuffer);
        return _dispatcher.Invoke<IPortAudioStream>(() => OpenStreamCore(request, framesPerBuffer));
    }

    public bool ShowControlPanel(int deviceIndex)
    {
        // On the dispatcher, like everything else: the panel is a COM dialog belonging to the
        // driver, and it has to be raised from the apartment the driver was initialised in.
        return _dispatcher.Invoke(() =>
        {
            IntPtr owner = OperatingSystem.IsWindows() ? PortAudioInterop.GetDesktopWindow() : IntPtr.Zero;

            int error;
            try
            {
                error = PortAudioInterop.PaAsio_ShowControlPanel(deviceIndex, owner);
            }
            catch (EntryPointNotFoundException)
            {
                // A PortAudio built without ASIO does not export this symbol at all, so the
                // P/Invoke fails before any PaError can come back — the two codes handled below
                // cover "no ASIO host API in a build that has the function", not "no function".
                // Every non-Windows build is in that position (Homebrew's and the distros' are
                // compiled without the ASIO SDK). Unreachable today, because
                // SoundCardCatalog.ShowControlPanel short-circuits on HostApiKind == Asio and no
                // such device can exist in those builds; kept so the next caller of this method
                // does not have to know that.
                return false;
            }

            if (error == PaError.NoError)
                return true;

            // No panel here, which is not a fault: the button is offered only for ASIO, so
            // reaching this means the device list moved underneath the dialog. Both codes are
            // checked because they mean the same thing from two different places -- the device
            // belongs to another host API, or this build has no ASIO at all.
            if (error is PaError.InvalidDevice or PaError.HostApiNotFound)
                return false;

            throw new PortAudioException(
                $"The card's control panel could not be opened: {PortAudioInterop.ErrorText(error)}", error);
        });
    }

    private IPortAudioStream OpenStreamCore(SoundCardStreamRequest request, int framesPerBuffer)
    {
        IntPtr inputExtra = IntPtr.Zero, outputExtra = IntPtr.Zero;
        IntPtr inputBlock = IntPtr.Zero, outputBlock = IntPtr.Zero;

        // The two allocations have different lifetimes and the distinction is load-bearing. The
        // parameter blocks are read during Pa_OpenStream and are the caller's to free the moment it
        // returns; the WASAPI descriptors are not, and ownership of those passes to the stream.
        // Until the stream exists to own them, a throw on either path has to free them here, which
        // the pre-2026-11-11 shape did only for the error return and not for a throw out of the
        // second AllocParameters.
        bool handedOver = false;

        try
        {
            inputBlock = AllocParameters(request, output: false, out inputExtra);
            outputBlock = AllocParameters(request, output: true, out outputExtra);

            int error = PortAudioInterop.Pa_OpenStream(
                out IntPtr handle,
                inputBlock,
                outputBlock,
                request.SampleRateHz,
                new CULong((nuint)framesPerBuffer),
                PortAudioInterop.StreamFlagsDitherOff,
                IntPtr.Zero,
                IntPtr.Zero);

            if (error != PaError.NoError)
            {
                throw new PortAudioException(
                    $"Could not open the audio stream at {request.SampleRateHz:0} Hz: {PortAudioInterop.ErrorText(error)}",
                    error);
            }

            var stream = new NativeStream(handle, inputExtra, outputExtra, _dispatcher);
            handedOver = true;
            return stream;
        }
        finally
        {
            Free(inputBlock);
            Free(outputBlock);

            if (!handedOver)
            {
                Free(inputExtra);
                Free(outputExtra);
            }
        }
    }

    /// <summary>
    /// Deliberately does nothing. The library is initialised once per process and never terminated
    /// (see <see cref="Shared"/>), so there is nothing here to release - an open stream is closed by
    /// its own Dispose. This exists only to satisfy <see cref="IPortAudio"/>, whose fake
    /// implementation in the tests does have state to drop.
    /// </summary>
    public void Dispose()
    {
    }

    private void Initialise()
    {
        int error = PortAudioInterop.Pa_Initialize();
        if (error != PaError.NoError)
            throw new PortAudioException($"PortAudio could not start: {PortAudioInterop.ErrorText(error)}", error);

        VersionText = PortAudioInterop.ReadUtf8(PortAudioInterop.Pa_GetVersionText());
    }

    private void ReadTopology()
    {
        int apiCount = Math.Max(0, PortAudioInterop.Pa_GetHostApiCount());
        var apis = new SoundCardHostApi[apiCount];
        var kinds = new SoundCardHostApiKind[apiCount];
        var names = new string[apiCount];

        for (int i = 0; i < apiCount; i++)
        {
            IntPtr p = PortAudioInterop.Pa_GetHostApiInfo(i);
            if (p == IntPtr.Zero)
            {
                apis[i] = new SoundCardHostApi(i, $"host API {i}", SoundCardHostApiKind.Other);
                names[i] = apis[i].Name;
                continue;
            }

            var info = Marshal.PtrToStructure<PortAudioInterop.PaHostApiInfo>(p);
            names[i] = PortAudioInterop.ReadUtf8(info.Name);
            kinds[i] = KindOf(info.Type);
            apis[i] = new SoundCardHostApi(i, names[i], kinds[i]);
        }

        int deviceCount = Math.Max(0, PortAudioInterop.Pa_GetDeviceCount());
        var devices = new List<SoundCardDeviceDescriptor>(deviceCount);

        for (int d = 0; d < deviceCount; d++)
        {
            IntPtr p = PortAudioInterop.Pa_GetDeviceInfo(d);
            if (p == IntPtr.Zero)
                continue;

            var info = Marshal.PtrToStructure<PortAudioInterop.PaDeviceInfo>(p);
            int api = info.HostApi;
            devices.Add(new SoundCardDeviceDescriptor(
                Index: d,
                Name: PortAudioInterop.ReadUtf8(info.Name),
                HostApiIndex: api,
                HostApiName: (uint)api < (uint)apiCount ? names[api] : $"host API {api}",
                HostApiKind: (uint)api < (uint)apiCount ? kinds[api] : SoundCardHostApiKind.Other,
                MaxInputChannels: info.MaxInputChannels,
                MaxOutputChannels: info.MaxOutputChannels,
                DefaultLowInputLatencyS: info.DefaultLowInputLatency,
                DefaultLowOutputLatencyS: info.DefaultLowOutputLatency,
                DefaultSampleRateHz: info.DefaultSampleRate));
        }

        _hostApis = apis;
        _devices = [.. devices];
    }

    private static SoundCardHostApiKind KindOf(PaHostApiTypeId type) => type switch
    {
        PaHostApiTypeId.Asio => SoundCardHostApiKind.Asio,
        PaHostApiTypeId.Wasapi => SoundCardHostApiKind.Wasapi,
        PaHostApiTypeId.Wdmks => SoundCardHostApiKind.WdmKs,
        PaHostApiTypeId.DirectSound => SoundCardHostApiKind.DirectSound,
        PaHostApiTypeId.Mme => SoundCardHostApiKind.Mme,
        PaHostApiTypeId.Alsa => SoundCardHostApiKind.Alsa,
        PaHostApiTypeId.Jack => SoundCardHostApiKind.Jack,
        PaHostApiTypeId.CoreAudio => SoundCardHostApiKind.CoreAudio,
        _ => SoundCardHostApiKind.Other,
    };

    /// <summary>
    /// Builds one side's parameters on the unmanaged heap, or returns <see cref="IntPtr.Zero"/>
    /// where the request does not carry that direction — which is PortAudio's own way of saying
    /// "no input" or "no output", and the reason the two P/Invokes take pointers rather than
    /// <c>ref</c>.
    ///
    /// <para>Two allocations, two lifetimes. The returned block is read during
    /// <c>Pa_OpenStream</c> / <c>Pa_IsFormatSupported</c> and the caller frees it as soon as the
    /// call returns. <paramref name="hostApiSpecific"/> is the WASAPI exclusive-mode descriptor and
    /// is <b>not</b> the caller's to free on the success path — ownership passes to the stream,
    /// which frees it on close.</para>
    /// </summary>
    private IntPtr AllocParameters(
        SoundCardStreamRequest request, bool output, out IntPtr hostApiSpecific)
    {
        hostApiSpecific = IntPtr.Zero;

        if (output ? !request.HasOutput : !request.HasInput)
            return IntPtr.Zero;

        int device = output ? request.OutputDevice : request.InputDevice;
        int channels = output ? request.OutputChannelCount : request.InputChannelCount;
        var descriptor = _devices.FirstOrDefault(d => d.Index == device);

        double latency = request.SuggestedLatencyS
            ?? (output ? descriptor?.DefaultLowOutputLatencyS : descriptor?.DefaultLowInputLatencyS)
            ?? 0.0;

        if (request.Exclusive && descriptor?.HostApiKind == SoundCardHostApiKind.Wasapi)
        {
            var wasapi = PaWasapiStreamInfo.Exclusive();
            hostApiSpecific = Marshal.AllocHGlobal((int)wasapi.Size);
            Marshal.StructureToPtr(wasapi, hostApiSpecific, fDeleteOld: false);
        }

        var parameters = new PortAudioInterop.PaStreamParameters
        {
            Device = device,
            ChannelCount = channels,
            SampleFormat = PortAudioInterop.SampleFormatFloat32,
            SuggestedLatency = latency,
            HostApiSpecificStreamInfo = hostApiSpecific,
        };

        IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<PortAudioInterop.PaStreamParameters>());
        Marshal.StructureToPtr(parameters, block, fDeleteOld: false);
        return block;
    }

    private static void Free(IntPtr p)
    {
        if (p != IntPtr.Zero)
            Marshal.FreeHGlobal(p);
    }

    /// <summary>
    /// Every call here runs on the dispatcher thread. Read and Write are the hot path and are
    /// expected to be called from a loop that is already running there, where
    /// <see cref="StaDispatcher.Invoke{T}"/> is a direct call and costs nothing.
    /// </summary>
    private sealed class NativeStream(IntPtr handle, IntPtr inputExtra, IntPtr outputExtra, StaDispatcher dispatcher)
        : IPortAudioStream
    {
        private IntPtr _handle = handle;
        private bool _started;

        public double InputLatencyS { get; private set; } = ReadInfo(handle).InputLatency;

        public double OutputLatencyS { get; private set; } = ReadInfo(handle).OutputLatency;

        public double SampleRateHz { get; private set; } = ReadInfo(handle).SampleRate;

        public void Start() => dispatcher.Invoke(() =>
        {
            Check(PortAudioInterop.Pa_StartStream(_handle), "start");
            _started = true;
        });

        public void Stop() => dispatcher.Invoke(() =>
        {
            if (!_started)
                return;

            _started = false;

            // Stop drains the output; abort discards it. Teardown wants the drain, but a device
            // that has gone away will not finish one, so a failed stop falls through to abort
            // rather than leaving the handle open -- the sound-card equivalent of the rule that a
            // stream left open is what a user reads as "it needs a power cycle".
            if (PortAudioInterop.Pa_StopStream(_handle) != PaError.NoError)
                PortAudioInterop.Pa_AbortStream(_handle);
        });

        public bool Read(float[] buffer, int frames) => dispatcher.Invoke(() =>
        {
            int error = PortAudioInterop.Pa_ReadStream(_handle, buffer, new CULong((nuint)frames));
            if (error == PaError.InputOverflowed)
                return true;

            Check(error, "read from");
            return false;
        });

        public bool Write(float[] buffer, int frames) => dispatcher.Invoke(() =>
        {
            int error = PortAudioInterop.Pa_WriteStream(_handle, buffer, new CULong((nuint)frames));
            if (error == PaError.OutputUnderflowed)
                return true;

            Check(error, "write to");
            return false;
        });

        // A negative return is a PaError rather than a count, and a diagnostic may not throw on a
        // host API that declines to answer: both come back as -1, which the log prints as unknown.
        public long FramesAvailableToRead() => dispatcher.Invoke(() =>
        {
            long frames = (long)PortAudioInterop.Pa_GetStreamReadAvailable(_handle).Value;
            return frames < 0 ? -1 : frames;
        });

        public long FramesAvailableToWrite() => dispatcher.Invoke(() =>
        {
            long frames = (long)PortAudioInterop.Pa_GetStreamWriteAvailable(_handle).Value;
            return frames < 0 ? -1 : frames;
        });

        public void Dispose() => dispatcher.Invoke(() =>
        {
            if (_handle == IntPtr.Zero)
                return;

            Stop();
            PortAudioInterop.Pa_CloseStream(_handle);
            _handle = IntPtr.Zero;
            Free(inputExtra);
            Free(outputExtra);
        });

        private static PortAudioInterop.PaStreamInfo ReadInfo(IntPtr handle)
        {
            IntPtr p = PortAudioInterop.Pa_GetStreamInfo(handle);
            return p == IntPtr.Zero
                ? default
                : Marshal.PtrToStructure<PortAudioInterop.PaStreamInfo>(p);
        }

        private static void Check(int error, string what)
        {
            if (error != PaError.NoError)
                throw new PortAudioException($"Could not {what} the audio stream: {PortAudioInterop.ErrorText(error)}", error);
        }
    }
}
