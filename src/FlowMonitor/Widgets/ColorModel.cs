using System.Globalization;
using Microsoft.Win32;
using Vortice.Mathematics;

namespace FlowMonitor.Widgets;

/// <summary>Straight RGBA colour plus the HSV conversions the colour picker needs.</summary>
public readonly record struct Rgba
{
    public Rgba(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }

    /// <summary>Parses #RGB, #RRGGBB or bare hex; anything unreadable becomes white.</summary>
    public static Rgba FromHex(string? hex)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6) return new Rgba(255, 255, 255);
        return new Rgba(Hex(h, 0), Hex(h, 2), Hex(h, 4));
    }

    static byte Hex(string h, int at) =>
        byte.Parse(h.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>#RRGGBB, the form widget configs store.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public Hsv ToHsv() => Hsv.FromRgb(R, G, B);

    public Color4 ToColor4() => new(R / 255f, G / 255f, B / 255f, A / 255f);

    public static Rgba FromColor4(Color4 c) => new(
        (byte)Math.Clamp(c.R * 255f, 0f, 255f),
        (byte)Math.Clamp(c.G * 255f, 0f, 255f),
        (byte)Math.Clamp(c.B * 255f, 0f, 255f),
        (byte)Math.Clamp(c.A * 255f, 0f, 255f));
}

/// <summary>Hue in degrees [0,360), saturation and value in [0,1].</summary>
public readonly record struct Hsv(double H, double S, double V)
{
    public Hsv Normalized() => new(
        ((H % 360.0) + 360.0) % 360.0, Math.Clamp(S, 0.0, 1.0), Math.Clamp(V, 0.0, 1.0));

    public (byte R, byte G, byte B) ToRgb()
    {
        var (h, s, v) = Normalized();
        double c = v * s;
        double x = c * (1.0 - Math.Abs(h / 60.0 % 2.0 - 1.0));
        double m = v - c;
        double r = h < 60 ? c : h < 120 ? x : h < 180 ? 0 : h < 240 ? 0 : h < 300 ? x : c;
        double g = h < 60 ? x : h < 120 ? c : h < 180 ? c : h < 240 ? x : h < 300 ? 0 : 0;
        double b = h < 60 ? 0 : h < 120 ? 0 : h < 180 ? x : h < 240 ? c : h < 300 ? c : x;
        return (Byte((r + m) * 255.0), Byte((g + m) * 255.0), Byte((b + m) * 255.0));
    }

    static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0.0, 255.0);

    public static Hsv FromRgb(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min;
        if (d <= 0) return new Hsv(max <= 0 ? 0 : 0, 0, max);
        double h = max switch
        {
            _ when max == rf => 60 * (((gf - bf) / d) % 6),
            _ when max == gf => 60 * ((bf - rf) / d + 2),
            _ => 60 * ((rf - gf) / d + 4),
        };
        return new Hsv(h, d / max, max).Normalized();
    }

}

/// <summary>System theme lookup and the picker's theme-following palette.</summary>
public static class SystemTheme
{
    static bool? _light;

    /// <summary>Forces the theme; null restores the registry read. Selftest only.</summary>
    internal static bool? OverrideLight
    {
        get => _override;
        set { _override = value; _light = null; }
    }
    static bool? _override;

    /// <summary>Re-reads AppsUseLightTheme; call when a picker opens.</summary>
    public static void Refresh() => _light = null;

    /// <summary>True when Windows apps are in light mode. Unreadable means dark.</summary>
    public static bool IsLight()
    {
        if (_override is bool forced) return forced;
        if (_light is bool cached) return cached;
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) { Log.Warn("theme: read failed, assuming dark: " + ex.Message); }
        _light = light;
        return light;
    }

    // #424242 dark, #CCCCCC light: the two card fills the picker must show.
    // Fully opaque: at 0.98 the desktop showed through the popup.
    public static Color4 Card => CardFor(IsLight());
    public static Color4 CardFor(bool light) => light
        ? new Color4(0.800f, 0.800f, 0.800f, 1f)   // #CCCCCC
        : new Color4(0.259f, 0.259f, 0.259f, 1f); // #424242
    public static Color4 Hairline => IsLight() ? new Color4(0f, 0f, 0f, 0.20f)
                                               : new Color4(1f, 1f, 1f, 0.12f);
    public static Color4 Ink => IsLight() ? new Color4(0f, 0f, 0f, 0.87f)
                                          : new Color4(1f, 1f, 1f, 0.92f);
    public static Color4 MutedInk => IsLight() ? new Color4(0f, 0f, 0f, 0.55f)
                                               : new Color4(1f, 1f, 1f, 0.55f);
    public static Color4 Pill => IsLight() ? new Color4(1f, 1f, 1f, 0.70f)
                                          : new Color4(1f, 1f, 1f, 0.10f);
    public static Color4 Field => IsLight() ? new Color4(1f, 1f, 1f, 0.55f)
                                           : new Color4(0f, 0f, 0f, 0.25f);
}
