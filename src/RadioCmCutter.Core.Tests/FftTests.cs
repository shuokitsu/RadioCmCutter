using System.Numerics;
using RadioCmCutter.Core.Detection;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class FftTests
{
    [Fact]
    public void Forward_OfPureSineWave_HasPeakAtExpectedBin()
    {
        const int n = 64;
        const int targetBin = 5;
        var buffer = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            buffer[i] = new Complex(Math.Sin(2 * Math.PI * targetBin * i / n), 0);
        }

        Fft.Forward(buffer);

        var magnitudes = buffer.Take(n / 2).Select(c => c.Magnitude).ToArray();
        var peakBin = Array.IndexOf(magnitudes, magnitudes.Max());

        Assert.Equal(targetBin, peakBin);
    }

    [Fact]
    public void Forward_RejectsNonPowerOfTwoLength()
    {
        var buffer = new Complex[6];
        Assert.Throws<ArgumentException>(() => Fft.Forward(buffer));
    }
}
