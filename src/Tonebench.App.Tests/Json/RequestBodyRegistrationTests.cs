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
