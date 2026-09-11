using System.Numerics;

namespace RadioCmCutter.Core.Detection;

/// <summary>教科書的な反復版 Radix-2 Cooley-Tukey FFT。長さは2のべき乗であること。</summary>
internal static class Fft
{
    public static void Forward(Complex[] buffer)
    {
        var n = buffer.Length;
        if (n <= 1) return;
        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFTの長さは2のべき乗である必要があります。", nameof(buffer));
        }

        // ビット反転並び替え
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = buffer[i + k];
                    var v = buffer[i + k + (len / 2)] * w;
                    buffer[i + k] = u + v;
                    buffer[i + k + (len / 2)] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
