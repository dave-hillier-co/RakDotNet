// Prints known-answer vectors computed by the DependentExtensions/cat code that RakNet compiles with
// LIBCAT_SECURITY=1 (Source/SecureHandshake.cpp), for the tests in tests/RakDotNet.Protocol.Tests/Security.
// Every input is deterministic: byte strings come from SplitMix64 streams with fixed seeds, and the cat CSPRNG
// is replaced by a deterministic FortunaOutput whose draws are recorded in the vectors, so the C# tests replay
// them. Writes one JSON file per primitive into the directory given as the only argument. Build and run with
// build.sh.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

// Reach cat's private state (FortunaOutput's constructor, EasyHandshake's CSPRNG, internal hash states) the way
// tools/bitstream-vectors reaches RakNet's. Standard headers are included first so only cat is affected, and
// the headers declaring a template<class T> before `class` becomes `struct`.
#include "NativeFeatureIncludes.h"
#define CAT_NEUTER_EXPORT
#include "cat/Singleton.hpp"
#include "cat/port/AlignedAlloc.hpp"
#define private public
#define protected public
#define class struct
#include "SecureHandshake.h"
#undef class
#undef private
#undef protected

#if LIBCAT_SECURITY != 1
#error Build with -DLIBCAT_SECURITY=1
#endif

using namespace cat;

typedef std::vector<u8> Bytes;

// ---------------------------------------------------------------------------------------------------------------
// Deterministic inputs

struct SplitMix
{
    u64 state;
    explicit SplitMix(u64 seed) : state(seed) {}
    u64 Next()
    {
        u64 z = (state += 0x9E3779B97F4A7C15ULL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
        return z ^ (z >> 31);
    }
    void Fill(u8 *out, int bytes)
    {
        for (int i = 0; i < bytes; ++i)
            out[i] = (u8)(Next() >> 56);
    }
    Bytes Take(int bytes)
    {
        Bytes b(bytes);
        if (bytes) Fill(&b[0], bytes);
        return b;
    }
};

// A FortunaOutput whose output is a scripted prefix followed by a SplitMix64 stream. Every byte handed out is
// recorded so the vectors can give the C# port the same randomness.
class ReplayRandom : public FortunaOutput
{
public:
    SplitMix source;
    Bytes prefix;
    size_t prefix_used;
    Bytes drawn;

    explicit ReplayRandom(u64 seed) : FortunaOutput(), source(seed), prefix_used(0) {}

    u32 Generate()
    {
        u32 x;
        Generate(&x, sizeof(x));
        return x;
    }

    void Generate(void *buffer, int bytes)
    {
        u8 *out = (u8 *)buffer;
        for (int i = 0; i < bytes; ++i)
        {
            u8 b;
            if (prefix_used < prefix.size()) b = prefix[prefix_used++];
            else source.Fill(&b, 1);
            out[i] = b;
            drawn.push_back(b);
        }
    }
};

// ---------------------------------------------------------------------------------------------------------------
// Minimal JSON writer

static std::string Hex(const void *data, size_t bytes)
{
    static const char digits[] = "0123456789abcdef";
    const u8 *p = (const u8 *)data;
    std::string s;
    for (size_t i = 0; i < bytes; ++i)
    {
        s += digits[p[i] >> 4];
        s += digits[p[i] & 15];
    }
    return s;
}

static std::string Hex(const Bytes &b) { return b.empty() ? std::string() : Hex(&b[0], b.size()); }

static std::string Quote(const std::string &s) { return "\"" + s + "\""; }

static std::string Num(long long v)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", v);
    return buf;
}

static std::string U64(u64 v)
{
    char buf[32];
    snprintf(buf, sizeof(buf), "\"%llu\"", (unsigned long long)v);
    return buf;
}

static std::string Bool(bool b) { return b ? "true" : "false"; }

// An object built as an ordered list of "key": value pairs.
struct Obj
{
    std::vector<std::string> fields;
    Obj &Add(const char *key, const std::string &raw)
    {
        fields.push_back(Quote(key) + ": " + raw);
        return *this;
    }
    Obj &Str(const char *key, const std::string &s) { return Add(key, Quote(s)); }
    Obj &HexField(const char *key, const void *data, size_t bytes) { return Str(key, Hex(data, bytes)); }
    Obj &HexField(const char *key, const Bytes &b) { return Str(key, Hex(b)); }
    std::string Render() const
    {
        std::string s = "{ ";
        for (size_t i = 0; i < fields.size(); ++i)
        {
            if (i) s += ", ";
            s += fields[i];
        }
        return s + " }";
    }
};

static std::string Array(const std::vector<std::string> &items, const char *indent)
{
    if (items.empty()) return "[]";
    std::string s = "[\n";
    for (size_t i = 0; i < items.size(); ++i)
    {
        s += indent;
        s += "  ";
        s += items[i];
        if (i + 1 < items.size()) s += ",";
        s += "\n";
    }
    return s + indent + "]";
}

static void WriteFile(const std::string &dir, const char *name, const char *description,
                      const std::vector<std::pair<std::string, std::string> > &sections)
{
    std::string path = dir + "/" + name + ".json";
    FILE *f = fopen(path.c_str(), "w");
    if (!f)
    {
        fprintf(stderr, "cannot write %s\n", path.c_str());
        exit(1);
    }
    fprintf(f, "{\n  \"primitive\": \"%s\",\n  \"description\": \"%s\",\n", name, description);
    fprintf(f, "  \"source\": \"tools/cat-vectors, RakNet 1a169895a900c9fc4841c556e16514182b75faf8 DependentExtensions/cat\"");
    for (size_t i = 0; i < sections.size(); ++i)
        fprintf(f, ",\n  \"%s\": %s", sections[i].first.c_str(), sections[i].second.c_str());
    fprintf(f, "\n}\n");
    fclose(f);
    printf("wrote %s\n", path.c_str());
}

// ---------------------------------------------------------------------------------------------------------------
// Skein (cat's Skein-256 in key, MAC and KDF modes)

