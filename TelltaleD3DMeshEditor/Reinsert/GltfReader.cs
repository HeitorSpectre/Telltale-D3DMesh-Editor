using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Minimal glTF 2.0 reader for the reimporter: opens a binary GLB or a .gltf (+ .bin / data URIs)
// and returns each primitive's geometry (positions, normals, UVs 0-3, indices) plus referenced
// material textures. Supports the accessors produced by Blender and by this tool's own exporter.
// V convention: glTF samples V from the top-left, while Telltale samples in S-row space. The
// reimporter flips (1 - v) while writing, so this reader returns raw glTF V values.
public sealed class GltfModel
{
    public required List<GltfPrimitive> Primitives { get; init; }
    public IReadOnlyList<GltfJoint> Joints { get; init; } = [];

    // The full skeleton read from the first skin (joint names, hierarchy and local pose), used to
    // rebuild a .skl on reimport. Null when the glTF has no skin.
    public SkeletonData? Skeleton { get; init; }
}

public sealed class GltfPrimitive
{
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector2[]? Uv0 { get; init; }
    public Vector2[]? Uv1 { get; init; }
    public Vector2[]? Uv2 { get; init; }
    public Vector2[]? Uv3 { get; init; }
    public Vector4[]? Color0 { get; init; }
    public Vector4[]? Tangents { get; init; }
    public Vector4[]? Binormals { get; init; }
    public float[]? Unknown1 { get; init; }
    public ushort[]? Joints0 { get; init; } // 4 per vertex (flattened)
    public Vector4[]? Weights0 { get; init; }
    public required int[] Indices { get; init; }
    public string? MaterialName { get; init; }
    public int? BonePaletteIndex { get; init; }
    public string? SourceMeshPath { get; init; }
    public int? SourceSubmeshIndex { get; init; }

    // The original detail/line texture name recovered from the adjacent line-overlay primitive's
    // material name (e.g. "base__tt_lines_<lineName>"). Blender strips the material/primitive extras
    // that normally carry the line slot, so without this the detail_diffuse slot would be cleared on
    // reimport and the character outlines would vanish. Set during parsing, consumed by the reinsert
    // texture service to keep the template's original line binding.
    public string? RecoveredDetailLineTextureName { get; set; }
    public GltfImage? RecoveredDetailLineImage { get; set; }
    public bool IsSkinned { get; init; }
    public GltfImage? BaseColor { get; init; }
    public IReadOnlyDictionary<string, GltfImage> TextureSlots { get; init; } =
        new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, GltfImage> ReferencedTextures { get; init; } =
        new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);

    public int VertexCount => Positions.Length;
}

public sealed class GltfJoint
{
    public string? Name { get; init; }
    public ulong? Hash { get; init; }
}

public sealed class GltfImage
{
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    public string MimeType { get; init; } = "image/png";
}

