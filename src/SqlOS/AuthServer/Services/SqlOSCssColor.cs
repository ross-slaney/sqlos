using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Strict CSS color grammar for values persisted into AuthPage and email branding and later
/// interpolated into public HTML. The allowlist is deliberate: canonical hex, rgb/rgba, hsl/hsla,
/// and <c>transparent</c>. Markup, URLs, rule delimiters, and executable CSS are rejected.
/// </summary>
public static class SqlOSCssColor
{
    public const int MaxLength = 32;

    public static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        if (!TryNormalize(value, out var normalized))
        {
            throw new InvalidOperationException(
                $"{name} must be a supported CSS color (#RGB, #RRGGBB, #RRGGBBAA, rgb/rgba, hsl/hsla, or transparent).");
        }

        return normalized;
    }

    public static string Render(string? value, string fallback)
        => TryNormalize(value, out var normalized) ? normalized : fallback;

    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength || !IsAllowedCharset(trimmed))
        {
            return false;
        }

        if (trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "transparent";
            return true;
        }

        if (trimmed[0] == '#')
        {
            return TryNormalizeHex(trimmed, out normalized);
        }

        return TryNormalizeFunction(trimmed, out normalized);
    }

    public static bool TryGetRgb(string? value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        if (!TryNormalize(value, out var normalized))
        {
            return false;
        }

        if (normalized[0] == '#')
        {
            return TryReadHexRgb(normalized, out red, out green, out blue);
        }

        if (normalized.StartsWith("rgb", StringComparison.Ordinal))
        {
            return TryReadRgbFunction(normalized, out red, out green, out blue);
        }

        if (normalized.StartsWith("hsl", StringComparison.Ordinal))
        {
            return TryReadHslFunction(normalized, out red, out green, out blue);
        }

        return false;
    }

    private static bool IsAllowedCharset(string value)
    {
        foreach (var character in value)
        {
            if (character is (>= '0' and <= '9')
                or (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or '#' or '(' or ')' or ',' or '%' or '.' or ' ')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryNormalizeHex(string value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value.Length is not (4 or 7 or 9))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        if (value.Length == 4)
        {
            normalized = string.Create(7, value, static (span, source) =>
            {
                span[0] = '#';
                span[1] = char.ToLowerInvariant(source[1]);
                span[2] = char.ToLowerInvariant(source[1]);
                span[3] = char.ToLowerInvariant(source[2]);
                span[4] = char.ToLowerInvariant(source[2]);
                span[5] = char.ToLowerInvariant(source[3]);
                span[6] = char.ToLowerInvariant(source[3]);
            });
            return true;
        }

        normalized = string.Create(value.Length, value, static (span, source) =>
        {
            span[0] = '#';
            for (var index = 1; index < source.Length; index++)
            {
                span[index] = char.ToLowerInvariant(source[index]);
            }
        });
        return true;
    }

    private static bool TryNormalizeFunction(string value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        var open = value.IndexOf('(');
        if (open <= 0 || value[^1] != ')')
        {
            return false;
        }

        var name = value[..open].Trim().ToLowerInvariant();
        var inner = value[(open + 1)..^1];
        var parts = inner.Split(',');
        if (name is "rgb" or "hsl")
        {
            if (parts.Length != 3)
            {
                return false;
            }
        }
        else if (name is "rgba" or "hsla")
        {
            if (parts.Length != 4)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (name[0] == 'r')
        {
            if (!TryParseByte(parts[0], out var red) ||
                !TryParseByte(parts[1], out var green) ||
                !TryParseByte(parts[2], out var blue))
            {
                return false;
            }

            if (name.Length == 3)
            {
                normalized = string.Create(CultureInfo.InvariantCulture, $"rgb({red},{green},{blue})");
                return true;
            }

            if (!TryParseAlpha(parts[3], out var alpha))
            {
                return false;
            }

            normalized = string.Create(CultureInfo.InvariantCulture, $"rgba({red},{green},{blue},{alpha})");
            return true;
        }

        if (!TryParseHue(parts[0], out var hue) ||
            !TryParsePercent(parts[1], out var saturation) ||
            !TryParsePercent(parts[2], out var lightness))
        {
            return false;
        }

        if (name.Length == 3)
        {
            normalized = string.Create(CultureInfo.InvariantCulture, $"hsl({hue},{saturation}%,{lightness}%)");
            return true;
        }

        if (!TryParseAlpha(parts[3], out var hslAlpha))
        {
            return false;
        }

        normalized = string.Create(CultureInfo.InvariantCulture, $"hsla({hue},{saturation}%,{lightness}%,{hslAlpha})");
        return true;
    }

    private static bool TryParseByte(ReadOnlySpan<char> text, out int value)
    {
        text = text.Trim();
        value = 0;
        if (text.IsEmpty || text.Length > 3)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value is >= 0 and <= 255;
    }

    private static bool TryParseHue(ReadOnlySpan<char> text, out int value)
    {
        text = text.Trim();
        value = 0;
        if (text.IsEmpty || text.Length > 3)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value is >= 0 and <= 360;
    }

    private static bool TryParsePercent(ReadOnlySpan<char> text, out int value)
    {
        text = text.Trim();
        value = 0;
        if (text.Length < 2 || text[^1] != '%')
        {
            return false;
        }

        return TryParseHue(text[..^1], out value) && value <= 100;
    }

    private static bool TryParseAlpha(ReadOnlySpan<char> text, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        text = text.Trim();
        if (text.IsEmpty || text.Length > 5)
        {
            return false;
        }

        var dot = text.IndexOf('.');
        if (dot < 0)
        {
            if (text.Length != 1 || text[0] is not ('0' or '1'))
            {
                return false;
            }

            normalized = text[0] == '1' ? "1" : "0";
            return true;
        }

        if (dot == 0 || dot != 1 || text.Length < 3 || text.Length > 5)
        {
            return false;
        }

        if (text[0] is not ('0' or '1'))
        {
            return false;
        }

        for (var index = 2; index < text.Length; index++)
        {
            if (text[index] is < '0' or > '9')
            {
                return false;
            }
        }

        if (text[0] == '1')
        {
            for (var index = 2; index < text.Length; index++)
            {
                if (text[index] != '0')
                {
                    return false;
                }
            }

            normalized = "1";
            return true;
        }

        normalized = text.ToString();
        return true;
    }

    private static bool TryReadHexRgb(string value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        return int.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
            && int.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
            && int.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }

    private static bool TryReadRgbFunction(string value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        var open = value.IndexOf('(');
        var parts = value[(open + 1)..^1].Split(',');
        return TryParseByte(parts[0], out red)
            && TryParseByte(parts[1], out green)
            && TryParseByte(parts[2], out blue);
    }

    private static bool TryReadHslFunction(string value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        var open = value.IndexOf('(');
        var parts = value[(open + 1)..^1].Split(',');
        if (!TryParseHue(parts[0], out var hue)
            || !TryParsePercent(parts[1], out var saturation)
            || !TryParsePercent(parts[2], out var lightness))
        {
            return false;
        }

        HslToRgb(hue, saturation / 100d, lightness / 100d, out red, out green, out blue);
        return true;
    }

    private static void HslToRgb(int hue, double saturation, double lightness, out int red, out int green, out int blue)
    {
        if (saturation == 0)
        {
            var gray = (int)Math.Round(lightness * 255, MidpointRounding.AwayFromZero);
            red = gray;
            green = gray;
            blue = gray;
            return;
        }

        var q = lightness < 0.5
            ? lightness * (1 + saturation)
            : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;
        var hk = hue / 360d;
        red = ToByte(HueToRgb(p, q, hk + (1d / 3d)));
        green = ToByte(HueToRgb(p, q, hk));
        blue = ToByte(HueToRgb(p, q, hk - (1d / 3d)));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1d / 6d)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1d / 2d)
        {
            return q;
        }

        if (t < 2d / 3d)
        {
            return p + ((q - p) * ((2d / 3d) - t) * 6);
        }

        return p;
    }

    private static int ToByte(double value)
        => (int)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
