using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using TelltaleToolKit.Hashing;
using TelltaleToolKit.T3Types.Animations;
using TelltaleToolKit.T3Types.Skeletons;

namespace TelltaleD3DMeshEditor.Export;

// Decodes Telltale .anm animation values into per-bone translation/rotation tracks for GLB export.
// The CSPK2 decoder is ported 1:1 from TelltaleHydra's C++ reference implementation (delta rotation
// accumulation without normalization, 20-bit lo/hi translation extraction, RangeVector scaling).
public static class AnimationExporter
{
    public sealed record BoneTrack(
        string BoneName, ulong BoneHash,
        List<float> Times, List<Vector3> Translations, List<Quaternion> Rotations);

    public static List<BoneTrack> ExtractTracks(Animation anim)
    {
        var tracks = new List<BoneTrack>();
        // CSPK2 covers every bone; skip the redundant KeyframedValue root-bone track when present.
        bool hasCspk2 = anim.Values.Any(v => v is CompressedSkeletonPoseKeys2);
        foreach (var v in anim.Values)
        {
            if (hasCspk2 && v is KeyframedValue<Transform>)
                continue;
            ExtractFromValue(v, tracks);
        }
        return tracks;
    }

    private static void ExtractFromValue(IAnimatedValueInterface value, List<BoneTrack> tracks)
    {
        var crc = value.AnimationValueInterfaceBase.Name.Crc64;
        var name = value.AnimationValueInterfaceBase.Name.DebugString ?? $"0x{crc:X16}";
        switch (value)
        {
            case KeyframedValue<Transform> kft: ExtractKeyframedTransform(kft, name, crc, tracks); break;
            case CompressedSkeletonPoseKeys cspk: ExtractCspk(cspk, tracks); break;
            case CompressedSkeletonPoseKeys2 cspk2: ExtractCspk2(cspk2, tracks); break;
        }
    }

    private static void ExtractKeyframedTransform(KeyframedValue<Transform> kft, string name, ulong crc, List<BoneTrack> tracks)
    {
        if (kft.Samples.Count == 0) return;
        var times = new List<float>(kft.Samples.Count);
        var trans = new List<Vector3>(kft.Samples.Count);
        var rots = new List<Quaternion>(kft.Samples.Count);
        foreach (var s in kft.Samples) { times.Add(s.Time); trans.Add(s.Value.Translation); rots.Add(s.Value.Rotation); }
        tracks.Add(new BoneTrack(name, crc, times, trans, rots));
    }

    private static void ExtractCspk(CompressedSkeletonPoseKeys cspk, List<BoneTrack> tracks)
    {
        try { foreach (var t in DecodeV1(cspk.Data, cspk.GetHeader())) tracks.Add(t); } catch { }
    }

    private static void ExtractCspk2(CompressedSkeletonPoseKeys2 cspk2, List<BoneTrack> tracks)
    {
        try
        {
            var hdr = cspk2.GetHeader();
            ScaleHeaderRanges(ref hdr);
            var result = DecodeCspk2Commands(cspk2.Data, hdr);
            foreach (var kv in result) tracks.Add(kv.Value);
        }
        catch { }
    }

    // ── Hydra-based CSPK2 decoder ──

    private static void ScaleHeaderRanges(ref CompressedSkeletonPoseKeys2.Header hdr)
    {
        // From TelltaleHydra: stored ranges are scaled by hardcoded factors.
        // Must mutate through local Vector3 copies since nested struct fields aren't lvalues.
        var rv = hdr.RangeVector; rv.X *= 9.536752e-07f; rv.Y *= 2.384186e-07f; rv.Z *= 2.384186e-07f; hdr.RangeVector = rv;
        var rdv = hdr.RangeDeltaV; rdv.X *= 0.0009775171f; rdv.Y *= 0.0004885198f; rdv.Z *= 0.0004885198f; hdr.RangeDeltaV = rdv;
        var rdq = hdr.RangeDeltaQ; rdq.X *= 0.0009775171f; rdq.Y *= 0.0004885198f; rdq.Z *= 0.0004885198f; hdr.RangeDeltaQ = rdq;
    }

    private static Dictionary<int, BoneTrack> DecodeCspk2Commands(byte[] data, CompressedSkeletonPoseKeys2.Header hdr)
    {
        int hdrSz = Marshal.SizeOf<CompressedSkeletonPoseKeys2.Header>();
        var buf = data;
        int cspkOff = hdrSz + 8; // skip header + 8-byte unknown field
        int boneNameOff = cspkOff + (int)hdr.SampleDataSize;  // bone CRC64 array (BoneCount × 8 bytes)
        int cmdOff = boneNameOff + (int)hdr.BoneCount * 8;    // command headers start after bone names

        // Read the REAL bone CRC64s from the animation data.
        var boneCRCs = new ulong[hdr.BoneCount];
        for (int i = 0; i < hdr.BoneCount; i++)
        {
            if (boneNameOff + 8 <= buf.Length)
                boneCRCs[i] = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(boneNameOff + i * 8, 8));
            else
                boneCRCs[i] = Crc64.Compute($"bone{i}");
        }

