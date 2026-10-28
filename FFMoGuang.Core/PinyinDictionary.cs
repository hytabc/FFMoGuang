using System.Globalization;
using System.Text;

namespace FFMoGuang.Core;

public static class PinyinDictionary
{
    private static readonly Lazy<Dictionary<int, string>> Readings = new(Load);
    public static bool TryRead(Rune character, out string pinyin) => Readings.Value.TryGetValue(character.Value, out pinyin!);
    public static bool IsHan(Rune character) => character.Value is >= 0x3400 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF or 0x3007;
    public static string Normalize(string pinyin) => string.Concat(pinyin.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Replace("u\u0308", "v", StringComparison.Ordinal)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
    private static Dictionary<int, string> Load()
    {
        using var stream = typeof(PinyinDictionary).Assembly.GetManifestResourceStream("FFMoGuang.Core.Resources.pinyin.txt")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            var reading = line[(colon + 1)..].Split('#')[0].Split(',')[0].Trim();
            var normalized = Normalize(reading);
            if (normalized.Length != 0 && normalized.All(c => c is >= 'a' and <= 'z'))
                result[int.Parse(line.AsSpan(2, colon - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = normalized;
        }
        return result;
    }
}
