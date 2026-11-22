using System;
using System.Globalization;

namespace CompAI;

/// <summary>A bounded display-coordinate link. Parsing never opens a map or an external URL.</summary>
internal readonly record struct MapLocationLink(uint TerritoryId, uint MapId, decimal X, decimal Y)
{
    internal const string Scheme = "compai-map://";
    internal const int MaxUrlLength = 96;
    internal bool IsValid => TerritoryId is > 0 and <= ushort.MaxValue && MapId is > 0 and <= ushort.MaxValue &&
        X is >= 1m and <= 100m && Y is >= 1m and <= 100m &&
        decimal.Round(X, 3) == X && decimal.Round(Y, 3) == Y;

    internal string ToUrl()
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(MapLocationLink));
        return FormattableString.Invariant($"{Scheme}{TerritoryId}/{MapId}?x={X:0.###}&y={Y:0.###}");
    }

    internal string CoordinateLabel => FormattableString.Invariant($"X: {X:0.###}, Y: {Y:0.###}");

    internal static bool TryParse(string? text, out MapLocationLink link)
    {
        link = default;
        if (text is null || text.Length > MaxUrlLength || !text.StartsWith(Scheme, StringComparison.Ordinal)) return false;
        var remainder = text.AsSpan(Scheme.Length);
        var slash = remainder.IndexOf('/');
        if (slash < 1 || !Id(remainder[..slash], out var territory)) return false;
        remainder = remainder[(slash + 1)..];
        var query = remainder.IndexOf("?x=", StringComparison.Ordinal);
        if (query < 1 || !Id(remainder[..query], out var map)) return false;
        remainder = remainder[(query + 3)..];
        var separator = remainder.IndexOf("&y=", StringComparison.Ordinal);
        if (separator < 1 || !Coordinate(remainder[..separator], out var x) ||
            !Coordinate(remainder[(separator + 3)..], out var y)) return false;
        link = new(territory, map, x, y);
        return link.IsValid;
    }

    private static bool Id(ReadOnlySpan<char> value, out uint id)
    {
        id = 0;
        return value.Length is > 0 and <= 5 && value[0] != '0' && Digits(value) &&
            uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id <= ushort.MaxValue;
    }

    private static bool Coordinate(ReadOnlySpan<char> value, out decimal coordinate)
    {
        coordinate = 0;
        if (value.Length is < 1 or > 7 || value[0] == '0') return false;
        var dot = value.IndexOf('.');
        if (dot >= 0)
        {
            if (dot is < 1 or > 3 || value.Length - dot - 1 is < 1 or > 3 ||
                !Digits(value[..dot]) || !Digits(value[(dot + 1)..])) return false;
        }
        else if (value.Length > 3 || !Digits(value)) return false;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out coordinate) &&
            coordinate is >= 1m and <= 100m;
    }

    private static bool Digits(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (c is < '0' or > '9') return false;
        return true;
    }
}
