namespace TelltaleD3DMeshEditor.Formats.Skeleton;

internal static class BoneNameAliases
{
    private static readonly string[] CharacterSpecificPrefixes =
    [
        "eye",
        "brow",
        "nose",
        "cheek",
        "mouth",
        "laughline",
        "jaw",
        "tongue",
    ];

    public static bool TryGetCharacterSpecificAlias(string? name, out string alias)
    {
        alias = "";
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("bone_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = name.IndexOf('_');
        if (separator <= 0 || separator + 1 >= name.Length)
        {
            return false;
        }

        var suffix = name[(separator + 1)..];
        if (!LooksCharacterSpecific(suffix))
        {
            return false;
        }

        alias = suffix;
        return true;
    }

    private static bool LooksCharacterSpecific(string suffix)
        => CharacterSpecificPrefixes.Any(prefix =>
            suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
