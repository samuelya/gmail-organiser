using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GmailOrganiser.Data;

/// <summary>
/// Stores an enum as its lower snake_case name (<c>NotAnalysed</c> ↔ <c>not_analysed</c>), matching DESIGN §7.
/// An unknown database value throws rather than silently mapping to a default.
/// </summary>
public sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(v => ToDb(v), v => FromDb(v))
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> Names =
        Enum.GetValues<TEnum>().ToFrozenDictionary(v => v, v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

    private static readonly FrozenDictionary<string, TEnum> Values = Names.ToFrozenDictionary(p => p.Value, p => p.Key);

    public static string ToDb(TEnum value) => Names[value];

    /// <summary>The snake_case names in declaration order, comma-separated, for validation messages.</summary>
    public static readonly string NamesList = string.Join(", ", Enum.GetValues<TEnum>().Select(ToDb));

    /// <summary>Parses user input: the snake_case name, trimmed, case-insensitive.</summary>
    public static bool TryFromDb(string value, out TEnum parsed) =>
        Values.TryGetValue(value.Trim().ToLowerInvariant(), out parsed);

    public static TEnum FromDb(string value) =>
        Values.TryGetValue(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"'{value}' is not a valid {typeof(TEnum).Name}.");
}
