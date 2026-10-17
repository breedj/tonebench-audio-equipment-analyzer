namespace Tonebench.App.Endpoints;

/// <summary>
/// Which measurement a runner's results belong to — the client's own "projectId:typeId" string,
/// which this code never parses. Blank or absent means nobody, the state a fresh process is in.
///
/// <para>One record for all eight runners rather than one per endpoint group. Every
/// <c>POST .../owner</c> body is this single field, and eight identical records would be eight
/// chances for one of them to drift into meaning something slightly different.
/// See <see cref="Services.ThdSweepRunner.SwitchOwner"/> for what the owner is for.</para>
/// </summary>
public sealed record RunOwnerRequest(string? Owner)
{
    /// <summary>The owner as the runners understand it: whitespace is nobody, not a measurement called " ".</summary>
    public string? Normalized => string.IsNullOrWhiteSpace(Owner) ? null : Owner;
}
