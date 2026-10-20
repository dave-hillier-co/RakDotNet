namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::BigTwistedEdwards (math/BigTwistedEdwards.hpp, src/math/edward): the curve -x^2 + y^2 = 1 + d x^2 y^2
/// over p = 2^bits - C in extended coordinates (X, Y, T, Z), with cat's w-MOF scalar multiplication. A point is
/// four consecutive registers. Ported call for call so projective results, and therefore every saved coordinate,
/// match cat's; the multiplication has cat's fixed sequence of doublings and additions for a given window.
/// </summary>
internal sealed class BigTwistedEdwards : BigPseudoMersenne
{
    private const int PointRegs = 4;
    private const int WindowBits = 6;
    private const int PrecompPoints = 1 << (WindowBits - 1);
    private const int TeOverhead = (1 + PrecompPoints) * PointRegs + 9 + PointRegs * 2; // BigTwistedEdwards.hpp:78

    // PtMultiply.cpp MOF_LUT: (index of the odd multiple to add, doubles after the addition) per table entry.
    private static ReadOnlySpan<byte> MofLut =>
    [
        0, 0, 1, 0, 1, 1, 2, 0, 1, 2, 3, 0, 2, 1, 4, 0, 1, 3,
        5, 0, 3, 1, 6, 0, 2, 2, 7, 0, 4, 1, 8, 0, 1, 4,
        9, 0, 5, 1, 10, 0, 3, 2, 11, 0, 6, 1, 12, 0, 2, 3,
        13, 0, 7, 1, 14, 0, 4, 2, 15, 0, 8, 1, 16, 0, 1, 5,
        17, 0, 9, 1, 18, 0, 5, 2, 19, 0, 10, 1, 20, 0, 3, 3,
        21, 0, 11, 1, 22, 0, 6, 2, 23, 0, 12, 1, 24, 0, 2, 4,
        25, 0, 13, 1, 26, 0, 7, 2, 27, 0, 14, 1, 28, 0, 4, 3,
        29, 0, 15, 1, 30, 0, 8, 2, 31, 0, 16, 1, 32, 0, 1, 6,
        33, 0, 17, 1, 34, 0, 9, 2, 35, 0, 18, 1, 36, 0, 5, 3,
        37, 0, 19, 1, 38, 0, 10, 2, 39, 0, 20, 1, 40, 0, 3, 4,
        41, 0, 21, 1, 42, 0, 11, 2, 43, 0, 22, 1, 44, 0, 6, 3,
        45, 0, 23, 1, 46, 0, 12, 2, 47, 0, 24, 1, 48, 0, 2, 5,
        49, 0, 25, 1, 50, 0, 13, 2, 51, 0, 26, 1, 52, 0, 7, 3,
        53, 0, 27, 1, 54, 0, 14, 2, 55, 0, 28, 1, 56, 0, 4, 4,
        57, 0, 29, 1, 58, 0, 15, 2, 59, 0, 30, 1, 60, 0, 8, 3,
        61, 0, 31, 1, 62, 0, 16, 2, 63, 0, 32, 1, 64, 0, 1, 7,
    ];

    private readonly int _teRegs;
    private readonly int _yOff;
    private readonly int _tOff;
    private readonly int _zOff;
    private readonly int _pointStride;

    public BigTwistedEdwards(int regs, int bits, int modulusC, int paramD, ReadOnlySpan<byte> q, ReadOnlySpan<byte> generatorPoint)
        : base(regs + TeOverhead, bits, modulusC)
    {
        _teRegs = regs + TeOverhead;
        CurveD = (ulong)paramD;
        _yOff = Legs;
        _tOff = Legs * 2;
        _zOff = Legs * 3;
        _pointStride = Legs * PointRegs;

        var generator = GeneratorRegister;
        Load(generatorPoint[..RegBytes], generator);
        Load(generatorPoint[RegBytes..], generator[_yOff..]);
        PtUnpack(generator);
        Load(q[..RegBytes], Get(_teRegs - 9));
    }

    public ulong CurveD { get; }

    public ReadOnlySpan<ulong> CurveQ => Get(_teRegs - 9)[..Legs];

    public ReadOnlySpan<ulong> Generator => GeneratorRegister[.._pointStride];

    public int PtLegs => Legs * PointRegs;

