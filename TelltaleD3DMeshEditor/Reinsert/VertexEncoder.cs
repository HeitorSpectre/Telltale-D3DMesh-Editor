using System.Buffers.Binary;
using System.Numerics;

namespace TelltaleD3DMeshEditor.Reinsert;

// Encodes a V13 vertex in the exact binary layout of the template. Writes attributes in parser order
// (position, uv1, uv2, uv3, uv4, bones, weights, colors, unknown1, normals, binormals, tangents),
// each only when the template format is != 0, using that exact format. Attributes that do not come
// from the GLB (unknown1, binormals, tangents) are preserved when available.
// UV scales (mults) come from the uvScales block, recalculated by the reimporter for the model's
// real range to avoid int16 overflow.
public sealed class VertexEncoder
{
    private readonly VertexAttrLayout _attrs;
    private readonly UvMults _mults;

    public VertexEncoder(VertexAttrLayout attrs, UvMults mults)
    {
        _attrs = attrs;
        _mults = mults;
    }

    public int Stride => _attrs.Stride;

    public void Write(Span<byte> dst, in EncVertex v)
    {
        if (dst.Length < Stride)
        {
            throw new ArgumentException($"Vertex buffer is too small: {dst.Length} < {Stride}");
        }

        var p = 0;
        WritePosition(dst, ref p, v);
        WriteUv(dst, ref p, _attrs.Uv1.Format, v.U0, v.V0, _mults.Uv1X, _mults.Uv1Y);
        WriteUv(dst, ref p, _attrs.Uv2.Format, v.U1, v.V1, _mults.Uv2X, _mults.Uv2Y);
        WriteUv(dst, ref p, _attrs.Uv3.Format, v.U2, v.V2, _mults.Uv3X, _mults.Uv3Y);
        WriteUv(dst, ref p, _attrs.Uv4.Format, v.U3, v.V3, _mults.Uv4X, _mults.Uv4Y);
        WriteBones(dst, ref p, _attrs.Bones.Format, v);
        WriteWeights(dst, ref p, _attrs.Weights.Format, v);
        WriteColor(dst, ref p, _attrs.Colors.Format, v);
        WriteUnknown(dst, ref p, _attrs.Unknown1.Format, v.Unknown1);
        WriteNormal(dst, ref p, _attrs.Normals.Format, v.Nx, v.Ny, v.Nz);
        WriteVector4(dst, ref p, _attrs.Binormals.Format, v.Bx, v.By, v.Bz, v.Bw);
        WriteVector4(dst, ref p, _attrs.Tangents.Format, v.Tx, v.Ty, v.Tz, v.Tw);
    }