// A Skein script: operations on numbered hash objects, replayed by the C# test.
struct SkeinScript
{
    Skein h[4];
    std::vector<std::string> ops;

    void BeginKey(int i)
    {
        h[i].BeginKey(256);
        ops.push_back(Obj().Str("op", "beginKey").Add("hash", Num(i)).Add("bits", "256").Render());
    }
    void SetKey(int i, int parent)
    {
        h[i].SetKey(&h[parent]);
        ops.push_back(Obj().Str("op", "setKey").Add("hash", Num(i)).Add("parent", Num(parent)).Render());
    }
    void BeginMac(int i)
    {
        h[i].BeginMAC();
        ops.push_back(Obj().Str("op", "beginMac").Add("hash", Num(i)).Render());
    }
    void BeginKdf(int i)
    {
        h[i].BeginKDF();
        ops.push_back(Obj().Str("op", "beginKdf").Add("hash", Num(i)).Render());
    }
    void Crunch(int i, const Bytes &b)
    {
        h[i].Crunch(b.empty() ? "" : (const void *)&b[0], (int)b.size());
        ops.push_back(Obj().Str("op", "crunch").Add("hash", Num(i)).HexField("data", b).Render());
    }
    void CrunchString(int i, const char *s)
    {
        Bytes b((const u8 *)s, (const u8 *)s + strlen(s) + 1);
        Crunch(i, b);
    }
    void End(int i)
    {
        h[i].End();
        ops.push_back(Obj().Str("op", "end").Add("hash", Num(i)).Render());
    }
    void Generate(int i, int bytes)
    {
        Bytes out(bytes + 1);
        h[i].Generate(&out[0], bytes);
        out.resize(bytes);
        ops.push_back(Obj().Str("op", "generate").Add("hash", Num(i)).Add("bytes", Num(bytes)).HexField("output", out).Render());
    }
};

static std::string SkeinCase(const char *name, SkeinScript &s)
{
    return Obj().Str("name", name).Add("ops", Array(s.ops, "      ")).Render();
}

static void SkeinVectors(const std::string &dir)
{
    SplitMix rng(0x5ce1);
    std::vector<std::string> cases;

    {
        SkeinScript s;
        s.BeginKey(0);
        s.Crunch(0, Bytes());
        s.End(0);
        s.Generate(0, 32);
        cases.push_back(SkeinCase("empty message", s));
    }

    const int lengths[] = { 1, 31, 32, 33, 63, 64, 65, 95, 96, 97, 100, 128, 200, 1000 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); ++i)
    {
        SkeinScript s;
        s.BeginKey(0);
        s.Crunch(0, rng.Take(lengths[i]));
        s.End(0);
        s.Generate(0, 32);
        std::string name = "one crunch of " + Num(lengths[i]) + " bytes";
        cases.push_back(SkeinCase(name.c_str(), s));
    }

    {
        SkeinScript s;
        s.BeginKey(0);
        const int chunks[] = { 10, 22, 40, 1, 64, 33, 0, 31, 32, 97 };
        for (size_t i = 0; i < sizeof(chunks) / sizeof(chunks[0]); ++i)
            s.Crunch(0, rng.Take(chunks[i]));
        s.End(0);
        s.Generate(0, 100);
        s.Generate(0, 13);
        s.Generate(0, 32);
        s.Generate(0, 7);
        cases.push_back(SkeinCase("chunked crunches and continued output", s));
    }

    {
        // The chain the key agreement and AuthenticatedEncryption::SetKey use.
        SkeinScript s;
        s.BeginKey(0);
        s.Crunch(0, rng.Take(64));
        s.Crunch(0, rng.Take(64));
        s.Crunch(0, rng.Take(96));
        s.End(0);
        s.Generate(0, 32);
        s.BeginKdf(0);
        s.Crunch(0, rng.Take(32));
        s.End(0);
        s.SetKey(1, 0);
        s.BeginMac(1);
        s.CrunchString(1, "shfolder.dll");
        s.End(1);
        s.Generate(1, 32);
        s.SetKey(2, 0);
        s.BeginKdf(2);
        s.CrunchString(2, "NtQuerySystemInformation");
        s.End(2);
        s.SetKey(3, 2);
        s.BeginKdf(3);
        s.CrunchString(3, "Advapi32.dll");
        s.End(3);
        s.Generate(3, 32);
        s.SetKey(3, 2);
        s.BeginKdf(3);
        s.CrunchString(3, "RichEd20.Dll");
        s.End(3);
        s.Generate(3, 8);
        cases.push_back(SkeinCase("key, KDF and MAC chain", s));
    }

    std::vector<std::pair<std::string, std::string> > sections;
    sections.push_back(std::make_pair(std::string("cases"), Array(cases, "  ")));
    WriteFile(dir, "skein", "cat::Skein with 256-bit state: operation scripts on numbered hash objects and the output of every Generate.", sections);
}

// ---------------------------------------------------------------------------------------------------------------
// HMAC-MD5 (cat's little-endian variant keyed from Skein)

static void HmacMd5Vectors(const std::string &dir)
{
    SplitMix rng(0x4d35);
    std::vector<std::string> cases;
    const int lengths[] = { 0, 1, 8, 47, 48, 55, 56, 57, 63, 64, 65, 119, 120, 127, 128, 129, 1000 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); ++i)
    {
        Bytes keySeed = rng.Take(40);
        Skein parent;
        parent.BeginKey(256);
        parent.Crunch(&keySeed[0], (int)keySeed.size());
        parent.End();

        HMAC_MD5 key;
        key.SetKey(&parent);

        // Two crunches, as AuthenticatedEncryption crunches the IV and then the message.
        Bytes first = rng.Take(8);
        Bytes message = rng.Take(lengths[i]);
        HMAC_MD5 mac;
        mac.RekeyFromMD5(&key);
        mac.BeginMAC();
        mac.Crunch(&first[0], (int)first.size());
        mac.Crunch(message.empty() ? "" : (const void *)&message[0], (int)message.size());
        mac.End();
        u8 out[16];
        mac.Generate(out, 16);

        cases.push_back(Obj()
                            .HexField("keySkeinInput", keySeed)
                            .HexField("cachedInitialState", key.CachedInitialState, 16)
                            .HexField("cachedFinalState", key.CachedFinalState, 16)
                            .HexField("first", first)
                            .HexField("message", message)
                            .HexField("mac", out, 16)
                            .Render());
    }
    std::vector<std::pair<std::string, std::string> > sections;
    sections.push_back(std::make_pair(std::string("cases"), Array(cases, "  ")));
    WriteFile(dir, "hmac-md5", "cat::HMAC_MD5 keyed by SetKey from a Skein-256 that crunched keySkeinInput once; the MAC crunches first then message.", sections);
}

