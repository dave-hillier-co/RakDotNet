using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::BigRTL (math/BigRTL.hpp, src/math/rtl): a register file of little-endian 64-bit legs and the big-integer
/// operations the curve uses. Registers are slices of one array, as cat's are pointers into one block, and
/// <see cref="Get"/> returns everything from a register on so a point or a double-width product spans the
/// registers that follow it. Ported literally, including where results are left unreduced, so every value saved
/// or hashed is the one cat computes.
/// </summary>
internal class BigRtl
{
    private const int BigOverhead = 7; // BigRTL.hpp:49, overhead for ModularInverse()
    private const int LegBits = 64;

    private readonly int _libraryRegs;
    private readonly ulong[] _libraryMemory;

    public BigRtl(int regs, int bits)
    {
        Legs = bits / LegBits;
        _libraryRegs = regs + BigOverhead;
        _libraryMemory = new ulong[Legs * _libraryRegs];
    }

    public int Legs { get; }

    public int RegBytes => Legs * sizeof(ulong);

    /// <summary>BigRTL::Get: the register and every register after it.</summary>
    public Span<ulong> Get(int regIndex) => _libraryMemory.AsSpan(Legs * regIndex);

    /// <summary>Zeroes every register, as ~BigRTL does.</summary>
    public void Clear() => CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_libraryMemory.AsSpan()));

    /// <summary>BigRTL::Save: <c>output.Length / 8</c> legs, little-endian, then zero bytes (rtl/io/Save.cpp).</summary>
    public void Save(ReadOnlySpan<ulong> input, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(output.Length, RegBytes);
        var legs = output.Length / sizeof(ulong);
        for (var i = 0; i < legs; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(output[(i * 8)..], input[i]);
        output[(legs * 8)..].Clear();
    }

    /// <summary>BigRTL::Load: up to one register of little-endian legs, the rest zero (rtl/io/Load.cpp).</summary>
    public void Load(ReadOnlySpan<byte> input, Span<ulong> output)
    {
        var legs = Math.Min(input.Length / sizeof(ulong), Legs);
        for (var i = 0; i < legs; i++)
            output[i] = BinaryPrimitives.ReadUInt64LittleEndian(input[(i * 8)..]);
        output[legs..Legs].Clear();
    }

    public void Copy(ReadOnlySpan<ulong> input, Span<ulong> output) => input[..Legs].CopyTo(output);

    public void CopyX(ulong input, Span<ulong> output)
    {
        output[0] = input;
        output[1..Legs].Clear();
    }

    public int LegsUsed(ReadOnlySpan<ulong> input)
    {
        for (var legs = Legs - 1; legs >= 0; --legs)
            if (input[legs] != 0)
                return legs + 1;
        return 0;
    }

    // rtl/addsub/Compare.cpp
    public bool Greater(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        for (var legs = Legs - 1; legs >= 0; --legs)
        {
            if (a[legs] < b[legs]) return false;
            if (a[legs] > b[legs]) return true;
        }
        return false;
    }

    public bool GreaterX(ReadOnlySpan<ulong> input, ulong x)
    {
        for (var i = Legs - 1; i > 0; --i)
            if (input[i] != 0) return true;
        return input[0] > x;
    }

    public bool Less(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        for (var legs = Legs - 1; legs >= 0; --legs)
        {
            if (a[legs] > b[legs]) return false;
            if (a[legs] < b[legs]) return true;
        }
        return false;
    }

    public bool LessX(ReadOnlySpan<ulong> input, ulong x)
    {
        for (var i = Legs - 1; i > 0; --i)
            if (input[i] != 0) return false;
        return input[0] < x;
    }

    public bool EqualX(ReadOnlySpan<ulong> input, ulong x)
    {
        if (input[0] != x) return false;
        for (var i = 1; i < Legs; ++i)
            if (input[i] != 0) return false;
        return true;
    }

    public bool IsZero(ReadOnlySpan<ulong> input)
    {
        for (var i = 0; i < Legs; ++i)
            if (input[i] != 0) return false;
        return true;
    }

    public byte Add(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output) => Add(Legs, a, b, output);

    // rtl/addsub/Add.cpp
    protected static byte Add(int legs, ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        ulong carry = 0;
        for (var i = 0; i < legs; ++i)
        {
            var sum = (UInt128)a[i] + b[i] + carry;
            output[i] = (ulong)sum;
            carry = (ulong)(sum >> LegBits);
        }
        return (byte)carry;
    }

    // rtl/addsub/AddX.cpp
    public byte AddX(Span<ulong> inout, ulong x)
    {
        if ((inout[0] += x) >= x)
            return 0;
        for (var i = 1; i < Legs; ++i)
            if (++inout[i] != 0) return 0;
        return 1;
    }

    public byte Subtract(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output) => Subtract(Legs, a, b, output);

    // rtl/addsub/Subtract.cpp
    protected static byte Subtract(int legs, ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        ulong borrow = 0;
        for (var i = 0; i < legs; ++i)
        {
            var ai = a[i];
            var bi = b[i];
            var d = ai - bi - borrow;
            borrow = borrow != 0 ? (ai <= bi ? 1UL : 0UL) : (ai < bi ? 1UL : 0UL);
            output[i] = d;
        }
        return (byte)borrow;
    }

    // rtl/addsub/SubtractX.cpp
    public byte SubtractX(Span<ulong> inout, ulong x)
    {
        var t = inout[0];
        inout[0] = t - x;
        if (t >= x) return 0;
        for (var i = 1; i < Legs; ++i)
            if (inout[i]-- != 0) return 0;
        return 1;
    }

    // rtl/addsub/Double.cpp
    public byte Double(ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        var last = input[0];
        output[0] = last << 1;
        for (var i = 1; i < Legs; ++i)
        {
            var next = input[i];
            output[i] = (next << 1) | (last >> (LegBits - 1));
            last = next;
        }
        return (byte)(last >> (LegBits - 1));
    }

    // rtl/addsub/Shift.cpp
    protected static ulong ShiftLeft(int legs, ReadOnlySpan<ulong> input, int shift, Span<ulong> output)
    {
        if (shift == 0)
        {
            input[..legs].CopyTo(output);
            return 0;
        }
        var carry = input[0];
        output[0] = carry << shift;
        for (var i = 1; i < legs; ++i)
        {
            var x = input[i];
            output[i] = (x << shift) | (carry >> (LegBits - shift));
            carry = x;
        }
        return carry >> (LegBits - shift);
    }

    protected static ulong ShiftRight(int legs, ReadOnlySpan<ulong> input, int shift, Span<ulong> output)
    {
        if (shift == 0)
        {
            input[..legs].CopyTo(output);
            return 0;
        }
        var carry = input[legs - 1];
        output[legs - 1] = carry >> shift;
        for (var i = legs - 2; i >= 0; --i)
        {
            var x = input[i];
            output[i] = (x >> shift) | (carry << (LegBits - shift));
            carry = x;
        }
        return carry << (LegBits - shift);
    }

    public ulong MultiplyX(ReadOnlySpan<ulong> a, ulong b, Span<ulong> output) => MultiplyX(Legs, a, b, output);

    // rtl/mul/MultiplyX.cpp
    protected static ulong MultiplyX(int legs, ReadOnlySpan<ulong> a, ulong b, Span<ulong> output)
    {
        ulong high = 0;
        for (var i = 0; i < legs; ++i)
        {
            var product = (UInt128)a[i] * b + high;
            output[i] = (ulong)product;
            high = (ulong)(product >> LegBits);
        }
        return high;
    }

    public ulong MultiplyXAdd(ReadOnlySpan<ulong> a, ulong b, ReadOnlySpan<ulong> c, Span<ulong> output) => MultiplyXAdd(Legs, a, b, c, output);

    // rtl/mul/MultiplyXAdd.cpp: output = a * b + c
    protected static ulong MultiplyXAdd(int legs, ReadOnlySpan<ulong> a, ulong b, ReadOnlySpan<ulong> c, Span<ulong> output)
    {
        ulong high = 0;
        for (var i = 0; i < legs; ++i)
        {
            var product = (UInt128)a[i] * b + c[i] + high;
            output[i] = (ulong)product;
            high = (ulong)(product >> LegBits);
        }
        return high;
    }

    /// <summary>
    /// BigRTL::Multiply: the full product into <paramref name="output"/> and the register after it. cat unrolls the
    /// 4-leg case (rtl/mul/Multiply.cpp); every variant computes the same product.
    /// </summary>
    public void Multiply(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> output)
    {
        Span<ulong> product = stackalloc ulong[Legs * 2];
        product[Legs] = MultiplyX(Legs, a, b[0], product);
        for (var i = 1; i < Legs; ++i)
            product[Legs + i] = MultiplyXAdd(Legs, a, b[i], product[i..], product[i..]);
        product.CopyTo(output);
        product.Clear();
    }

    /// <summary>BigRTL::Square: the same product as <see cref="Multiply"/> of the input with itself.</summary>
    public void Square(ReadOnlySpan<ulong> input, Span<ulong> output) => Multiply(input, input, output);

    /// <summary>BigRTL::MulMod (rtl/div/MulMod.cpp): r = a * b (mod m).</summary>
    public void MulMod(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, ReadOnlySpan<ulong> m, Span<ulong> r)
    {
        var p = Get(_libraryRegs - 5);
        Multiply(a, b, p);
        DivideProduct(p, m, p, r);
    }

    /// <summary>
    /// BigRTL::DivideProduct (rtl/div/Divide.cpp): the double-width a (a+1:a) divided by b, quotient into two
    /// registers and remainder into one. False when b is zero.
    /// </summary>
    public bool DivideProduct(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Span<ulong> quotient, Span<ulong> remainder)
    {
        var bUsed = LegsUsed(b);
        if (bUsed == 0) return false;
        var aUsed = LegsUsed(a[Legs..]);
        if (aUsed != 0)
        {
            aUsed += Legs;
        }
        else
        {
            if (Less(a, b))
            {
                Copy(a, remainder);
                // Divide.cpp:98 clears library_legs * 2 bytes of the quotient, not legs.
                quotient[..(Legs * 2 / sizeof(ulong))].Clear();
                return true;
            }
            aUsed = LegsUsed(a);
        }

        var shiftedA = Get(_libraryRegs - 2);
        var shiftedB = Get(_libraryRegs - 3);
        var shift = LegBits - (63 - BitOperations.LeadingZeroCount(b[bUsed - 1])) - 1;
        var aOverflow = ShiftLeft(aUsed, a, shift, shiftedA);
        ShiftLeft(bUsed, b, shift, shiftedB);
        DivideCore(aUsed, aOverflow, shiftedA, bUsed, shiftedB, quotient);

        var offset = aUsed - bUsed + 1;
        quotient[offset..(Legs * 2)].Clear();
        remainder[bUsed..Legs].Clear();
        ShiftRight(bUsed, shiftedA, shift, remainder);
        return true;
    }

    // rtl/div/DivideGeneric.cpp DivideCore: schoolbook division of the normalised A by B; A becomes the remainder.
    private static void DivideCore(int aUsed, ulong aOverflow, Span<ulong> a, int bUsed, ReadOnlySpan<ulong> b, Span<ulong> q)
    {
        var offset = aUsed - bUsed;
        var bHigh = b[bUsed - 1];
        var aOffset = a[offset..];

        ulong qHigh;
        if (bHigh > aOverflow)
        {
            qHigh = 0;
        }
        else
        {
            qHigh = 1;
            if (bHigh == aOverflow)
            {
                for (var i = bUsed - 2; i >= 0; --i)
                {
                    var ai = aOffset[i];
                    var bi = b[i];
                    if (bi < ai) break;
                    if (bi > ai) { qHigh = 0; break; }
                }
            }
        }
        q[offset + 1] = qHigh;

        if (qHigh != 0)
        {
            ulong c = 0;
            for (var i = 0; i < bUsed - 1; ++i)
            {
                var ai = aOffset[i + 1];
                var bi = b[i];
                var d = ai - bi - c;
                c = c != 0 ? (ai < d || bi == ulong.MaxValue ? 1UL : 0UL) : (ai < bi ? 1UL : 0UL);
                aOffset[i + 1] = d;
            }
            aOverflow -= bHigh + c;
        }

        do
        {
            aOffset = a[offset..];
            var quotient = (((UInt128)aOverflow << LegBits) | aOffset[bUsed - 1]) / bHigh;
            var qHi = (ulong)(quotient >> LegBits);
            var qLo = (ulong)quotient;
            ulong pHigh = 0, pLow, bLast = 0, c = 0;
            if (qHi != 0)
            {
                // A -= (1 | q_lo) * B
                for (var i = 0; i < bUsed; ++i)
                {
                    var x = b[i];
                    var product = (UInt128)x * qLo + pHigh + bLast;
                    pLow = (ulong)product;
                    pHigh = (ulong)(product >> LegBits);
                    bLast = x;
                    var ai = aOffset[i];
                    var d = ai - pLow - c;
                    c = c != 0 ? (ai < d || pLow == ulong.MaxValue ? 1UL : 0UL) : (ai < pLow ? 1UL : 0UL);
                    aOffset[i] = d;
                }
                aOverflow -= bLast;
            }
            else
            {
                // A -= q_lo * B
                for (var i = 0; i < bUsed; ++i)
                {
                    var product = (UInt128)b[i] * qLo + pHigh;
                    pLow = (ulong)product;
                    pHigh = (ulong)(product >> LegBits);
                    var ai = aOffset[i];
                    var d = ai - pLow - c;
                    c = c != 0 ? (ai < d || pLow == ulong.MaxValue ? 1UL : 0UL) : (ai < pLow ? 1UL : 0UL);
                    aOffset[i] = d;
                }
            }
            aOverflow -= pHigh + c;

            // Correct the trial quotient, at most twice.
            while (aOverflow != 0)
            {
                --qLo;
                c = 0;
                for (var i = 0; i < bUsed; ++i)
                {
                    var ai = aOffset[i];
                    var sum = ai + b[i] + c;
                    c = c != 0 ? (sum <= ai ? 1UL : 0UL) : (sum < ai ? 1UL : 0UL);
                    aOffset[i] = sum;
                }
                aOverflow += c;
            }

            q[offset] = qLo;
            aOverflow = aOffset[bUsed - 1];
        } while (offset-- > 0);
    }
}
