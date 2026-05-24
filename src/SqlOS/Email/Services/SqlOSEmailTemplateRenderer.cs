using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlOS.Email.Contracts;
using SqlOS.Email.Models;

namespace SqlOS.Email.Services;

public sealed partial class SqlOSEmailTemplateRenderer
{
    public SqlOSRenderedEmailPreview Render(
        SqlOSEmailTemplate template,
        IReadOnlyDictionary<string, object?> variables)
        => Render(
            template.SubjectTemplate,
            template.HtmlBodyTemplate,
            template.TextBodyTemplate,
            variables);

    public SqlOSRenderedEmailPreview Render(
        string subjectTemplate,
        string htmlBodyTemplate,
        string textBodyTemplate,
        IReadOnlyDictionary<string, object?> variables)
    {
        var requiredVariables = ExtractVariables(subjectTemplate, htmlBodyTemplate, textBodyTemplate);
        var missing = requiredVariables
            .Where(variable => !variables.ContainsKey(variable))
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            throw new SqlOSEmailTemplateValidationException(missing);
        }

        return new SqlOSRenderedEmailPreview(
            RenderTemplate(subjectTemplate, variables, htmlEncode: false),
            RenderTemplate(htmlBodyTemplate, variables, htmlEncode: true),
            RenderTemplate(textBodyTemplate, variables, htmlEncode: false),
            requiredVariables);
    }

    public IReadOnlyList<string> ExtractVariables(params string?[] templates)
    {
        var variables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template))
            {
                continue;
            }

            foreach (Match match in PlaceholderRegex().Matches(template))
            {
                variables.Add(match.Groups["name"].Value);
            }
        }

        return variables.ToList();
    }

    public static Dictionary<string, object?> ToDictionary(JsonObject? variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables == null)
        {
            return result;
        }

        foreach (var item in variables)
        {
            result[item.Key] = item.Value;
        }

        return result;
    }

    private static string RenderTemplate(
        string template,
        IReadOnlyDictionary<string, object?> variables,
        bool htmlEncode)
        => PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            var value = variables.TryGetValue(name, out var rawValue)
                ? ConvertVariableValue(rawValue)
                : string.Empty;

            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });

    private static string ConvertVariableValue(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value switch
        {
            JsonValue jsonValue => ConvertJsonNode(jsonValue),
            JsonObject jsonObject => jsonObject.ToJsonString(),
            JsonArray jsonArray => jsonArray.ToJsonString(),
            JsonElement element => ConvertJsonElement(element),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string ConvertJsonNode(JsonValue value)
    {
        if (value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue ? "true" : "false";
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }

    private static string ConvertJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => element.GetRawText()
        };

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