// ---------------------------------------------------------------------------------------------------------------
// ChaCha (cat's 12-round variant)

static void ChaChaVectors(const std::string &dir)
{
    SplitMix rng(0xc4ac);
    std::vector<std::string> cases;
    const int lengths[] = { 0, 1, 3, 4, 5, 63, 64, 65, 127, 128, 129, 300 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); ++i)
    {
        Bytes key = rng.Take(32);
        u64 iv = rng.Next();
        if (i == 1) iv = 0;
        if (i == 2) iv = ~(u64)0;
        Bytes input = rng.Take(lengths[i]);
        Bytes output(input.size() + 1);
        ChaChaKey k;
        k.Set(&key[0], (int)key.size());
        ChaChaOutput c(k, iv);
        c.Crypt(input.empty() ? output.data() : &input[0], &output[0], (int)input.size());
        output.resize(input.size());
        cases.push_back(Obj().HexField("key", key).Add("iv", U64(iv)).HexField("input", input).HexField("output", output).Render());
    }
    std::vector<std::pair<std::string, std::string> > sections;
    sections.push_back(std::make_pair(std::string("cases"), Array(cases, "  ")));
    WriteFile(dir, "chacha", "cat::ChaChaKey::Set then ChaChaOutput(key, iv).Crypt(input).", sections);
}

// ---------------------------------------------------------------------------------------------------------------
// CookieJar

static void CookieJarVectors(const std::string &dir)
{
    SplitMix rng(0xc00c);
    std::vector<std::string> cases;
    const int lengths[] = { 16, 16, 16, 28, 4, 5, 6, 7, 1, 2, 3, 0, 48 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); ++i)
    {
        ReplayRandom random(0xc00c0000 + i);
        CookieJar jar;
        jar.Initialize(&random);
        Bytes address = rng.Take(lengths[i]);
        u32 epoch = (u32)rng.Next();
        u32 hash = jar.Hash(address.empty() ? "" : (const void *)&address[0], (int)address.size(), epoch);
        cases.push_back(Obj()
                            .HexField("key", random.drawn)
                            .HexField("address", address)
                            .Add("epoch", Num(epoch))
                            .Add("hash", Num(hash))
                            .Render());
    }
    std::vector<std::pair<std::string, std::string> > sections;
    sections.push_back(std::make_pair(std::string("cases"), Array(cases, "  ")));
    WriteFile(dir, "cookie-jar", "cat::CookieJar::Hash(address, epoch) after Initialize drew key from the CSPRNG.", sections);
}

// ---------------------------------------------------------------------------------------------------------------
// Curve arithmetic

static Bytes SaveReg(BigTwistedEdwards *math, const Leg *reg)
{
    Bytes b(math->RegBytes());
    math->Save(reg, &b[0], (int)b.size());
    return b;
}

static Bytes SaveXY(BigTwistedEdwards *math, const Leg *pt)
{
    Bytes b(math->RegBytes() * 2);
    math->SaveAffineXY(pt, &b[0], &b[math->RegBytes()]);
    return b;
}

static Bytes SaveX(BigTwistedEdwards *math, const Leg *pt)
{
    Bytes b(math->RegBytes());
    math->SaveAffineX(pt, &b[0]);
    return b;
}

static Bytes Scalar(const char *kind, BigTwistedEdwards *math, SplitMix &rng)
{
    int n = math->RegBytes();
    Bytes b(n, 0);
    std::string k = kind;
    if (k == "zero") return b;
    if (k == "one") { b[0] = 1; return b; }
    if (k == "two") { b[0] = 2; return b; }
    if (k == "q-1" || k == "q" || k == "q+1")
    {
        math->Save(math->GetCurveQ(), &b[0], n);
        if (k == "q-1") b[0] -= 1;
        if (k == "q+1") b[0] += 1;
        return b;
    }
    if (k == "max") { memset(&b[0], 0xff, n); return b; }
    if (k == "high-bit") { b[n - 1] = 0x80; return b; }
    if (k == "alternating") { for (int i = 0; i < n; ++i) b[i] = (i & 1) ? 0x55 : 0xaa; return b; }
    return rng.Take(n);
}

