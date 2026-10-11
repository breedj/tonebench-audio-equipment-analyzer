namespace Tonebench.Data;

/// <summary>
/// Reads the step labels of a <c>.step</c>ped run out of the simulator's log file, which is the only
/// place they exist.
///
/// <para><b>They are not in the <c>.raw</c>.</b> Measured 2026-10-11 on four generated stepped runs:
/// the header says <c>stepped</c> and nothing more, so a file alone can say how many sweeps it holds
/// and never what distinguishes them. LTspice writes one line per step into the sibling
/// <c>.log</c> — <c>.step freq=1000</c>, <c>.step freq=2000</c>, in order — and that is what this
/// reads. QSpice prints the same thing to the console and writes no log file at all, so a QSpice
/// run yields nothing here and its sweeps are numbered instead.</para>
///
/// <para>Purely a label: nothing numeric is taken from it, and a missing, truncated or unreadable
/// log costs the user a name and not an import. Hence no exceptions out of here at all — a log that
/// cannot be read is the same as a log that is not there.</para>
/// </summary>
public static class SpiceStepLog
{
    /// <summary>The <c>.log</c> beside a <c>.raw</c>/<c>.qraw</c>, whether or not it exists.</summary>
    public static string LogPathFor(string rawPath) =>
        Path.ChangeExtension(rawPath, ".log");

    /// <summary>
    /// Each step's label, in the order the simulator ran them, or an empty list when there is no log
    /// to read. A label is the text after <c>.step </c>, e.g. <c>freq=1000</c>.
    /// </summary>
    public static IReadOnlyList<string> ReadLabels(string rawPath)
    {
        try
        {
            string logPath = LogPathFor(rawPath);
            if (!File.Exists(logPath))
                return [];

            var labels = new List<string>();
            foreach (string line in File.ReadLines(logPath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(".step ", StringComparison.OrdinalIgnoreCase))
                    continue;

                string label = trimmed[".step ".Length..].Trim();

                // The directive itself, not a result line: `.step param freq 1k 3k 1k` is what the
                // schematic asked for, where `.step freq=1000` is one run of it. LTspice's log holds
                // only the latter, but a netlist echoed into a log would hold the former, and
                // labelling three sweeps "param freq 1k 3k 1k" is worse than numbering them.
                if (label.Length > 0 && label.Contains('=', StringComparison.Ordinal))
                    labels.Add(label);
            }

            return labels;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