    // Local workspace (BigTwistedEdwards.cpp:43-53).
    private Span<ulong> A => Get(_teRegs - 1);
    private Span<ulong> B => Get(_teRegs - 2);
    private Span<ulong> C => Get(_teRegs - 3);
    private Span<ulong> D => Get(_teRegs - 4);
    private Span<ulong> E => Get(_teRegs - 5);
    private Span<ulong> F => Get(_teRegs - 6);
    private Span<ulong> G => Get(_teRegs - 7);
    private Span<ulong> H => Get(_teRegs - 8);
    private Span<ulong> TempPt => Get(_teRegs - 13);
    private Span<ulong> GeneratorRegister => Get(_teRegs - 17);

    private ReadOnlySpan<ulong> Y(ReadOnlySpan<ulong> point) => point[_yOff..];
    private ReadOnlySpan<ulong> T(ReadOnlySpan<ulong> point) => point[_tOff..];
    private ReadOnlySpan<ulong> Z(ReadOnlySpan<ulong> point) => point[_zOff..];
    private Span<ulong> Y(Span<ulong> point) => point[_yOff..];
    private Span<ulong> T(Span<ulong> point) => point[_tOff..];
    private Span<ulong> Z(Span<ulong> point) => point[_zOff..];

    /// <summary>Extended point (X, Y, X*Y, 1) from the affine (x, y) in its first two registers.</summary>
    public void PtUnpack(Span<ulong> inout)
    {
        MrMultiply(inout, Y(inout), T(inout));
        CopyX(1, Z(inout));
    }

    public void PtIdentity(Span<ulong> inout)
    {
        CopyX(0, inout);
        CopyX(1, Y(inout));
        CopyX(0, T(inout));
        CopyX(1, Z(inout));
    }

    public bool IsAffineIdentity(ReadOnlySpan<ulong> input) => EqualX(input, 0);

    public void PtCopy(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        Copy(input, output);
        Copy(Y(input), Y(output));
        Copy(T(input), T(output));
        Copy(Z(input), Z(output));
    }

    /// <summary>Loads affine (x, y) and checks it is on the curve; T and Z are left as they were.</summary>
    public bool LoadVerifyAffineXY(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<ulong> output)
    {
        Load(x[..RegBytes], output);
        Load(y[..RegBytes], Y(output));
        return PtValidAffine(output);
    }

    // edward/io/PtValidAffine.cpp: 0 = 1 + d*x^2*y^2 + x^2 - y^2
    public bool PtValidAffine(ReadOnlySpan<ulong> input)
    {
        MrSquare(input, A);
        MrSquare(Y(input), B);
        MrMultiply(A, B, C);
        MrMultiplyX(C, CurveD, C);
        MrAddX(C, 1);
        MrAdd(C, A, C);
        MrSubtract(C, B, C);
        MrReduce(C);
        return IsZero(C);
    }

    // edward/io/SaveAffineX.cpp: a point whose Z is exactly 1 is saved as it stands.
    public void SaveAffineX(ReadOnlySpan<ulong> input, Span<byte> x)
    {
        if (EqualX(Z(input), 1))
        {
            Save(input, x[..RegBytes]);
            return;
        }
        MrInvert(Z(input), A);
        MrMultiply(input, A, B);
        MrReduce(B);
        Save(B, x[..RegBytes]);
    }

    // edward/io/SaveAffineXY.cpp
    public void SaveAffineXY(ReadOnlySpan<ulong> input, Span<byte> x, Span<byte> y)
    {
        if (EqualX(Z(input), 1))
        {
            Save(input, x[..RegBytes]);
            Save(Y(input), y[..RegBytes]);
            return;
        }
        MrInvert(Z(input), A);
        MrMultiply(input, A, B);
        MrReduce(B);
        MrMultiply(Y(input), A, C);
        MrReduce(C);
        Save(B, x[..RegBytes]);
        Save(C, y[..RegBytes]);
    }

    // edward/io/PtNormalize.cpp
    public void PtNormalize(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        MrInvert(Z(input), A);
        MrMultiply(input, A, output);
        MrReduce(output);
        MrMultiply(Y(input), A, Y(output));
        MrReduce(Y(output));
        PtUnpack(output);
    }

    // edward/addsub/PtNegate.cpp
    public void PtNegate(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        MrNegate(input, output);
        Copy(Y(input), Y(output));
        MrNegate(T(input), T(output));
        Copy(Z(input), Z(output));
    }

    public void PtEAdd(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output) => PtAdd(a, b, output, extendedT: true);

