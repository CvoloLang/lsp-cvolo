using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Shared reader for a <c>workspace/didChangeConfiguration</c> payload. Both shapes a client may send
/// are understood: the fully qualified dotted keys (<c>cvolo.section.name</c>) and the nested
/// <c>cvolo</c> section. Anything absent or of the wrong type is treated as absent, never guessed, so
/// a malformed payload cannot silently replace an effective setting.
/// </summary>
internal static class ConfigurationReader
{
    /// <summary>
    /// Reads one raw setting value, trying the fully qualified key first, then the section object the
    /// client may already have nested under <c>cvolo</c>. <paramref name="remaining"/> bounds how many
    /// such container levels are unwrapped, so a self-referential payload cannot loop.
    /// </summary>
    public static JToken? ReadToken(JObject settings, string section, string name, int remaining = 2)
    {
        if (TryReadToken(settings, $"cvolo.{section}.{name}", out JToken? qualified))
        {
            return qualified;
        }

        if (TryReadToken(settings, section, out JToken? sectionToken)
            && sectionToken is JObject nested
            && TryReadToken(nested, name, out JToken? nestedToken))
        {
            return nestedToken;
        }

        if (remaining > 0
            && TryReadToken(settings, "cvolo", out JToken? container)
            && container is JObject scoped)
        {
            return ReadToken(scoped, section, name, remaining - 1);
        }

        return null;
    }

    /// <summary>
    /// Reads one object member by its exact key first, then case-insensitively, so a client's casing
    /// choice never hides a setting.
    /// </summary>
    public static bool TryReadToken(JObject container, string key, out JToken? token)
    {
        JToken? exact = container[key];
        if (exact is not null)
        {
            token = exact;
            return true;
        }

        foreach (JProperty property in container.Properties())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                token = property.Value;
                return true;
            }
        }

        token = null;
        return false;
    }

    /// <summary>
    /// Only a real JSON boolean is accepted. A value of another type is treated as absent rather than
    /// guessed, so a malformed configuration notification cannot silently flip a default.
    /// </summary>
    public static bool? AsBool(JToken? token) => token?.Type == JTokenType.Boolean ? token.Value<bool>() : null;

    /// <summary>
    /// Only a real JSON integer that fits in an <see cref="int"/> is accepted. A float, string, null,
    /// boolean or out-of-range number is treated as absent rather than coerced, so a malformed value
    /// cannot silently replace the effective one.
    /// </summary>
    public static int? AsInt(JToken? token)
    {
        if (token?.Type != JTokenType.Integer)
        {
            return null;
        }

        long value = token.Value<long>();
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }
}
