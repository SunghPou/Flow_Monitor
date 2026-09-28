using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// Blender's eyedropper icon as a 32x32 mask, so the icon painted in the picker's
/// dropper field and the Windows pointer are the same art.
/// Source: release/datafiles/icons_svg/eyedropper.svg (blender/blender, GPL-2.0-or-later,
/// reference only) flattened to a bitmap: 32 rows of 32 bits, MSB = leftmost pixel.
/// The bulb sits at the top right and the tip at the bottom left, as drawn there.
/// </summary>
public static class BlenderDropper
{
    public const int Size = 32;

    /// <summary>Hot spot: the pipette tip, so the sampled pixel sits under the point.</summary>
    public const int HotX = 6;
    public const int HotY = 29;

    const string Bits =
        "0000000000000000000001e0000003f800000ff800001ff800003ffc00067ffc" +
        "0007fffc0003fff80001fff00001fff00003ffe000073fc0000e1f80001c1f00" +
        "00383f000070738000e0e1c001c1c0c00383800003060000030c000007180000" +
        "0ef000001de000000b8000000700000002000000000000000000000000000000";

    static readonly uint[] Rows = Parse();

    static uint[] Parse()
    {
        var rows = new uint[Size];
        // 8 hex digits per row: 32 bits, most significant nibble is the leftmost pixel.
        for (int y = 0; y < Size; y++)
            for (int b = 0; b < Size / 4; b++)
            {
                int digit = HexDigit(Bits[(y * (Size / 4)) + b]);
                rows[y] |= (uint)digit << (28 - b * 4);
            }
        return rows;
    }

    static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0,
    };

    /// <summary>True when the icon covers this pixel of the 32x32 mask.</summary>
    public static bool Lit(int x, int y)
        => (uint)x < Size && (uint)y < Size
        && (Rows[y] & (1u << (31 - x))) != 0;
}
