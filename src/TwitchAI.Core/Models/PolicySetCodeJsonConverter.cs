using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchAI.Core.Models;

public sealed class PolicySetCodeJsonConverter : JsonConverter<PolicySetCode>
{
    public override PolicySetCode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A policy set code must be a string.");
        }

        return reader.GetString() switch
        {
            "G" => PolicySetCode.General,
            "B" => PolicySetCode.Backseatgaming,
            var value => throw new JsonException($"Unknown policy set code '{value}'."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        PolicySetCode value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(
            value switch
            {
                PolicySetCode.General => "G",
                PolicySetCode.Backseatgaming => "B",
                _ => throw new JsonException($"Unknown policy set '{value}'."),
            });
    }
}
