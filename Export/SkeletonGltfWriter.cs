using System.Text.Json;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Export;

// Writes a .skl as a node-only .gltf: a joint hierarchy with local TRS and no mesh. Useful for
// inspecting the skeleton by itself. Each node carries extras.hash to trace the source bone.
public static class SkeletonGltfWriter
{
    public static void WriteSkeletonGltf(SkeletonData skeleton, string name, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nodes = new List<Dictionary<string, object>>();
        var roots = new List<int>();

        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            var bone = skeleton.Bones[i];
            var node = new Dictionary<string, object>
            {
                ["name"] = bone.Name,
                ["translation"] = new[] { bone.X, bone.Y, bone.Z },
                ["rotation"] = new[] { bone.Qx, bone.Qy, bone.Qz, bone.Qw },
                ["extras"] = new Dictionary<string, object>
                {
                    ["hash"] = $"0x{bone.Hash:X16}",
                    ["parentHash"] = $"0x{bone.ParentHash:X16}",
                    ["translationBits"] = GltfCommon.FloatBits(bone.X, bone.Y, bone.Z),
                    ["rotationBits"] = GltfCommon.FloatBits(bone.Qx, bone.Qy, bone.Qz, bone.Qw),
                },
            };

            var children = skeleton.Bones
                .Select((candidate, index) => (candidate, index))
                .Where(pair => pair.candidate.ParentIndex == i)
                .Select(pair => pair.index)
                .ToArray();
            if (children.Length > 0)
            {
                node["children"] = children;
            }

            if (bone.ParentIndex < 0)
            {
                roots.Add(i);
            }

            nodes.Add(node);
        }

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object>
            {
                ["version"] = "2.0",
                ["generator"] = "TelltaleD3DMeshEditor",
                ["extras"] = new Dictionary<string, object>
                {
                    ["telltale"] = new Dictionary<string, object> { ["kind"] = "skl" },
                },
            },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["name"] = name, ["nodes"] = roots } },
            ["nodes"] = nodes,
        };

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(gltf, GltfCommon.JsonOptions));
    }
}