    public void PtAdd(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output) => PtAdd(a, b, output, extendedT: false);

    // edward/addsub/PtAdd.cpp: unified addition in 8M 1D 9a; without T it leaves T stale.
    private void PtAdd(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output, bool extendedT)
    {
        MrSubtract(Y(a), a, C);
        MrSubtract(Y(b), b, D);
        MrMultiply(C, D, A);
        MrAdd(Y(a), a, G);
        MrAdd(Y(b), b, H);
        MrMultiply(G, H, B);
        MrMultiply(T(a), T(b), C);
        MrMultiplyX(C, CurveD * 2, C);
        MrMultiply(Z(a), Z(b), D);
        MrDouble(D, D);
        MrSubtract(B, A, E);
        MrSubtract(D, C, F);
        MrAdd(D, C, G);
        MrAdd(B, A, H);
        MrMultiply(E, F, output);
        MrMultiply(G, H, Y(output));
        if (extendedT)
            MrMultiply(E, H, T(output));
        MrMultiply(F, G, Z(output));
    }

    public void PtEDouble(ReadOnlySpan<ulong> input, Span<ulong> output) => PtDouble(input, output, extendedT: true);

    public void PtDouble(ReadOnlySpan<ulong> input, Span<ulong> output) => PtDouble(input, output, extendedT: false);

    // edward/addsub/PtDouble.cpp: dedicated doubling in 4M 4S 5a.
    private void PtDouble(ReadOnlySpan<ulong> input, Span<ulong> output, bool extendedT)
    {
        MrAdd(input, Y(input), E);
        MrSquare(E, E);
        MrSquare(input, A);
        MrSquare(Y(input), B);
        MrSquare(Z(input), C);
        MrDouble(C, C);
        MrNegate(A, A);
        MrAdd(A, B, G);
        MrSubtract(A, B, H);
        MrSubtract(G, C, F);
        MrAdd(E, H, E);
        MrMultiply(E, F, output);
        MrMultiply(G, H, Y(output));
        if (extendedT)
            MrMultiply(E, H, T(output));
        MrMultiply(F, G, Z(output));
    }

    public void PtEDoubleZ1(ReadOnlySpan<ulong> input, Span<ulong> output) => PtDoubleZ1(input, output, extendedT: true);

    public void PtDoubleZ1(ReadOnlySpan<ulong> input, Span<ulong> output) => PtDoubleZ1(input, output, extendedT: false);

    // edward/addsub/PtDoubleZ1.cpp: doubling of a point with Z = 1, reading only X and Y.
    private void PtDoubleZ1(ReadOnlySpan<ulong> input, Span<ulong> output, bool extendedT)
    {
        MrSquare(input, A);
        MrSquare(Y(input), B);
        MrNegate(A, A);
        MrAdd(A, B, G);
        Copy(G, F);
        MrSubtractX(F, 2);
        MrSubtract(A, B, H);
        MrAdd(input, Y(input), E);
        MrSquare(E, E);
        MrAdd(E, H, E);
        MrMultiply(E, F, output);
        MrMultiply(G, H, Y(output));
        if (extendedT)
            MrMultiply(E, H, T(output));
        MrMultiply(F, G, Z(output));
    }

    // edward/mul/PtPrecompAddSub.cpp: a + b and a - b together, each stored with its negation.
    private void PtPrecompAddSub(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> sum, Span<ulong> diff, int negOffset)
    {
        MrSubtract(Y(a), a, C);
        MrSubtract(Y(b), b, D);
        MrMultiply(C, D, F);
        MrAdd(Y(a), a, E);
        MrAdd(Y(b), b, H);
        MrMultiply(E, H, G);
        MrMultiply(C, H, A);
        MrMultiply(E, D, B);
        MrMultiply(T(a), T(b), C);
        MrMultiplyX(C, CurveD * 2, C);
        MrMultiply(Z(a), Z(b), D);
        MrDouble(D, D);
        MrSubtract(G, F, E);
        MrAdd(G, F, H);
        MrSubtract(D, C, F);
        MrAdd(D, C, G);
        MrMultiply(E, F, sum);
        MrMultiply(G, H, Y(sum));
        MrMultiply(E, H, T(sum));
        MrMultiply(F, G, Z(sum));
        PtNegate(sum, sum[negOffset..]);
        MrSubtract(B, A, E);
        MrAdd(B, A, H);
        MrMultiply(E, G, diff);
        MrMultiply(F, H, Y(diff));
        MrMultiply(E, H, T(diff));
        MrMultiply(G, F, Z(diff));
        PtNegate(diff, diff[negOffset..]);
    }

