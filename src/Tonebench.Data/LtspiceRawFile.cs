using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tonebench.Data;

/// <summary>Which simulator wrote a file, so advice can be given in the right dialect.</summary>
public enum SpiceFlavour
{
    Unknown,
    LTspice,
    QSpice,
}

/// <summary>Which kind of run a <c>.raw</c> file holds, from its <c>Plotname:</c> line.</summary>
public enum LtspiceAnalysis
{
    /// <summary>A <c>.tran</c> run — the only kind a simulation import can read.</summary>
    Transient,

    /// <summary>What the <c>.op.raw</c> sitting beside every simulation holds. No time variable at all.</summary>
    OperatingPoint,

    /// <summary>A <c>.ac</c> run. Complex, and against frequency rather than time.</summary>
    AcAnalysis,

    /// <summary>LTspice's own FFT export (<c>.fft</c>), which is a <c>.raw</c> container too.</summary>
    Fft,

    /// <summary>A DC sweep, a noise run, or anything else.</summary>
    Other,
}

/// <summary>One column of a <c>.raw</c> file.</summary>
/// <param name="Index">Its position in the header's variable table, which is also its position in a data point.</param>
/// <param name="Name">As LTspice spells it — <c>time</c>, <c>V(out)</c>, <c>I(C1)</c>.</param>
/// <param name="Type">LTspice's own type word: <c>time</c>, <c>voltage</c>, <c>device_current</c>.</param>
public sealed record LtspiceVariable(int Index, string Name, string Type)
{
    /// <summary>
    /// The SI unit this column is in. It has to travel with the samples: a <c>device_current</c>
    /// trace is in amps, and everything downstream of an import — the level tiles, the chart's dBV
    /// axis — is written for volts, so the only honest options are to label it or to refuse it.
    /// Empty when LTspice's type word is one we do not recognise.
    /// </summary>
    public string Unit => Type switch
    {
        "voltage" => "V",
        "device_current" or "subckt_current" or "branch_current" or "current" => "A",
        "time" => "s",
        "frequency" => "Hz",
        _ => "",
    };

    public bool IsTime => Type == "time";
}

/// <summary>
/// Everything the ASCII (well, UTF-16) part of a <c>.raw</c> file says, plus where the data starts
/// and how wide one point is.
/// </summary>
public sealed record LtspiceRawHeader(
    string Title,
    string? Date,
    string Plotname,
    LtspiceAnalysis Analysis,
    IReadOnlySet<string> Flags,
    int PointCount,
    double OffsetS,
    IReadOnlyList<LtspiceVariable> Variables,
    long DataOffset,
    int BytesPerPoint,
    bool IsBinary,
    string Command = "")
{
    /// <summary>
    /// What the <c>double</c> flag says: LTspice sets it for <c>.options numdgt=7</c>. Reported
    /// because it is worth telling the user about; <see cref="ValuesAreDouble"/> is what the
    /// reader actually believes.
    /// </summary>
    public bool IsDouble => Flags.Contains("double");

    /// <summary>
    /// Whether every column really is float64, resolved from the file's own size rather than from
    /// the flag.
    ///
    /// <para>The flag is not reliable across simulators. QSpice writes <c>Flags: real</c> with no
    /// <c>double</c> and then stores every column as float64 anyway -- measured, a 2-variable
    /// export is 16 bytes a point where the flag implies 12, and a 97-variable native run is 776
    /// where the flag implies 392. Trusting the flag there yields a column of plausible-looking
    /// nonsense shifted by four bytes a point, so the size is the authority and the flag is a
    /// hint.</para>
    /// </summary>
    public bool ValuesAreDouble { get; init; }

    /// <summary>An <c>.ac</c> run or an FFT: each value is a complex pair. Not importable here.</summary>
    public bool IsComplex => Flags.Contains("complex");

    /// <summary>Columns stored transposed instead of interleaved. Produced by the waveform viewer, not by a run.</summary>
    public bool IsFastAccess => Flags.Contains("fastaccess");

    /// <summary>
    /// False when LTspice compressed the waveform, i.e. <c>.options plotwinsize=0</c> was missing.
    /// Measured on a real amplifier run: compression kept 5.7 % of the points and made the same
    /// capture read THD 0.626 % against a true 0.378 %, with H3 4.2 dB wrong. It does not look
    /// broken, it looks like a worse amplifier, which is why this is the one flag worth refusing on.
    /// </summary>
    public bool IsUncompressed => Flags.Contains("nocompression");

    public LtspiceVariable? TimeVariable => Variables.FirstOrDefault(v => v.IsTime);

    /// <summary>
    /// Which simulator wrote this, from the <c>Command:</c> line — "QSPICE64, Build ..." or
    /// "Linear Technology Corporation LTspice". Only used to give advice in the right dialect:
    /// the directive that pins the timestep is spelled differently in each, and naming LTspice's
    /// at someone running QSpice is worse than saying nothing.
    /// </summary>
    public SpiceFlavour Flavour =>
        Command.Contains("QSPICE", StringComparison.OrdinalIgnoreCase) ? SpiceFlavour.QSpice
        : Command.Contains("LTspice", StringComparison.OrdinalIgnoreCase) ? SpiceFlavour.LTspice
        : SpiceFlavour.Unknown;
}

