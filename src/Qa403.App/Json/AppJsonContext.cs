using System.Text.Json.Serialization;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;

namespace Qa403.App.Json;

/// <summary>
/// Source-generated JSON metadata for every request/response DTO the Minimal API surface and
/// <see cref="Qa403.App.Services.MeasurementSaveService"/> (de)serialize.
///
/// Required because <c>-p:PublishTrimmed=true</c> disables System.Text.Json's reflection-based
/// fallback entirely (the "IsReflectionEnabledByDefault" feature switch the SDK flips off for
/// trimmed apps) — without this, <see cref="JsonSerializerOptions.TypeInfoResolverChain"/> ends up
/// empty at runtime and every request/response throws
/// <c>System.NotSupportedException: JsonTypeInfo metadata for type '...' was not provided by
/// TypeInfoResolver of type '[]'</c> the instant a route with a JSON body is hit — confirmed by
/// actually running the published trimmed win-x64 build (docs/qa403-app-stack-spec.md §8's
/// packaging task explicitly called for testing the published artifact, not just the debug build,
/// for exactly this kind of trim breakage).
///
/// One root type per record here is enough — the source generator walks each type's full property
/// graph (nested records, arrays, dictionaries, enums) on its own. <c>UseStringEnumConverter</c>
/// replaces the old runtime <see cref="JsonStringEnumConverter"/> registration in Program.cs, which
/// itself needs reflection to construct a converter per enum type and would have the same problem.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DiagnosticsEndpoints.DiagnosticsConfigRequest))]
[JsonSerializable(typeof(DiagnosticsEndpoints.DiagnosticsStatus))]
[JsonSerializable(typeof(AcquisitionStartRequest))]
[JsonSerializable(typeof(SpectrumConfigRequest))]
[JsonSerializable(typeof(SpectrumPipelineOptions))]
[JsonSerializable(typeof(DeviceStatusResponse))]
[JsonSerializable(typeof(DeviceRangesResponse))]
[JsonSerializable(typeof(InputRangeRequest))]
[JsonSerializable(typeof(OutputRangeRequest))]
[JsonSerializable(typeof(AutoRangeRequest))]
[JsonSerializable(typeof(AutoRangeStepDownDelayRequest))]
[JsonSerializable(typeof(SimulatedProfileRequest))]
[JsonSerializable(typeof(GeneratorRequest))]
[JsonSerializable(typeof(SaveMeasurementRequest))]
[JsonSerializable(typeof(UpdateMeasurementRequest))]
[JsonSerializable(typeof(MeasurementListResponse))]
[JsonSerializable(typeof(MeasurementDetailResponse))]
[JsonSerializable(typeof(IdResponse))]
[JsonSerializable(typeof(SaveProjectRequest))]
[JsonSerializable(typeof(ProjectResponse))]
// Results.Ok(projects.Select(ToResponse)) — LINQ Select's static return type, not IReadOnlyList.
[JsonSerializable(typeof(IEnumerable<ProjectResponse>))]
[JsonSerializable(typeof(SaveMeasurementTypeRequest))]
[JsonSerializable(typeof(MeasurementTypeResponse))]
[JsonSerializable(typeof(IEnumerable<MeasurementTypeResponse>))]
[JsonSerializable(typeof(MeasurementTypeAppliedResponse))]
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(ThdSweepStartRequest))]
[JsonSerializable(typeof(ThdSweepStarted))]
[JsonSerializable(typeof(ThdSweepStatus))]
[JsonSerializable(typeof(SaveSweepRequest))]
[JsonSerializable(typeof(StoredSweepResponse))]
[JsonSerializable(typeof(ThdVsFrequencyParameters))]
[JsonSerializable(typeof(ThdAmplitudeSweepStartRequest))]
[JsonSerializable(typeof(ThdVsAmplitudeParameters))]
// Serialized directly to params_json/gen_json by MeasurementSaveService, not returned from an
// endpoint — same trim-metadata requirement applies.
[JsonSerializable(typeof(FftSpectrumParameters))]
[JsonSerializable(typeof(GeneratorConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
