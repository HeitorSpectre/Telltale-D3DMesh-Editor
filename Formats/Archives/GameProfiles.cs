using System.ComponentModel.DataAnnotations;
using System.Reflection;
using TelltaleToolKit.Encryption;

namespace TelltaleD3DMeshEditor.Formats.Archives;

// Bridges to the TelltaleToolKit's catalogue of per-game Blowfish keys (the T3BlowfishKey enum). Each
// game has a real binary key (returned by GetBlowfishKey) and a friendly display name. The toolkit
// owns this list, so support expands automatically as it adds games — no keys are hard-coded here.
// Note: an archive's correct key is the binary one from this enum, NOT the short label that appears in
// the toolkit's game-profile JSON files (e.g. "Twd2"), which is only a name.
public static class GameProfiles
{
    public sealed record GameKey(string Name, string Key);

    private static List<GameKey>? _cache;

    public static IReadOnlyList<GameKey> All => _cache ??= Build();

    // Keys to try when opening an archive whose game is unknown: the empty key (an unencrypted archive)
    // first, then every distinct real key from the catalogue.
    public static IReadOnlyList<string> CandidateKeys()
    {
        var keys = new List<string> { string.Empty };
        keys.AddRange(All.Select(game => game.Key).Where(key => !string.IsNullOrEmpty(key)));
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    // Friendly description of which game a detected key belongs to.
    public static string DescribeKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "unencrypted archive";
        }

        var names = All
            .Where(game => string.Equals(game.Key, key, StringComparison.Ordinal))
            .Select(game => game.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count > 0 ? string.Join(" / ", names) : "an encrypted Telltale archive";
    }

    private static List<GameKey> Build()
    {
        var list = new List<GameKey>();
        foreach (var game in Enum.GetValues<T3BlowfishKey>())
        {
            string key;
            try
            {
                key = game.GetBlowfishKey();
            }
            catch
            {
                // Some enum entries may not have a key defined yet; skip them.
                continue;
            }

            list.Add(new GameKey(DisplayName(game), key));
        }

        return list;
    }

    private static string DisplayName(T3BlowfishKey game)
    {
        var member = typeof(T3BlowfishKey).GetMember(game.ToString()).FirstOrDefault();
        return member?.GetCustomAttribute<DisplayAttribute>()?.Name ?? game.ToString();
    }
}