    /// <summary>A table for <see cref="PtMultiplyPrecomp"/> with window <paramref name="w"/>.</summary>
    public ulong[] PtMultiplyPrecompAlloc(int w) => new ulong[(1 + (1 << (w - 1))) * _pointStride];

    // edward/mul/PtMultiplyPrecomp.cpp: the identity, then odd multiples of the input and their negations.
    public void PtMultiplyPrecomp(ReadOnlySpan<ulong> input, int w, Span<ulong> table)
    {
        var negOffset = _pointStride << (w - 2);
        PtIdentity(table);
        var output = table[_pointStride..];

        var preA = 0;
        PtCopy(input, output);
        PtNegate(input, output[negOffset..]);
        var p1 = preA;

        var pre2 = TempPt;
        PtEDouble(input, pre2);

        var preB = preA + _pointStride;
        PtEAdd(output[preA..], pre2, output[preB..]);
        PtNegate(output[preB..], output[(preB + negOffset)..]);
        var p3 = preB;

        if (w == 6)
        {
            preA = preB + _pointStride;
            PtEAdd(output[preB..], pre2, output[preA..]);
            PtNegate(output[preA..], output[(preA + negOffset)..]);
            var p5 = preA;

            preB = preA + _pointStride;
            PtEAdd(output[preA..], pre2, output[preB..]);
            PtNegate(output[preB..], output[(preB + negOffset)..]);
            var p7 = preB;

            preA = preB + _pointStride;
            PtEAdd(output[preB..], pre2, output[preA..]);
            PtNegate(output[preA..], output[(preA + negOffset)..]);
            var p9 = preA;

            preB = preA + _pointStride;
            PtEAdd(output[preA..], pre2, output[preB..]);
            PtNegate(output[preB..], output[(preB + negOffset)..]);

            // 22P, then 22P +/- P, 3P, 5P, 7P, 9P fill the rest of the table.
            PtEDouble(output[preB..], pre2);
            preB += _pointStride * 5;
            preA = preB + _pointStride;
            foreach (var odd in (ReadOnlySpan<int>)[p1, p3, p5, p7, p9])
            {
                PtPrecompAddSub(pre2, output[odd..], output[preA..], output[preB..], negOffset);
                preB -= _pointStride;
                preA += _pointStride;
            }
        }
        else
        {
            var positivePointCount = 1 << (w - 2);
            for (var tableIndex = 2; tableIndex < positivePointCount; tableIndex += 2)
            {
                preA = preB + _pointStride;
                PtEAdd(output[preB..], pre2, output[preA..]);
                PtNegate(output[preA..], output[(preA + negOffset)..]);
                preB = preA + _pointStride;
                PtEAdd(output[preA..], pre2, output[preB..]);
                PtNegate(output[preB..], output[(preB + negOffset)..]);
            }
        }
    }

    /// <summary>k*P with the default 6-bit window; <paramref name="msbK"/> extends k by one high bit.</summary>
    public void PtMultiply(ReadOnlySpan<ulong> point, ReadOnlySpan<ulong> k, byte msbK, Span<ulong> output)
    {
        var defaultPrecomp = Get(_teRegs - TeOverhead);
        PtMultiplyPrecomp(point, WindowBits, defaultPrecomp);
        PtMultiply(defaultPrecomp, WindowBits, k, msbK, output);
    }

    // edward/mul/PtMultiply.cpp: w-MOF scalar multiplication from a precomputed table.
    public void PtMultiply(ReadOnlySpan<ulong> precomp, int w, ReadOnlySpan<ulong> k, byte msbK, Span<ulong> output)
    {
        var leg = Legs - 1;
        var offset = 64 + w;
        var doublesBefore = 0;
        var doublesSkip = 0;

        ulong lastLeg = msbK;
        PtCopy(lastLeg != 0 ? precomp[_pointStride..] : precomp, output);

        for (; ; )
        {
            ulong bits;
            if (offset <= 64)
            {
                bits = lastLeg >> (64 - offset);
            }
            else if (leg >= 0)
            {
                var newLeg = k[leg--];
                offset -= 64;
                bits = (lastLeg << offset) | (newLeg >> (64 - offset));
                lastLeg = newLeg;
            }
            else if (offset <= 64 + w)
            {
                bits = lastLeg << (offset - 64);
                doublesSkip = offset - 64 - 1;
            }
            else
            {
                break;
            }

            var (precompOffset, doublesAfter) = Window(bits, w);
            doublesBefore += w - doublesAfter;
            while (--doublesBefore != 0)
                PtDouble(output, output);
            PtEDouble(output, output);
            PtAdd(output, precomp[precompOffset..], output);
            doublesBefore = doublesAfter;
            offset += w;
        }

        if (doublesBefore > doublesSkip)
        {
            doublesBefore -= doublesSkip;
            while (doublesBefore-- != 0)
                PtDouble(output, output);
        }
    }