public static class GltfReader
{
    public static GltfModel Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67))
        {
            return LoadGlb(bytes);
        }

        var json = JsonDocument.Parse(bytes);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Parse(json.RootElement, glbBin: null, baseDir);
    }

    private static GltfModel LoadGlb(byte[] bytes)
    {
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67)
        {
            throw new InvalidDataException("Invalid GLB: missing 'glTF' magic.");
        }

        var totalLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        byte[]? jsonChunk = null;
        byte[]? binChunk = null;
        var pos = 12;
        while (pos + 8 <= totalLength && pos + 8 <= bytes.Length)
        {
            var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            var dataStart = pos + 8;
            if (dataStart + chunkLength > bytes.Length)
            {
                break;
            }

            var chunk = bytes.AsSpan(dataStart, chunkLength).ToArray();
            if (chunkType == 0x4E4F534A)
            {
                jsonChunk = chunk;
            }
            else if (chunkType == 0x004E4942)
            {
                binChunk = chunk;
            }

            pos = dataStart + chunkLength;
        }

        if (jsonChunk is null)
        {
            throw new InvalidDataException("GLB sem chunk JSON.");
        }

        using var doc = JsonDocument.Parse(jsonChunk);
        return Parse(doc.RootElement, binChunk, baseDir: ".");
    }

    private static GltfModel Parse(JsonElement root, byte[]? glbBin, string baseDir)
    {
        var buffers = LoadBuffers(root, glbBin, baseDir);
        var bufferViews = root.TryGetProperty("bufferViews", out var bv) ? bv : default;
        var accessors = root.TryGetProperty("accessors", out var ac) ? ac : default;
        var materials = root.TryGetProperty("materials", out var mt) ? mt : default;
        var textures = root.TryGetProperty("textures", out var tx) ? tx : default;
        var images = root.TryGetProperty("images", out var im) ? im : default;
        var skins = root.TryGetProperty("skins", out var sk) ? sk : default;

        var ctx = new ParseContext(buffers, bufferViews, accessors, materials, textures, images, skins, baseDir);

        var primitives = new List<GltfPrimitive>();
        var joints = ReadFirstSkinJoints(root);
        if (root.TryGetProperty("nodes", out var nodes) &&
            nodes.ValueKind == JsonValueKind.Array &&
            root.TryGetProperty("meshes", out var meshes) &&
            meshes.ValueKind == JsonValueKind.Array)
        {
            var rootNodes = GetSceneRootNodes(root, nodes).ToArray();
            var nodeWorlds = BuildNodeWorldTransforms(nodes, rootNodes);
            foreach (var rootNode in rootNodes)
            {
                ReadNodePrimitives(rootNode, Matrix4x4.Identity, nodes, meshes, ctx, primitives, nodeWorlds);
            }
            AutoUprightSkinnedModel(primitives);
        }
        else if (root.TryGetProperty("meshes", out meshes))
        {
            foreach (var mesh in meshes.EnumerateArray())
            {
                if (!mesh.TryGetProperty("primitives", out var prims))
                {
                    continue;
                }

                foreach (var prim in prims.EnumerateArray())
                {
                    if (IsGeneratedLineOverlayPrimitive(prim, ctx))
                    {
                        AttachRecoveredLineOverlay(prim, ctx, primitives);
                        continue;
                    }

                    primitives.Add(ReadPrimitive(prim, ctx, Matrix4x4.Identity));
                }
            }
        }

        return new GltfModel { Primitives = primitives, Joints = joints, Skeleton = ReadSkeletonData(root) };
    }

    // Reads the first skin's joints into a full SkeletonData: name, hash, parent and local transform
    // for every bone. The exporter stores exact float bits in node extras, so an unedited skeleton
    // reads back to the original values (and rebuilds byte-identically); Blender edits come through as
    // the node's translation/rotation.
    private static SkeletonData? ReadSkeletonData(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("skins", out var skins) || skins.ValueKind != JsonValueKind.Array ||
            skins.GetArrayLength() == 0)
        {
            return null;
        }

        var skin = skins[0];
        if (!skin.TryGetProperty("joints", out var jointNodes) || jointNodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var jointNodeIndices = jointNodes.EnumerateArray().Select(j => j.GetInt32()).ToList();
        if (jointNodeIndices.Count == 0)
        {
            return null;
        }

        var jointIndexByNode = new Dictionary<int, int>();
        for (var i = 0; i < jointNodeIndices.Count; i++)
        {
            jointIndexByNode[jointNodeIndices[i]] = i;
        }

        if (!UsesExactLocalTransformBits(nodes, jointNodeIndices))
        {
            return ReadBakedSkeletonData(root, nodes, jointNodeIndices);
        }

        var parentNodeByNode = BuildParentNodeMap(nodes);

        var skeleton = new SkeletonData();
        foreach (var nodeIndex in jointNodeIndices)
        {
            var node = nodes[nodeIndex];
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var hash = ReadNodeHash(node) ?? (string.IsNullOrEmpty(name) ? 0UL : Crc64Ecma.Compute(name));

            var (x, y, z) = ReadNodeTranslation(node);
            var (qx, qy, qz, qw) = ReadNodeRotation(node);

            var parentIndex = -1;
            ulong parentHash = 0;
            if (parentNodeByNode.TryGetValue(nodeIndex, out var parentNode) &&
                jointIndexByNode.TryGetValue(parentNode, out var resolvedParentIndex))
            {
                parentIndex = resolvedParentIndex;
                var parentNodeElem = nodes[parentNode];
                parentHash = ReadNodeHash(parentNodeElem)
                    ?? (parentNodeElem.TryGetProperty("name", out var pn) && pn.GetString() is { Length: > 0 } pname
                        ? Crc64Ecma.Compute(pname)
                        : 0);
            }

            skeleton.Bones.Add(new BoneData(name, hash, parentIndex, x, y, z, qx, qy, qz, qw, parentHash));
        }

        return skeleton;
    }

    private static bool UsesExactLocalTransformBits(JsonElement nodes, IReadOnlyList<int> jointNodeIndices)
    {
        foreach (var nodeIndex in jointNodeIndices)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
            {
                return false;
            }

            var node = nodes[nodeIndex];
            if (!TryReadFloatBits(node, "translationBits", 3, out _) ||
                !TryReadFloatBits(node, "rotationBits", 4, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static SkeletonData ReadBakedSkeletonData(JsonElement root, JsonElement nodes, IReadOnlyList<int> jointNodeIndices)
    {
        var jointNodeSet = jointNodeIndices.ToHashSet();
        var parentNodeByNode = BuildParentNodeMap(nodes);
        var orderedJointNodes = BuildJointNodeOrder(nodes, jointNodeIndices, parentNodeByNode, jointNodeSet);
        var jointIndexByNode = orderedJointNodes
            .Select((node, index) => (node, index))
            .ToDictionary(item => item.node, item => item.index);
        var rootNodes = GetSceneRootNodes(root, nodes).ToArray();
        var nodeWorlds = BuildNodeWorldTransforms(nodes, rootNodes);
        var bakedWorlds = new Matrix4x4[orderedJointNodes.Count];

        var skeleton = new SkeletonData();
        for (var i = 0; i < orderedJointNodes.Count; i++)
        {
            var nodeIndex = orderedJointNodes[i];
            var node = nodes[nodeIndex];
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var hash = ReadNodeHash(node) ?? (string.IsNullOrEmpty(name) ? 0UL : Crc64Ecma.Compute(name));

            var world = nodeWorlds.TryGetValue(nodeIndex, out var foundWorld)
                ? RemoveScale(foundWorld)
                : RemoveScale(ReadNodeLocalTransform(node));

            var parentIndex = -1;
            ulong parentHash = 0;
            if (parentNodeByNode.TryGetValue(nodeIndex, out var parentNode) &&
                jointIndexByNode.TryGetValue(parentNode, out var resolvedParentIndex))
            {
                parentIndex = resolvedParentIndex;
                var parentNodeElem = nodes[parentNode];
                parentHash = ReadNodeHash(parentNodeElem)
                    ?? (parentNodeElem.TryGetProperty("name", out var pn) && pn.GetString() is { Length: > 0 } pname
                        ? Crc64Ecma.Compute(pname)
                        : 0);
            }

            Matrix4x4 local;
            if (parentIndex >= 0 && Matrix4x4.Invert(bakedWorlds[parentIndex], out var inverseParent))
            {
                local = world * inverseParent;
            }
            else
            {
                local = world;
            }

            if (!Matrix4x4.Decompose(local, out _, out var rotation, out var translation))
            {
                rotation = Quaternion.Identity;
                translation = Vector3.Zero;
            }

            translation *= GetInheritedScale(nodes, parentNodeByNode, nodeIndex);
            rotation = rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
            bakedWorlds[i] = world;
            skeleton.Bones.Add(new BoneData(
                name,
                hash,
                parentIndex,
                translation.X,
                translation.Y,
                translation.Z,
                rotation.X,
                rotation.Y,
                rotation.Z,
                rotation.W,
                parentHash));
        }

        return skeleton;
    }

    private static Vector3 GetInheritedScale(JsonElement nodes, IReadOnlyDictionary<int, int> parentNodeByNode, int nodeIndex)
    {
        var scale = Vector3.One;
        var seen = new HashSet<int>();
        var current = nodeIndex;
        while (parentNodeByNode.TryGetValue(current, out var parentNode) &&
               parentNode >= 0 &&
               parentNode < nodes.GetArrayLength() &&
               seen.Add(parentNode))
        {
            scale *= ReadVector3(nodes[parentNode], "scale", Vector3.One);
            current = parentNode;
        }

        return scale;
    }

    private static List<int> BuildJointNodeOrder(
        JsonElement nodes,
        IReadOnlyList<int> jointNodeIndices,
        IReadOnlyDictionary<int, int> parentNodeByNode,
        IReadOnlySet<int> jointNodeSet)
    {
        var childrenByJoint = new Dictionary<int, List<int>>();
        foreach (var nodeIndex in jointNodeIndices)
        {
            if (parentNodeByNode.TryGetValue(nodeIndex, out var parentNode) &&
                jointNodeSet.Contains(parentNode))
            {
                if (!childrenByJoint.TryGetValue(parentNode, out var children))
                {
                    children = [];
                    childrenByJoint[parentNode] = children;
                }

                children.Add(nodeIndex);
            }
        }

        var result = new List<int>(jointNodeIndices.Count);
        var visited = new HashSet<int>();
        foreach (var nodeIndex in jointNodeIndices)
        {
            if (!parentNodeByNode.TryGetValue(nodeIndex, out var parentNode) ||
                !jointNodeSet.Contains(parentNode))
            {
                AddJointDepthFirst(nodeIndex, childrenByJoint, visited, result);
            }
        }

        foreach (var nodeIndex in jointNodeIndices)
        {
            AddJointDepthFirst(nodeIndex, childrenByJoint, visited, result);
        }

        return result;
    }

    private static void AddJointDepthFirst(
        int nodeIndex,
        IReadOnlyDictionary<int, List<int>> childrenByJoint,
        HashSet<int> visited,
        List<int> result)
    {
        if (!visited.Add(nodeIndex))
        {
            return;
        }

        result.Add(nodeIndex);
        if (!childrenByJoint.TryGetValue(nodeIndex, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            AddJointDepthFirst(child, childrenByJoint, visited, result);
        }
    }

    private static Matrix4x4 RemoveScale(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out _, out var rotation, out var translation))
        {
            return matrix;
        }

        rotation = rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static Dictionary<int, int> BuildParentNodeMap(JsonElement nodes)
    {
        var parentByChild = new Dictionary<int, int>();
        for (var i = 0; i < nodes.GetArrayLength(); i++)
        {
            if (nodes[i].TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    parentByChild[child.GetInt32()] = i;
                }
            }
        }

        return parentByChild;
    }

    private static (float X, float Y, float Z) ReadNodeTranslation(JsonElement node)
    {
        if (TryReadFloatBits(node, "translationBits", 3, out var bits))
        {
            return (bits[0], bits[1], bits[2]);
        }

        if (node.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 3)
        {
            return (t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle());
        }

        return (0f, 0f, 0f);
    }

    private static (float X, float Y, float Z, float W) ReadNodeRotation(JsonElement node)
    {
        if (TryReadFloatBits(node, "rotationBits", 4, out var bits))
        {
            return (bits[0], bits[1], bits[2], bits[3]);
        }

        if (node.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 4)
        {
            return (r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
        }

        return (0f, 0f, 0f, 1f);
    }

    private static bool TryReadFloatBits(JsonElement node, string key, int count, out float[] values)
    {
        values = [];
        if (!node.TryGetProperty("extras", out var extras) ||
            !extras.TryGetProperty(key, out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() != count)
        {
            return false;
        }

        values = new float[count];
        for (var i = 0; i < count; i++)
        {
            // The exporter stores bits as hex strings, e.g. "0x3F800000".
            var text = arr[i].GetString();
            if (string.IsNullOrEmpty(text))
            {
                values = [];
                return false;
            }

            text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawBits))
            {
                values = [];
                return false;
            }

            values[i] = BitConverter.UInt32BitsToSingle(rawBits);
        }

        return true;
    }

    private static IEnumerable<int> GetSceneRootNodes(JsonElement root, JsonElement nodes)
    {
        if (root.TryGetProperty("scene", out var sceneIndexElem) &&
            root.TryGetProperty("scenes", out var scenes) &&
            scenes.ValueKind == JsonValueKind.Array)
        {
            var sceneIndex = sceneIndexElem.GetInt32();
            if (sceneIndex >= 0 &&
                sceneIndex < scenes.GetArrayLength() &&
                scenes[sceneIndex].TryGetProperty("nodes", out var sceneNodes) &&
                sceneNodes.ValueKind == JsonValueKind.Array)
            {
                return sceneNodes.EnumerateArray().Select(node => node.GetInt32()).ToArray();
            }
        }

        var childNodes = new HashSet<int>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("children", out var children) ||
                children.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var child in children.EnumerateArray())
            {
                childNodes.Add(child.GetInt32());
            }
        }

        return Enumerable.Range(0, nodes.GetArrayLength())
            .Where(index => !childNodes.Contains(index))
            .ToArray();
    }

    private static void ReadNodePrimitives(
        int nodeIndex,
        Matrix4x4 parentTransform,
        JsonElement nodes,
        JsonElement meshes,
        ParseContext ctx,
        List<GltfPrimitive> primitives,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorlds)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
        {
            return;
        }

        var node = nodes[nodeIndex];
        var worldTransform = ReadNodeLocalTransform(node) * parentTransform;
        var hasSkin = node.TryGetProperty("skin", out _);
        if (node.TryGetProperty("mesh", out var meshElem))
        {
            var meshIndex = meshElem.GetInt32();
            if (meshIndex >= 0 &&
                meshIndex < meshes.GetArrayLength() &&
                meshes[meshIndex].TryGetProperty("primitives", out var prims))
            {
                foreach (var prim in prims.EnumerateArray())
                {
                    if (IsGeneratedLineOverlayPrimitive(prim, ctx))
                    {
                        AttachRecoveredLineOverlay(prim, ctx, primitives);
                        continue;
                    }

                    primitives.Add(ReadPrimitive(prim, ctx, worldTransform, isSkinned: hasSkin));
                }
            }
        }

        if (node.TryGetProperty("children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ReadNodePrimitives(child.GetInt32(), worldTransform, nodes, meshes, ctx, primitives, nodeWorlds);
            }
        }
    }

    private static bool IsGeneratedLineOverlayPrimitive(JsonElement prim, ParseContext ctx)
    {
        if (prim.TryGetProperty("extras", out var extras) &&
            extras.TryGetProperty("telltaleLineOverlayFor", out _))
        {
            return true;
        }

        if (!TryGetMaterial(prim, ctx, out var material))
        {
            return false;
        }

        if (material.TryGetProperty("extras", out var materialExtras) &&
            materialExtras.TryGetProperty("telltaleLineOverlay", out _))
        {
            return true;
        }

        if (material.TryGetProperty("name", out var nameElem) &&
            IsGeneratedLineOverlayName(nameElem.GetString()))
        {
            return true;
        }

        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
            pbr.TryGetProperty("baseColorTexture", out var textureInfo) &&
            TryResolveTextureName(textureInfo, ctx, out var textureName) &&
            IsGeneratedLineOverlayName(textureName))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetMaterial(JsonElement prim, ParseContext ctx, out JsonElement material)
    {
        material = default;
        if (!prim.TryGetProperty("material", out var matElem) ||
            ctx.Materials.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var matIndex = matElem.GetInt32();
        if (matIndex < 0 || matIndex >= ctx.Materials.GetArrayLength())
        {
            return false;
        }

        material = ctx.Materials[matIndex];
        return true;
    }

    private static bool TryResolveTextureName(JsonElement textureInfo, ParseContext ctx, out string name)
    {
        name = "";
        if (!textureInfo.TryGetProperty("index", out var texIndexElem) ||
            ctx.Textures.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var textureIndex = texIndexElem.GetInt32();
        if (textureIndex < 0 || textureIndex >= ctx.Textures.GetArrayLength())
        {
            return false;
        }

        var texture = ctx.Textures[textureIndex];
        if (!texture.TryGetProperty("source", out var sourceElem) ||
            ctx.Images.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var imageIndex = sourceElem.GetInt32();
        if (imageIndex < 0 || imageIndex >= ctx.Images.GetArrayLength())
        {
            return false;
        }

        var image = ctx.Images[imageIndex];
        if (image.TryGetProperty("name", out var imageName) &&
            !string.IsNullOrWhiteSpace(imageName.GetString()))
        {
            name = imageName.GetString()!;
            return true;
        }

        if (image.TryGetProperty("uri", out var uriElem) &&
            !string.IsNullOrWhiteSpace(uriElem.GetString()))
        {
            name = Path.GetFileNameWithoutExtension(uriElem.GetString()!);
            return true;
        }

        return false;
    }

    private static bool IsGeneratedLineOverlayName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               (name.Contains("__tt_lines_", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("__tt_lines_overlay", StringComparison.OrdinalIgnoreCase));
    }

    // A line-overlay primitive carries the original detail/line texture name only in its material
    // name ("<baseMaterial>__tt_lines_<lineTexture>") once Blender has stripped the extras. Recover
    // that name and attach it to the base primitive it shadows, so the reinserter can keep the line
    // binding instead of clearing the slot. The overlay is emitted right after its base and shares the
    // base index count, so we match the most recent base primitive with an equal index count.
    private static void AttachRecoveredLineOverlay(JsonElement prim, ParseContext ctx, List<GltfPrimitive> primitives)
    {
        if (primitives.Count == 0 ||
            !TryGetMaterial(prim, ctx, out var material) ||
            !material.TryGetProperty("name", out var nameElem) ||
            ExtractLineTextureNameFromOverlayMaterial(nameElem.GetString()) is not { } lineTextureName)
        {
            return;
        }

        var overlayIndexCount = prim.TryGetProperty("indices", out var indicesElem)
            ? ctx.AccessorCount(indicesElem.GetInt32())
            : -1;

        for (var i = primitives.Count - 1; i >= 0; i--)
        {
            var candidate = primitives[i];
            if (candidate.RecoveredDetailLineTextureName is not null)
            {
                continue;
            }

            if (overlayIndexCount < 0 || candidate.Indices.Length == overlayIndexCount)
            {
                candidate.RecoveredDetailLineTextureName = lineTextureName;
                var overlayTextures = ResolveMaterialTextures(material, ctx, includePbrExtras: false);
                if (overlayTextures.TryGetValue("diffuse", out var overlayImage))
                {
                    candidate.RecoveredDetailLineImage = new GltfImage
                    {
                        Name = lineTextureName,
                        Data = overlayImage.Data,
                        MimeType = overlayImage.MimeType,
                    };
                }
                return;
            }
        }
    }

    private static string? ExtractLineTextureNameFromOverlayMaterial(string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return null;
        }

        const string separator = "__tt_lines_";
        var index = materialName.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var lineName = materialName[(index + separator.Length)..];
        // Drop a trailing "__tt_lines_overlay" tag and any Blender ".001" duplicate suffix.
        var overlayTag = lineName.IndexOf("__tt_lines_overlay", StringComparison.OrdinalIgnoreCase);
        if (overlayTag >= 0)
        {
            lineName = lineName[..overlayTag];
        }

        lineName = NormalizeImageName(lineName);
        return string.IsNullOrWhiteSpace(lineName) ? null : lineName;
    }

    private static void AutoUprightSkinnedModel(IReadOnlyList<GltfPrimitive> primitives)
    {
        if (primitives.Count == 0 || primitives.Any(primitive => !primitive.IsSkinned))
        {
            return;
        }

        if (primitives.Any(HasTelltaleSourceMetadata))
        {
            return;
        }

        var positions = primitives.SelectMany(primitive => primitive.Positions).ToArray();
        if (positions.Length == 0)
        {
            return;
        }

        var xExtent = positions.Max(v => v.X) - positions.Min(v => v.X);
        var yExtent = positions.Max(v => v.Y) - positions.Min(v => v.Y);
        var zExtent = positions.Max(v => v.Z) - positions.Min(v => v.Z);
        var minY = positions.Min(v => v.Y);
        var maxY = positions.Max(v => v.Y);
        var minZ = positions.Min(v => v.Z);
        var maxZ = positions.Max(v => v.Z);

        // Some downloaded skinned models are already Y-up, but their bind mesh sits upside down
        // below the origin. In game that becomes a character entering head-first. Rotate 180
        // degrees around X only when Y is clearly the height axis and the whole model is inverted.
        if (yExtent >= xExtent * 0.8f &&
            yExtent >= zExtent * 1.4f &&
            maxY <= yExtent * 0.1f &&
            minY < -yExtent * 0.6f)
        {
            foreach (var primitive in primitives)
            {
                RotateX180(primitive.Positions);
                if (primitive.Normals is not null)
                {
                    RotateX180(primitive.Normals, normalize: true);
                }
                RotateX180(primitive.Tangents, normalize: true);
                RotateX180(primitive.Binormals, normalize: true);
            }

            return;
        }

        // Many external skinned character GLTFs keep the mesh in Z-up bind space. Once reimported
        // as a static Telltale prop, that appears lying down. Only rotate when Y is clearly not the
        // vertical axis and Z is a plausible height axis.
        if (zExtent <= yExtent * 1.4f || zExtent < xExtent * 0.6f)
        {
            return;
        }

        foreach (var primitive in primitives)
        {
            var positiveZIsUp = maxZ >= -minZ;
            RotateZUpToYUp(primitive.Positions, positiveZIsUp);
            if (primitive.Normals is not null)
            {
                RotateZUpToYUp(primitive.Normals, positiveZIsUp, normalize: true);
            }
            RotateZUpToYUp(primitive.Tangents, positiveZIsUp, normalize: true);
            RotateZUpToYUp(primitive.Binormals, positiveZIsUp, normalize: true);
        }
    }

    private static bool HasTelltaleSourceMetadata(GltfPrimitive primitive)
        => primitive.BonePaletteIndex is not null ||
           primitive.SourceSubmeshIndex is not null ||
           !string.IsNullOrWhiteSpace(primitive.SourceMeshPath);

    private static bool HasTelltaleSourceMetadata((int? BonePaletteIndex, string? SourceMeshPath, int? SourceSubmeshIndex) source)
        => source.BonePaletteIndex is not null ||
           source.SourceSubmeshIndex is not null ||
           !string.IsNullOrWhiteSpace(source.SourceMeshPath);

    private static void RotateZUpToYUp(Vector3[] vectors, bool positiveZIsUp, bool normalize = false)
    {
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            var rotated = positiveZIsUp
                ? new Vector3(v.X, v.Z, -v.Y)
                : new Vector3(v.X, -v.Z, v.Y);
            vectors[i] = normalize && rotated.LengthSquared() > 0.00000001f
                ? Vector3.Normalize(rotated)
                : rotated;
        }
    }

    private static void RotateZUpToYUp(Vector4[]? vectors, bool positiveZIsUp, bool normalize = false)
    {
        if (vectors is null)
        {
            return;
        }

        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            var rotated = positiveZIsUp
                ? new Vector3(v.X, v.Z, -v.Y)
                : new Vector3(v.X, -v.Z, v.Y);
            rotated = normalize && rotated.LengthSquared() > 0.00000001f
                ? Vector3.Normalize(rotated)
                : rotated;
            vectors[i] = new Vector4(rotated, v.W);
        }
    }

    private static void RotateX180(Vector3[] vectors, bool normalize = false)
    {
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            var rotated = new Vector3(v.X, -v.Y, -v.Z);
            vectors[i] = normalize && rotated.LengthSquared() > 0.00000001f
                ? Vector3.Normalize(rotated)
                : rotated;
        }
    }

    private static void RotateX180(Vector4[]? vectors, bool normalize = false)
    {
        if (vectors is null)
        {
            return;
        }

        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            var rotated = new Vector3(v.X, -v.Y, -v.Z);
            rotated = normalize && rotated.LengthSquared() > 0.00000001f
                ? Vector3.Normalize(rotated)
                : rotated;
            vectors[i] = new Vector4(rotated, v.W);
        }
    }

    private static Dictionary<int, Matrix4x4> BuildNodeWorldTransforms(JsonElement nodes, IReadOnlyList<int> rootNodes)
    {
        var result = new Dictionary<int, Matrix4x4>();
        foreach (var rootNode in rootNodes)
        {
            BuildNodeWorldTransform(rootNode, Matrix4x4.Identity, nodes, result);
        }

        return result;
    }

    private static void BuildNodeWorldTransform(int nodeIndex, Matrix4x4 parentTransform, JsonElement nodes, Dictionary<int, Matrix4x4> result)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
        {
            return;
        }

        var node = nodes[nodeIndex];
        var world = ReadNodeLocalTransform(node) * parentTransform;
        result[nodeIndex] = world;
        if (!node.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            BuildNodeWorldTransform(child.GetInt32(), world, nodes, result);
        }
    }

    private static Matrix4x4 ReadNodeLocalTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrixElem) &&
            matrixElem.ValueKind == JsonValueKind.Array &&
            matrixElem.GetArrayLength() == 16)
        {
            var values = matrixElem.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Matrix4x4(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]);
        }

        var scale = ReadVector3(node, "scale", new Vector3(1, 1, 1));
        var rotation = ReadQuaternion(node, "rotation", Quaternion.Identity);
        var translation = ReadVector3(node, "translation", Vector3.Zero);

        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static Vector3 ReadVector3(JsonElement owner, string property, Vector3 fallback)
    {
        if (!owner.TryGetProperty(property, out var elem) ||
            elem.ValueKind != JsonValueKind.Array ||
            elem.GetArrayLength() < 3)
        {
            return fallback;
        }

        return new Vector3(elem[0].GetSingle(), elem[1].GetSingle(), elem[2].GetSingle());
    }

    private static Quaternion ReadQuaternion(JsonElement owner, string property, Quaternion fallback)
    {
        if (!owner.TryGetProperty(property, out var elem) ||
            elem.ValueKind != JsonValueKind.Array ||
            elem.GetArrayLength() < 4)
        {
            return fallback;
        }

        return new Quaternion(elem[0].GetSingle(), elem[1].GetSingle(), elem[2].GetSingle(), elem[3].GetSingle());
    }

    private static GltfPrimitive ReadPrimitive(JsonElement prim, ParseContext ctx, Matrix4x4 transform, bool isSkinned = false)
    {
        var attrs = prim.GetProperty("attributes");
        var source = ReadTelltaleSource(prim);
        var effectiveTransform = !isSkinned && HasTelltaleSourceMetadata(source)
            ? Matrix4x4.Identity
            : transform;
        var positions = ReadVec3(ctx, GetAttr(attrs, "POSITION"))
            ?? throw new InvalidDataException("primitiva sem POSITION.");
        var normals = ReadVec3(ctx, GetAttr(attrs, "NORMAL"));
        var tangents = ReadVec4(ctx, GetAttr(attrs, "TANGENT"));
        var binormals = ReadVec4(ctx, GetAttr(attrs, "_TT_BINORMAL"));
        ApplyOriginalTangentWs(tangents, ReadScalarFloats(ctx, GetAttr(attrs, "_TT_TANGENT_W")));
        ApplyTransform(positions, normals, tangents, binormals, effectiveTransform);
        var uv0 = ReadVec2(ctx, GetAttr(attrs, "TEXCOORD_0"));
        var uv1 = ReadVec2(ctx, GetAttr(attrs, "TEXCOORD_1"));
        var uv2 = ReadVec2(ctx, GetAttr(attrs, "TEXCOORD_2"));
        var uv3 = ReadVec2(ctx, GetAttr(attrs, "TEXCOORD_3"));
        var color0 = ReadVec4(ctx, GetAttr(attrs, "COLOR_0"));
        var unknown1 = ReadScalarFloats(ctx, GetAttr(attrs, "_TT_UNKNOWN1"));
        var joints0 = ReadUShort4(ctx, GetAttr(attrs, "JOINTS_0"));
        var weights0 = ReadVec4(ctx, GetAttr(attrs, "WEIGHTS_0"));
        int[] indices;
        if (prim.TryGetProperty("indices", out var indicesElem))
        {
            indices = ReadScalarInts(ctx, indicesElem.GetInt32());
        }
        else
        {
            indices = new int[positions.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
            }
        }

        string? materialName = null;
        IReadOnlyDictionary<string, GltfImage> textureSlots = new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, GltfImage> referencedTextures = new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);
        if (prim.TryGetProperty("material", out var matElem) && ctx.Materials.ValueKind == JsonValueKind.Array)
        {
            var matIndex = matElem.GetInt32();
            if (matIndex >= 0 && matIndex < ctx.Materials.GetArrayLength())
            {
                var material = ctx.Materials[matIndex];
                if (material.TryGetProperty("name", out var nameElem))
                {
                    materialName = nameElem.GetString();
                }

                referencedTextures = ResolveMaterialTextures(material, ctx, includePbrExtras: true);
                textureSlots = ResolveMaterialTextures(material, ctx, includePbrExtras: false);
            }
        }

        return new GltfPrimitive
        {
            Positions = positions,
            Normals = normals,
            Uv0 = uv0,
            Uv1 = uv1,
            Uv2 = uv2,
            Uv3 = uv3,
            Color0 = color0,
            Tangents = tangents,
            Binormals = binormals,
            Unknown1 = unknown1,
            Joints0 = joints0,
            Weights0 = weights0,
            Indices = indices,
            MaterialName = materialName,
            BonePaletteIndex = source.BonePaletteIndex,
            SourceMeshPath = source.SourceMeshPath,
            SourceSubmeshIndex = source.SourceSubmeshIndex,
            IsSkinned = isSkinned,
            BaseColor = textureSlots.TryGetValue("diffuse", out var baseColor) ? baseColor : null,
            TextureSlots = textureSlots,
            ReferencedTextures = referencedTextures,
        };
    }

    private static void ApplyTransform(Vector3[] positions, Vector3[]? normals, Vector4[]? tangents, Vector4[]? binormals, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return;
        }

        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = Vector3.Transform(positions[i], transform);
        }

        Matrix4x4 normalTransform;
        if (Matrix4x4.Invert(transform, out var inverse))
        {
            normalTransform = Matrix4x4.Transpose(inverse);
        }
        else
        {
            normalTransform = transform;
        }

        if (normals is not null)
        {
            for (var i = 0; i < normals.Length; i++)
            {
                var normal = Vector3.TransformNormal(normals[i], normalTransform);
                normals[i] = normal.LengthSquared() > 0.00000001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            }
        }

        TransformVector4Directions(tangents, normalTransform);
        TransformVector4Directions(binormals, normalTransform);
    }

    private static void TransformVector4Directions(Vector4[]? vectors, Matrix4x4 normalTransform)
    {
        if (vectors is null)
        {
            return;
        }

        for (var i = 0; i < vectors.Length; i++)
        {
            var value = vectors[i];
            var direction = Vector3.TransformNormal(new Vector3(value.X, value.Y, value.Z), normalTransform);
            direction = direction.LengthSquared() > 0.00000001f ? Vector3.Normalize(direction) : Vector3.UnitX;
            vectors[i] = new Vector4(direction, value.W);
        }
    }

    private static (int? BonePaletteIndex, string? SourceMeshPath, int? SourceSubmeshIndex) ReadTelltaleSource(JsonElement prim)
    {
        if (!prim.TryGetProperty("extras", out var extras) ||
            !extras.TryGetProperty("telltaleSubmesh", out var telltale) ||
            telltale.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        int? bonePaletteIndex = null;
        if (telltale.TryGetProperty("bonePaletteIndex", out var indexElem) &&
            indexElem.ValueKind == JsonValueKind.Number &&
            indexElem.TryGetInt32(out var index))
        {
            bonePaletteIndex = index;
        }

        string? sourceMeshPath = null;
        if (telltale.TryGetProperty("sourceMesh", out var sourceMeshElem) &&
            sourceMeshElem.ValueKind == JsonValueKind.String)
        {
            sourceMeshPath = sourceMeshElem.GetString();
        }

        int? sourceSubmeshIndex = null;
        if (telltale.TryGetProperty("sourceSubmeshIndex", out var sourceSubmeshElem) &&
            sourceSubmeshElem.ValueKind == JsonValueKind.Number &&
            sourceSubmeshElem.TryGetInt32(out var sourceSubmeshIndexValue))
        {
            sourceSubmeshIndex = sourceSubmeshIndexValue;
        }

        return (bonePaletteIndex, sourceMeshPath, sourceSubmeshIndex);
    }

    private static IReadOnlyList<GltfJoint> ReadFirstSkinJoints(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("skins", out var skins) ||
            skins.ValueKind != JsonValueKind.Array ||
            skins.GetArrayLength() == 0)
        {
            return [];
        }

        var skin = skins[0];
        if (!skin.TryGetProperty("joints", out var jointsElem) ||
            jointsElem.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var joints = new List<GltfJoint>();
        foreach (var jointElem in jointsElem.EnumerateArray())
        {
            var nodeIndex = jointElem.GetInt32();
            if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
            {
                joints.Add(new GltfJoint());
                continue;
            }

            var node = nodes[nodeIndex];
            var name = node.TryGetProperty("name", out var nameElem)
                ? nameElem.GetString()
                : null;
            joints.Add(new GltfJoint
            {
                Name = name,
                Hash = ReadNodeHash(node),
            });
        }

        return joints;
    }

    private static ulong? ReadNodeHash(JsonElement node)
    {
        if (!node.TryGetProperty("extras", out var extras) ||
            !extras.TryGetProperty("hash", out var hashElem))
        {
            return null;
        }

        if (hashElem.ValueKind == JsonValueKind.String)
        {
            var text = hashElem.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash)
                ? hash
                : null;
        }

        if (hashElem.ValueKind == JsonValueKind.Number &&
            hashElem.TryGetUInt64(out var numericHash))
        {
            return numericHash;
        }

        return null;
    }

    private static Dictionary<string, GltfImage> ResolveMaterialTextures(JsonElement material, ParseContext ctx, bool includePbrExtras)
    {
        var result = new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);

        // First preserve exact Telltale slots from GLTFs exported by this tool.
        var telltaleTextures = default(JsonElement);
        var hasTelltaleTextures =
            material.TryGetProperty("extras", out var extras) &&
            extras.TryGetProperty("telltaleTextures", out telltaleTextures) &&
            telltaleTextures.ValueKind == JsonValueKind.Object;
        if (hasTelltaleTextures)
        {
            foreach (var slotProperty in telltaleTextures.EnumerateObject())
            {
                if (slotProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Resolve by the stored image name first: it survives a Blender round-trip, whereas the
                // absolute textureIndex does not (Blender rewrites the textures/images arrays).
                var storedName = slotProperty.Value.TryGetProperty("name", out var nameElem) &&
                                 nameElem.ValueKind == JsonValueKind.String
                    ? nameElem.GetString()
                    : null;
                if (TryResolveTextureByImageName(storedName, ctx, out var namedImage))
                {
                    result[slotProperty.Name] = namedImage;
                }
                else if (string.IsNullOrWhiteSpace(storedName) &&
                         slotProperty.Value.TryGetProperty("textureIndex", out var textureIndexElem) &&
                         TryResolveTextureByIndex(textureIndexElem.GetInt32(), ctx, out var indexedImage))
                {
                    // Legacy exports without a stored name fall back to the index. When a name *is*
                    // present but no longer resolves (Blender pruned the image because nothing bound it),
                    // the slot is left empty rather than binding an unrelated image by a stale index. The
                    // reinserter then keeps the template submesh's own original binding for that slot,
                    // which is the correct, unmodified line/detail texture.
                    result[slotProperty.Name] = indexedImage;
                }
            }
        }

        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            AddTexture(result, "diffuse", pbr, "baseColorTexture", ctx);
            if (!result.ContainsKey("diffuse") && !hasTelltaleTextures)
            {
                result["diffuse"] = BuildSolidBaseColorImage(material, pbr);
            }

            if (includePbrExtras)
            {
                AddTexture(result, "metallicRoughness", pbr, "metallicRoughnessTexture", ctx);
            }
        }

        if (includePbrExtras)
        {
            AddTexture(result, "normal", material, "normalTexture", ctx);
            AddTexture(result, "occlusion", material, "occlusionTexture", ctx);
            AddTexture(result, "emissive", material, "emissiveTexture", ctx);
        }

        return result;
    }

    private static GltfImage BuildSolidBaseColorImage(JsonElement material, JsonElement pbr)
    {
        var rgba = ReadBaseColorFactor(pbr);
        var r = ToByte(rgba.X);
        var g = ToByte(rgba.Y);
        var b = ToByte(rgba.Z);
        var a = ToByte(rgba.W);
        var materialName = material.TryGetProperty("name", out var nameElem) &&
                           nameElem.ValueKind == JsonValueKind.String
            ? SanitizeName(nameElem.GetString())
            : "material";
        var imageName = $"{materialName}_baseColor_{r:X2}{g:X2}{b:X2}{a:X2}.png";
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(a, r, g, b));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new GltfImage
        {
            Name = imageName,
            Data = stream.ToArray(),
            MimeType = "image/png",
        };
    }

    private static Vector4 ReadBaseColorFactor(JsonElement pbr)
    {
        if (!pbr.TryGetProperty("baseColorFactor", out var factor) ||
            factor.ValueKind != JsonValueKind.Array ||
            factor.GetArrayLength() < 3)
        {
            return Vector4.One;
        }

        var values = factor.EnumerateArray().Select(element => element.GetSingle()).ToArray();
        return new Vector4(
            values.Length > 0 ? values[0] : 1f,
            values.Length > 1 ? values[1] : 1f,
            values.Length > 2 ? values[2] : 1f,
            values.Length > 3 ? values[3] : 1f);
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "material";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => ch > 0x7F || invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "material" : sanitized;
    }

    private static void AddTexture(Dictionary<string, GltfImage> result, string slot, JsonElement owner, string property, ParseContext ctx)
    {
        if (!result.ContainsKey(slot) &&
            owner.TryGetProperty(property, out var textureInfo) &&
            ResolveTextureInfo(textureInfo, ctx) is { } image)
        {
            result[slot] = image;
        }
    }

    private static GltfImage? ResolveTextureInfo(JsonElement textureInfo, ParseContext ctx)
    {
        if (!textureInfo.TryGetProperty("index", out var texIndexElem))
        {
            return null;
        }

        return TryResolveTextureByIndex(texIndexElem.GetInt32(), ctx, out var image)
            ? image
            : null;
    }

    private static bool TryResolveTextureByIndex(int textureIndex, ParseContext ctx, out GltfImage image)
    {
        image = null!;
        if (ctx.Textures.ValueKind != JsonValueKind.Array ||
            textureIndex < 0 ||
            textureIndex >= ctx.Textures.GetArrayLength())
        {
            return false;
        }

        var texture = ctx.Textures[textureIndex];
        if (!texture.TryGetProperty("source", out var sourceElem) || ctx.Images.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var imageIndex = sourceElem.GetInt32();
        return TryBuildImageFromIndex(imageIndex, ctx, out image);
    }

    // Resolves a Telltale texture slot by the image NAME stored in the material's telltaleTextures
    // extras, rather than by the absolute textureIndex. A Blender round-trip preserves the embedded
    // image names and the extras JSON verbatim, but rewrites the textures/images arrays — so the
    // stored textureIndex goes stale and would bind the wrong image (e.g. the hand line slot lands on
    // the head line, the body outline drops entirely). Matching by name survives that reindexing.
    private static bool TryResolveTextureByImageName(string? name, ParseContext ctx, out GltfImage image)
    {
        image = null!;
        if (string.IsNullOrWhiteSpace(name) || ctx.Images.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var imageCount = ctx.Images.GetArrayLength();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < imageCount; i++)
            {
                var imageElem = ctx.Images[i];
                if (!imageElem.TryGetProperty("name", out var nameElem) ||
                    nameElem.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = nameElem.GetString();
                var matches = pass == 0
                    ? string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)
                    : NormalizeImageName(candidate).Equals(NormalizeImageName(name), StringComparison.OrdinalIgnoreCase);
                if (matches && TryBuildImageFromIndex(i, ctx, out image))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Blender can append a ".001" disambiguation suffix and a file extension to image names on
    // round-trip; normalizing both sides lets the name match still land on the right image.
    private static string NormalizeImageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var stem = name;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".webp", ".d3dtx" })
        {
            if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^ext.Length];
                break;
            }
        }

        // Strip a trailing Blender duplicate suffix like ".001".
        if (stem.Length > 4 && stem[^4] == '.' &&
            char.IsDigit(stem[^1]) && char.IsDigit(stem[^2]) && char.IsDigit(stem[^3]))
        {
            stem = stem[..^4];
        }

        return stem;
    }

    private static bool TryBuildImageFromIndex(int imageIndex, ParseContext ctx, out GltfImage image)
    {
        image = null!;
        if (ctx.Images.ValueKind != JsonValueKind.Array ||
            imageIndex < 0 ||
            imageIndex >= ctx.Images.GetArrayLength())
        {
            return false;
        }

        var imageElem = ctx.Images[imageIndex];
        var name = imageElem.TryGetProperty("name", out var n) ? n.GetString() ?? $"image_{imageIndex}" : $"image_{imageIndex}";
        var mime = imageElem.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "image/png" : "image/png";

        // Image embedded in a bufferView (GLB) or loaded through uri (data: base64 or external file).
        if (imageElem.TryGetProperty("bufferView", out var bvElem))
        {
            var data = ctx.SliceBufferView(bvElem.GetInt32());
            image = new GltfImage { Name = name, Data = data, MimeType = mime };
            return true;
        }

        if (imageElem.TryGetProperty("uri", out var uriElem))
        {
            var uri = uriElem.GetString() ?? "";
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = uri.IndexOf(',');
                if (comma > 0)
                {
                    image = new GltfImage { Name = name, Data = Convert.FromBase64String(uri[(comma + 1)..]), MimeType = mime };
                    return true;
                }
            }
            else
            {
                var file = Path.Combine(ctx.BaseDir, Uri.UnescapeDataString(uri));
                if (File.Exists(file))
                {
                    image = new GltfImage { Name = Path.GetFileNameWithoutExtension(file), Data = File.ReadAllBytes(file), MimeType = mime };
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetAttr(JsonElement attrs, string name)
        => attrs.TryGetProperty(name, out var elem) ? elem.GetInt32() : -1;

    private static Vector3[]? ReadVec3(ParseContext ctx, int accessor)
    {
        if (accessor < 0)
        {
            return null;
        }

        var floats = ctx.ReadFloats(accessor, 3);
        var result = new Vector3[floats.Length / 3];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
        }

        return result;
    }

    private static Vector2[]? ReadVec2(ParseContext ctx, int accessor)
    {
        if (accessor < 0)
        {
            return null;
        }

        var floats = ctx.ReadFloats(accessor, 2);
        var result = new Vector2[floats.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector2(floats[i * 2], floats[i * 2 + 1]);
        }

        return result;
    }

    private static Vector4[]? ReadVec4(ParseContext ctx, int accessor)
    {
        if (accessor < 0)
        {
            return null;
        }

        var floats = ctx.ReadFloats(accessor, 4);
        var result = new Vector4[floats.Length / 4];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector4(floats[i * 4], floats[i * 4 + 1], floats[i * 4 + 2], floats[i * 4 + 3]);
        }

        return result;
    }

    private static float[]? ReadScalarFloats(ParseContext ctx, int accessor)
    {
        return accessor < 0 ? null : ctx.ReadFirstFloatComponents(accessor);
    }

    private static void ApplyOriginalTangentWs(Vector4[]? tangents, float[]? originalWs)
    {
        if (tangents is null || originalWs is null || originalWs.Length != tangents.Length)
        {
            return;
        }

        for (var i = 0; i < tangents.Length; i++)
        {
            tangents[i] = new Vector4(tangents[i].X, tangents[i].Y, tangents[i].Z, originalWs[i]);
        }
    }

    private static ushort[]? ReadUShort4(ParseContext ctx, int accessor)
    {
        if (accessor < 0)
        {
            return null;
        }

        return ctx.ReadIntsRaw(accessor, 4).Select(v => (ushort)v).ToArray();
    }

    private static int[] ReadScalarInts(ParseContext ctx, int accessor)
        => ctx.ReadIntsRaw(accessor, 1);

    private static List<byte[]> LoadBuffers(JsonElement root, byte[]? glbBin, string baseDir)
    {
        var result = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var buffers))
        {
            return result;
        }

        foreach (var buffer in buffers.EnumerateArray())
        {
            if (buffer.TryGetProperty("uri", out var uriElem))
            {
                var uri = uriElem.GetString() ?? "";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    result.Add(comma > 0 ? Convert.FromBase64String(uri[(comma + 1)..]) : []);
                }
                else
                {
                    var file = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
                    result.Add(File.Exists(file) ? File.ReadAllBytes(file) : []);
                }
            }
            else
            {
                result.Add(glbBin ?? []);
            }
        }

        return result;
    }

    private sealed class ParseContext(
        List<byte[]> buffers,
        JsonElement bufferViews,
        JsonElement accessors,
        JsonElement materials,
        JsonElement textures,
        JsonElement images,
        JsonElement skins,
        string baseDir)
    {
        public JsonElement Materials => materials;
        public JsonElement Textures => textures;
        public JsonElement Images => images;
        public JsonElement Skins => skins;
        public string BaseDir => baseDir;

        public int AccessorCount(int accessorIndex)
        {
            if (accessors.ValueKind != JsonValueKind.Array ||
                accessorIndex < 0 ||
                accessorIndex >= accessors.GetArrayLength())
            {
                return -1;
            }

            return accessors[accessorIndex].TryGetProperty("count", out var count) ? count.GetInt32() : -1;
        }

        public byte[] SliceBufferView(int index)
        {
            var view = bufferViews[index];
            var bufferIndex = view.GetProperty("buffer").GetInt32();
            var byteOffset = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            var byteLength = view.GetProperty("byteLength").GetInt32();
            return buffers[bufferIndex].AsSpan(byteOffset, byteLength).ToArray();
        }

        // Reads an accessor as floats (normalizing integers when needed). componentsExpected only
        // valida o tipo.
        public float[] ReadFloats(int accessorIndex, int componentsExpected)
        {
            var accessor = accessors[accessorIndex];
            var (data, count, components, componentType, normalized, stride) = ResolveAccessor(accessor);
            _ = componentsExpected;
            var result = new float[count * components];
            for (var i = 0; i < count; i++)
            {
                var elemBase = i * stride;
                for (var c = 0; c < components; c++)
                {
                    result[i * components + c] = ReadComponentAsFloat(data, elemBase + c * ComponentSize(componentType), componentType, normalized);
                }
            }

            return result;
        }

        // Reads an integer accessor (indices, joints) without normalizing.
        public int[] ReadIntsRaw(int accessorIndex, int componentsExpected)
        {
            var accessor = accessors[accessorIndex];
            var (data, count, components, componentType, _, stride) = ResolveAccessor(accessor);
            _ = componentsExpected;
            var result = new int[count * components];
            for (var i = 0; i < count; i++)
            {
                var elemBase = i * stride;
                for (var c = 0; c < components; c++)
                {
                    result[i * components + c] = ReadComponentAsInt(data, elemBase + c * ComponentSize(componentType), componentType);
                }
            }

            return result;
        }

        public Matrix4x4[] ReadMatrices(int accessorIndex)
        {
            var floats = ReadFloats(accessorIndex, 16);
            var result = new Matrix4x4[floats.Length / 16];
            for (var i = 0; i < result.Length; i++)
            {
                var offset = i * 16;
                result[i] = new Matrix4x4(
                    floats[offset + 0], floats[offset + 4], floats[offset + 8], floats[offset + 12],
                    floats[offset + 1], floats[offset + 5], floats[offset + 9], floats[offset + 13],
                    floats[offset + 2], floats[offset + 6], floats[offset + 10], floats[offset + 14],
                    floats[offset + 3], floats[offset + 7], floats[offset + 11], floats[offset + 15]);
            }

            return result;
        }

        public float[] ReadFirstFloatComponents(int accessorIndex)
        {
            var accessor = accessors[accessorIndex];
            var (data, count, _, componentType, normalized, stride) = ResolveAccessor(accessor);
            var result = new float[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = ReadComponentAsFloat(data, i * stride, componentType, normalized);
            }

            return result;
        }

        private (byte[] Data, int Count, int Components, int ComponentType, bool Normalized, int Stride) ResolveAccessor(JsonElement accessor)
        {
            var count = accessor.GetProperty("count").GetInt32();
            var componentType = accessor.GetProperty("componentType").GetInt32();
            var type = accessor.GetProperty("type").GetString() ?? "SCALAR";
            var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
            var components = type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                _ => 1,
            };

            var accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            var bufferViewIndex = accessor.GetProperty("bufferView").GetInt32();
            var view = bufferViews[bufferViewIndex];
            var bufferIndex = view.GetProperty("buffer").GetInt32();
            var viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            var elementSize = components * ComponentSize(componentType);
            var stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : elementSize;

            var dataOffset = viewOffset + accessorOffset;
            var totalNeeded = (count - 1) * stride + elementSize;
            var data = buffers[bufferIndex].AsSpan(dataOffset, totalNeeded).ToArray();
            return (data, count, components, componentType, normalized, stride);
        }

        private static int ComponentSize(int componentType) => componentType switch
        {
            5120 => 1, // byte
            5121 => 1, // ubyte
            5122 => 2, // short
            5123 => 2, // ushort
            5125 => 4, // uint
            5126 => 4, // float
            _ => throw new InvalidDataException($"Unknown componentType: {componentType}"),
        };

        private static float ReadComponentAsFloat(byte[] data, int offset, int componentType, bool normalized) => componentType switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4)),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
            5121 => normalized ? data[offset] / 255f : data[offset],
            5120 => normalized ? Math.Max(unchecked((sbyte)data[offset]) / 127f, -1f) : unchecked((sbyte)data[offset]),
            _ => throw new InvalidDataException($"Unknown componentType: {componentType}"),
        };

        private static int ReadComponentAsInt(byte[] data, int offset, int componentType) => componentType switch
        {
            5125 => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
            5122 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
            5121 => data[offset],
            5120 => unchecked((sbyte)data[offset]),
            _ => throw new InvalidDataException($"Unknown componentType: {componentType}"),
        };
    }
}