/// <summary>
/// Reads LTspice <c>.raw</c> transient files: the header, and selected columns by a strided pass
/// over the data block.
///
/// <para>The format details here were measured off four real files rather than taken from a
/// description of it, because two of them are commonly stated wrongly. <b>The header is UTF-16LE</b>
/// (an older LTspice wrote ASCII, so both are detected). And <b>the data layout follows the
/// <c>double</c> flag</b>: with it, every column including time is float64, so a point is
/// <c>8 * variables</c> bytes; without it, time is float64 and every other column float32, so a
/// point is <c>8 + 4 * (variables - 1)</c>. Verified by arithmetic against the files' own sizes —
/// 141 columns with the flag is 1128 bytes a point, 141 without is 568, 85 without is 344.</para>
///
/// <para>Nothing here refuses a file for being the wrong <i>kind</i> of run; that is the importing
/// service's job, since the messages belong with the thing that knows what the user asked for. This
/// class throws <see cref="FormatException"/> only when the bytes do not parse as a <c>.raw</c> file
/// at all.</para>
/// </summary>
public static class LtspiceRawFile
{
    /// <summary>
    /// How much of the file to read looking for the end of the header. A 141-variable header is
    /// about 9 KB; this leaves room for a few thousand variables without ever reading the data
    /// block, which on a real run is 100 MB.
    /// </summary>
    private const int MaxHeaderBytes = 4 * 1024 * 1024;

    private const string BinaryMarker = "Binary:";
    private const string ValuesMarker = "Values:";