    // edward/mul/PtMultiply.cpp PtSiMultiply: k*P + l*Q with interleaved windows.
    public void PtSiMultiply(ReadOnlySpan<ulong> precompP, ReadOnlySpan<ulong> precompQ, int w,
                             ReadOnlySpan<ulong> k, byte msbK, ReadOnlySpan<ulong> l, byte msbL, Span<ulong> output)
    {
        var leg = Legs - 1;
        var offset = 64 + w;
        var doublesBefore = 0;
        var doublesSkip = 0;

        ulong lastLegK = msbK;
        PtCopy(lastLegK != 0 ? precompP[_pointStride..] : precompP, output);
        ulong lastLegL = msbL;
        PtAdd(output, lastLegL != 0 ? precompQ[_pointStride..] : precompQ, output);

        for (; ; )
        {
            ulong bitsK, bitsL;
            if (offset <= 64)
            {
                bitsK = lastLegK >> (64 - offset);
                bitsL = lastLegL >> (64 - offset);
            }
            else if (leg >= 0)
            {
                var newLegK = k[leg];
                var newLegL = l[leg--];
                offset -= 64;
                bitsK = (lastLegK << offset) | (newLegK >> (64 - offset));
                lastLegK = newLegK;
                bitsL = (lastLegL << offset) | (newLegL >> (64 - offset));
                lastLegL = newLegL;
            }
            else if (offset <= 64 + w)
            {
                bitsK = lastLegK << (offset - 64);
                bitsL = lastLegL << (offset - 64);
                doublesSkip = offset - 64 - 1;
            }
            else
            {
                break;
            }

            var (offsetK, afterK) = Window(bitsK, w);
            var (offsetL, afterL) = Window(bitsL, w);
            int after1, after2, add1, add2;
            if (afterK >= afterL)
            {
                after1 = afterK;
                after2 = afterL;
                add1 = offsetK;
                add2 = offsetL;
            }
            else
            {
                after1 = afterL;
                after2 = afterK;
                add1 = offsetL;
                add2 = offsetK;
            }
            var addTable1 = afterK >= afterL ? precompP : precompQ;
            var addTable2 = afterK >= afterL ? precompQ : precompP;

            doublesBefore += w - after1;
            while (--doublesBefore != 0)
                PtDouble(output, output);
            PtEDouble(output, output);

            doublesBefore = after1 - after2;
            if (doublesBefore != 0)
            {
                PtAdd(output, addTable1[add1..], output);
                while (--doublesBefore != 0)
                    PtDouble(output, output);
                PtEDouble(output, output);
            }
            else
            {
                PtEAdd(output, addTable1[add1..], output);
            }

            PtAdd(output, addTable2[add2..], output);
            doublesBefore = after2;
            offset += w;
        }

        if (doublesBefore > doublesSkip)
        {
            doublesBefore -= doublesSkip;
            while (doublesBefore-- != 0)
                PtDouble(output, output);
        }
    }

    // The table entry and trailing doubles for one window of bits (PtMultiply.cpp:122-131).
    private (int PrecompOffset, int DoublesAfter) Window(ulong bits, int w)
    {
        var z = (((bits ^ (0 - ((bits >> w) & 1))) & ((1UL << w) - 1)) + 1) >> 1;
        var negMask = (bits & (1UL << w)) >> 2;
        if (z == 0)
            negMask = 0;
        var addIndex = MofLut[(int)z * 2];
        int doublesAfter = MofLut[(int)z * 2 + 1];
        if (z == 0)
            doublesAfter = w - 1;
        return ((int)(addIndex + negMask) * _pointStride, doublesAfter);
    }
}
