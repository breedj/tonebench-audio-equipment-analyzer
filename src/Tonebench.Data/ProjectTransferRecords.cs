namespace Tonebench.Data;

/// <summary>
/// What to do with an incoming measurement whose name a live measurement in the destination project
/// already has. The destination's <c>ix_measurement_type_name</c> is unique on
/// <c>(project_id, name) WHERE deleted_utc IS NULL</c>, so one of these three has to happen.
/// </summary>
public enum TypeNameClash
{
    /// <summary>
    /// Copy it in under a suffixed name — "Amp 1 kHz (2)". The default, because it is the only
    /// policy that can neither fail nor quietly put two benches' runs under one measurement.
    /// </summary>
    Rename,

    /// <summary>
    /// Put the incoming results under the measurement that is already there, leaving its own
    /// <c>params_json</c> and notes untouched. Refused when the two kinds differ.
    /// </summary>
    Merge,

    /// <summary>Copy nothing for that measurement — neither it nor the results picked under it.</summary>
    Skip,
}

/// <summary>What became of one picked measurement. See <see cref="TransferredType"/>.</summary>
public enum TransferOutcome
{
    /// <summary>A new measurement was created in the destination, possibly under a suffixed name.</summary>
    Created,

    /// <summary>The results went under a measurement that was already there.</summary>
    Merged,

    /// <summary>Nothing was copied for it.</summary>
    Skipped,
}

/// <summary>
/// One measurement to copy and which of its saved results to take with it. An empty
/// <paramref name="SourceResultIds"/> is meaningful and allowed: it copies the measurement's
/// settings and none of its runs, which is what <c>Save As…</c> without results does for a whole
/// project.
/// </summary>
/// <param name="OnClash">
/// What to do if this measurement's name is taken in the destination, overriding the request's own
/// default. <see langword="null"/> — the usual case — takes that default. It is per measurement
/// because the answer is: the user can reasonably want one incoming measurement merged into the one
/// they already have and the next one kept separate, and that is exactly the decision a dialog
/// showing both names is in a position to make.
/// </param>
public sealed record TransferSelection(
    long SourceTypeId,
    IReadOnlyList<long> SourceResultIds,
    TypeNameClash? OnClash = null);

/// <summary>
/// What happened to one picked measurement, named on both sides so the client can report a rename
/// without having to reproduce the suffix rule.
/// </summary>
/// <param name="DestinationName">
/// The name it ended up under — equal to <paramref name="SourceName"/> unless it was renamed,
/// and <see langword="null"/> when it was skipped.
/// </param>
public sealed record TransferredType(
    long SourceTypeId,
    string SourceName,
    long? DestinationTypeId,
    string? DestinationName,
    TransferOutcome Outcome);

/// <summary>The outcome of one export or import, as the status bar reports it.</summary>
public sealed record TransferReport(
    IReadOnlyList<TransferredType> Types,
    int ResultsCopied,
    int CapturesCopied);

/// <summary>
/// One project file's measurements and saved results — the tree both the export and the import
/// dialog show. Produced for the open project and for a file that is not open, from the same code,
/// so the two dialogs cannot disagree about the shape.
/// </summary>
public sealed record ProjectContents(
    string Path,
    long ProjectId,
    string ProjectName,
    string? ProjectNotes,
    int SchemaVersion,
    IReadOnlyList<ProjectContentsType> Types);

/// <summary>One measurement in <see cref="ProjectContents"/>, with its live results.</summary>
public sealed record ProjectContentsType(
    long TypeId,
    string Kind,
    string Name,
    string? Notes,
    int SortOrder,
    IReadOnlyList<ProjectContentsResult> Results);

/// <summary>
/// One saved result in <see cref="ProjectContents"/>.
/// </summary>
/// <param name="ApproximateBytes">
/// Roughly what this result's stored blobs come to, so a dialog can warn before someone tries to
/// e-mail half a gigabyte. A <see langword="long"/>, deliberately: a <c>double</c> here could be
/// NaN or infinity, which <c>System.Text.Json</c> refuses to write <i>part way through</i> the
/// response — truncating a 200 mid-body, which the client sees as unparseable JSON and no error
/// anywhere (the rule <c>ImpedanceStatusJsonTests</c> exists for). Turning it into "512 MB" is the
/// client's job.
/// </param>
public sealed record ProjectContentsResult(
    long ResultId,
    string Name,
    DateTimeOffset Timestamp,
    string? Source,
    int Captures,
    long ApproximateBytes);