static void CurveVectors(const std::string &dir)
{
    BigTwistedEdwards *math = KeyAgreementCommon::InstantiateMath(256);
    SplitMix rng(0xed25);
    const int n = math->RegBytes();
    std::vector<std::pair<std::string, std::string> > sections;

    sections.push_back(std::make_pair(std::string("curve"),
                                      Obj()
                                          .HexField("q", SaveReg(math, math->GetCurveQ()))
                                          .HexField("modulus", SaveReg(math, math->GetModulus()))
                                          .HexField("generator", SaveXY(math, math->GetGenerator()))
                                          .Add("d", Num((long long)math->GetCurveD()))
                                          .Render()));

    // Key pairs, as EasyHandshake::GenerateServerKey makes them.
    {
        std::vector<std::string> cases;
        for (int i = 0; i < 6; ++i)
        {
            EasyHandshake handshake;
            ReplayRandom *random = new ReplayRandom(0x6e790000 + i);
            if (i == 1)
            {
                // First draw is rejected (>= q after the high bit is cleared), the second is 1 (rejected), the third used.
                random->prefix.assign(n, 0xff);
                Bytes one(n, 0);
                one[0] = 1;
                random->prefix.insert(random->prefix.end(), one.begin(), one.end());
            }
            delete handshake.tls_csprng;
            handshake.tls_csprng = random;
            u8 publicKey[EasyHandshake::PUBLIC_KEY_BYTES], privateKey[EasyHandshake::PRIVATE_KEY_BYTES];
            bool ok = handshake.GenerateServerKey(publicKey, privateKey);
            cases.push_back(Obj()
                                .HexField("random", random->drawn)
                                .Add("ok", Bool(ok))
                                .HexField("publicKey", publicKey, sizeof(publicKey))
                                .HexField("privateKey", privateKey, sizeof(privateKey))
                                .Render());
        }
        sections.push_back(std::make_pair(std::string("keyPairs"), Array(cases, "  ")));
    }

    // Points to multiply: the generator and points made from random scalars.
    std::vector<Bytes> points;
    points.push_back(SaveXY(math, math->GetGenerator()));
    for (int i = 0; i < 3; ++i)
    {
        Bytes k = rng.Take(n);
        k[n - 1] &= 0x3f;
        Leg *kr = math->Get(0);
        Leg *pt = math->Get(1);
        math->Load(&k[0], n, kr);
        math->PtMultiply(math->GetGenerator(), kr, 0, pt);
        points.push_back(SaveXY(math, pt));
    }

    const char *scalarKinds[] = { "zero", "one", "two", "q-1", "q", "q+1", "max", "high-bit", "alternating", "random", "random", "random" };
    const int scalarCount = sizeof(scalarKinds) / sizeof(scalarKinds[0]);

    // PtMultiply with the default 6-bit window, and with explicit 6-bit and 8-bit precomputed tables.
    {
        std::vector<std::string> cases;
        for (size_t p = 0; p < points.size(); ++p)
            for (int s = 0; s < scalarCount; ++s)
                for (int msb = 0; msb < 2; ++msb)
                {
                    if (msb && s % 3) continue;
                    Bytes k = Scalar(scalarKinds[s], math, rng);
                    Leg *pt = math->Get(0);
                    Leg *kr = math->Get(4);
                    Leg *out = math->Get(5);
                    if (!math->LoadVerifyAffineXY(&points[p][0], &points[p][n], pt)) abort();
                    math->PtUnpack(pt);
                    math->Load(&k[0], n, kr);
                    math->PtMultiply(pt, kr, (u8)msb, out);
                    Bytes result = SaveXY(math, out);

                    std::vector<std::string> windows;
                    const int ws[] = { 6, 8 };
                    for (int w = 0; w < 2; ++w)
                    {
                        Leg *table = math->PtMultiplyPrecompAlloc(ws[w]);
                        math->PtMultiplyPrecomp(pt, ws[w], table);
                        math->PtMultiply(table, ws[w], kr, (u8)msb, out);
                        windows.push_back(Obj().Add("w", Num(ws[w])).HexField("result", SaveXY(math, out)).Render());
                        Aligned::Delete(table);
                    }
                    cases.push_back(Obj()
                                        .HexField("point", points[p])
                                        .Str("scalarKind", scalarKinds[s])
                                        .HexField("scalar", k)
                                        .Add("msb", Num(msb))
                                        .HexField("result", result)
                                        .Add("precomputed", Array(windows, "    "))
                                        .Render());
                }
        sections.push_back(std::make_pair(std::string("multiply"), Array(cases, "  ")));
    }

    // PtSiMultiply: k*P + l*Q, then SaveAffineX as the key agreement does.
    {
        std::vector<std::string> cases;
        const int ws[] = { 6, 8 };
        for (int c = 0; c < 10; ++c)
        {
            int w = ws[c & 1];
            const Bytes &P = points[c % points.size()];
            const Bytes &Q = points[(c + 1) % points.size()];
            Bytes k = Scalar(scalarKinds[(c * 5) % scalarCount], math, rng);
            Bytes l = Scalar(scalarKinds[(c * 7 + 3) % scalarCount], math, rng);
            int msbK = c == 8, msbL = c == 9;
            Leg *p = math->Get(0);
            Leg *q = math->Get(4);
            Leg *kr = math->Get(8);
            Leg *lr = math->Get(9);
            Leg *out = math->Get(10);
            math->LoadVerifyAffineXY(&P[0], &P[n], p);
            math->PtUnpack(p);
            math->LoadVerifyAffineXY(&Q[0], &Q[n], q);
            math->PtUnpack(q);
            math->Load(&k[0], n, kr);
            math->Load(&l[0], n, lr);
            Leg *tp = math->PtMultiplyPrecompAlloc(w);
            Leg *tq = math->PtMultiplyPrecompAlloc(w);
            math->PtMultiplyPrecomp(p, w, tp);
            math->PtMultiplyPrecomp(q, w, tq);
            math->PtSiMultiply(tp, tq, w, kr, (u8)msbK, lr, (u8)msbL, out);
            Bytes x = SaveX(math, out);
            Bytes xy = SaveXY(math, out);
            Aligned::Delete(tp);
            Aligned::Delete(tq);
            cases.push_back(Obj()
                                .HexField("p", P)
                                .HexField("q", Q)
                                .Add("w", Num(w))
                                .HexField("k", k)
                                .Add("msbK", Num(msbK))
                                .HexField("l", l)
                                .Add("msbL", Num(msbL))
                                .HexField("resultX", x)
                                .HexField("result", xy)
                                .Render());
        }
        sections.push_back(std::make_pair(std::string("simultaneousMultiply"), Array(cases, "  ")));
    }

    // h*P = PtDoubleZ1 then PtEDouble, and PtNormalize.
    {
        std::vector<std::string> cases;
        for (size_t p = 0; p < points.size(); ++p)
        {
            Leg *pt = math->Get(0);
            Leg *h = math->Get(4);
            Leg *norm = math->Get(8);
            math->LoadVerifyAffineXY(&points[p][0], &points[p][n], pt);
            math->PtDoubleZ1(pt, h);
            math->PtEDouble(h, h);
            Bytes hx = SaveXY(math, h);
            math->PtNormalize(h, norm);
            Bytes normalized(n * 4);
            for (int r = 0; r < 4; ++r)
                math->Save(norm + r * math->Legs(), &normalized[r * n], n);
            cases.push_back(Obj().HexField("point", points[p]).HexField("cofactorMultiple", hx).HexField("normalizedXYTZ", normalized).Render());
        }
        sections.push_back(std::make_pair(std::string("cofactor"), Array(cases, "  ")));
    }

    // LoadVerifyAffineXY and IsAffineIdentity.
    {
        std::vector<std::string> cases;
        std::vector<Bytes> inputs;
        for (size_t p = 0; p < points.size(); ++p) inputs.push_back(points[p]);
        {
            Bytes identity(n * 2, 0);
            identity[n] = 1;
            inputs.push_back(identity);
        }
        {
            // x = p (congruent to the identity's x = 0), y = 1
            Bytes b(n * 2, 0);
            math->Save(math->GetModulus(), &b[0], n);
            b[n] = 1;
            inputs.push_back(b);
        }
        {
            // y + p for a valid point when it still fits in 256 bits, and a flipped coordinate.
            Bytes b = points[1];
            b[0] ^= 1;
            inputs.push_back(b);
            Bytes c = points[2];
            c[n] ^= 0x80;
            inputs.push_back(c);
        }
        for (int i = 0; i < 4; ++i) inputs.push_back(rng.Take(n * 2));
        {
            Bytes zero(n * 2, 0);
            inputs.push_back(zero);
            Bytes ones(n * 2, 0xff);
            inputs.push_back(ones);
        }
        for (size_t i = 0; i < inputs.size(); ++i)
        {
            Leg *pt = math->Get(0);
            bool valid = math->LoadVerifyAffineXY(&inputs[i][0], &inputs[i][n], pt);
            bool identity = math->IsAffineIdentity(pt);
            cases.push_back(Obj().HexField("xy", inputs[i]).Add("valid", Bool(valid)).Add("identity", Bool(identity)).Render());
        }
        sections.push_back(std::make_pair(std::string("validate"), Array(cases, "  ")));
    }

    // MulMod by q and MrInvert.
    {
        std::vector<std::string> mulmod, invert;
        const char *kinds[] = { "zero", "one", "q-1", "q", "q+1", "max", "random", "random", "random", "random" };
        const int kindCount = sizeof(kinds) / sizeof(kinds[0]);
        for (int i = 0; i < kindCount; ++i)
            for (int j = 0; j < kindCount; j += 3)
            {
                Bytes a = Scalar(kinds[i], math, rng);
                Bytes b = Scalar(kinds[j], math, rng);
                Leg *ar = math->Get(0);
                Leg *br = math->Get(1);
                Leg *r = math->Get(2);
                math->Load(&a[0], n, ar);
                math->Load(&b[0], n, br);
                math->MulMod(ar, br, math->GetCurveQ(), r);
                mulmod.push_back(Obj().HexField("a", a).HexField("b", b).HexField("result", SaveReg(math, r)).Render());
            }
        for (int i = 0; i < kindCount; ++i)
        {
            Bytes a = Scalar(kinds[i], math, rng);
            Leg *ar = math->Get(0);
            Leg *r = math->Get(1);
            math->Load(&a[0], n, ar);
            math->MrInvert(ar, r);
            invert.push_back(Obj().HexField("input", a).HexField("result", SaveReg(math, r)).Render());
        }
        sections.push_back(std::make_pair(std::string("mulModQ"), Array(mulmod, "  ")));
        sections.push_back(std::make_pair(std::string("invert"), Array(invert, "  ")));
    }

    delete math;
    WriteFile(dir, "curve", "cat::BigTwistedEdwards over p = 2^256 - 435, d = 31720 (KeyAgreementCommon::InstantiateMath(256)). Points are affine x || y, scalars and field elements 32 bytes little-endian.", sections);
}

