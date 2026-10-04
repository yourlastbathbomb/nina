#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;

namespace System.Windows.Media {

    /// <summary>
    /// The 141 WPF named colours (same names and ARGB values as WPF's KnownColor table; generated from the
    /// standard SVG/X11 colour set, which System.Drawing.KnownColor Transparent..YellowGreen also carries).
    /// </summary>
    public sealed class Colors {

        private Colors() {
        }

        public static Color AliceBlue => FromUInt32(0xFFF0F8FFu);

        public static Color AntiqueWhite => FromUInt32(0xFFFAEBD7u);

        public static Color Aqua => FromUInt32(0xFF00FFFFu);

        public static Color Aquamarine => FromUInt32(0xFF7FFFD4u);

        public static Color Azure => FromUInt32(0xFFF0FFFFu);

        public static Color Beige => FromUInt32(0xFFF5F5DCu);

        public static Color Bisque => FromUInt32(0xFFFFE4C4u);

        public static Color Black => FromUInt32(0xFF000000u);

        public static Color BlanchedAlmond => FromUInt32(0xFFFFEBCDu);

        public static Color Blue => FromUInt32(0xFF0000FFu);

        public static Color BlueViolet => FromUInt32(0xFF8A2BE2u);

        public static Color Brown => FromUInt32(0xFFA52A2Au);

        public static Color BurlyWood => FromUInt32(0xFFDEB887u);

        public static Color CadetBlue => FromUInt32(0xFF5F9EA0u);

        public static Color Chartreuse => FromUInt32(0xFF7FFF00u);

        public static Color Chocolate => FromUInt32(0xFFD2691Eu);

        public static Color Coral => FromUInt32(0xFFFF7F50u);

        public static Color CornflowerBlue => FromUInt32(0xFF6495EDu);

        public static Color Cornsilk => FromUInt32(0xFFFFF8DCu);

        public static Color Crimson => FromUInt32(0xFFDC143Cu);

        public static Color Cyan => FromUInt32(0xFF00FFFFu);

        public static Color DarkBlue => FromUInt32(0xFF00008Bu);

        public static Color DarkCyan => FromUInt32(0xFF008B8Bu);

        public static Color DarkGoldenrod => FromUInt32(0xFFB8860Bu);

        public static Color DarkGray => FromUInt32(0xFFA9A9A9u);

        public static Color DarkGreen => FromUInt32(0xFF006400u);

        public static Color DarkKhaki => FromUInt32(0xFFBDB76Bu);

        public static Color DarkMagenta => FromUInt32(0xFF8B008Bu);

        public static Color DarkOliveGreen => FromUInt32(0xFF556B2Fu);

        public static Color DarkOrange => FromUInt32(0xFFFF8C00u);

        public static Color DarkOrchid => FromUInt32(0xFF9932CCu);

        public static Color DarkRed => FromUInt32(0xFF8B0000u);

        public static Color DarkSalmon => FromUInt32(0xFFE9967Au);

        public static Color DarkSeaGreen => FromUInt32(0xFF8FBC8Fu);

        public static Color DarkSlateBlue => FromUInt32(0xFF483D8Bu);

        public static Color DarkSlateGray => FromUInt32(0xFF2F4F4Fu);

        public static Color DarkTurquoise => FromUInt32(0xFF00CED1u);

        public static Color DarkViolet => FromUInt32(0xFF9400D3u);

        public static Color DeepPink => FromUInt32(0xFFFF1493u);

        public static Color DeepSkyBlue => FromUInt32(0xFF00BFFFu);

        public static Color DimGray => FromUInt32(0xFF696969u);

        public static Color DodgerBlue => FromUInt32(0xFF1E90FFu);

        public static Color Firebrick => FromUInt32(0xFFB22222u);

        public static Color FloralWhite => FromUInt32(0xFFFFFAF0u);

        public static Color ForestGreen => FromUInt32(0xFF228B22u);

        public static Color Fuchsia => FromUInt32(0xFFFF00FFu);

        public static Color Gainsboro => FromUInt32(0xFFDCDCDCu);

