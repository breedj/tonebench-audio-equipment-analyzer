using System.Text.Json.Serialization;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Dsp;

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
[JsonSerializable(typeof(DeviceKindRequest))]
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
[JsonSerializable(typeof(ProjectResponse))]
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
[JsonSerializable(typeof(FrequencyResponseStartRequest))]
[JsonSerializable(typeof(BandwidthReferenceRequest))]
[JsonSerializable(typeof(FrequencyResponseStarted))]
[JsonSerializable(typeof(FrequencyResponseStatus))]
[JsonSerializable(typeof(SaveFrequencyResponseRequest))]

// The impedance measurement. ImpedanceCalibration rides inside both the start request and the
// status, so it is reachable through them rather than registered on its own.
[JsonSerializable(typeof(ImpedanceStartRequest))]
[JsonSerializable(typeof(ImpedanceStarted))]
[JsonSerializable(typeof(ImpedanceStatus))]
[JsonSerializable(typeof(SaveImpedanceRequest))]
[JsonSerializable(typeof(ImpedanceFixtureConfig))]
[JsonSerializable(typeof(UiLayoutConfig))]
[JsonSerializable(typeof(CaptureDelayConfig))]
[JsonSerializable(typeof(StartupConfig))]
[JsonSerializable(typeof(AutosaveConfig))]
[JsonSerializable(typeof(RecentProjectsConfig))]
[JsonSerializable(typeof(LastOpenedProjectConfig))]
// The project session: the open file, its dialogs' requests, the recent list and the file browser.
[JsonSerializable(typeof(ProjectSessionResponse))]
[JsonSerializable(typeof(OpenProjectRequest))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(DeleteProjectRequest))]
[JsonSerializable(typeof(SaveProjectAsRequest))]
[JsonSerializable(typeof(ProjectPropertiesRequest))]
[JsonSerializable(typeof(RecentProjectResponse))]
[JsonSerializable(typeof(IEnumerable<RecentProjectResponse>))]
[JsonSerializable(typeof(RemoveRecentProjectRequest))]
[JsonSerializable(typeof(FileBrowseResponse))]
// A Qa403.Data record, so AppJsonContextTests' scan of the Endpoints namespace does not see it —
// registered by hand, like the three config documents above it.
[JsonSerializable(typeof(ReportSettingsConfig))]
// The PDF report the browser assembles (Endpoints/ReportsEndpoints.cs). Its chapters, sections and
// rows are reached through the root; the PNG bytes ride as base64.
[JsonSerializable(typeof(ReportDocument))]
[JsonSerializable(typeof(StoredResponseTracesResponse))]
[JsonSerializable(typeof(FrequencyResponseParameters))]
// The stepped-sine response's own DTOs. Missing until now, which is exactly the failure this file's
// remarks describe: reflection-based JSON is still on in a debug build, so the endpoints worked
// everywhere except a trimmed publish, where they would have thrown "JsonTypeInfo metadata ... was
// not provided". (Its /save endpoint reuses SaveFrequencyResponseRequest, so that one was already
// covered.) AppJsonContextTests now checks the whole surface rather than trusting the next
// person to remember.
// Found by AppJsonContextTests when it was written, alongside the stepped-sine three: the sidebar
// reorder endpoint has been unable to accept a body on a trimmed publish since it shipped.
[JsonSerializable(typeof(ReorderMeasurementTypesRequest))]
[JsonSerializable(typeof(SteppedResponseStartRequest))]
[JsonSerializable(typeof(SteppedResponseStarted))]
[JsonSerializable(typeof(SteppedResponseParameters))]
[JsonSerializable(typeof(ImpulseResponseStartRequest))]
[JsonSerializable(typeof(ImpulseResponseStarted))]
[JsonSerializable(typeof(ImpulseResponseStatus))]
[JsonSerializable(typeof(SaveImpulseResponseRequest))]
[JsonSerializable(typeof(ImpulseReferenceCaptureRequest))]
[JsonSerializable(typeof(LoopbackReferenceInfo))]
[JsonSerializable(typeof(LoopbackReferencesResponse))]
[JsonSerializable(typeof(StoredImpulseResult))]
[JsonSerializable(typeof(StoredImpulseTraceResponse))]
[JsonSerializable(typeof(ImpulseCsdRequest))]
[JsonSerializable(typeof(ImpulseBurstDecayRequest))]
[JsonSerializable(typeof(DecaySurface))]
[JsonSerializable(typeof(ImpulseResponseParameters))]
// Serialized directly to params_json/gen_json by MeasurementSaveService, not returned from an
// endpoint — same trim-metadata requirement applies.
[JsonSerializable(typeof(FftSpectrumParameters))]
[JsonSerializable(typeof(GeneratorConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