// ---------------------------------------------------------------------------------------------------------------
// Handshake and authenticated encryption

struct Party
{
    AuthenticatedEncryption *auth;
    std::vector<Bytes> sent;
};

struct Traffic
{
    Party client, server;
    std::vector<std::string> ops;
    SplitMix rng;

    Traffic(AuthenticatedEncryption *c, AuthenticatedEncryption *s) : rng(0x7aff1c)
    {
        client.auth = c;
        server.auth = s;
    }

    Party &Side(const char *by) { return strcmp(by, "client") == 0 ? client : server; }

    void Encrypt(const char *by, int bytes) { EncryptPlain(by, rng.Take(bytes), true); }

    void EncryptPlain(const char *by, const Bytes &plain, bool record)
    {
        Party &p = Side(by);
        int bytes = (int)plain.size();
        Bytes buffer(bytes + AuthenticatedEncryption::OVERHEAD_BYTES + 1);
        if (bytes) memcpy(&buffer[0], &plain[0], bytes);
        u32 length = (u32)bytes;
        bool ok = p.auth->Encrypt(&buffer[0], (u32)buffer.size(), length);
        if (!ok) abort();
        buffer.resize(length);
        p.sent.push_back(buffer);
        if (record)
            ops.push_back(Obj().Str("op", "encrypt").Str("by", by).HexField("plain", plain).HexField("cipher", buffer).Render());
    }

    // Plaintext i of the batch is bytes long with byte j = (u8)(i + j); ciphertexts are not recorded.
    void EncryptMany(const char *by, int count, int bytes)
    {
        for (int i = 0; i < count; ++i)
        {
            Bytes plain(bytes);
            for (int j = 0; j < bytes; ++j) plain[j] = (u8)(i + j);
            EncryptPlain(by, plain, false);
        }
        ops.push_back(Obj().Str("op", "encryptMany").Str("by", by).Add("count", Num(count)).Add("bytes", Num(bytes)).Render());
    }