        public static Color GhostWhite => FromUInt32(0xFFF8F8FFu);

        public static Color Gold => FromUInt32(0xFFFFD700u);

        public static Color Goldenrod => FromUInt32(0xFFDAA520u);

        public static Color Gray => FromUInt32(0xFF808080u);

        public static Color Green => FromUInt32(0xFF008000u);

        public static Color GreenYellow => FromUInt32(0xFFADFF2Fu);

        public static Color Honeydew => FromUInt32(0xFFF0FFF0u);

        public static Color HotPink => FromUInt32(0xFFFF69B4u);

        public static Color IndianRed => FromUInt32(0xFFCD5C5Cu);

        public static Color Indigo => FromUInt32(0xFF4B0082u);

        public static Color Ivory => FromUInt32(0xFFFFFFF0u);

        public static Color Khaki => FromUInt32(0xFFF0E68Cu);

        public static Color Lavender => FromUInt32(0xFFE6E6FAu);

        public static Color LavenderBlush => FromUInt32(0xFFFFF0F5u);

        public static Color LawnGreen => FromUInt32(0xFF7CFC00u);

        public static Color LemonChiffon => FromUInt32(0xFFFFFACDu);

        public static Color LightBlue => FromUInt32(0xFFADD8E6u);

        public static Color LightCoral => FromUInt32(0xFFF08080u);

        public static Color LightCyan => FromUInt32(0xFFE0FFFFu);

        public static Color LightGoldenrodYellow => FromUInt32(0xFFFAFAD2u);

        public static Color LightGray => FromUInt32(0xFFD3D3D3u);

        public static Color LightGreen => FromUInt32(0xFF90EE90u);

        public static Color LightPink => FromUInt32(0xFFFFB6C1u);

        public static Color LightSalmon => FromUInt32(0xFFFFA07Au);

        public static Color LightSeaGreen => FromUInt32(0xFF20B2AAu);

        public static Color LightSkyBlue => FromUInt32(0xFF87CEFAu);

        public static Color LightSlateGray => FromUInt32(0xFF778899u);

        public static Color LightSteelBlue => FromUInt32(0xFFB0C4DEu);

        public static Color LightYellow => FromUInt32(0xFFFFFFE0u);

        public static Color Lime => FromUInt32(0xFF00FF00u);

        public static Color LimeGreen => FromUInt32(0xFF32CD32u);

        public static Color Linen => FromUInt32(0xFFFAF0E6u);

        public static Color Magenta => FromUInt32(0xFFFF00FFu);

        public static Color Maroon => FromUInt32(0xFF800000u);

        public static Color MediumAquamarine => FromUInt32(0xFF66CDAAu);

        public static Color MediumBlue => FromUInt32(0xFF0000CDu);

        public static Color MediumOrchid => FromUInt32(0xFFBA55D3u);

        public static Color MediumPurple => FromUInt32(0xFF9370DBu);

        public static Color MediumSeaGreen => FromUInt32(0xFF3CB371u);

        public static Color MediumSlateBlue => FromUInt32(0xFF7B68EEu);

        public static Color MediumSpringGreen => FromUInt32(0xFF00FA9Au);

        public static Color MediumTurquoise => FromUInt32(0xFF48D1CCu);

        public static Color MediumVioletRed => FromUInt32(0xFFC71585u);

        public static Color MidnightBlue => FromUInt32(0xFF191970u);

        public static Color MintCream => FromUInt32(0xFFF5FFFAu);

        public static Color MistyRose => FromUInt32(0xFFFFE4E1u);

        public static Color Moccasin => FromUInt32(0xFFFFE4B5u);

        public static Color NavajoWhite => FromUInt32(0xFFFFDEADu);

        public static Color Navy => FromUInt32(0xFF000080u);

        public static Color OldLace => FromUInt32(0xFFFDF5E6u);

        public static Color Olive => FromUInt32(0xFF808000u);

        public static Color OliveDrab => FromUInt32(0xFF6B8E23u);

        public static Color Orange => FromUInt32(0xFFFFA500u);

