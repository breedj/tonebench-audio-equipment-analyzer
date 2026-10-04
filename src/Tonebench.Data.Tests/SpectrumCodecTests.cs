using Tonebench.Data;

namespace Tonebench.Data.Tests;

public class SpectrumCodecTests
{
    [Fact]
    public void RoundTrip_RandomDbValues_ErrorBelowHalfStep()
    {
        // Fixed-seed sweep over a realistic dBV range. Deliberately avoids values that land
        // exactly on a half-step (see Encode_MidpointValues_RoundsAwayFromZero) — those have an
        // error of exactly 0.005 dB, which a strict "< 0.005" assertion would reject.
        var random = new Random(20260808);
        var input = new double[10000];
        for (int i = 0; i < input.Length; i++)
            input[i] = random.NextDouble() * 220.0 - 200.0; // [-200, +20] dBV

        var bytes = new byte[SpectrumCodec.ByteLength(input.Length)];
        SpectrumCodec.EncodeTo(input, bytes);

        var output = new double[input.Length];
        SpectrumCodec.DecodeTo(bytes, output);

        for (int i = 0; i < input.Length; i++)
        {
            double error = Math.Abs(output[i] - input[i]);
            Assert.True(error < 0.005, $"bin {i}: input={input[i]}, output={output[i]}, error={error}");
        }
    }

    [Theory]
    [InlineData(100, 101)]   // db = 1.005 -> raw 101, not the decimal literal (not exactly representable)
    [InlineData(-100, -101)] // db = -1.005 -> raw -101
    [InlineData(0, 1)]       // db = 0.005 -> raw 1
    [InlineData(0, -1)]      // db = -0.005 -> raw -1
    public void Encode_MidpointValues_RoundsAwayFromZero(short nearZeroRaw, short expectedRaw)
    {
        // Constructed by averaging the two adjacent raw values, not from a decimal literal: 1.005
        // has no exact double representation, so a literal input lands fractionally short of the
        // true midpoint and rounds the "wrong" way. (near + away) / 2 * StepDb is the boundary the
        // codec actually sees, and is symmetric for both the positive and negative side.
        double midpointDb = (nearZeroRaw + expectedRaw) / 2.0 * SpectrumCodec.StepDb;

        var bytes = new byte[SpectrumCodec.ByteLength(1)];
        SpectrumCodec.EncodeTo([midpointDb], bytes);

        var output = new double[1];
        SpectrumCodec.DecodeTo(bytes, output);

        Assert.Equal(expectedRaw * SpectrumCodec.StepDb, output[0], 9);
    }

    [Theory]
    [InlineData(500.0, SpectrumCodec.MaxDb)]
    [InlineData(-500.0, SpectrumCodec.MinDb)]
    [InlineData(double.PositiveInfinity, SpectrumCodec.MaxDb)]
    [InlineData(double.NegativeInfinity, SpectrumCodec.MinDb)]
    public void Encode_OutOfRangeValues_Clamps(double input, double expectedDecoded)
    {
        var bytes = new byte[SpectrumCodec.ByteLength(1)];
        SpectrumCodec.EncodeTo([input], bytes);

        var output = new double[1];
        SpectrumCodec.DecodeTo(bytes, output);

        Assert.Equal(expectedDecoded, output[0], 9);
    }

    [Fact]
    public void Encode_ProducesTwoBytesPerBin()
    {
        var input = new double[517];
        var bytes = new byte[SpectrumCodec.ByteLength(input.Length)];

        SpectrumCodec.EncodeTo(input, bytes);

        Assert.Equal(input.Length * 2, bytes.Length);
        Assert.Equal(input.Length * 2, SpectrumCodec.ByteLength(input.Length));
    }
}
