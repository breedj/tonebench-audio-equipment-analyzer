namespace Qa403.Dsp.Fft;

internal static class FftBackendValidation
{
    public static int ValidateAndGetSpecLength(int timeInLength, int specOutLength)
    {
        if (timeInLength == 0 || (timeInLength & (timeInLength - 1)) != 0)
            throw new ArgumentException("Input length must be a power of two.", nameof(timeInLength));

        int expected = timeInLength / 2 + 1;
        if (specOutLength != expected)
            throw new ArgumentException(
                $"specOut must have length {expected} (timeIn.Length / 2 + 1), got {specOutLength}.",
                nameof(specOutLength));

        return expected;
    }
}