        public static Color OrangeRed => FromUInt32(0xFFFF4500u);

        public static Color Orchid => FromUInt32(0xFFDA70D6u);

        public static Color PaleGoldenrod => FromUInt32(0xFFEEE8AAu);

        public static Color PaleGreen => FromUInt32(0xFF98FB98u);

        public static Color PaleTurquoise => FromUInt32(0xFFAFEEEEu);

        public static Color PaleVioletRed => FromUInt32(0xFFDB7093u);

        public static Color PapayaWhip => FromUInt32(0xFFFFEFD5u);

        public static Color PeachPuff => FromUInt32(0xFFFFDAB9u);

        public static Color Peru => FromUInt32(0xFFCD853Fu);

        public static Color Pink => FromUInt32(0xFFFFC0CBu);

        public static Color Plum => FromUInt32(0xFFDDA0DDu);

        public static Color PowderBlue => FromUInt32(0xFFB0E0E6u);

        public static Color Purple => FromUInt32(0xFF800080u);

        public static Color Red => FromUInt32(0xFFFF0000u);

        public static Color RosyBrown => FromUInt32(0xFFBC8F8Fu);

        public static Color RoyalBlue => FromUInt32(0xFF4169E1u);

        public static Color SaddleBrown => FromUInt32(0xFF8B4513u);

        public static Color Salmon => FromUInt32(0xFFFA8072u);

        public static Color SandyBrown => FromUInt32(0xFFF4A460u);

        public static Color SeaGreen => FromUInt32(0xFF2E8B57u);

        public static Color SeaShell => FromUInt32(0xFFFFF5EEu);

        public static Color Sienna => FromUInt32(0xFFA0522Du);

        public static Color Silver => FromUInt32(0xFFC0C0C0u);

        public static Color SkyBlue => FromUInt32(0xFF87CEEBu);

        public static Color SlateBlue => FromUInt32(0xFF6A5ACDu);

        public static Color SlateGray => FromUInt32(0xFF708090u);

        public static Color Snow => FromUInt32(0xFFFFFAFAu);

        public static Color SpringGreen => FromUInt32(0xFF00FF7Fu);

        public static Color SteelBlue => FromUInt32(0xFF4682B4u);

        public static Color Tan => FromUInt32(0xFFD2B48Cu);

        public static Color Teal => FromUInt32(0xFF008080u);

        public static Color Thistle => FromUInt32(0xFFD8BFD8u);

        public static Color Tomato => FromUInt32(0xFFFF6347u);

        public static Color Transparent => FromUInt32(0x00FFFFFFu);

        public static Color Turquoise => FromUInt32(0xFF40E0D0u);

        public static Color Violet => FromUInt32(0xFFEE82EEu);

        public static Color Wheat => FromUInt32(0xFFF5DEB3u);

        public static Color White => FromUInt32(0xFFFFFFFFu);

        public static Color WhiteSmoke => FromUInt32(0xFFF5F5F5u);

        public static Color Yellow => FromUInt32(0xFFFFFF00u);

        public static Color YellowGreen => FromUInt32(0xFF9ACD32u);

