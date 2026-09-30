using System.Runtime.InteropServices;

namespace Qa403.Device.SoundCard;

/// <summary>Thrown for any PortAudio error that is not one of the two non-fatal transfer conditions.</summary>
public sealed class PortAudioException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// <see cref="IPortAudio"/> over the real library. Owns the one process-wide
/// <c>Pa_Initialize</c>/<c>Pa_Terminate</c> pair.
/// </summary>
internal sealed class PortAudioNative : IPortAudio
{
    private readonly Lock _gate = new();
    private bool _initialised;
    private bool _disposed;
    private SoundCardHostApi[] _hostApis = [];
    private SoundCardDeviceDescriptor[] _devices = [];

    public PortAudioNative()
    {
        Initialise();
        ReadTopology();
    }

    public string VersionText { get; private set; } = string.Empty;

    public IReadOnlyList<SoundCardHostApi> HostApis => _hostApis;

    public IReadOnlyList<SoundCardDeviceDescriptor> Devices => _devices;

    /// <summary>
    /// PortAudio reads the device list once, at initialise, so a card plugged in afterwards is
    /// invisible until the library is torn down and brought back up. That is what the dialog's
    /// Refresh does.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_initialised)
            {
                PortAudioInterop.Pa_Terminate();
                _initialised = false;
            }

            Initialise();
            ReadTopology();
        }
    }

    public bool IsFormatSupported(SoundCardStreamRequest request)
    {
        var input = BuildParameters(request, output: false, out IntPtr inputExtra);
        var outputParams = BuildParameters(request, output: true, out IntPtr outputExtra);
        try
        {
            return PortAudioInterop.Pa_IsFormatSupported(ref input, ref outputParams, request.SampleRateHz)
                == PaError.NoError;
        }
        finally
        {
            Free(inputExtra);
            Free(outputExtra);
        }
    }

    public IPortAudioStream OpenStream(SoundCardStreamRequest request, int framesPerBuffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerBuffer);

        var input = BuildParameters(request, output: false, out IntPtr inputExtra);
        var outputParams = BuildParameters(request, output: true, out IntPtr outputExtra);

        int error = PortAudioInterop.Pa_OpenStream(
            out IntPtr handle,
            ref input,
            ref outputParams,
            request.SampleRateHz,
            new CULong((nuint)framesPerBuffer),
            PortAudioInterop.StreamFlagsDitherOff,
            IntPtr.Zero,
            IntPtr.Zero);

        if (error != PaError.NoError)
        {
            Free(inputExtra);
            Free(outputExtra);
            throw new PortAudioException(
                $"Could not open the audio stream at {request.SampleRateHz:0} Hz: {PortAudioInterop.ErrorText(error)}",
                error);
        }

        return new NativeStream(handle, inputExtra, outputExtra);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_initialised)
            {
                PortAudioInterop.Pa_Terminate();
                _initialised = false;
            }
        }
    }

    private void Initialise()
    {
        int error = PortAudioInterop.Pa_Initialize();
        if (error != PaError.NoError)
            throw new PortAudioException($"PortAudio could not start: {PortAudioInterop.ErrorText(error)}", error);

        _initialised = true;
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
        _ => SoundCardHostApiKind.Other,
    };

    /// <summary>
    /// Builds one side's parameters, allocating a WASAPI descriptor when exclusive mode is asked
    /// for. The allocation has to outlive the call — PortAudio reads it during
    /// <c>Pa_OpenStream</c> — so ownership passes to the caller, and the stream frees it on close.
    /// </summary>
    private PortAudioInterop.PaStreamParameters BuildParameters(
        SoundCardStreamRequest request, bool output, out IntPtr hostApiSpecific)
    {
        int device = output ? request.OutputDevice : request.InputDevice;
        int channels = output ? request.OutputChannelCount : request.InputChannelCount;
        var descriptor = _devices.FirstOrDefault(d => d.Index == device);

        double latency = request.SuggestedLatencyS
            ?? (output ? descriptor?.DefaultLowOutputLatencyS : descriptor?.DefaultLowInputLatencyS)
            ?? 0.0;

        hostApiSpecific = IntPtr.Zero;
        if (request.Exclusive && descriptor?.HostApiKind == SoundCardHostApiKind.Wasapi)
        {
            var wasapi = PaWasapiStreamInfo.Exclusive();
            hostApiSpecific = Marshal.AllocHGlobal((int)wasapi.Size);
            Marshal.StructureToPtr(wasapi, hostApiSpecific, fDeleteOld: false);
        }

        return new PortAudioInterop.PaStreamParameters
        {
            Device = device,
            ChannelCount = channels,
            SampleFormat = PortAudioInterop.SampleFormatFloat32,
            SuggestedLatency = latency,
            HostApiSpecificStreamInfo = hostApiSpecific,
        };
    }

    private static void Free(IntPtr p)
    {
        if (p != IntPtr.Zero)
            Marshal.FreeHGlobal(p);
    }

    private sealed class NativeStream(IntPtr handle, IntPtr inputExtra, IntPtr outputExtra) : IPortAudioStream
    {
        private IntPtr _handle = handle;
        private bool _started;

        public double InputLatencyS { get; private set; } = ReadInfo(handle).InputLatency;

        public double OutputLatencyS { get; private set; } = ReadInfo(handle).OutputLatency;

        public double SampleRateHz { get; private set; } = ReadInfo(handle).SampleRate;

        public void Start()
        {
            Check(PortAudioInterop.Pa_StartStream(_handle), "start");
            _started = true;
        }

        public void Stop()
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
        }

        public bool Read(float[] buffer, int frames)
        {
            int error = PortAudioInterop.Pa_ReadStream(_handle, buffer, new CULong((nuint)frames));
            if (error == PaError.InputOverflowed)
                return true;

            Check(error, "read from");
            return false;
        }

        public bool Write(float[] buffer, int frames)
        {
            int error = PortAudioInterop.Pa_WriteStream(_handle, buffer, new CULong((nuint)frames));
            if (error == PaError.OutputUnderflowed)
                return true;

            Check(error, "write to");
            return false;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
                return;

            Stop();
            PortAudioInterop.Pa_CloseStream(_handle);
            _handle = IntPtr.Zero;
            Free(inputExtra);
            Free(outputExtra);
        }

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
