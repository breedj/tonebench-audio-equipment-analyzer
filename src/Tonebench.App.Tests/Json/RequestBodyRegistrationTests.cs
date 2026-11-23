using System.Text.RegularExpressions;

namespace Tonebench.App.Tests.Json;

/// <summary>
/// Every request body a minimal-API route binds has to be registered in <c>AppJsonContext</c>, and
/// nothing but a published build says so.
///
/// <para><b>What this costs when it is missing.</b> A published build turns reflection-based
/// serialization off, so the minimal-API source generator resolves each body type through the
/// source-generated context — and it does that while <i>building the route table</i>, not while
/// handling a request. One unregistered type therefore takes down every endpoint in the
/// application: the first request throws
/// <c>NotSupportedException: JsonTypeInfo metadata for type '...' was not provided</c> out of the
/// routing middleware, and every request after it does the same. From outside, the app starts,
/// logs a clean startup, and then answers nothing at all.</para>
///
/// <para>Measured on 2026-11-14: <c>ClockDriftRequest</c> reached main unregistered and the
/// v0.8.0 release failed, both package jobs reporting only <c>App did not respond within 20s</c>.
/// The whole non-hardware suite passed in the same run, on all three platforms — a dev build keeps
/// reflection enabled, so there is no configuration short of <c>dotnet publish</c> in which the
/// fault exists. That is the gap this test closes.</para>
///
/// <para>A text audit, like <c>UdevRulesTests</c>, and for the same reason: the registration is an
/// attribute on a partial class, so there is no runtime surface to reflect over that would not
/// already have thrown.</para>
/// </summary>
public class RequestBodyRegistrationTests
{
    /// <summary>
    /// Types whose names end this way are request bodies by this codebase's own convention; a body
    /// parameter named anything else would not be recognised here, which is why the pattern is
    /// stated rather than inferred.
    /// </summary>
    private static readonly string[] BodySuffixes = ["Request", "Config", "Settings", "Body"];

    [Fact]
    public void EveryRequestBodyType_IsRegisteredInTheJsonContext()
    {
        string root = RepositoryRoot();
        var registered = RegisteredTypeNames(Path.Combine(root, "src", "Tonebench.App", "Json", "AppJsonContext.cs"));

        var unregistered = new List<string>();

        foreach (string file in Directory.GetFiles(Path.Combine(root, "src", "Tonebench.App", "Endpoints"), "*.cs"))
        {
            string source = File.ReadAllText(file);

            // The body is the lambda's first parameter: Map{Post,Put,Patch}("route", (Type name, ...
            foreach (Match match in Regex.Matches(
                source,
                @"Map(?:Post|Put|Patch)\(\s*""[^""]*""\s*,\s*(?:async\s*)?\(\s*\[?[^)]*?\]?\s*([A-Z]\w+)\s+\w+"))
            {
                string type = match.Groups[1].Value;
                if (!BodySuffixes.Any(suffix => type.EndsWith(suffix, StringComparison.Ordinal)))
                    continue;

                if (!registered.Contains(type))
                    unregistered.Add($"{type} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(
            unregistered.Count == 0,
            "These request bodies are bound by a route but are not in AppJsonContext, so a published "
                + "build will fail to build its route table and answer nothing at all:"
                + Environment.NewLine + string.Join(Environment.NewLine, unregistered.Distinct().Order()));
    }


    /// <summary>
    /// And every measurement kind’s <c>*Parameters</c> record, which the net above does not reach.
    ///
    /// <para>Those are serialized straight to <c>params_json</c> by <c>MeasurementSaveService</c>
    /// rather than bound from a route, so they end in <c>Parameters</c> and carry none of the
    /// suffixes the request-body audit looks for — and most of them are never returned from an
    /// endpoint either, so nothing else drags them into the context by reference.</para>
    ///
    /// <para><b>Measured 2026-11-22.</b> <c>OscilloscopeParameters</c> and
    /// <c>ImpedanceParameters</c> both reached 0.10.0 unregistered, so in every published build
    /// <c>POST /api/projects/{id}/measurement-types</c> answered <b>500</b> for those two kinds:
    /// the seed path serializes the record through the app’s own options, a published build has
    /// reflection off, and the chain is then <c>AppJsonContext</c> alone. Reported from a bench as
    /// "Oscilloscope fails HTTP 500 on QA402", which is why it is worth saying that the hardware had
    /// nothing to do with it — the measurement could not be <i>created</i>, so nothing on the scope
    /// path ever ran. It is narrower than the request-body fault, which takes down every endpoint at
    /// once; here the rest of the app works, which is what made it read as a device problem.</para>
    ///
    /// <para>A dev build keeps reflection enabled and answers 200, so there is no configuration
    /// short of <c>dotnet publish</c> in which the fault exists — the same gap the audit above
    /// closes, and the reason both are text audits.</para>
    /// </summary>
    [Fact]
    public void EveryMeasurementParametersType_IsRegisteredInTheJsonContext()
    {
        string root = RepositoryRoot();
        var registered = RegisteredTypeNames(Path.Combine(root, "src", "Tonebench.App", "Json", "AppJsonContext.cs"));

        var declared = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src", "Tonebench.Data"), "*.cs"))
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(file),
                @"public\s+(?:sealed\s+)?record\s+(\w+Parameters)\b"))
            {
                declared.Add(match.Groups[1].Value);
            }
        }

