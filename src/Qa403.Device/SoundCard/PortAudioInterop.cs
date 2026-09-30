using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Qa403.Device.SoundCard;

/// <summary>
/// The raw PortAudio C API. Nothing outside this file names the native library or its structs —
/// <see cref="IPortAudio"/> is what the rest of the folder programs against.
///
/// <para><c>DllImport</c> rather than <see cref="LibraryImportAttribute"/>: the source generator
/// requires <c>AllowUnsafeBlocks</c> across the whole assembly, and it buys nothing here. Every
/// signature below is blittable, so the runtime pins and passes straight through with no
/// marshalling stub, and IL trimming keeps a <c>DllImport</c> target — the generator's advantage is
/// NativeAOT, which this publish is not (single-file and trimmed, see <c>Qa403.App.csproj</c>).</para>
/// </summary>
internal static class PortAudioInterop
{
    /// <summary>
    /// The name the imports below use. It is resolved by <see cref="Resolve"/> rather than being a
    /// real file name, because the two platforms spell it differently: a vendored
    /// <c>portaudio.dll</c> beside the exe on Windows, the distro's <c>libportaudio.so.2</c> on
    /// Linux (see <c>packaging/native/portaudio.md</c>).
    /// </summary>
    private const string Library = "portaudio";

    [ModuleInitializer]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(PortAudioInterop).Assembly, Resolve);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library)
            return IntPtr.Zero;

        // The Linux package is libportaudio2, which installs libportaudio.so.2; the unversioned
        // .so is in the -dev package and is not something a user of the .deb will have. Try the
        // versioned name first and fall back, rather than depending on a development package.
        foreach (string candidate in OperatingSystem.IsWindows()
            ? ["portaudio.dll"]
            : (string[])["libportaudio.so.2", "libportaudio.so"])
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    // ---- scalars -----------------------------------------------------------------------------
    //
    // PaSampleFormat and PaStreamFlags are C `unsigned long` (portaudio.h:489, 661), and so is
    // Pa_OpenStream's framesPerBuffer. That is *four* bytes on Windows (LLP64) and *eight* on Linux
    // (LP64), so a `uint` here would be right on one platform and silently wrong on the other --
    // and PaStreamParameters happens to be 32 bytes either way, so the damage would be an
    // uninitialised upper half rather than a size mismatch anything would notice. CULong exists for
    // exactly this and is blittable.

    /// <summary>paFloat32 — the only sample format this app asks for; conversion is PortAudio's job.</summary>
    internal static readonly CULong SampleFormatFloat32 = new(0x00000001);

    /// <summary>paNoFlag.</summary>
    internal static readonly CULong StreamFlagsNone = new(0);

    /// <summary>
    /// paDitherOff (portaudio.h:674). PortAudio dithers by default when converting float32 down to
    /// the device's native format, and a dither floor on the <i>stimulus</i> is measured distortion
    /// that is not the DUT's.
    ///
    /// <para>Note the neighbouring value: 0x04 is <c>paNeverDropInput</c>, which is valid only on a
    /// full-duplex <i>callback</i> stream and which PortAudio rejects outright on a blocking one
    /// ("Invalid flag" from <c>Pa_OpenStream</c>). Clipping is deliberately left <b>on</b> —
    /// <c>SoundCardCalibration.VoltsToSample</c> clamps for the same reason, and a DAC asked for
    /// more than full scale should clip rather than wrap.</para>
    /// </summary>
    internal static readonly CULong StreamFlagsDitherOff = new(0x00000002);

    /// <summary>paFramesPerBufferUnspecified. Deliberately not used for WASAPI exclusive, which picks ~131072 frames.</summary>
    internal static readonly CULong FramesPerBufferUnspecified = new(0);

    internal const int NoDevice = -1;
    internal const int NoError = 0;

    // ---- structs -----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaHostApiInfo
    {
        public int StructVersion;
        public PaHostApiTypeId Type;
        public IntPtr Name;
        public int DeviceCount;
        public int DefaultInputDevice;
        public int DefaultOutputDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaDeviceInfo
    {
        public int StructVersion;
        public IntPtr Name;
        public int HostApi;
        public int MaxInputChannels;
        public int MaxOutputChannels;
        public double DefaultLowInputLatency;
        public double DefaultLowOutputLatency;
        public double DefaultHighInputLatency;
        public double DefaultHighOutputLatency;
        public double DefaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaStreamParameters
    {
        public int Device;
        public int ChannelCount;
        public CULong SampleFormat;
        public double SuggestedLatency;
        public IntPtr HostApiSpecificStreamInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaStreamInfo
    {
        public int StructVersion;
        public double InputLatency;
        public double OutputLatency;
        public double SampleRate;
    }

    // ---- entry points ------------------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_Initialize();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_Terminate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Pa_GetVersionText();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Pa_GetErrorText(int errorCode);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_GetHostApiCount();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_GetDeviceCount();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Pa_GetHostApiInfo(int hostApi);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Pa_GetDeviceInfo(int device);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_IsFormatSupported(
        ref PaStreamParameters inputParameters, ref PaStreamParameters outputParameters, double sampleRate);

    /// <summary>Opened with a null callback, i.e. PortAudio's blocking read/write interface.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_OpenStream(
        out IntPtr stream,
        ref PaStreamParameters inputParameters,
        ref PaStreamParameters outputParameters,
        double sampleRate,
        CULong framesPerBuffer,
        CULong streamFlags,
        IntPtr streamCallback,
        IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_StartStream(IntPtr stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_StopStream(IntPtr stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_AbortStream(IntPtr stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_CloseStream(IntPtr stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Pa_GetStreamInfo(IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId type);

    /// <summary>WASAPI-only rescan. The only way to notice a card plugged in after Pa_Initialize, since re-initialising is fatal once ASIO is in the build.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int PaWasapi_UpdateDeviceList();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_ReadStream(IntPtr stream, [Out] float[] buffer, CULong frames);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_WriteStream(IntPtr stream, [In] float[] buffer, CULong frames);

    internal static string ReadUtf8(IntPtr p) => p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(p) ?? string.Empty);

    internal static string ErrorText(int code) => ReadUtf8(Pa_GetErrorText(code));
}

/// <summary>PortAudio's host API identifiers (portaudio.h <c>PaHostApiTypeId</c>).</summary>
internal enum PaHostApiTypeId
{
    InDevelopment = 0,
    DirectSound = 1,
    Mme = 2,
    Asio = 3,
    SoundManager = 4,
    CoreAudio = 5,
    Oss = 7,
    Alsa = 8,
    Al = 9,
    BeOs = 10,
    Wdmks = 11,
    Jack = 12,
    Wasapi = 13,
    AudioScienceHpi = 14,
    AudioIo = 15,
    PulseAudio = 16,
    Sndio = 17,
}

/// <summary>
/// The error codes this code reacts to by name. <c>Pa_ReadStream</c> and <c>Pa_WriteStream</c>
/// report the two overflow conditions as <b>non-fatal</b> returns — the transfer still happened —
/// which is why they are handled rather than thrown.
/// </summary>
internal static class PaError
{
    internal const int NoError = 0;
    internal const int InvalidSampleRate = -9997;
    internal const int DeviceUnavailable = -9985;
    internal const int InputOverflowed = -9981;
    internal const int OutputUnderflowed = -9980;
}

/// <summary>
/// The parts of <c>pa_win_wasapi.h</c> this app needs. WASAPI <b>exclusive</b> mode is not a
/// preference here but a requirement: measured on a MOTU M4 (2026-09-30), shared mode offers only
/// 48 kHz — the Windows mixer's rate, with 44.1, 96 and 192 kHz all refused as "Invalid sample
/// rate" — while exclusive mode opens all four at their true rate. Shared mode would also resample
/// silently, which no measurement downstream could detect.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PaWasapiStreamInfo
{
    public uint Size;
    public PaHostApiTypeId HostApiType;
    public uint Version;
    public uint Flags;
    public uint ChannelMask;
    public IntPtr HostProcessorOutput;
    public IntPtr HostProcessorInput;
    public int ThreadPriority;
    public int StreamCategory;
    public int StreamOption;
    public int PassthroughFormatId;
    public uint PassthroughEncodedSamplesPerSec;
    public uint PassthroughEncodedChannelCount;
    public uint PassthroughAverageBytesPerSec;

    /// <summary>paWinWasapiExclusive.</summary>
    internal const uint ExclusiveFlag = 1 << 0;

    /// <summary>
    /// A descriptor asking for exclusive mode. <c>Size</c> is checked by PortAudio, and the struct
    /// is 72 bytes on x64 — verified against the built DLL rather than assumed.
    /// </summary>
    internal static PaWasapiStreamInfo Exclusive() => new()
    {
        Size = (uint)Marshal.SizeOf<PaWasapiStreamInfo>(),
        HostApiType = PaHostApiTypeId.Wasapi,
        Version = 1,
        Flags = ExclusiveFlag,
    };
}
