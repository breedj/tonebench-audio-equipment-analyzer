using System.Runtime.InteropServices;

namespace Tonebench.Device.SoundCard;

/// <summary>
/// The raw PortAudio C API. Nothing outside this file names the native library or its structs —
/// <see cref="IPortAudio"/> is what the rest of the folder programs against.
///
/// <para><c>DllImport</c> rather than <see cref="LibraryImportAttribute"/>: the source generator
/// requires <c>AllowUnsafeBlocks</c> across the whole assembly, and it buys nothing here. Every
/// signature below is blittable, so the runtime pins and passes straight through with no
/// marshalling stub, and IL trimming keeps a <c>DllImport</c> target — the generator's advantage is
/// NativeAOT, which this publish is not (single-file and trimmed, see <c>Tonebench.App.csproj</c>).</para>
/// </summary>
internal static class PortAudioInterop
{
    /// <summary>
    /// The name the imports below use. It is resolved by <see cref="Resolve"/> rather than being a
    /// real file name, because the three platforms spell it differently: a vendored
    /// <c>portaudio.dll</c> beside the exe on Windows, the distro's <c>libportaudio.so.2</c> on
    /// Linux (see <c>packaging/native/portaudio.md</c>), and Homebrew's
    /// <c>libportaudio.2.dylib</c> on macOS.
    /// </summary>
    private const string Library = "portaudio";

    /// <summary>
    /// Registers the resolver below before the first <c>DllImport</c> in this class can run.
    ///
    /// <para>A <b>static constructor</b> rather than a <c>[ModuleInitializer]</c>: the attribute is
    /// meant for application code and an analyzer says so (CA2255), and it is more than is needed.
    /// Every import is a static method <i>of this type</i>, and an explicit static constructor
    /// makes the type not <c>beforefieldinit</c> — so the CLR guarantees this has run before the
    /// first of them is invoked, which is exactly the ordering the resolver requires. It also stops
    /// the registration happening for any assembly load that never touches a sound card.</para>
    /// </summary>
    static PortAudioInterop() =>
        NativeLibrary.SetDllImportResolver(typeof(PortAudioInterop).Assembly, Resolve);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library)
            return IntPtr.Zero;

        // The Linux package is libportaudio2, which installs libportaudio.so.2; the unversioned
        // .so is in the -dev package and is not something a user of the .deb will have. Try the
        // versioned name first and fall back, rather than depending on a development package.
        //
        // macOS needs the absolute paths as well as the leaf names, and that is the whole reason
        // this branch is three entries rather than two. dyld's default search covers
        // /usr/local/lib and /usr/lib; Homebrew on Apple Silicon installs into /opt/homebrew/lib,
        // which is on neither -- so a leaf name alone finds nothing on exactly the machine this
        // was written for. The leaf names come first so a copy beside the binary (what an .app
        // bundle would do) or a DYLD_*_LIBRARY_PATH the user has set still wins, and because on an
        // Intel Mac Homebrew's own prefix *is* /usr/local. MacPorts is the third prefix.
        foreach (string candidate in OperatingSystem.IsWindows()
            ? ["portaudio.dll"]
            : OperatingSystem.IsMacOS()
                ? (string[])
                [
                    "libportaudio.2.dylib",
                    "libportaudio.dylib",
                    "/opt/homebrew/lib/libportaudio.2.dylib",
                    "/opt/local/lib/libportaudio.2.dylib",
                ]
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

    /// <summary>
    /// Both parameter blocks are passed as raw pointers rather than <c>ref</c>, and that is the
    /// whole of what makes a one-direction stream possible: PortAudio's own contract is that
    /// <c>inputParameters</c> is NULL for an output-only stream and <c>outputParameters</c> is NULL
    /// for an input-only one, and a <c>ref</c> parameter has no way to say NULL. There is
    /// deliberately no <c>ref</c> overload left — with one, an absent direction would be passed a
    /// zeroed struct instead, which names device 0 with 0 channels and is a different request
    /// altogether.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_IsFormatSupported(
        IntPtr inputParameters, IntPtr outputParameters, double sampleRate);

    /// <summary>
    /// Opened with a null callback, i.e. PortAudio's blocking read/write interface. See
    /// <see cref="Pa_IsFormatSupported"/> for why the parameter blocks are pointers.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Pa_OpenStream(
        out IntPtr stream,
        IntPtr inputParameters,
        IntPtr outputParameters,
        double sampleRate,
        CULong framesPerBuffer,
        CULong streamFlags,
        IntPtr streamCallback,
        IntPtr userData);

    /// <summary>
    /// Opens the ASIO driver's own control panel — where the buffer size, the clock source and
    /// often the sample rate live, none of which PortAudio can set.
    ///
    /// <para>ASIO only; there is no equivalent for the other host APIs. Called for a device that
    /// is not an ASIO device it returns <c>paInvalidDevice</c> — or <c>paHostApiNotFound</c> if the
    /// library was built without ASIO — rather than doing something surprising.</para>
    ///
    /// <para><c>systemSpecific</c> is the owner window on Windows — see
    /// <see cref="GetDesktopWindow"/> for what is passed and why. <b>The dialog is modal and runs
    /// its own message loop on the calling thread</b>, which is why the caller refuses while a
    /// stream is running: the calling thread is the STA dispatcher, and the transfer loop lives
    /// on it.</para>
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PaAsio_ShowControlPanel(int device, IntPtr systemSpecific);

    /// <summary>
    /// The window an ASIO driver's panel is parented to. This process has none of its own — the UI
    /// is a browser — and a driver handed a null parent may decline to show anything, so the
    /// desktop window is passed instead: it is always a valid top-level HWND and parenting a
    /// dialog to it is what a process with no window of its own is expected to do.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetDesktopWindow();

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

    /// <summary>
    /// Frames already buffered on each side of a blocking stream. Both return a <c>signed long</c>
    /// that is a frame count when positive and a <c>PaError</c> when negative, which is why they are
    /// <see cref="CLong"/> rather than <see cref="CULong"/>.
    ///
    /// <para>Read only when a transfer loop iteration has already run late — see
    /// <see cref="SoundCardLoopTiming"/>, which is the one caller.</para>
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CLong Pa_GetStreamReadAvailable(IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CLong Pa_GetStreamWriteAvailable(IntPtr stream);

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
    /// <summary>
    /// The blocking interface waited longer than the stream's own timeout. <b>Not fatal</b>: the
    /// device is still there and the next transfer usually succeeds — see the transfer loop, which
    /// counts these rather than tearing the session down over one.
    /// </summary>
    internal const int TimedOut = -9987;

    internal const int InputOverflowed = -9981;
    internal const int OutputUnderflowed = -9980;

    /// <summary>
    /// The two ways <c>PaAsio_ShowControlPanel</c> says "there is no ASIO panel here": the device
    /// belongs to another host API, or ASIO is not in this build of the library at all. Derived
    /// from <c>PaErrorCode</c>'s ordering (contiguous from -10000), like the codes above, and
    /// confirmed against the vendored DLL — both mean "no panel", neither means a fault.
    /// </summary>
    internal const int InvalidDevice = -9996;

    internal const int HostApiNotFound = -9979;
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