    public static LtspiceRawHeader ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadHeader(stream, path);
    }

    internal static LtspiceRawHeader ReadHeader(Stream stream, string path)
    {
        long length = stream.Length;
        byte[] prefix = new byte[(int)Math.Min(length, MaxHeaderBytes)];
        int read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);

        bool utf16 = read >= 2 && prefix[0] != 0 && prefix[1] == 0;
        var encoding = utf16 ? Encoding.Unicode : Encoding.Latin1;
        int unitSize = utf16 ? 2 : 1;

        (int markerAt, bool isBinary) = FindMarker(prefix.AsSpan(0, read), encoding);
        if (markerAt < 0)
            throw new FormatException(
                $"{Path.GetFileName(path)} is not an LTspice .raw file: no \"Binary:\" or \"Values:\" line in the first {read} bytes.");

        string headerText = encoding.GetString(prefix, 0, markerAt);
        string marker = isBinary ? BinaryMarker : ValuesMarker;

        // The data begins immediately after the marker and its newline. LTspice writes a bare \n
        // here, not \r\n -- counted in encoding units, so one character is two bytes in UTF-16.
        long dataOffset = markerAt + ((marker.Length + 1) * (long)unitSize);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variables = new List<LtspiceVariable>();
        foreach (string rawLine in headerText.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            // A variable row is tab-indented: "\t<index>\t<name>\t<type>". Everything else is a
            // "Key: value" field, of which Backannotation: may legitimately repeat.
            if (line[0] == '\t' || line[0] == ' ')
            {
                string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out int index))
                    variables.Add(new LtspiceVariable(index, parts[1].Trim(), parts[2].Trim().ToLowerInvariant()));
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (!fields.ContainsKey(key))
                fields[key] = value;
        }

        string plotname = fields.GetValueOrDefault("Plotname", "").Trim();
        var flags = fields.GetValueOrDefault("Flags", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        int variableCount = ParseInt(fields, "No. Variables", path);
        int pointCount = ParseInt(fields, "No. Points", path);
        if (pointCount <= 0)
            throw new FormatException($"{Path.GetFileName(path)} says it holds {pointCount} points.");
        if (variables.Count != variableCount)
            throw new FormatException(
                $"{Path.GetFileName(path)} declares {variableCount} variables but its table lists {variables.Count}.");

        bool isComplex = flags.Contains("complex");
        bool isDouble = flags.Contains("double");

        // Two layouts are possible for a real run: every column float64, or float64 time with
        // float32 values. Which one a file uses is settled by its own size, not by the `double`
        // flag -- see ValuesAreDouble for why the flag cannot be trusted across simulators. The
        // flag only decides it when there is nothing to measure against (an ASCII body, or no
        // points).
        int allDouble = isComplex ? variableCount * 16 : variableCount * 8;
        int mixed = isComplex ? variableCount * 16 : 8 + ((variableCount - 1) * 4);
        bool valuesAreDouble = isDouble || isComplex;
        int bytesPerPoint = valuesAreDouble ? allDouble : mixed;

        if (isBinary && pointCount > 0)
        {
            long dataBytes = length - dataOffset;
            long actual = dataBytes / pointCount;
            if (actual == allDouble)
            {
                valuesAreDouble = true;
                bytesPerPoint = allDouble;
            }
            else if (actual == mixed)
            {
                valuesAreDouble = false;
                bytesPerPoint = mixed;
            }
            else
            {
                throw new FormatException(
                    $"{Path.GetFileName(path)} does not match either known layout: {variableCount} variables " +
                    $"is {allDouble} bytes per point if every column is double precision and {mixed} if only " +
                    $"the time column is, but {dataBytes} bytes of data over {pointCount} points is {actual}.");
            }
        }

        return new LtspiceRawHeader(
            Title: fields.GetValueOrDefault("Title", "").Trim(),
            Date: fields.GetValueOrDefault("Date"),
            Plotname: plotname,
            Analysis: ClassifyPlotname(plotname),
            Flags: flags,
            PointCount: pointCount,
            OffsetS: ParseDouble(fields, "Offset", path, defaultValue: 0.0),
            Variables: variables,
            DataOffset: dataOffset,
            BytesPerPoint: bytesPerPoint,
            IsBinary: isBinary,
            Command: fields.GetValueOrDefault("Command", "").Trim())
        {
            ValuesAreDouble = valuesAreDouble,
        };
    }

    /// <summary>
    /// Reads the given columns in one sequential pass. One pass rather than one per column because
    /// a real run is ~100 MB of which two or three columns of 141 are wanted, and a pass per column
    /// is a pass over the whole file per column.
    /// </summary>
    /// <remarks>
    /// The time column comes back as absolute seconds: <c>abs(stored) + Offset</c>. The offset is
    /// LTspice's <c>.tran</c> start-save time (5 ms in the measured file) and the stored values are
    /// relative to it, so without the add, "analyse from t =" would not mean the number the user
    /// reads in LTspice. The <c>abs</c> is because LTspice is known to set the sign bit on some
    /// stored time values; none of the measured files did, but a non-monotonic axis breaks the
    /// resampler and the guard costs nothing.
    /// </remarks>
    public static double[][] ReadColumns(
        string path, LtspiceRawHeader header, IReadOnlyList<int> variableIndices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(variableIndices);

        if (!header.IsBinary)
            throw new FormatException(
                $"{Path.GetFileName(path)} stores its data as text (\"Values:\"), which this import does not read. " +
                "Re-run the simulation so LTspice writes a binary .raw.");
        if (header.IsFastAccess)
            throw new FormatException(
                $"{Path.GetFileName(path)} is saved in LTspice's Fast Access format, which stores the columns " +
                "transposed. Re-run the simulation without converting the waveform file.");
        if (header.IsComplex)
            throw new FormatException(
                $"{Path.GetFileName(path)} holds complex data ({header.Plotname}), not a time-domain waveform.");

        foreach (int index in variableIndices)
        {
            if (index < 0 || index >= header.Variables.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(variableIndices), $"No variable {index} in {Path.GetFileName(path)}.");
        }

        var result = new double[variableIndices.Count][];
        for (int c = 0; c < result.Length; c++)
            result[c] = new double[header.PointCount];

        int bytesPerPoint = header.BytesPerPoint;

        // A whole number of points per buffer, so a point is never split across two reads. At least
        // one point even if a single point is larger than the nominal buffer.
        const int TargetBufferBytes = 1 << 20;
        int pointsPerBuffer = Math.Max(1, TargetBufferBytes / bytesPerPoint);
        byte[] buffer = new byte[(long)pointsPerBuffer * bytesPerPoint <= int.MaxValue
            ? pointsPerBuffer * bytesPerPoint
            : bytesPerPoint];

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0, FileOptions.SequentialScan);
        stream.Seek(header.DataOffset, SeekOrigin.Begin);

        int point = 0;
        while (point < header.PointCount)
        {
            ct.ThrowIfCancellationRequested();

            int wanted = Math.Min(pointsPerBuffer, header.PointCount - point);
            int bytes = wanted * bytesPerPoint;
            int got = stream.ReadAtLeast(buffer.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
            int complete = got / bytesPerPoint;
            if (complete == 0)
                throw new FormatException(
                    $"{Path.GetFileName(path)} ends after {point} of its declared {header.PointCount} points.");

            for (int c = 0; c < variableIndices.Count; c++)
            {
                int index = variableIndices[c];
                var variable = header.Variables[index];
                double[] destination = result[c];
                (int fieldOffset, bool isDouble) = FieldLayout(index, header.ValuesAreDouble);

                for (int p = 0; p < complete; p++)
                {
                    var span = buffer.AsSpan((p * bytesPerPoint) + fieldOffset);
                    double value = isDouble
                        ? BinaryPrimitives.ReadDoubleLittleEndian(span)
                        : BinaryPrimitives.ReadSingleLittleEndian(span);
                    destination[point + p] = variable.IsTime ? Math.Abs(value) + header.OffsetS : value;
                }
            }

            point += complete;
        }

        return result;
    }

    /// <summary>
    /// Where column <paramref name="index"/> sits inside one data point, and whether it is an f64.
    /// Time is an f64 even when the other columns are f32, which is what makes the stride
    /// <c>8 + 4*(n-1)</c> rather than <c>4*n</c>.
    /// </summary>
    private static (int Offset, bool IsDouble) FieldLayout(int index, bool allDouble) =>
        allDouble ? (index * 8, true)
        : index == 0 ? (0, true)
        : (8 + ((index - 1) * 4), false);

    private static (int At, bool IsBinary) FindMarker(ReadOnlySpan<byte> prefix, Encoding encoding)
    {
        int binaryAt = IndexOf(prefix, encoding.GetBytes(BinaryMarker));
        if (binaryAt >= 0)
            return (binaryAt, true);

        return (IndexOf(prefix, encoding.GetBytes(ValuesMarker)), false);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        int at = haystack.IndexOf(needle);
        return at;
    }

    private static LtspiceAnalysis ClassifyPlotname(string plotname)
    {
        if (plotname.StartsWith("Transient", StringComparison.OrdinalIgnoreCase))
            return LtspiceAnalysis.Transient;
        if (plotname.StartsWith("Operating Point", StringComparison.OrdinalIgnoreCase))
            return LtspiceAnalysis.OperatingPoint;
        if (plotname.StartsWith("AC Analysis", StringComparison.OrdinalIgnoreCase))
            return LtspiceAnalysis.AcAnalysis;
        if (plotname.StartsWith("FFT", StringComparison.OrdinalIgnoreCase))
            return LtspiceAnalysis.Fft;
        return LtspiceAnalysis.Other;
    }

    private static int ParseInt(Dictionary<string, string> fields, string key, string path)
    {
        if (!fields.TryGetValue(key, out string? text))
            throw new FormatException($"{Path.GetFileName(path)} has no \"{key}:\" line.");
        if (!int.TryParse(text.Trim(), CultureInfo.InvariantCulture, out int value))
            throw new FormatException($"{Path.GetFileName(path)} has \"{key}: {text}\", which is not a number.");
        return value;
    }

    private static double ParseDouble(Dictionary<string, string> fields, string key, string path, double defaultValue)
    {
        if (!fields.TryGetValue(key, out string? text))
            return defaultValue;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : defaultValue;
    }
}
