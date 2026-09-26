using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Reads a JSON value that may be either a string or a number into a <see cref="string"/>. The
/// osu! v1 API has two flavours: <c>old.ppy.sh</c> returns every field as a quoted string, but the
/// keyless <c>osu.direct</c> mirror returns numeric fields (ids, lengths, ratings) as bare JSON
/// numbers. Modelling those fields as <c>string?</c> would throw on the mirror, so this converter
/// accepts both shapes and normalises numbers to their invariant string form.
/// </summary>
/// <remarks>
/// Null and missing values become <c>null</c>; booleans and other token types are also normalised
/// to their literal text so an unexpected shape never throws on the lookup hot path.
/// </remarks>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null,
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