        internal static readonly Dictionary<string, uint> ByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) {
            ["AliceBlue"] = 0xFFF0F8FFu,
            ["AntiqueWhite"] = 0xFFFAEBD7u,
            ["Aqua"] = 0xFF00FFFFu,
            ["Aquamarine"] = 0xFF7FFFD4u,
            ["Azure"] = 0xFFF0FFFFu,
            ["Beige"] = 0xFFF5F5DCu,
            ["Bisque"] = 0xFFFFE4C4u,
            ["Black"] = 0xFF000000u,
            ["BlanchedAlmond"] = 0xFFFFEBCDu,
            ["Blue"] = 0xFF0000FFu,
            ["BlueViolet"] = 0xFF8A2BE2u,
            ["Brown"] = 0xFFA52A2Au,
            ["BurlyWood"] = 0xFFDEB887u,
            ["CadetBlue"] = 0xFF5F9EA0u,
            ["Chartreuse"] = 0xFF7FFF00u,
            ["Chocolate"] = 0xFFD2691Eu,
            ["Coral"] = 0xFFFF7F50u,
            ["CornflowerBlue"] = 0xFF6495EDu,
            ["Cornsilk"] = 0xFFFFF8DCu,
            ["Crimson"] = 0xFFDC143Cu,
            ["Cyan"] = 0xFF00FFFFu,
            ["DarkBlue"] = 0xFF00008Bu,
            ["DarkCyan"] = 0xFF008B8Bu,
            ["DarkGoldenrod"] = 0xFFB8860Bu,
            ["DarkGray"] = 0xFFA9A9A9u,
            ["DarkGreen"] = 0xFF006400u,
            ["DarkKhaki"] = 0xFFBDB76Bu,
            ["DarkMagenta"] = 0xFF8B008Bu,
            ["DarkOliveGreen"] = 0xFF556B2Fu,
            ["DarkOrange"] = 0xFFFF8C00u,
            ["DarkOrchid"] = 0xFF9932CCu,
            ["DarkRed"] = 0xFF8B0000u,
            ["DarkSalmon"] = 0xFFE9967Au,
            ["DarkSeaGreen"] = 0xFF8FBC8Fu,
            ["DarkSlateBlue"] = 0xFF483D8Bu,
            ["DarkSlateGray"] = 0xFF2F4F4Fu,
            ["DarkTurquoise"] = 0xFF00CED1u,
            ["DarkViolet"] = 0xFF9400D3u,
            ["DeepPink"] = 0xFFFF1493u,
            ["DeepSkyBlue"] = 0xFF00BFFFu,
            ["DimGray"] = 0xFF696969u,
            ["DodgerBlue"] = 0xFF1E90FFu,
            ["Firebrick"] = 0xFFB22222u,
            ["FloralWhite"] = 0xFFFFFAF0u,
            ["ForestGreen"] = 0xFF228B22u,
            ["Fuchsia"] = 0xFFFF00FFu,
            ["Gainsboro"] = 0xFFDCDCDCu,
            ["GhostWhite"] = 0xFFF8F8FFu,
            ["Gold"] = 0xFFFFD700u,
            ["Goldenrod"] = 0xFFDAA520u,
            ["Gray"] = 0xFF808080u,
            ["Green"] = 0xFF008000u,
            ["GreenYellow"] = 0xFFADFF2Fu,
            ["Honeydew"] = 0xFFF0FFF0u,
            ["HotPink"] = 0xFFFF69B4u,
            ["IndianRed"] = 0xFFCD5C5Cu,
            ["Indigo"] = 0xFF4B0082u,
            ["Ivory"] = 0xFFFFFFF0u,
            ["Khaki"] = 0xFFF0E68Cu,
            ["Lavender"] = 0xFFE6E6FAu,
            ["LavenderBlush"] = 0xFFFFF0F5u,
            ["LawnGreen"] = 0xFF7CFC00u,
            ["LemonChiffon"] = 0xFFFFFACDu,
            ["LightBlue"] = 0xFFADD8E6u,
            ["LightCoral"] = 0xFFF08080u,
            ["LightCyan"] = 0xFFE0FFFFu,
            ["LightGoldenrodYellow"] = 0xFFFAFAD2u,
            ["LightGray"] = 0xFFD3D3D3u,
            ["LightGreen"] = 0xFF90EE90u,
            ["LightPink"] = 0xFFFFB6C1u,
            ["LightSalmon"] = 0xFFFFA07Au,
            ["LightSeaGreen"] = 0xFF20B2AAu,
            ["LightSkyBlue"] = 0xFF87CEFAu,
            ["LightSlateGray"] = 0xFF778899u,
            ["LightSteelBlue"] = 0xFFB0C4DEu,
            ["LightYellow"] = 0xFFFFFFE0u,
            ["Lime"] = 0xFF00FF00u,
            ["LimeGreen"] = 0xFF32CD32u,
            ["Linen"] = 0xFFFAF0E6u,
            ["Magenta"] = 0xFFFF00FFu,
            ["Maroon"] = 0xFF800000u,
            ["MediumAquamarine"] = 0xFF66CDAAu,
            ["MediumBlue"] = 0xFF0000CDu,
            ["MediumOrchid"] = 0xFFBA55D3u,
            ["MediumPurple"] = 0xFF9370DBu,
            ["MediumSeaGreen"] = 0xFF3CB371u,
            ["MediumSlateBlue"] = 0xFF7B68EEu,
            ["MediumSpringGreen"] = 0xFF00FA9Au,
            ["MediumTurquoise"] = 0xFF48D1CCu,
            ["MediumVioletRed"] = 0xFFC71585u,
            ["MidnightBlue"] = 0xFF191970u,
            ["MintCream"] = 0xFFF5FFFAu,
            ["MistyRose"] = 0xFFFFE4E1u,
            ["Moccasin"] = 0xFFFFE4B5u,
            ["NavajoWhite"] = 0xFFFFDEADu,
            ["Navy"] = 0xFF000080u,
            ["OldLace"] = 0xFFFDF5E6u,
            ["Olive"] = 0xFF808000u,
            ["OliveDrab"] = 0xFF6B8E23u,
            ["Orange"] = 0xFFFFA500u,
            ["OrangeRed"] = 0xFFFF4500u,
            ["Orchid"] = 0xFFDA70D6u,
            ["PaleGoldenrod"] = 0xFFEEE8AAu,
            ["PaleGreen"] = 0xFF98FB98u,
            ["PaleTurquoise"] = 0xFFAFEEEEu,
            ["PaleVioletRed"] = 0xFFDB7093u,
            ["PapayaWhip"] = 0xFFFFEFD5u,
            ["PeachPuff"] = 0xFFFFDAB9u,
            ["Peru"] = 0xFFCD853Fu,
            ["Pink"] = 0xFFFFC0CBu,
            ["Plum"] = 0xFFDDA0DDu,
            ["PowderBlue"] = 0xFFB0E0E6u,
            ["Purple"] = 0xFF800080u,
            ["Red"] = 0xFFFF0000u,
            ["RosyBrown"] = 0xFFBC8F8Fu,
            ["RoyalBlue"] = 0xFF4169E1u,
            ["SaddleBrown"] = 0xFF8B4513u,
            ["Salmon"] = 0xFFFA8072u,
            ["SandyBrown"] = 0xFFF4A460u,
            ["SeaGreen"] = 0xFF2E8B57u,
            ["SeaShell"] = 0xFFFFF5EEu,
            ["Sienna"] = 0xFFA0522Du,
            ["Silver"] = 0xFFC0C0C0u,
            ["SkyBlue"] = 0xFF87CEEBu,
            ["SlateBlue"] = 0xFF6A5ACDu,
            ["SlateGray"] = 0xFF708090u,
            ["Snow"] = 0xFFFFFAFAu,
            ["SpringGreen"] = 0xFF00FF7Fu,
            ["SteelBlue"] = 0xFF4682B4u,
            ["Tan"] = 0xFFD2B48Cu,
            ["Teal"] = 0xFF008080u,
            ["Thistle"] = 0xFFD8BFD8u,
            ["Tomato"] = 0xFFFF6347u,
            ["Transparent"] = 0x00FFFFFFu,
            ["Turquoise"] = 0xFF40E0D0u,
            ["Violet"] = 0xFFEE82EEu,
            ["Wheat"] = 0xFFF5DEB3u,
            ["White"] = 0xFFFFFFFFu,
            ["WhiteSmoke"] = 0xFFF5F5F5u,
            ["Yellow"] = 0xFFFFFF00u,
            ["YellowGreen"] = 0xFF9ACD32u,
        };

        internal static Color FromUInt32(uint argb) {
            return Color.FromArgb((byte)((argb & 0xff000000) >> 24), (byte)((argb & 0x00ff0000) >> 16), (byte)((argb & 0x0000ff00) >> 8), (byte)(argb & 0x000000ff));
        }
    }
}