    private void WritePosition(Span<byte> dst, ref int p, in EncVertex v)
    {
        if (_attrs.Position.Format == 0)
        {
            return;
        }

        BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.X); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.Y); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.Z); p += 4;
    }

    // Inverse of D3DMeshParser.ReadUv. The exporter writes gltf_v = 1 - telltale_v; on reimport,
    // the stored normalized value = gltf_v (and gltf_u for U). So: int16 = round(coord / mult * max).
    private static void WriteUv(Span<byte> dst, ref int p, uint format, float u, float v, float multX, float multY)
    {
        switch (format)
        {
            case 0:
                return;
            case 1:
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], u); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], 1f - v); p += 4;
                return;
            case 4:
                WriteScaledInt16(dst, ref p, u, multX);
                WriteScaledInt16(dst, ref p, v, multY);
                return;
            case 5:
                WriteScaledUInt16(dst, ref p, u, multX);
                WriteScaledUInt16(dst, ref p, v, multY);
                return;
            case 11:
                BinaryPrimitives.WriteHalfLittleEndian(dst[p..], (Half)(u / 2f)); p += 2;
                BinaryPrimitives.WriteHalfLittleEndian(dst[p..], (Half)(v / 2f)); p += 2;
                return;
            default:
                throw new InvalidDataException($"Unsupported UV format for writing: {format}");
        }
    }

    private static void WriteScaledInt16(Span<byte> dst, ref int p, float value, float mult)
    {
        var scaled = mult > 1e-9f ? value / mult : value;
        var i = (int)Math.Round(scaled * 32767.0, MidpointRounding.AwayFromZero);
        BinaryPrimitives.WriteInt16LittleEndian(dst[p..], (short)Math.Clamp(i, short.MinValue, short.MaxValue));
        p += 2;
    }

    private static void WriteScaledUInt16(Span<byte> dst, ref int p, float value, float mult)
    {
        var scaled = mult > 1e-9f ? value / mult : value;
        var i = (int)Math.Round(scaled * 65535.0, MidpointRounding.AwayFromZero);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[p..], (ushort)Math.Clamp(i, 0, 65535));
        p += 2;
    }

    private static void WriteNormal(Span<byte> dst, ref int p, uint format, float x, float y, float z)
    {
        switch (format)
        {
            case 0:
                return;
            case 2:
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(x * 127f), -127, 127);
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(y * 127f), -127, 127);
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(z * 127f), -127, 127);
                dst[p++] = 0;
                return;
            case 4:
                WriteNormInt16(dst, ref p, x);
                WriteNormInt16(dst, ref p, y);
                WriteNormInt16(dst, ref p, z);
                WriteNormInt16(dst, ref p, 0f);
                return;
            default:
                throw new InvalidDataException($"Unsupported normal format for writing: {format}");
        }
    }

    private static void WriteVector4(Span<byte> dst, ref int p, uint format, float x, float y, float z, float w)
    {
        switch (format)
        {
            case 0:
                return;
            case 2:
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(x * 127f), -127, 127);
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(y * 127f), -127, 127);
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(z * 127f), -127, 127);
                dst[p++] = (byte)(sbyte)Math.Clamp((int)Math.Round(w * 127f), -127, 127);
                return;
            case 4:
                WriteNormInt16(dst, ref p, x);
                WriteNormInt16(dst, ref p, y);
                WriteNormInt16(dst, ref p, z);
                WriteNormInt16(dst, ref p, w);
                return;
            default:
                throw new InvalidDataException($"Unsupported vector format for writing: {format}");
        }
    }

    private static void WriteNormInt16(Span<byte> dst, ref int p, float value)
    {
        var i = (int)Math.Round(value * 32767.0, MidpointRounding.AwayFromZero);
        BinaryPrimitives.WriteInt16LittleEndian(dst[p..], (short)Math.Clamp(i, short.MinValue, short.MaxValue));
        p += 2;
    }

    private static void WriteBones(Span<byte> dst, ref int p, uint format, in EncVertex v)
    {
        switch (format)
        {
            case 0:
                return;
            case 3:
            case 8:
                dst[p++] = (byte)Math.Clamp(v.Bone0, 0, 255);
                dst[p++] = (byte)Math.Clamp(v.Bone1, 0, 255);
                dst[p++] = (byte)Math.Clamp(v.Bone2, 0, 255);
                dst[p++] = (byte)Math.Clamp(v.Bone3, 0, 255);
                return;
            default:
                throw new InvalidDataException($"Unsupported bone format for writing: {format}");
        }
    }

    private static void WriteWeights(Span<byte> dst, ref int p, uint format, in EncVertex v)
    {
        switch (format)
        {
            case 0:
                return;
            case 1:
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.W0); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.W1); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.W2); p += 4;
                return;
            case 4:
            case 5:
                WriteWeightUInt16(dst, ref p, v.W0);
                WriteWeightUInt16(dst, ref p, v.W1);
                WriteWeightUInt16(dst, ref p, v.W2);
                WriteWeightUInt16(dst, ref p, v.W3);
                return;
            default:
                throw new InvalidDataException($"Unsupported weight format for writing: {format}");
        }
    }

    private static void WriteWeightUInt16(Span<byte> dst, ref int p, float value)
    {
        var i = (int)Math.Round(Math.Clamp(value, 0f, 1f) * 65535.0, MidpointRounding.AwayFromZero);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[p..], (ushort)Math.Clamp(i, 0, 65535));
        p += 2;
    }

    private static void WriteColor(Span<byte> dst, ref int p, uint format, in EncVertex v)
    {
        switch (format)
        {
            case 0:
                return;
            case 1:
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.ColR); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.ColG); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.ColB); p += 4;
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], v.ColA); p += 4;
                return;
            case 3:
                dst[p++] = (byte)Math.Clamp((int)Math.Round(v.ColR * 255f), 0, 255);
                dst[p++] = (byte)Math.Clamp((int)Math.Round(v.ColG * 255f), 0, 255);
                dst[p++] = (byte)Math.Clamp((int)Math.Round(v.ColB * 255f), 0, 255);
                dst[p++] = (byte)Math.Clamp((int)Math.Round(v.ColA * 255f), 0, 255);
                return;
            default:
                throw new InvalidDataException($"Unsupported color format for writing: {format}");
        }
    }

    private static void WriteUnknown(Span<byte> dst, ref int p, uint format, float value)
    {
        switch (format)
        {
            case 0:
                return;
            case 1:
                BinaryPrimitives.WriteSingleLittleEndian(dst[p..], value);
                p += 4;
                return;
            default:
                throw new InvalidDataException($"Unsupported unknown attribute format for writing: {format}");
        }
    }
}

// Per-channel UV scales (multipliers) read/recalculated from the uvScales block.
public readonly record struct UvMults(
    float Uv1X, float Uv1Y,
    float Uv2X, float Uv2Y,
    float Uv3X, float Uv3Y,
    float Uv4X, float Uv4Y);

// Neutral vertex ready to encode. Position/normal/UVs come from the GLB; tangent is calculated;
// bones/weights/color are optional (static props usually use format 0 for these).
public struct EncVertex
{
    public float X, Y, Z;
    public float Nx, Ny, Nz;
    public float Tx, Ty, Tz, Tw;
    public float Bx, By, Bz, Bw;
    public float Unknown1;
    public float U0, V0, U1, V1, U2, V2, U3, V3;
    public int Bone0, Bone1, Bone2, Bone3;
    public float W0, W1, W2, W3;
    public float ColR, ColG, ColB, ColA;
}