    // Decrypts the index-th datagram the other side sent.
    void Decrypt(const char *by, int index)
    {
        Party &p = Side(by);
        Party &other = &p == &client ? server : client;
        if (index < 0) index += (int)other.sent.size();
        Bytes buffer = other.sent[index];
        u32 length = (u32)buffer.size();
        bool ok = p.auth->Decrypt(buffer.empty() ? 0 : &buffer[0], length);
        Obj o;
        o.Str("op", "decrypt").Str("by", by).Add("index", Num(index)).Add("ok", Bool(ok));
        if (ok) o.HexField("plain", &buffer[0], length);
        ops.push_back(o.Render());
    }

    void DecryptRaw(const char *by, const Bytes &cipher)
    {
        Party &p = Side(by);
        Bytes buffer = cipher;
        buffer.push_back(0);
        u32 length = (u32)cipher.size();
        bool ok = p.auth->Decrypt(&buffer[0], length);
        Obj o;
        o.Str("op", "decryptRaw").Str("by", by).HexField("cipher", cipher).Add("ok", Bool(ok));
        if (ok) o.HexField("plain", &buffer[0], length);
        ops.push_back(o.Render());
    }

    void Proof(const char *by, int bytes)
    {
        Party &p = Side(by);
        Bytes proof(bytes);
        p.auth->GenerateProof(&proof[0], bytes);
        Party &other = &p == &client ? server : client;
        bool ok = other.auth->ValidateProof(&proof[0], bytes);
        Bytes bad = proof;
        bad[bytes - 1] ^= 1;
        bool badOk = other.auth->ValidateProof(&bad[0], bytes);
        ops.push_back(Obj().Str("op", "proof").Str("by", by).Add("bytes", Num(bytes)).HexField("proof", proof).Add("validated", Bool(ok)).Add("tamperedValidated", Bool(badOk)).Render());
    }
};

// AuthenticatedEncryption and ChaChaKey keep their state in implicitly private members, which the access
// defines above cannot reach; this mirrors AuthenticatedEncryption's layout (it has no virtual members).
struct AuthenticatedEncryptionLayout
{
    bool is_initiator, accept_out_of_order;
    Skein key_hash;
    HMAC_MD5 local_mac_key, remote_mac_key;
    u32 local_cipher_state[16], remote_cipher_state[16];
    u64 local_iv, remote_iv;
    u64 iv_bitmap[16];
};

static std::string AuthState(AuthenticatedEncryption *auth)
{
    typedef char LayoutMatches[sizeof(AuthenticatedEncryptionLayout) == sizeof(AuthenticatedEncryption) ? 1 : -1];
    (void)sizeof(LayoutMatches);
    const AuthenticatedEncryptionLayout *a = reinterpret_cast<const AuthenticatedEncryptionLayout *>(auth);
    return Obj()
        .Add("isInitiator", Bool(a->is_initiator))
        .Add("localIv", U64(a->local_iv))
        .Add("remoteIv", U64(a->remote_iv))
        .HexField("localCipherState", a->local_cipher_state, 48)
        .HexField("remoteCipherState", a->remote_cipher_state, 48)
        .HexField("localMacInitial", a->local_mac_key.CachedInitialState, 16)
        .HexField("localMacFinal", a->local_mac_key.CachedFinalState, 16)
        .HexField("remoteMacInitial", a->remote_mac_key.CachedInitialState, 16)
        .HexField("remoteMacFinal", a->remote_mac_key.CachedFinalState, 16)
        .Render();
}

static void ReplaceRandom(EasyHandshake &h, ReplayRandom *r)
{
    delete h.tls_csprng;
    h.tls_csprng = r;
}

static void ServerKey(u64 seed, u8 *publicKey, u8 *privateKey, Bytes &drawn)
{
    EasyHandshake maker;
    ReplayRandom *r = new ReplayRandom(seed);
    ReplaceRandom(maker, r);
    if (!maker.GenerateServerKey(publicKey, privateKey)) abort();
    drawn = r->drawn;
}

