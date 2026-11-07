using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>读取逐条规则及旧全局格式，保存时统一使用逐条规则格式。</summary>
public sealed class DanmuRegexFilterSettingsJsonConverter : JsonConverter<DanmuRegexFilterSettings>
{
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override DanmuRegexFilterSettings Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The danmu regex filter must be an object.");
        }
        using var document = JsonDocument.ParseValue(ref reader);
        var names = new HashSet<string>(StringComparer.Ordinal);
        List<DanmuRegexRuleSettings>? rules = null;
        List<string>? patterns = null;
        var blockDisplay = false;
        var blockTranslation = false;
        var hasLegacy = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case nameof(DanmuRegexFilterSettings.Rules):
                    RequireUnique(names, property.Name);
                    rules = ReadRules(property.Value);
                    break;
                case "Enabled":
                    RequireUnique(names, property.Name);
                    hasLegacy = true;
                    blockDisplay = ReadBoolean(property.Value, property.Name);
                    break;
                case "BlockTranslation":
                    RequireUnique(names, property.Name);
                    hasLegacy = true;
                    blockTranslation = ReadBoolean(property.Value, property.Name);
                    break;
                case "Patterns":
                    RequireUnique(names, property.Name);
                    hasLegacy = true;
                    patterns = ReadPatterns(property.Value);
                    break;
            }
        }
        if (rules is not null)
        {
            if (hasLegacy)
            {
                throw new JsonException("Danmu regex rules cannot mix current and legacy settings.");
            }
            return new() { Rules = rules };
        }
        var result = new DanmuRegexFilterSettings();
        if (patterns is not null)
        {
            result.Rules = patterns.Select(pattern => new DanmuRegexRuleSettings { Pattern = pattern }).ToList();
        }
        foreach (var rule in result.Rules)
        {
            rule.BlockDisplay = blockDisplay;
            rule.BlockTranslation = blockTranslation;
        }
        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DanmuRegexFilterSettings value, JsonSerializerOptions options)
    {
        if (value is null || value.Rules is null)
        {
            throw new JsonException("The danmu regex rules are missing.");
        }
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(DanmuRegexFilterSettings.Rules));
        writer.WriteStartArray();
        foreach (var rule in value.Rules)
        {
            if (rule is null || rule.Pattern is null)
            {
                throw new JsonException("A danmu regex rule must contain a pattern.");
            }
            writer.WriteStartObject();
            writer.WriteString(nameof(DanmuRegexRuleSettings.Pattern), rule.Pattern);
            writer.WriteBoolean(nameof(DanmuRegexRuleSettings.BlockDisplay), rule.BlockDisplay);
            writer.WriteBoolean(nameof(DanmuRegexRuleSettings.BlockTranslation), rule.BlockTranslation);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<DanmuRegexRuleSettings> ReadRules(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array, "Rules");
        var result = new List<DanmuRegexRuleSettings>();
        foreach (var entry in element.EnumerateArray())
        {
            RequireKind(entry, JsonValueKind.Object, "Rule");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var rule = new DanmuRegexRuleSettings();
            var hasPattern = false;
            foreach (var property in entry.EnumerateObject())
            {
                switch (property.Name)
                {
                    case nameof(DanmuRegexRuleSettings.Pattern):
                        RequireUnique(names, property.Name);
                        RequireKind(property.Value, JsonValueKind.String, property.Name);
                        rule.Pattern = property.Value.GetString()!;
                        hasPattern = true;
                        break;
                    case nameof(DanmuRegexRuleSettings.BlockDisplay):
                        RequireUnique(names, property.Name);
                        rule.BlockDisplay = ReadBoolean(property.Value, property.Name);
                        break;
                    case nameof(DanmuRegexRuleSettings.BlockTranslation):
                        RequireUnique(names, property.Name);
                        rule.BlockTranslation = ReadBoolean(property.Value, property.Name);
                        break;
                }
            }
            if (!hasPattern)
            {
                throw new JsonException("A danmu regex rule must contain a pattern.");
            }
            result.Add(rule);
        }
        return result;
    }

    private static List<string> ReadPatterns(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array, "Patterns");
        var result = new List<string>();
        foreach (var entry in element.EnumerateArray())
        {
            RequireKind(entry, JsonValueKind.String, "Pattern");
            result.Add(entry.GetString()!);
        }
        return result;
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"{name} must be a boolean.");
        }
        return element.GetBoolean();
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string name)
    {
        if (element.ValueKind != kind)
        {
            throw new JsonException($"{name} must have JSON kind {kind}.");
        }
    }

    private static void RequireUnique(HashSet<string> names, string name)
    {
        if (!names.Add(name))
        {
            throw new JsonException($"{name} cannot be repeated.");
        }
    }
}
