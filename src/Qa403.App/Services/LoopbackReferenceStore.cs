using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>What a caller asks for when capturing a new loopback calibration.</summary>
/// <param name="Name">Shown in the picker. Unique — saving over an existing name replaces it.</param>
public sealed record ImpulseReferenceCaptureRequest(
    string Name,
    double FrequencySpanHz,
    int ResolutionMultiplier,
    double GeneratorLevelDbv,
    int AverageCount = ImpulseReferenceCaptureRequest.MinAverageCount,
    double ChirpStartHz = 2.0)
{
    /// <summary>
    /// A calibration is divided into every measurement that uses it, so its own noise lands in all
    /// of them. Averaging fewer times than this would make the correction noisier than the thing it
    /// corrects.
    /// </summary>
    public const int MinAverageCount = 4;
}

/// <summary>
/// Where loopback calibrations live.
///
/// <para>An interface rather than a direct repository dependency for one concrete reason: it lets
/// <c>ImpulseResponseRunnerTests</c> capture a calibration and measure against it without a
/// database, which is the only way to test the correction end to end at all.</para>
/// </summary>
public interface ILoopbackReferenceStore
{
    Task<IReadOnlyList<LoopbackReferenceInfo>> ListAsync(CancellationToken ct);

    /// <summary>Null when no calibration has that id.</summary>
    Task<(LoopbackReferenceInfo Info, LoopbackReference Reference)?> GetAsync(long id, CancellationToken ct);

    Task<LoopbackReferenceInfo> SaveAsync(
        string name,
        LoopbackReference reference,
        double chirpStartHz,
        double chirpEndHz,
        int sourceFftSize,
        string? deviceSerial,
        bool isSimulated,
        CancellationToken ct);

    Task<bool> DeleteAsync(long id, CancellationToken ct);
}