        var cspkBuf = new Span<byte>(buf, cspkOff, buf.Length - cspkOff);
        var cmdBuf = new Span<byte>(buf, cmdOff, buf.Length - cmdOff);

        int cspkPos = 0;
        int cmdPos = 0;

        // Staging arrays: values are decoded four at a time.
        var stagedDelQ = 4; var stagedAbsQ = 4;
        var stagedDelV = 4; var stagedAbsV = 4;
        Quaternion[] delQ = new Quaternion[4], absQ = new Quaternion[4];
        Vector3[] delV = new Vector3[4], absV = new Vector3[4];

        // Hydra-compatible: separate translation and rotation tracks per bone, each with an
        // independent timeline (no shared Times array, no value padding).
        // Key: boneIdx for translation, -(boneIdx+1) for rotation.
        var tracks = new Dictionary<int, BoneTrack>();

        BoneTrack GetOrCreateTrack(int boneIdx, bool isRotation)
        {
            int key = isRotation ? -(boneIdx + 1) : boneIdx;
            if (!tracks.TryGetValue(key, out var bt))
            {
                var crc = boneIdx < boneCRCs.Length ? boneCRCs[boneIdx] : Crc64.Compute($"bone{boneIdx}");
                bt = new BoneTrack($"0x{crc:X16}", crc, new(), new(), new());
                tracks[key] = bt;
            }
            return bt;
        }