static void HandshakeVectors(const std::string &dir)
{
    std::vector<std::string> cases;

    for (int variant = 0; variant < 2; ++variant)
    {
        bool identity = variant == 1;
        u8 serverPublic[EasyHandshake::PUBLIC_KEY_BYTES], serverPrivate[EasyHandshake::PRIVATE_KEY_BYTES];
        Bytes serverKeyRandom;
        ServerKey(0x5e4e0000 + variant, serverPublic, serverPrivate, serverKeyRandom);
        u8 clientPublic[EasyHandshake::PUBLIC_KEY_BYTES], clientPrivate[EasyHandshake::PRIVATE_KEY_BYTES];
        Bytes clientKeyRandom;
        ServerKey(0xc11e0000 + variant, clientPublic, clientPrivate, clientKeyRandom);

        ServerEasyHandshake server;
        ReplayRandom *serverRandom = new ReplayRandom(0x5e4e1000 + variant);
        ReplaceRandom(server, serverRandom);
        ClientEasyHandshake client;
        ReplayRandom *clientRandom = new ReplayRandom(0xc11e1000 + variant);
        ReplaceRandom(client, clientRandom);

        bool serverInit = server.Initialize(serverPublic, serverPrivate);
        CookieJar jar;
        server.FillCookieJar(&jar);
        bool clientInit = client.Initialize(serverPublic);
        bool clientIdentity = !identity || client.SetIdentity(clientPublic, clientPrivate);
        u8 challenge[EasyHandshake::CHALLENGE_BYTES];
        bool challenged = client.GenerateChallenge(challenge);

        AuthenticatedEncryption serverAuth, clientAuth;
        u8 answer[EasyHandshake::ANSWER_BYTES];
        bool answered = server.ProcessChallenge(challenge, answer, &serverAuth);

        u8 ident[EasyHandshake::IDENTITY_BYTES];
        memset(ident, 0, sizeof(ident));
        bool processed = identity ? client.ProcessAnswerWithIdentity(answer, ident, &clientAuth)
                                  : client.ProcessAnswer(answer, &clientAuth);
        u8 verifiedKey[EasyHandshake::PUBLIC_KEY_BYTES];
        memset(verifiedKey, 0, sizeof(verifiedKey));
        bool verified = false, tamperedVerified = false;
        if (identity)
        {
            verified = server.VerifyInitiatorIdentity(answer, ident, verifiedKey);
            u8 bad[EasyHandshake::IDENTITY_BYTES];
            memcpy(bad, ident, sizeof(bad));
            bad[EasyHandshake::BYTES * 3] ^= 1; // signature e
            u8 unused[EasyHandshake::PUBLIC_KEY_BYTES];
            tamperedVerified = server.VerifyInitiatorIdentity(answer, bad, unused);
        }

        Obj handshake;
        handshake.Add("twoWayAuthentication", Bool(identity))
            .HexField("serverKeyRandom", serverKeyRandom)
            .HexField("serverPublicKey", serverPublic, sizeof(serverPublic))
            .HexField("serverPrivateKey", serverPrivate, sizeof(serverPrivate));
        if (identity)
            handshake.HexField("clientKeyRandom", clientKeyRandom)
                .HexField("clientPublicKey", clientPublic, sizeof(clientPublic))
                .HexField("clientPrivateKey", clientPrivate, sizeof(clientPrivate));
        handshake.Add("serverInitialized", Bool(serverInit))
            .Add("clientInitialized", Bool(clientInit && clientIdentity))
            .HexField("challenge", challenge, sizeof(challenge))
            .Add("answered", Bool(answered))
            .HexField("answer", answer, sizeof(answer))
            .Add("answerProcessed", Bool(processed));
        if (identity)
            handshake.HexField("identity", ident, sizeof(ident))
                .Add("identityVerified", Bool(verified))
                .HexField("verifiedPublicKey", verifiedKey, sizeof(verifiedKey))
                .Add("tamperedIdentityVerified", Bool(tamperedVerified));
        handshake.Add("serverAuth", AuthState(&serverAuth)).Add("clientAuth", AuthState(&clientAuth));

        // Traffic once both sides are keyed.
        Traffic t(&clientAuth, &serverAuth);
        t.Proof("client", EasyHandshake::PROOF_BYTES);
        t.Proof("server", EasyHandshake::PROOF_BYTES);
        const int sizes[] = { 0, 1, 5, 64, 100, 1400 };
        for (int i = 0; i < 6; ++i) t.Encrypt("client", sizes[i]);
        for (int i = 0; i < 6; ++i) t.Decrypt("server", i);
        for (int i = 0; i < 3; ++i) t.Encrypt("server", 20 + i);
        for (int i = 0; i < 3; ++i) t.Decrypt("client", i);
        t.Decrypt("server", 2); // replay
        // Out of order within the window.
        t.Encrypt("client", 10);
        t.Encrypt("client", 11);
        t.Encrypt("client", 12);
        t.Decrypt("server", 8);
        t.Decrypt("server", 6);
        t.Decrypt("server", 7);
        t.Decrypt("server", 6);
        // Tampering and truncation.
        t.Encrypt("client", 30);
        {
            Bytes c = t.client.sent.back();
            Bytes flipped = c;
            flipped[3] ^= 0x10;
            t.DecryptRaw("server", flipped);
            Bytes flippedIv = c;
            flippedIv[c.size() - 1] ^= 0x01;
            t.DecryptRaw("server", flippedIv);
            Bytes shortOne(c.begin(), c.begin() + 10);
            t.DecryptRaw("server", shortOne);
        }
        t.Decrypt("server", 9);
        // A gap larger than the 1024-bit replay window.
        t.EncryptMany("client", 1100, 3);
        t.Decrypt("server", -1);
        t.Decrypt("server", -1100);
        t.Decrypt("server", -2);
        t.Decrypt("server", -1000);
        t.Decrypt("server", -1000);
        t.Decrypt("server", -1024);
        t.Decrypt("server", -1025);
        t.Decrypt("server", -60);
        t.Decrypt("server", -64);
        t.Decrypt("server", -65);
        t.EncryptMany("client", 70, 2);
        t.Decrypt("server", -1);
        t.Decrypt("server", -70);
        t.Decrypt("server", -71);
        t.Decrypt("server", -134);
        // Window shifts by whole 64-bit words (AcceptIV's bit_shift of 0).
        for (int words = 1; words <= 2; ++words)
        {
            t.EncryptMany("client", 64 * words, 1);
            t.Decrypt("server", -1);
            t.Decrypt("server", -2);
            t.Decrypt("server", -64 * words);
            t.Decrypt("server", -64 * words - 1);
            t.Decrypt("server", -64 * words - 2);
            t.Decrypt("server", -64 * words - 3);
            t.Decrypt("server", -64 * words - 64);
            t.Decrypt("server", -64 * words - 65);
        }

        handshake.HexField("serverRandom", serverRandom->drawn)
            .HexField("clientRandom", clientRandom->drawn)
            .Add("traffic", Array(t.ops, "      "));
        cases.push_back(handshake.Render());
    }

    // Rejections: an invalid challenge point, a tampered answer, and the server's ephemeral rekey after 100 challenges.
    std::vector<std::string> rekeys;
    {
        u8 serverPublic[EasyHandshake::PUBLIC_KEY_BYTES], serverPrivate[EasyHandshake::PRIVATE_KEY_BYTES];
        Bytes serverKeyRandom;
        ServerKey(0x5e4e2000, serverPublic, serverPrivate, serverKeyRandom);
        ServerEasyHandshake server;
        ReplayRandom *serverRandom = new ReplayRandom(0x5e4e3000);
        ReplaceRandom(server, serverRandom);
        ClientEasyHandshake client;
        ReplayRandom *clientRandom = new ReplayRandom(0xc11e3000);
        ReplaceRandom(client, clientRandom);
        server.Initialize(serverPublic, serverPrivate);
        client.Initialize(serverPublic);
        u8 challenge[EasyHandshake::CHALLENGE_BYTES];
        client.GenerateChallenge(challenge);

        std::vector<std::string> answers;
        for (int i = 0; i < 103; ++i)
        {
            AuthenticatedEncryption auth;
            u8 answer[EasyHandshake::ANSWER_BYTES];
            bool ok = server.ProcessChallenge(challenge, answer, &auth);
            answers.push_back(Obj().Add("ok", Bool(ok)).HexField("answer", answer, sizeof(answer)).Render());
        }
        u8 badChallenge[EasyHandshake::CHALLENGE_BYTES];
        memcpy(badChallenge, challenge, sizeof(badChallenge));
        badChallenge[5] ^= 0x20;
        AuthenticatedEncryption unused;
        u8 badAnswerOut[EasyHandshake::ANSWER_BYTES];
        bool badChallengeOk = server.ProcessChallenge(badChallenge, badAnswerOut, &unused);

        u8 identityChallenge[EasyHandshake::CHALLENGE_BYTES];
        memset(identityChallenge, 0, sizeof(identityChallenge));
        identityChallenge[EasyHandshake::BYTES] = 1; // (0, 1) is on the curve but is the identity
        bool identityChallengeOk = server.ProcessChallenge(identityChallenge, badAnswerOut, &unused);

        // Tamper with the MAC, then with r, of the last good answer.
        u8 lastAnswer[EasyHandshake::ANSWER_BYTES];
        AuthenticatedEncryption serverAuth;
        server.ProcessChallenge(challenge, lastAnswer, &serverAuth);
        u8 tampered[EasyHandshake::ANSWER_BYTES];
        memcpy(tampered, lastAnswer, sizeof(tampered));
        tampered[EasyHandshake::ANSWER_BYTES - 1] ^= 1;
        AuthenticatedEncryption clientAuth;
        bool tamperedMacOk = client.ProcessAnswer(tampered, &clientAuth);
        memcpy(tampered, lastAnswer, sizeof(tampered));
        tampered[EasyHandshake::BYTES * 2] ^= 1;
        bool tamperedNonceOk = client.ProcessAnswer(tampered, &clientAuth);
        bool goodOk = client.ProcessAnswer(lastAnswer, &clientAuth);

        rekeys.push_back(Obj()
                             .HexField("serverPublicKey", serverPublic, sizeof(serverPublic))
                             .HexField("serverPrivateKey", serverPrivate, sizeof(serverPrivate))
                             .HexField("challenge", challenge, sizeof(challenge))
                             .Add("answers", Array(answers, "      "))
                             .HexField("invalidChallenge", badChallenge, sizeof(badChallenge))
                             .Add("invalidChallengeAnswered", Bool(badChallengeOk))
                             .HexField("identityChallenge", identityChallenge, sizeof(identityChallenge))
                             .Add("identityChallengeAnswered", Bool(identityChallengeOk))
                             .HexField("lastAnswer", lastAnswer, sizeof(lastAnswer))
                             .Add("tamperedMacProcessed", Bool(tamperedMacOk))
                             .Add("tamperedNonceProcessed", Bool(tamperedNonceOk))
                             .Add("lastAnswerProcessed", Bool(goodOk))
                             .HexField("serverRandom", serverRandom->drawn)
                             .HexField("clientRandom", clientRandom->drawn)
                             .Render());
    }

    std::vector<std::string> counters;
    {
        SplitMix rng(0x1c0);
        const u64 lasts[] = { 0, 0xffffff, 0x1000000, 0x7fffff, 0x800000, 0xfffffffffffffff0ULL, 0x123456789abcdefULL };
        for (size_t i = 0; i < sizeof(lasts) / sizeof(lasts[0]); ++i)
        {
            const u32 partials[] = { 0, 1, 0x7fffff, 0x800000, 0x800001, 0xffffff, (u32)(lasts[i] & 0xffffff), (u32)((lasts[i] + 5) & 0xffffff), (u32)((lasts[i] - 5) & 0xffffff), (u32)(rng.Next() & 0xffffff) };
            for (size_t j = 0; j < sizeof(partials) / sizeof(partials[0]); ++j)
            {
                u64 r = ReconstructCounter<AuthenticatedEncryption::IV_BITS>(lasts[i], partials[j]);
                counters.push_back(Obj().Add("last", U64(lasts[i])).Add("partial", Num(partials[j])).Add("result", U64(r)).Render());
            }
        }
    }

    std::vector<std::pair<std::string, std::string> > sections;
    sections.push_back(std::make_pair(std::string("handshakes"), Array(cases, "  ")));
    sections.push_back(std::make_pair(std::string("serverChallenges"), Array(rekeys, "  ")));
    sections.push_back(std::make_pair(std::string("reconstructCounter"), Array(counters, "  ")));
    WriteFile(dir, "handshake",
              "cat::ServerEasyHandshake and ClientEasyHandshake with recorded CSPRNG draws, the AuthenticatedEncryption state each side derives, and encrypted traffic between them. Plaintext i of an encryptMany batch has byte j = (i + j) mod 256.",
              sections);
}

int main(int argc, char **argv)
{
    if (argc != 2)
    {
        fprintf(stderr, "usage: cat-vectors <output directory>\n");
        return 2;
    }
    if (!EasyHandshake::Initialize())
    {
        fprintf(stderr, "EasyHandshake::Initialize failed\n");
        return 1;
    }
    std::string dir = argv[1];
    SkeinVectors(dir);
    HmacMd5Vectors(dir);
    ChaChaVectors(dir);
    CookieJarVectors(dir);
    CurveVectors(dir);
    HandshakeVectors(dir);
    EasyHandshake::Shutdown();
    return 0;
}