        Assert.True(declared.Count >= 13, $"Only {declared.Count} *Parameters records were found; the parse is wrong.");

        var unregistered = declared.Distinct().Where(type => !registered.Contains(type)).Order().ToList();

        Assert.True(
            unregistered.Count == 0,
            "These measurement parameter records are serialized to params_json but are not in "
                + "AppJsonContext, so creating a measurement of that kind answers 500 in a published "
                + "build:" + Environment.NewLine + string.Join(Environment.NewLine, unregistered));
    }


    /// <summary>
    /// And no call site may serialize through options it built for itself, because registering a
    /// type does nothing for one that never consults the context.
    ///
    /// <para><c>JsonSerializerOptions.Web</c> and a bare <c>new JsonSerializerOptions(...)</c> both
    /// carry <b>no</b> <c>TypeInfoResolver</c>, so they reach for reflection — which a published
    /// build has turned off. The throw is then <c>InvalidOperationException: Reflection-based
    /// serialization has been disabled</c>, a different exception from the registration gap’s
    /// <c>NotSupportedException</c>, and one that a <c>catch (JsonException)</c> written around the
    /// read does not stop.</para>
    ///
    /// <para><b>Measured 2026-11-22.</b> <c>ProjectsEndpoints.ReadGlobalFixtureAsync</c> read the
    /// stored impedance fixture this way. <c>ImpedanceFixtureConfig</c> <i>was</i> registered, so
    /// the audit above was satisfied and creating an Impedance measurement still answered 500 in
    /// every published build — which is the whole reason this is a third test rather than a wider
    /// net on the second. Every other config document in the App layer is read as
    /// <c>Deserialize(stored, AppJsonContext.Default.XConfig)</c>; that one site had departed from
    /// it.</para>
    ///
    /// <para>The options ASP.NET hands us in <c>Program.cs</c> are configured rather than
    /// constructed, so they are not what this looks for.</para>
    /// </summary>
    [Fact]
    public void TheAppLayer_NeverBuildsItsOwnSerializerOptions()
    {
        string root = RepositoryRoot();
        string appRoot = Path.Combine(root, "src", "Tonebench.App");

        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            // bin/ and obj/ hold generated copies of the same sources, which would report twice.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Regex.IsMatch(line, @"JsonSerializerOptions\.Web\b")
                    || Regex.IsMatch(line, @"new\s+JsonSerializerOptions\s*[({]"))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These serialize through options that carry no TypeInfoResolver, so they reach for "
                + "reflection and throw in a published build however well the type is registered. "
                + "Use AppJsonContext.Default.<Type>:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The audit is only worth anything if it is actually reading the two files, so this fails
    /// loudly on an empty read rather than passing vacuously — the mistake that makes a text audit
    /// worse than no test at all.
    /// </summary>
    [Fact]
    public void TheAudit_ReadsBothSidesOfTheComparison()
    {
        string root = RepositoryRoot();

        var registered = RegisteredTypeNames(Path.Combine(root, "src", "Tonebench.App", "Json", "AppJsonContext.cs"));
        Assert.True(registered.Count > 100, $"Only {registered.Count} registered types were found; the parse is wrong.");
        Assert.Contains("ClockDriftRequest", registered);

        string[] endpoints = Directory.GetFiles(Path.Combine(root, "src", "Tonebench.App", "Endpoints"), "*.cs");
        Assert.True(endpoints.Length > 10, $"Only {endpoints.Length} endpoint files were found; the path is wrong.");

        // And the other audit's own side of it: a renamed or moved Tonebench.Data would make it
        // pass by finding nothing, which is the one way a text audit is worse than no test.
        string[] data = Directory.GetFiles(Path.Combine(root, "src", "Tonebench.Data"), "*.cs");
        Assert.True(data.Length > 5, $"Only {data.Length} Tonebench.Data files were found; the path is wrong.");
        Assert.Contains("OscilloscopeParameters", registered);
    }

    private static HashSet<string> RegisteredTypeNames(string contextPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(File.ReadAllText(contextPath), @"JsonSerializable\(typeof\(([^)<,]+)"))
        {
            string name = match.Groups[1].Value.Trim();
            int dot = name.LastIndexOf('.');
            names.Add(dot >= 0 ? name[(dot + 1)..] : name);
        }

        return names;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tonebench.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
