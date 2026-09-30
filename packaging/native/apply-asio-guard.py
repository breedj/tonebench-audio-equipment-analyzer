"""Applies the ASIO enumeration guard to a PortAudio checkout, idempotently.

Run it against the PortAudio source tree before configuring the ASIO build:

    python packaging/native/apply-asio-guard.py <portaudio>/src/hostapi/asio/pa_asio.cpp

See packaging/native/portaudio.md for why this patch exists and what it costs.
"""
import io
import sys

if len(sys.argv) < 2:
    raise SystemExit('usage: apply-asio-guard.py <portaudio>/src/hostapi/asio/pa_asio.cpp')
path = sys.argv[1]
s = io.open(path, encoding='utf-8', errors='surrogateescape').read()

if 'PaAsio_GuardedInit' in s:
    print('already patched')
    sys.exit(0)

anchor = """static PaError LoadAsioDriver( PaAsioHostApiRepresentation *asioHostApi, const char *driverName,
        PaAsioDriverInfo *driverInfo, void *systemSpecific )
{"""

helper = """/*
    QA403 analyzer local patch -- see packaging/native/portaudio.md.

    Pa_Initialize calls ASIOInit on EVERY registered ASIO driver in order to ask it how many
    channels it has. A driver that faults there takes the whole host process down, and there is no
    way to reach the drivers listed behind it. Measured on this bench: Creative's Sound Blaster ASIO
    driver raises an access violation inside ASIOInit (with a null sysRef and with GetDesktopWindow()
    alike), which killed the app before the MOTU M4 sitting after it in the list was ever seen.

    ARTA and similar hosts avoid this by initialising only the driver the user picked; PortAudio's
    API cannot, because it must report every device up front. So a faulting driver is caught and
    reported as absent, exactly as one that politely returns an error would be.

    SEH lives in its own leaf function because MSVC forbids __try in a function that also needs C++
    unwinding.
*/
#if defined(_MSC_VER)
static ASIOError PaAsio_GuardedInit( ASIODriverInfo *info )
{
    __try
    {
        return ASIOInit( info );
    }
    __except( EXCEPTION_EXECUTE_HANDLER )
    {
        return ASE_NotPresent;
    }
}

static ASIOError PaAsio_GuardedGetChannels( long *inputs, long *outputs )
{
    __try
    {
        return ASIOGetChannels( inputs, outputs );
    }
    __except( EXCEPTION_EXECUTE_HANDLER )
    {
        return ASE_NotPresent;
    }
}
#else
#define PaAsio_GuardedInit ASIOInit
#define PaAsio_GuardedGetChannels ASIOGetChannels
#endif

"""

if anchor not in s:
    raise SystemExit('LoadAsioDriver not found - has pa_asio.cpp changed?')
s = s.replace(anchor, helper + anchor, 1)

pairs = [
    ("if( (asioError = ASIOInit( &driverInfo->asioDriverInfo )) != ASE_OK )",
     "if( (asioError = PaAsio_GuardedInit( &driverInfo->asioDriverInfo )) != ASE_OK )"),
    ("""if( (asioError = ASIOGetChannels(&driverInfo->inputChannelCount,
            &driverInfo->outputChannelCount)) != ASE_OK )""",
     """if( (asioError = PaAsio_GuardedGetChannels(&driverInfo->inputChannelCount,
            &driverInfo->outputChannelCount)) != ASE_OK )"""),
]

for old, new in pairs:
    if old not in s:
        raise SystemExit('call site not found:\n' + old)
    s = s.replace(old, new, 1)

io.open(path, 'w', encoding='utf-8', errors='surrogateescape', newline='').write(s)
print('patched', path)
