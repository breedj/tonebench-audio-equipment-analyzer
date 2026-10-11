using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Tonebench.Data;

namespace Tonebench.Data.Tests;

/// <summary>
/// The .raw reader, against fixtures this file writes. The real amplifier run it was developed
/// against is 102 MB, so it is deliberately not a committed fixture; the facts measured off it are
/// asserted here as arithmetic instead — above all the two byte layouts, which are the thing a
/// plausible-looking misreading would get wrong silently.
/// </summary>
public class LtspiceRawFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tonebench-raw-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadHeader_ReadsTheFieldsAndTheVariableTable()
    {
        string path = WriteRaw("tran.raw", "Transient Analysis", "real forward nocompression",
            Columns(8, 3), offsetS: 0.005);

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.Equal("Transient Analysis", header.Plotname);
        Assert.Equal(LtspiceAnalysis.Transient, header.Analysis);
        Assert.Equal(8, header.PointCount);
        Assert.Equal(0.005, header.OffsetS, 1e-12);
        Assert.True(header.IsBinary);
        Assert.True(header.IsUncompressed);
        Assert.False(header.IsDouble);
        Assert.False(header.IsComplex);

        Assert.Equal(["time", "V(1)", "V(2)"], header.Variables.Select(v => v.Name));
        Assert.Equal(["s", "V", "V"], header.Variables.Select(v => v.Unit));
        Assert.Same(header.Variables[0], header.TimeVariable);
    }

    /// <summary>
    /// `Flags:` is a space-separated set, not a string to compare whole. The measured files carry
    /// `real forward` and `real double forward nocompression`, so anything matching on the whole
    /// line reads one of them as "not a transient" or misses `nocompression` entirely.
    /// </summary>
    [Fact]
    public void ReadHeader_TreatsFlagsAsASet_WhateverTheirOrder()
    {
        string path = WriteRaw("flags.raw", "Transient Analysis", "nocompression forward real double",
            Columns(4, 2));

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.True(header.IsDouble);
        Assert.True(header.IsUncompressed);
        Assert.Contains("real", header.Flags);
        Assert.Contains("forward", header.Flags);
    }

    /// <summary>
    /// The layout rule, asserted as the arithmetic measured off three real files: with the `double`
    /// flag a point is 8 bytes per variable, and without it time alone is 8 and the rest are 4.
    /// 141 variables came to 1128 and 568 bytes, and 85 to 344 — those three are in the table.
    /// </summary>
    [Theory]
    [InlineData(141, true, 1128)]
    [InlineData(141, false, 568)]
    [InlineData(85, false, 344)]
    [InlineData(41, false, 168)]
    [InlineData(2, true, 16)]
    [InlineData(2, false, 12)]
    public void ReadHeader_DerivesBytesPerPointFromTheDoubleFlag(int variables, bool isDouble, int expected)
    {
        string flags = isDouble ? "real double forward nocompression" : "real forward nocompression";
        string path = WriteRaw($"layout{variables}{isDouble}.raw", "Transient Analysis", flags,
            Columns(5, variables));

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.Equal(expected, header.BytesPerPoint);
        Assert.Equal(isDouble, header.IsDouble);
    }

    /// <summary>
    /// When the <c>double</c> flag and the file's own size disagree, the size wins.
    ///
    /// <para>This is not a tie-break for its own sake: <b>QSpice writes <c>Flags: real</c> with no
    /// <c>double</c> and then stores every column as float64 anyway</b> — measured on real files,
    /// a 2-variable export is 16 bytes a point where the flag implies 12, and a 97-variable native
    /// run is 776 where the flag implies 392. Believing the flag there reads every value from four
    /// bytes into the wrong field and yields a column of plausible-looking nonsense.</para>
    /// </summary>
    [Theory]
    [InlineData("real forward", true)]        // QSpice: says nothing, writes doubles
    [InlineData("real double forward", false)] // the opposite disagreement, for symmetry
    public void ReadHeader_WhenTheFlagDisagreesWithTheFile_BelievesTheFile(string flags, bool dataIsDouble)
    {
        var columns = Columns(8, 4);
        string path = WriteRaw($"disagree{dataIsDouble}.raw", "Transient Analysis", flags, columns,
            dataAsDouble: dataIsDouble);

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.Equal(dataIsDouble, header.ValuesAreDouble);
        Assert.Equal(dataIsDouble ? 32 : 20, header.BytesPerPoint);

        // And the columns really do come back, which is what the flag would have cost.
        double[][] read = LtspiceRawFile.ReadColumns(path, header, [2]);
        double tolerance = dataIsDouble ? 1e-12 : 1e-6;
        for (int i = 0; i < columns[2].Length; i++)
            Assert.Equal(columns[2][i], read[0][i], tolerance);
    }

    /// <summary>
    /// A size matching neither layout is still an error — that is the assertion which catches a
    /// genuinely corrupt or unknown file instead of handing back shifted garbage.
    /// </summary>
    [Fact]
    public void ReadHeader_WhenTheSizeMatchesNeitherLayout_SaysSo()
    {
        string path = WriteRaw("truncated.raw", "Transient Analysis", "real forward", Columns(10, 4));
        byte[] all = File.ReadAllBytes(path);
        // Lop off a few bytes so the data divides into neither 32 nor 20 per point.
        File.WriteAllBytes(path, all.AsSpan(0, all.Length - 7).ToArray());

        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadHeader(path));
        Assert.Contains("does not match either known layout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The writer, from the <c>Command:</c> line. Only used so advice names the right directive:
    /// telling someone running QSpice to add an LTspice option is worse than saying nothing.
    /// </summary>
    [Theory]
    [InlineData("Linear Technology Corporation LTspice", SpiceFlavour.LTspice)]
    [InlineData("QSPICE64, Build Oct  6 2026 12:09:27", SpiceFlavour.QSpice)]
    [InlineData("QSPICE, Build Oct 10 2026 20:05:48", SpiceFlavour.QSpice)]
    [InlineData("something else entirely", SpiceFlavour.Unknown)]
    public void ReadHeader_NamesTheSimulatorThatWroteIt(string command, SpiceFlavour expected)
    {
        string path = WriteRaw($"flavour{expected}.raw", "Transient Analysis", "real forward",
            Columns(4, 2), command: command);

        Assert.Equal(expected, LtspiceRawFile.ReadHeader(path).Flavour);
    }

    [Theory]
    [InlineData("Transient Analysis", LtspiceAnalysis.Transient)]
    [InlineData("Operating Point", LtspiceAnalysis.OperatingPoint)]
    [InlineData("AC Analysis", LtspiceAnalysis.AcAnalysis)]
    [InlineData("FFT of time domain data", LtspiceAnalysis.Fft)]
    [InlineData("DC transfer characteristic", LtspiceAnalysis.Other)]
    public void ReadHeader_ClassifiesThePlotname(string plotname, LtspiceAnalysis expected)
    {
        string path = WriteRaw("plot.raw", plotname, "real forward", Columns(4, 2));

        Assert.Equal(expected, LtspiceRawFile.ReadHeader(path).Analysis);
    }

    /// <summary>
    /// The measured amplifier file carries eleven `Backannotation:` lines between `Command:` and
    /// `Variables:`. They are not fields, they repeat, and a reader that stops at the first
    /// unrecognised line never reaches the variable table.
    /// </summary>
    [Fact]
    public void ReadHeader_SkipsRepeatedBackannotationLines()
    {
        string path = WriteRaw("backannotation.raw", "Transient Analysis", "real forward", Columns(4, 2),
            extraHeaderLines: ["Backannotation: u5 1 2 3 4", "Backannotation: u2:x1 1 2 3", "Backannotation: u1 1 2 3 4"]);

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.Equal(2, header.Variables.Count);
        Assert.Equal("V(1)", header.Variables[1].Name);
    }

    [Fact]
    public void ReadHeader_ReadsAnAsciiHeaderAsWellAsAUtf16One()
    {
        string path = WriteRaw("ascii.raw", "Transient Analysis", "real forward", Columns(4, 2), utf16: false);

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.Equal(LtspiceAnalysis.Transient, header.Analysis);
        Assert.Equal(2, header.Variables.Count);
        Assert.Equal(4, header.PointCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadColumns_ReturnsTheRequestedColumnsInTheOrderAsked(bool isDouble)
    {
        var columns = Columns(16, 4);
        string flags = isDouble ? "real double forward" : "real forward";
        string path = WriteRaw($"cols{isDouble}.raw", "Transient Analysis", flags, columns);
        var header = LtspiceRawFile.ReadHeader(path);

        double[][] read = LtspiceRawFile.ReadColumns(path, header, [3, 1]);

        // f32 storage rounds, so compare at single precision when that is what was written.
        double tolerance = isDouble ? 1e-12 : 1e-6;
        for (int p = 0; p < columns[0].Length; p++)
        {
            Assert.Equal(columns[3][p], read[0][p], tolerance);
            Assert.Equal(columns[1][p], read[1][p], tolerance);
        }
    }

    /// <summary>
    /// Time comes back as absolute seconds. `Offset:` is the `.tran` start-save time (5 ms in the
    /// measured file) and the stored values are relative to it, so "analyse from t =" would
    /// otherwise not mean the number the user reads in LTspice. The `abs` guards LTspice's habit of
    /// setting the sign bit on some stored time values, which would make the axis non-monotonic.
    /// </summary>
    [Fact]
    public void ReadColumns_ReturnsTimeAsAbsoluteSeconds_OffsetAddedAndSignBitIgnored()
    {
        var columns = new[]
        {
            new[] { 0.0, -1e-6, 2e-6, -3e-6 },
            new[] { 1.0, 2.0, 3.0, 4.0 },
        };
        string path = WriteRaw("time.raw", "Transient Analysis", "real double forward", columns, offsetS: 0.005);
        var header = LtspiceRawFile.ReadHeader(path);

        double[][] read = LtspiceRawFile.ReadColumns(path, header, [0]);

        Assert.Equal([0.005, 0.005001, 0.005002, 0.005003], read[0].Select(t => Math.Round(t, 9)));
        Assert.True(read[0].Zip(read[0].Skip(1)).All(p => p.Second > p.First), "time must come back monotonic");
    }

    /// <summary>
    /// The reader buffers a whole number of points at a time, so a point is never split across two
    /// reads. This drives far more points than one buffer holds — the measured file has 95004 — and
    /// a stride mishandled at a buffer boundary shows up as a discontinuity rather than as an error.
    /// </summary>
    [Fact]
    public void ReadColumns_AcrossManyBufferFills_StaysContinuous()
    {
        const int points = 200_000;
        var columns = new[] { new double[points], new double[points] };
        for (int p = 0; p < points; p++)
        {
            columns[0][p] = p * 1e-6;
            columns[1][p] = p;
        }

        string path = WriteRaw("long.raw", "Transient Analysis", "real double forward", columns);
        var header = LtspiceRawFile.ReadHeader(path);

        double[][] read = LtspiceRawFile.ReadColumns(path, header, [1]);

        Assert.Equal(points, read[0].Length);
        for (int p = 0; p < points; p++)
            Assert.Equal(p, read[0][p], 1e-9);
    }

    [Fact]
    public void ReadColumns_OnAComplexFile_SaysSoRatherThanReadingNonsense()
    {
        string path = WriteRaw("ac.raw", "AC Analysis", "complex forward", Columns(4, 2), complexData: true);
        var header = LtspiceRawFile.ReadHeader(path);

        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadColumns(path, header, [1]));
        Assert.Contains("complex", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadColumns_OnAFastAccessFile_SaysSo()
    {
        string path = WriteRaw("fast.raw", "Transient Analysis", "real double forward fastaccess", Columns(4, 2));
        var header = LtspiceRawFile.ReadHeader(path);

        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadColumns(path, header, [1]));
        Assert.Contains("Fast Access", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadColumns_OnATextValuesFile_SaysSo()
    {
        string path = WriteRaw("text.raw", "Transient Analysis", "real forward", Columns(4, 2), textValues: true);
        var header = LtspiceRawFile.ReadHeader(path);

        Assert.False(header.IsBinary);
        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadColumns(path, header, [1]));
        Assert.Contains("text", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadHeader_WithNoMarkerLine_IsNotARawFile()
    {
        string path = Path.Combine(_dir, "garbage.raw");
        File.WriteAllText(path, "this is not a raw file at all");

        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadHeader(path));
        Assert.Contains("not an LTspice .raw file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("voltage", "V")]
    [InlineData("device_current", "A")]
    [InlineData("time", "s")]
    [InlineData("something_else", "")]
    public void Variable_CarriesTheUnitItsTypeImplies(string type, string expected) =>
        Assert.Equal(expected, new LtspiceVariable(0, "x", type).Unit);

    /// <summary>
    /// A complex run has two layouts of its own, and they differ only in the abscissa: LTspice
    /// gives frequency an imaginary part like every other column, QSpice writes it as a bare f64.
    /// Measured on the real files -- 141 variables is 2256 bytes a point one way, 97 variables is
    /// 1544 the other -- and the only thing that can tell them apart is the file's own size.
    /// </summary>
    [Theory]
    [InlineData(false, 4, 64)]   // LTspice: 4 * 16
    [InlineData(true, 4, 56)]    // QSpice:  8 + 3 * 16
    public void ReadHeader_OnAComplexFile_ResolvesWhichAbscissaLayoutFromTheSize(
        bool bareAbscissa, int variables, int expectedBytesPerPoint)
    {
        string path = WriteRaw(
            $"ac-{bareAbscissa}.raw", "AC Analysis", "complex forward", Columns(8, variables),
            complexData: true, abscissa: "frequency", bareAbscissa: bareAbscissa);

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.True(header.IsComplex);
        Assert.Equal(expectedBytesPerPoint, header.BytesPerPoint);
        Assert.Equal(!bareAbscissa, header.AbscissaIsComplex);
    }

    /// <summary>
    /// The `double` flag is absent from QSpice's AC header and its values are f64 pairs anyway --
    /// the same lie the transient path already measures around. Both complex layouts are
    /// all-double, so the flag can say nothing and still be right.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadHeader_OnAComplexFile_IsAllDoubleWhateverTheFlagSays(bool bareAbscissa)
    {
        string path = WriteRaw(
            $"ac-flagless-{bareAbscissa}.raw", "AC Analysis", "complex", Columns(8, 4),
            complexData: true, abscissa: "frequency", bareAbscissa: bareAbscissa);

        Assert.True(LtspiceRawFile.ReadHeader(path).ValuesAreDouble);
    }

    [Fact]
    public void ReadHeader_OnAComplexFileOfNeitherLayout_NamesBothShapes()
    {
        // Written in LTspice's layout (8 points of 4 complex columns = 64 bytes each), then given
        // eight stray bytes so the data divides to 65 and matches neither candidate.
        string path = WriteRaw(
            "ac-wrong.raw", "AC Analysis", "complex forward", Columns(8, 4),
            complexData: true, abscissa: "frequency");
        using (var stream = new FileStream(path, FileMode.Append))
            stream.Write(new byte[8]);

        var ex = Assert.Throws<FormatException>(() => LtspiceRawFile.ReadHeader(path));

        // The message has to name both complex shapes and the figure that fits neither, or the
        // next simulator's dialect is a mystery instead of a diagnosis.
        Assert.Contains("imaginary part", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Contains("56", ex.Message, StringComparison.Ordinal);
        Assert.Contains("65", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadHeader_FindsTheFrequencyVariable_WhateverTheSimulatorCallsIt()
    {
        // QSpice spells the name "Frequency" and LTspice "frequency"; both write the type word
        // "frequency", which is what IsFrequency keys off.
        string path = WriteRaw(
            "ac-named.raw", "AC Analysis", "complex forward", Columns(4, 3),
            complexData: true, abscissa: "frequency", abscissaName: "Frequency");

        var header = LtspiceRawFile.ReadHeader(path);

        Assert.NotNull(header.FrequencyVariable);
        Assert.Equal(0, header.FrequencyVariable!.Index);
        Assert.Equal("Hz", header.FrequencyVariable.Unit);
        Assert.Null(header.TimeVariable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadComplexColumns_ReturnsBothPartsOfEachColumn(bool bareAbscissa)
    {
        var real = Columns(6, 3);
        var imaginary = Columns(6, 3);
        for (int v = 1; v < 3; v++)
        {
            for (int p = 0; p < 6; p++)
                imaginary[v][p] = -(v + (p * 0.5));
        }

        string path = WriteRaw(
            $"ac-read-{bareAbscissa}.raw", "AC Analysis", "complex forward", real,
            complexData: true, abscissa: "frequency", bareAbscissa: bareAbscissa, imaginary: imaginary);

        var header = LtspiceRawFile.ReadHeader(path);
        var columns = LtspiceRawFile.ReadComplexColumns(path, header, [0, 2]);

        for (int p = 0; p < 6; p++)
        {
            Assert.Equal(real[0][p], columns[0][p].Real, 12);
            Assert.Equal(0.0, columns[0][p].Imaginary);       // the abscissa is handed back real-only
            Assert.Equal(real[2][p], columns[1][p].Real, 12);
            Assert.Equal(imaginary[2][p], columns[1][p].Imaginary, 12);
        }
    }

    /// <summary>
    /// <c>Offset:</c> is the transient start-save time. Adding it to a frequency would be reading a
    /// time-domain field on a sweep, so the complex reader must leave the abscissa alone.
    /// </summary>
    [Fact]
    public void ReadComplexColumns_DoesNotAddTheTransientOffsetToTheFrequency()
    {
        var real = Columns(4, 2);
        string path = WriteRaw(
            "ac-offset.raw", "AC Analysis", "complex forward", real,
            offsetS: 5e-3, complexData: true, abscissa: "frequency");

        var columns = LtspiceRawFile.ReadComplexColumns(path, LtspiceRawFile.ReadHeader(path), [0]);

        for (int p = 0; p < 4; p++)
            Assert.Equal(real[0][p], columns[0][p].Real, 12);
    }

    [Fact]
    public void ReadComplexColumns_OnARealFile_SaysSo()
    {
        string path = WriteRaw("real.raw", "Transient Analysis", "real forward", Columns(4, 2));

        var ex = Assert.Throws<FormatException>(
            () => LtspiceRawFile.ReadComplexColumns(path, LtspiceRawFile.ReadHeader(path), [0]));
        Assert.Contains("real data", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both simulators announce a <c>.step</c>ped run in the header, which is the one thing about
    /// these files that needs no inference. Measured 2026-10-11 on four generated runs: LTspice
    /// writes <c>complex double forward log nocompression stepped</c> and
    /// <c>real double forward nocompression stepped</c>, QSpice <c>complex stepped</c> and
    /// <c>real stepped</c>.
    /// </summary>
    [Theory]
    [InlineData("real double forward nocompression stepped", true)]
    [InlineData("complex stepped", true)]
    [InlineData("real double forward nocompression", false)]
    public void ReadHeader_ReadsTheSteppedFlag(string flags, bool expected)
    {
        string path = WriteRaw(
            $"stepflag-{expected}-{flags.Length}.raw", "Transient Analysis", flags, Columns(4, 2),
            complexData: flags.Contains("complex", StringComparison.Ordinal));

        Assert.Equal(expected, LtspiceRawFile.ReadHeader(path).IsStepped);
    }

    /// <summary>
    /// The sweeps of a stepped run are <b>not the same length</b> — measured, a
    /// <c>.step param freq 1k 3k 1k</c> transient came back as 5353 / 5682 / 5964 points from
    /// LTspice and 5311 / 5604 / 5949 from QSpice, because each run takes the timesteps it needs.
    /// So the boundaries have to be read off the axis; dividing the point count by the number of
    /// steps is right only for an <c>.ac</c> run, and silently wrong for every transient.
    /// </summary>
    [Fact]
    public void SweepStarts_FindsEveryRestart_WhateverTheSweepLengths()
    {
        double[] axis = [1e-6, 2e-6, 3e-6, 1e-6, 2e-6, 1e-6, 2e-6, 3e-6, 4e-6];

        Assert.Equal([0, 3, 5], LtspiceRawFile.SweepStarts(axis));
    }

    /// <summary>
    /// One sweep is one entry rather than none, so a caller never has to special-case the ordinary
    /// file. A repeated value counts as a restart too: a flat run of the abscissa is not a sweep
    /// continuing, and the readers downstream need every segment strictly increasing.
    /// </summary>
    [Fact]
    public void SweepStarts_OnAnOrdinaryRun_IsASingleEntry()
    {
        Assert.Equal([0], LtspiceRawFile.SweepStarts([0.0, 1e-6, 2e-6]));
        Assert.Equal([], LtspiceRawFile.SweepStarts([]));
        Assert.Equal([0, 2], LtspiceRawFile.SweepStarts([0.0, 1e-6, 1e-6, 2e-6]));
    }

    // ---- fixture writing ----

    private static double[][] Columns(int points, int variables)
    {
        var columns = new double[variables][];
        for (int v = 0; v < variables; v++)
        {
            columns[v] = new double[points];
            for (int p = 0; p < points; p++)
                columns[v][p] = v == 0 ? p * 1e-6 : (v * 100) + (p * 0.25);
        }
        return columns;
    }

    private string WriteRaw(
        string name,
        string plotname,
        string flags,
        double[][] columns,
        double offsetS = 0.0,
        bool utf16 = true,
        bool complexData = false,
        bool textValues = false,
        bool? dataAsDouble = null,
        string command = "Linear Technology Corporation LTspice",
        IReadOnlyList<string>? extraHeaderLines = null,
        string abscissa = "time",
        string? abscissaName = null,
        bool bareAbscissa = false,
        double[][]? imaginary = null)
    {
        int variables = columns.Length;
        int points = columns[0].Length;
        var flagSet = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        bool isDouble = dataAsDouble ?? flagSet.Contains("double");

        var header = new StringBuilder();
        header.Append("Title: * test fixture\n");
        header.Append("Date: Sun Oct 11 11:34:26 2026\n");
        header.Append(CultureInfo.InvariantCulture, $"Plotname: {plotname}\n");
        header.Append(CultureInfo.InvariantCulture, $"Flags: {flags}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Variables: {variables}\n");
        header.Append(CultureInfo.InvariantCulture, $"No. Points: {points,12}\n");
        header.Append(CultureInfo.InvariantCulture, $"Offset:   {offsetS.ToString("0.0000000000000000e+00", CultureInfo.InvariantCulture)}\n");
        header.Append(CultureInfo.InvariantCulture, $"Command: {command}\n");
        foreach (string line in extraHeaderLines ?? [])
            header.Append(CultureInfo.InvariantCulture, $"{line}\n");
        header.Append("Variables:\n");
        for (int v = 0; v < variables; v++)
        {
            string varName = v == 0 ? abscissaName ?? abscissa : $"V({v})";
            string varType = v == 0 ? abscissa : "voltage";
            header.Append(CultureInfo.InvariantCulture, $"\t{v}\t{varName}\t{varType}\n");
        }
        header.Append(textValues ? "Values:\n" : "Binary:\n");

        var encoding = utf16 ? Encoding.Unicode : (Encoding)Encoding.Latin1;
        string path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        stream.Write(encoding.GetBytes(header.ToString()));

        if (textValues)
        {
            stream.Write(encoding.GetBytes("0\t0.0\n"));
            return path;
        }

        int bytesPerPoint = complexData
            ? (bareAbscissa ? 8 + ((variables - 1) * 16) : variables * 16)
            : isDouble ? variables * 8 : 8 + ((variables - 1) * 4);
        int abscissaBytes = bareAbscissa ? 8 : 16;
        var point = new byte[bytesPerPoint];
        for (int p = 0; p < points; p++)
        {
            for (int v = 0; v < variables; v++)
            {
                if (complexData)
                {
                    // The abscissa is a complex pair on LTspice and a bare f64 on QSpice, which is
                    // what shifts every value column after it by eight bytes.
                    int at = v == 0 ? 0 : abscissaBytes + ((v - 1) * 16);
                    BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(at), columns[v][p]);
                    if (v > 0 || !bareAbscissa)
                    {
                        double im = v > 0 && imaginary is not null ? imaginary[v][p] : 0.0;
                        BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(at + 8), im);
                    }
                }
                else if (isDouble)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(v * 8), columns[v][p]);
                }
                else if (v == 0)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(0), columns[v][p]);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(point.AsSpan(8 + ((v - 1) * 4)), (float)columns[v][p]);
                }
            }
            stream.Write(point);
        }

        return path;
    }
}