        while (cmdPos + 4 <= cmdBuf.Length)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmdBuf[cmdPos..]);
            cmdPos += 4;

            float time = (cmd & 0xFFFF) * 1.525902e-05f * hdr.RangeTime;
            int boneIdx = (int)((cmd >> 16) & 0xFFF);

            if ((cmd & 0x40000000) == 0)
            {
                // ── TRANSLATION ──
                if ((int)cmd < 0)
                {
                    // Delta translation accumulates onto the previous sample.
                    if (stagedDelV > 3) { DecodeDeltaVec4(cspkBuf, ref cspkPos, hdr, delV); stagedDelV = 0; }
                    var bt = GetOrCreateTrack(boneIdx, false);
                    var val = delV[stagedDelV];
                    if (bt.Translations.Count > 0)
                    {
                        var prev = bt.Translations[^1];
                        val = new Vector3(prev.X + val.X, prev.Y + val.Y, prev.Z + val.Z);
                    }
                    bt.Times.Add(time);
                    bt.Translations.Add(val);
                    stagedDelV++;
                }
                else
                {
                    // Absolute translation.
                    if (stagedAbsV > 3) { DecodeAbsVec4(cspkBuf, ref cspkPos, hdr, absV); stagedAbsV = 0; }
                    var bt = GetOrCreateTrack(boneIdx, false);
                    bt.Times.Add(time);
                    bt.Translations.Add(absV[stagedAbsV]);
                    stagedAbsV++;
                }
            }
            else
            {
                // ── ROTATION ──
                if ((int)cmd < 0)
                {
                    // Delta rotation — Hydra: delQ[staged] = delQ[staged] * prev (NO normalization).
                    if (stagedDelQ > 3) { DecodeDeltaQuat4(cspkBuf, ref cspkPos, hdr, delQ); stagedDelQ = 0; }
                    var bt = GetOrCreateTrack(boneIdx, true);
                    delQ[stagedDelQ] = Quaternion.Multiply(delQ[stagedDelQ], bt.Rotations.Count > 0 ? bt.Rotations[^1] : Quaternion.Identity);
                    bt.Times.Add(time);
                    bt.Rotations.Add(delQ[stagedDelQ]);
                    stagedDelQ++;
                }
                else
                {
                    // Absolute rotation with per-command axis reordering.
                    if (stagedAbsQ > 3) { DecodeAbsQuat4(cspkBuf, ref cspkPos, absQ); stagedAbsQ = 0; }
                    int axisOrder = (int)((cmd >> 28) & 3);
                    var qt = SwizzleAxis(absQ[stagedAbsQ], axisOrder);
                    var bt = GetOrCreateTrack(boneIdx, true);
                    bt.Times.Add(time);
                    bt.Rotations.Add(qt);
                    stagedAbsQ++;
                }
            }
        }

        return tracks;
    }

    // ── Batch decoders (four values at a time) ──

    static void DecodeAbsVec4(Span<byte> buf, ref int pos, CompressedSkeletonPoseKeys2.Header hdr, Vector3[] out_)
    {
        for (int i = 0; i < 4; i++)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + i * 4)..]);
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + 16 + i * 4)..]);
            float x = (((hi & 0x3FF) << 10) | (lo & 0x3FF)) * hdr.RangeVector.X + hdr.MinVector.X;
            float y = ((((hi >> 10) & 0x7FF) << 11) | ((lo >> 10) & 0x7FF)) * hdr.RangeVector.Y + hdr.MinVector.Y;
            float z = (((hi >> 21) << 11) | (lo >> 21)) * hdr.RangeVector.Z + hdr.MinVector.Z;
            out_[i] = new Vector3(x, y, z);
        }
        pos += 32;
    }

    static void DecodeDeltaVec4(Span<byte> buf, ref int pos, CompressedSkeletonPoseKeys2.Header hdr, Vector3[] out_)
    {
        for (int i = 0; i < 4; i++)
        {
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + i * 4)..]);
            float x = (val & 0x3FF) * hdr.RangeDeltaV.X + hdr.MinDeltaV.X;
            float y = ((val >> 10) & 0x7FF) * hdr.RangeDeltaV.Y + hdr.MinDeltaV.Y;
            float z = (val >> 21) * hdr.RangeDeltaV.Z + hdr.MinDeltaV.Z;
            out_[i] = new Vector3(x, y, z);
        }
        pos += 16;
    }

    static void DecodeAbsQuat4(Span<byte> buf, ref int pos, Quaternion[] out_)
    {
        for (int i = 0; i < 4; i++)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + i * 4)..]);
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + 16 + i * 4)..]);
            float x = (((hi & 0x3FF) << 10) | (lo & 0x3FF)) * 1.3487e-06f - 0.7071068f;
            float y = ((((hi >> 10) & 0x7FF) << 11) | ((lo >> 10) & 0x7FF)) * 3.371749e-07f - 0.7071068f;
            float z = (((hi >> 21) << 11) | (lo >> 21)) * 3.371749e-07f - 0.7071068f;
            float w = 1f - x * x - y * y - z * z;
            out_[i] = Norm(new Quaternion(x, y, z, w > 0 ? MathF.Sqrt(w) : 0));
        }
        pos += 32;
    }

    static void DecodeDeltaQuat4(Span<byte> buf, ref int pos, CompressedSkeletonPoseKeys2.Header hdr, Quaternion[] out_)
    {
        for (int i = 0; i < 4; i++)
        {
            uint val = BinaryPrimitives.ReadUInt32LittleEndian(buf[(pos + i * 4)..]);
            float x = (val & 0x3FF) * hdr.RangeDeltaQ.X + hdr.MinDeltaQ.X;
            float y = ((val >> 10) & 0x7FF) * hdr.RangeDeltaQ.Y + hdr.MinDeltaQ.Y;
            float z = (val >> 21) * hdr.RangeDeltaQ.Z + hdr.MinDeltaQ.Z;
            float w = 1f - x * x - y * y - z * z;
            out_[i] = Norm(new Quaternion(x, y, z, w > 0 ? MathF.Sqrt(w) : 0));
        }
        pos += 16;
    }

    static Quaternion SwizzleAxis(Quaternion q, int axisOrder)
    {
        float[] c = { q.X, q.Y, q.Z, q.W };
        return Norm(new Quaternion(
            c[axisOrder],
            c[axisOrder ^ 1],
            c[axisOrder ^ 2],
            c[axisOrder ^ 3]));
    }

    // ── CSPK v1 decoder ──

    private static List<BoneTrack> DecodeV1(byte[] data, CompressedSkeletonPoseKeys.Header hdr)
    {
        var hdrSz = Marshal.SizeOf<CompressedSkeletonPoseKeys.Header>();
        var raw = data.AsSpan(hdrSz);
        var stride = hdr.BoneCount * 7 * 2;
        var total = hdr.SampleCount;
        var times = new List<float>(total);
        for (var s = 0; s < total; s++) times.Add(s * (1f / 30f));
        var tracks = new List<BoneTrack>();
        for (var b = 0; b < hdr.BoneCount; b++)
        {
            var tr = new List<Vector3>(total);
            var rt = new List<Quaternion>(total);
            for (var s = 0; s < total; s++)
            {
                var st = s * stride + b * 7 * 2;
                if (st + 14 > raw.Length) { tr.Add(Vector3.Zero); rt.Add(Quaternion.Identity); continue; }
                var sp = raw[st..];
                tr.Add(new Vector3(Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp), hdr.MinDeltaV.X, hdr.RangeDeltaV.X),
                                   Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[2..]), hdr.MinDeltaV.Y, hdr.RangeDeltaV.Y),
                                   Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[4..]), hdr.MinDeltaV.Z, hdr.RangeDeltaV.Z)));
                rt.Add(Norm(new Quaternion(Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[6..]), hdr.MinDeltaQ.X, hdr.RangeDeltaQ.X),
                                           Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[8..]), hdr.MinDeltaQ.Y, hdr.RangeDeltaQ.Y),
                                           Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[10..]), hdr.MinDeltaQ.Z, hdr.RangeDeltaQ.Z),
                                           Dq(BinaryPrimitives.ReadUInt16LittleEndian(sp[12..]), hdr.MinDeltaQ.W, hdr.RangeDeltaQ.W))));
            }
            tracks.Add(new BoneTrack($"bone_{b}", Crc64.Compute($"bone{b}"), times, tr, rt));
        }
        return tracks;
    }

    private static float Dq(ushort raw, float min, float range) => min + raw / 65535f * range;
    private static Quaternion Norm(Quaternion q) { var l = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W); return l > 1e-8f ? new Quaternion(q.X / l, q.Y / l, q.Z / l, q.W / l) : Quaternion.Identity; }
}
