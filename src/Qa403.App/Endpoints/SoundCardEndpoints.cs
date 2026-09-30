using Qa403.App.Services;
using Qa403.Device.SoundCard;

namespace Qa403.App.Endpoints;

/// <summary>
/// Reading the machine's audio devices. The sound card's <i>settings</i> live with every other
/// setting under <c>/api/config/soundcard</c>; this group is the part that asks the hardware.
/// </summary>
public static class SoundCardEndpoints
{
    public static void MapSoundCardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/soundcard");

        // Enumeration is a native call on PortAudio's own thread and takes a few milliseconds, so it
        // is a GET the dialog makes when it opens rather than something on the status poll.
        //
        // `refresh` re-reads the device table, and it is honest about its limits: PortAudio caches
        // the list at Pa_Initialize and the only way to rebuild it -- terminate and initialise again
        // -- crashes the process once ASIO is in the build. WASAPI's own rescan is used where it
        // exists; a newly plugged ASIO interface needs the app restarted, which is what every other
        // ASIO host asks for too.
        group.MapGet("/devices", (bool? refresh) =>
            Results.Ok(SoundCardCatalog.Enumerate(refresh ?? false)));

        // Whether a particular pairing works, and at which rates. Separate from the listing because
        // it has to be re-asked whenever either side or either channel offset changes, and because
        // probing every possible pair up front would be N² native calls.
        group.MapGet("/probe", (
            string inputHostApi, string inputDevice, string outputHostApi, string outputDevice,
            int? inputChannelOffset, int? outputChannelOffset) =>
            Results.Ok(SoundCardCatalog.Probe(
                inputHostApi, inputDevice, inputChannelOffset ?? 0,
                outputHostApi, outputDevice, outputChannelOffset ?? 0)));
    }
}
