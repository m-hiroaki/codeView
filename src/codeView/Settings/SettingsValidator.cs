using System.Text;
using System.Text.Json;

namespace CodeView.Settings;

internal sealed record SettingsValidation(SettingsSnapshot Snapshot, bool IsValid, bool CanSave);

internal static class SettingsValidator
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxNames = 256;
    public const int MaxNameScalars = 255;

    public static SettingsValidation Validate(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > MaxBytes)
            return Invalid();

        // A UTF-8 BOM is allowed in files but is not part of JSON content.
        if (utf8.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            utf8 = utf8[3..];

        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Invalid();

            var properties = new HashSet<string>(StringComparer.Ordinal);
            var hasUnknown = false;
            foreach (var property in root.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    return Invalid();
                hasUnknown |= property.Name is not ("schemaVersion" or "excludedDirectoryNames");
            }

            if (root.TryGetProperty("schemaVersion", out var version) &&
                (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1))
                return Invalid();

            if (!root.TryGetProperty("excludedDirectoryNames", out var names))
                return new(SettingsSnapshot.Default, true, !hasUnknown);
            if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() > MaxNames)
                return Invalid();

            var unique = new HashSet<string>(StringComparer.Ordinal);
            var values = new List<string>();
            foreach (var item in names.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return Invalid();
                var name = item.GetString()!;
                if (!IsDirectoryName(name))
                    return Invalid();
                if (unique.Add(name))
                    values.Add(name);
            }

            return new(new SettingsSnapshot(values), true, !hasUnknown);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (InvalidOperationException)
        {
            // JsonElement may reject an invalid Unicode escape while materializing a string.
            return Invalid();
        }
    }

    internal static bool IsDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        // Reject malformed UTF-16 before the JSON writer could replace it with U+FFFD.
        for (var index = 0; index < name.Length; index++)
        {
            if (!char.IsSurrogate(name[index])) continue;
            if (!char.IsSurrogatePair(name, index)) return false;
            index++;
        }
        var count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (++count > MaxNameScalars || Rune.IsControl(rune) ||
                rune.Value is '/' or '\\' or '*' or '?' or '[' or ']')
                return false;
        }
        return true;
    }

    private static SettingsValidation Invalid() => new(SettingsSnapshot.Default, false, false);
}
