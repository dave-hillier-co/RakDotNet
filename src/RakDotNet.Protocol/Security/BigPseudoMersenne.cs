namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::BigPseudoMersenne (math/BigPseudoMersenne.hpp, src/math/mersenne): arithmetic modulo p = 2^bits - C.
/// Results stay in [0, 2^bits) and may be one modulus too large until <see cref="MrReduce"/>, exactly as in cat.
/// </summary>
internal class BigPseudoMersenne : BigRtl
{
    private const int PmOverhead = 6; // BigPseudoMersenne.hpp:49, overhead for MrSquareRoot()

    private readonly int _pmRegs;
    private readonly ulong _modulusC;

    public BigPseudoMersenne(int regs, int bits, int c)
        : base(regs + PmOverhead, bits)
    {
        _pmRegs = regs + PmOverhead;
        _modulusC = (ulong)c;
        CopyModulus(Get(_pmRegs - 1));
    }

    /// <summary>The cached modulus p.</summary>
    public ReadOnlySpan<ulong> Modulus => Get(_pmRegs - 1)[..Legs];

    public void CopyModulus(Span<ulong> output)
    {
        output[0] = 0 - _modulusC;
        output[1..Legs].Fill(ulong.MaxValue);
    }

    // mersenne/reduce/MrReduce.cpp: subtract the modulus once if the input is at least the modulus.
    public void MrReduce(Span<ulong> inout)
    {
        for (var i = 1; i < Legs; ++i)
            if (~inout[i] != 0) return;
        if (inout[0] >= 0 - _modulusC)
            AddX(inout, _modulusC);
    }

    // mersenne/addsub/MrAdd.cpp
    public void MrAddX(Span<ulong> inout, ulong x)
    {
        if (AddX(inout, x) != 0)
            while (AddX(inout, _modulusC) != 0) { }
    }

    public void MrAdd(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        if (Add(a, b, output) != 0)
            while (AddX(output, _modulusC) != 0) { }
    }

    public void MrDouble(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        if (Double(input, output) != 0)
            while (AddX(output, _modulusC) != 0) { }
    }

    // mersenne/addsub/MrSubtract.cpp
    public void MrSubtractX(Span<ulong> inout, ulong x)
    {
        if (SubtractX(inout, x) != 0)
            while (SubtractX(inout, _modulusC) != 0) { }
    }

    public void MrSubtract(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        if (Subtract(a, b, output) != 0)
            while (SubtractX(output, _modulusC) != 0) { }
    }

    // mersenne/addsub/MrNegate.cpp: output = m - input = ~input - (c - 1)
    public void MrNegate(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        var t = ~input[0];
        var x = _modulusC - 1;
        output[0] = t - x;
        var i = 1;
        if (t < x)
        {
            while (i < Legs)
            {
                t = ~input[i];
                output[i++] = t - 1;
                if (t != 0) break;
            }
        }
        for (; i < Legs; ++i)
            output[i] = ~input[i];
    }

    // mersenne/mul/MrMultiply.cpp, MrSquare.cpp, MrMultiplyX.cpp
    public void MrMultiply(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        var tHigh = Get(_pmRegs - 2);
        var tLow = Get(_pmRegs - 3);
        Multiply(a, b, tLow);
        MrReduceProduct(tHigh, tLow, output);
    }

    public void MrSquare(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        var tHigh = Get(_pmRegs - 2);
        var tLow = Get(_pmRegs - 3);
        Square(input, tLow);
        MrReduceProduct(tHigh, tLow, output);
    }

    public void MrMultiplyX(ReadOnlySpan<ulong> a, ulong b, Span<ulong> output)
    {
        var overflow = MultiplyX(a, b, output);
        MrReduceProductX(overflow, output);
    }

    // mersenne/expm/MrInvert.cpp: input^(p - 2) with a 16-bit window.
    public void MrInvert(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        var t = Get(_pmRegs - 4);
        var s = Get(_pmRegs - 5);
        const int w = 16;

        Copy(input, s);
        var ctr = w - 1;
        while (ctr-- > 0)
        {
            MrSquare(s, s);
            MrMultiply(s, input, s);
        }
        Copy(s, t);

        var oneFrames = (RegBytes * 8 - w * 2) / w;
        while (oneFrames-- > 0)
        {
            for (var i = 0; i < 16; i++)
                MrSquare(s, s);
            MrMultiply(s, t, s);
        }

        var mLow = 0 - (_modulusC + 2);
        for (var bit = 1UL << (w - 1); bit != 0; bit >>= 1)
        {
            MrSquare(s, s);
            if ((mLow & bit) != 0)
                MrMultiply(s, input, s);
        }
        Copy(s, output);
    }

    // mersenne/reduce/MrReduceProduct.cpp
    private void MrReduceProductX(ulong overflow, Span<ulong> inout)
    {
        var product = (UInt128)overflow * _modulusC + inout[0];
        inout[0] = (ulong)product;
        var high = (ulong)(product >> 64);
        if ((inout[1] += high) < high)
        {
            for (var i = 2; i < Legs; ++i)
                if (++inout[i] != 0) return;
            while (AddX(inout, _modulusC) != 0) { }
        }
    }

    private void MrReduceProduct(ReadOnlySpan<ulong> high, ReadOnlySpan<ulong> low, Span<ulong> output)
    {
        var overflow = MultiplyXAdd(high, _modulusC, low, output);
        MrReduceProductX(overflow, output);
    }
}
