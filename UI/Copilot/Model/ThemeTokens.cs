#nullable enable
// The copilot pane's colour tokens, as ONE table (AI-311, plan item #27).
//
// WPF-free (strings only) so it is unit-tested without Revit or a dispatcher.
// Each token has the same key in XAML ({DynamicResource Cp.Ink}) and here;
// CopilotTheme turns this table into a ResourceDictionary per theme
// (CopilotTheme.NewThemeDictionary) and swaps it. Before this, the same values
// lived in three places that had to be kept by hand: CopilotTokens.xaml (light),
// a private Palette dictionary in CopilotTheme.cs (light and dark), and
// CopilotColors' light -> dark hex maps for the code-built views. The code-built
// views now look tokens up by key (CopilotColors.Token), and ThemeTokensTests
// fails on any hex colour literal in UI/Copilot outside this file and CopilotTokens.xaml.
// ThemeTokensTests pins this table to CopilotTokens.xaml, so XAML and C# cannot
// drift apart.
//
// Dark is the default (decision D5); light is the alternate.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ThemeTokens
    {
        /// <summary>D5: the pane opens dark; light is the alternate.</summary>
        public const bool DefaultDark = true;

        /// <summary>Token key -> (light hex, dark hex). Solid brushes only; gradients are
        /// <see cref="AccentGrad"/> and <see cref="AccentGradHover"/>.</summary>
        // Light column = v6-panel palette (docs/design/bina-copilot-v6-panel.dc.html):
        // paper #f7f7f6 ground, white surfaces, ink #22242a, accent #2a69c6,
        // divider 13% ink. Dark column = Slate (docs/design/copilot-panel-slate.dc.html).
        public static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Solid =
            new Dictionary<string, (string Light, string Dark)>
            {
                ["Cp.Bg"]            = ("#ffffff", "#131d2b"),
                ["Cp.Sunken"]        = ("#f0f1f7", "#0c1420"),
                ["Cp.Menu"]          = ("#ffffff", "#1a2433"),
                ["Cp.PanelBg"]       = ("#f7f7f6", "#0c1420"),
                ["Cp.Ink"]           = ("#22242a", "#e8eef6"),
                ["Cp.Ink2"]          = ("#1c1e26", "#f4f7fb"),
                ["Cp.Text"]          = ("#22242a", "#e8eef6"),
                ["Cp.Muted"]         = ("#5d6170", "#8a94a6"),
                ["Cp.Faint"]         = ("#9397ab", "#6b768a"),
                ["Cp.Line"]          = ("#2122242A", "#12FFFFFF"),
                ["Cp.Hair2"]         = ("#3D22242A", "#24FFFFFF"),
                ["Cp.LineSoft"]      = ("#1522242A", "#0DFFFFFF"),
                ["Cp.Hover"]         = ("#0D22242A", "#0DFFFFFF"),
                ["Cp.UserBubble"]    = ("#ffffff", "#222e40"),
                ["Cp.Blue"]          = ("#2a69c6", "#60a5fa"),
                ["Cp.Accent"]        = ("#2a69c6", "#60a5fa"),
                ["Cp.BlueHover"]     = ("#22549e", "#83b8fb"),
                ["Cp.BlueSoft"]      = ("#1A2A69C6", "#2660A5FA"),
                ["Cp.BlueText"]      = ("#22549e", "#9cc3f7"),
                ["Cp.AccentContrast"] = ("#ffffff", "#0c1420"),
                ["Cp.Green"]         = ("#2f9a72", "#34d399"),
                ["Cp.Amber"]         = ("#d98a2b", "#fbbf24"),
                ["Cp.Red"]           = ("#d95757", "#f87171"),
                ["Cp.Meter"]         = ("#d98a2b", "#f59e0b"),
                // Slash-command tool types: FIXED design hex, identical in both themes
                // (badgeColor() is theme-independent; a white icon sits on the saturated
                // tile and reads on light or dark). *Bg = exact 13% tint.
                ["Cp.Tool.Det"]      = ("#0d9488", "#0d9488"),
                ["Cp.Tool.Ai"]       = ("#7c3aed", "#7c3aed"),
                ["Cp.Tool.Rep"]      = ("#d97706", "#d97706"),
                ["Cp.Tool.DetBg"]    = ("#210d9488", "#210d9488"),
                ["Cp.Tool.AiBg"]     = ("#217c3aed", "#217c3aed"),
                ["Cp.Tool.RepBg"]    = ("#21d97706", "#21d97706"),
                ["Cp.Pin"]           = ("#f5a623", "#f5a623"),
                ["Cp.Purple"]        = ("#2a69c6", "#60a5fa"),
                ["Cp.PurpleSoft"]    = ("#edf3fd", "#2660A5FA"),
                ["Cp.PurpleLine"]    = ("#a9c6f4", "#3360A5FA"),
                ["Cp.PurpleDeep"]    = ("#1a3f76", "#cfe0fb"),
                ["Cp.CodeBg"]        = ("#f0f1f7", "#0c1420"),
                ["Cp.CodeFg"]        = ("#22549e", "#9cc3f7"),
                ["Cp.TabBadgeBg"]    = ("#f0f1f7", "#12FFFFFF"),
                ["Cp.Tier1Bg"]       = ("#d2f3e4", "#1F34D399"),
                ["Cp.Tier1Fg"]       = ("#143528", "#34d399"),
                ["Cp.Tier2Bg"]       = ("#d9e6fa", "#2660A5FA"),
                ["Cp.Tier2Fg"]       = ("#1a3f76", "#60a5fa"),
                ["Cp.OkBg"]          = ("#d2f3e4", "#1F34D399"),
                ["Cp.CodeFgAlt"]     = ("#22549e", "#9cc3f7"),

                // ---- Added with dark-by-default (AI-311). These had a light value in CopilotTokens.xaml
                // and no dark value anywhere, so a dark pane drew them light on a dark ground. Dark values are
                // taken from the token they equal in light (same role), else from the light -> dark map the
                // code-built views already used (CopilotColors). DERIVED, not from a dark design file:
                // needs a design review (see PR).
                // Success / warning / issue surfaces
                ["Cp.SuccessLn"]     = ("#a8e6cd", "#3334d399"),
                ["Cp.IssueBg"]       = ("#fef2f2", "#26ef4444"),
                ["Cp.IssueLn"]       = ("#fecaca", "#40ef4444"),
                ["Cp.IssueFg"]       = ("#b91c1c", "#fca5a5"),
                ["Cp.IssueFg2"]      = ("#7f1d1d", "#fca5a5"),
                ["Cp.AmberBg"]       = ("#fffbeb", "#1Af59e0b"),
                ["Cp.AmberLn"]       = ("#fde68a", "#40f59e0b"),
                ["Cp.AmberFg"]       = ("#92400e", "#fbbf24"),
                // Command-tile tints (Bg = tint, Fg = glyph)
                ["Cp.Tile.BlueBg"]   = ("#dbeafe", "#2660a5fa"), ["Cp.Tile.BlueFg"]   = ("#1d4ed8", "#60a5fa"),
                ["Cp.Tile.AmberBg"]  = ("#fef3c7", "#26f59e0b"), ["Cp.Tile.AmberFg"]  = ("#a16207", "#fbbf24"),
                ["Cp.Tile.GreenBg"]  = ("#dcfce7", "#1f34d399"), ["Cp.Tile.GreenFg"]  = ("#15803d", "#6ee7b7"),
                ["Cp.Tile.VioletBg"] = ("#ede9fe", "#268b5cf6"), ["Cp.Tile.VioletFg"] = ("#6d28d9", "#93c5fd"),
                ["Cp.Tile.SkyBg"]    = ("#e0f2fe", "#260ea5e9"), ["Cp.Tile.SkyFg"]    = ("#0369a1", "#7dd3fc"),
                ["Cp.Tile.RedBg"]    = ("#fee2e2", "#26ef4444"), ["Cp.Tile.RedFg"]    = ("#b91c1c", "#fca5a5"),
                ["Cp.Tile.IndigoBg"] = ("#e0e7ff", "#266366f1"), ["Cp.Tile.IndigoFg"] = ("#4338ca", "#a5b4fc"),
                ["Cp.Tile.SlateBg"]  = ("#f1f5f9", "#12ffffff"), ["Cp.Tile.SlateFg"]  = ("#475569", "#c3cdda"),
                // Reasoning trail and activity card
                ["Cp.Reasoning.Ink"]            = ("#1c1e26", "#f4f7fb"),
                ["Cp.Reasoning.InkHover"]       = ("#2c2f3a", "#ffffff"),
                ["Cp.Reasoning.TextPrimary"]    = ("#22242a", "#e8eef6"),
                ["Cp.Reasoning.TextSecondary"]  = ("#4a4e5c", "#c3cdda"),
                ["Cp.Reasoning.Text"]           = ("#75798c", "#8a94a6"),
                ["Cp.Reasoning.TextMuted"]      = ("#75798c", "#8a94a6"),
                ["Cp.Reasoning.TextRowCount"]   = ("#5d6170", "#8a94a6"),
                ["Cp.Reasoning.TextFaint"]      = ("#9397ab", "#6b768a"),
                ["Cp.Reasoning.TextFaint2"]     = ("#9397ab", "#6b768a"),
                ["Cp.Reasoning.Border"]         = ("#2122242A", "#12FFFFFF"),
                ["Cp.Reasoning.Border2"]        = ("#2122242A", "#12FFFFFF"),
                ["Cp.Reasoning.Border3"]        = ("#2122242A", "#12FFFFFF"),
                ["Cp.Reasoning.RailLine"]       = ("#2122242A", "#12FFFFFF"),
                ["Cp.Reasoning.BorderSubtle"]   = ("#1522242A", "#0DFFFFFF"),
                ["Cp.Reasoning.BorderSubtle2"]  = ("#1522242A", "#0DFFFFFF"),
                ["Cp.Reasoning.Surface"]        = ("#ffffff", "#131d2b"),
                ["Cp.Reasoning.SurfaceSunken"]  = ("#73FFFFFF", "#0DFFFFFF"),
                ["Cp.Reasoning.SurfaceSunken2"] = ("#f0f1f7", "#0c1420"),
                ["Cp.Reasoning.Hover"]          = ("#0D22242A", "#0DFFFFFF"),
                ["Cp.Reasoning.Hover2"]         = ("#0722242A", "#08FFFFFF"),
                ["Cp.Reasoning.Badge"]          = ("#f0f1f7", "#12FFFFFF"),
                ["Cp.Reasoning.BarTrack"]       = ("#d9dce9", "#24FFFFFF"),
                ["Cp.Reasoning.Accent"]         = ("#2a69c6", "#60a5fa"),
                ["Cp.Reasoning.Success"]        = ("#2f9a72", "#34d399"),
                ["Cp.Reasoning.WarnBg"]         = ("#f7e8d2", "#26f59e0b"),
                ["Cp.Reasoning.WarnFg"]         = ("#8a5a1e", "#fbbf24"),
                // Result proportion bars (system colour hints): saturated, read on both grounds.
                ["Cp.System.Supply"]  = ("#2563eb", "#2563eb"),
                ["Cp.System.Return"]  = ("#dc2626", "#dc2626"),
                ["Cp.System.Exhaust"] = ("#eab308", "#eab308"),
                ["Cp.System.None"]    = ("#d4d4da", "#d4d4da"),

                // The running card (design Running.dc.html, dark board): scoped set, see CopilotTokens.xaml.
                ["Cp.Run.Bg"]        = ("#f7f7f6", "#0E1014"),
                ["Cp.Run.Surface"]   = ("#ffffff", "#15181E"),
                ["Cp.Run.Surface2"]  = ("#f0f1f7", "#1B1F27"),
                ["Cp.Run.Raised"]    = ("#e4e6ee", "#232833"),
                ["Cp.Run.Composer"]  = ("#ffffff", "#121419"),
                ["Cp.Run.Line"]      = ("#2122242A", "#12FFFFFF"),
                ["Cp.Run.LineStrong"] = ("#3D22242A", "#1FFFFFFF"),
                ["Cp.Run.LineButton"] = ("#5222242A", "#29FFFFFF"),
                ["Cp.Run.Text"]      = ("#22242a", "#EDEFF3"),
                ["Cp.Run.Text2"]     = ("#3a3d48", "#D3D7DF"),
                ["Cp.Run.Muted"]     = ("#5d6170", "#A6ADBA"),
                ["Cp.Run.Faint"]     = ("#7a7f90", "#8A91A0"),
                ["Cp.Run.Accent"]    = ("#7fa800", "#D2F65A"),
                ["Cp.Run.AccentHalo"] = ("#247fa800", "#24D2F65A"),
                ["Cp.Run.AccentRing"] = ("#387fa800", "#38D2F65A"),
                ["Cp.Run.AccentInk"] = ("#ffffff", "#0E1014"),
                ["Cp.Run.Success"]   = ("#2f9a72", "#5BD69A"),
                ["Cp.Run.SuccessBg"] = ("#242f9a72", "#245BD69A"),
                ["Cp.Run.Warn"]      = ("#b9770e", "#F5B841"),
                ["Cp.Run.WarnBg"]    = ("#fdf3dc", "#221C10"),
                ["Cp.Run.WarnText"]  = ("#6b4a10", "#E9DCC0"),
                ["Cp.Run.Info"]      = ("#2a69c6", "#5EA2FF"),
                ["Cp.Run.Dash"]      = ("#4022242A", "#38FFFFFF"),

                // ---- Added when the code-built views moved off hex literals (AI-311). Each pair is the colour that
                // view drew before, in each theme (CopilotColors' light -> v6 and light -> dark maps applied), so
                // neither theme changes. Fixed ones (same in both themes) were drawn unthemed before.
                ["Cp.Raised"]        = ("#f7f7f6", "#16202e"),   // card ground (rate / feedback cards)
                ["Cp.AccentWash"]    = ("#edf3fd", "#182434"),   // history "current" card
                ["Cp.TextSecondary"] = ("#4a4e5c", "#c3cdda"),
                ["Cp.ErrorText"]     = ("#c24545", "#fca5a5"),
                ["Cp.Notice"]        = ("#d98a2b", "#f2a33a"),   // attention notice card
                ["Cp.NoticeBg"]      = ("#1ad97706", "#1ff2a33a"),
                ["Cp.Brand"]         = ("#2a69c6", "#2a69c6"),   // light accent, base of the fixed accent tints
                ["Cp.BrandGreen"]    = ("#2f9a72", "#2f9a72"),   // progress bar gradient end
                ["Cp.BrandTint10"]   = ("#1A2A69C6", "#1A2A69C6"),
                ["Cp.BrandTint12"]   = ("#1F2A69C6", "#1F2A69C6"),
                ["Cp.BrandLine35"]   = ("#592A69C6", "#592A69C6"),
                ["Cp.ChipOnBg"]      = ("#1a1d4ed8", "#1a1d4ed8"),
                ["Cp.RowLine"]       = ("#0f22242a", "#0f22242a"),
                ["Cp.RowHover"]      = ("#f7f9fb", "#f7f9fb"),
                ["Cp.ConfirmHover"]  = ("#0da271", "#0da271"),
                ["Cp.WaitBg"]        = ("#fbf0dd", "#fbf0dd"),   // run status "needs input" / "awaiting confirmation"
                ["Cp.WaitFg"]        = ("#6b4a12", "#6b4a12"),
                ["Cp.IndexingVeil"]  = ("#F7FFFFFF", "#F7FFFFFF"),
                ["Cp.Scrim"]         = ("#66000000", "#66000000"),
                ["Cp.ScrimSoft"]     = ("#3D22242A", "#3D22242A"),
                ["Cp.WhatsApp"]      = ("#25D366", "#25D366"),   // brand mark
                ["Cp.Chart.Cyan"]    = ("#0891b2", "#0891b2"),
                // Rating sheet stars
                ["Cp.Rating.Fill"]   = ("#f59e0b", "#f59e0b"),
                ["Cp.Rating.Hi"]     = ("#FFE07A", "#FFE07A"),
                ["Cp.Rating.Mid"]    = ("#FBB72B", "#FBB72B"),
                ["Cp.Rating.Lo"]     = ("#E8941A", "#E8941A"),
                ["Cp.Rating.Text"]   = ("#E0941A", "#E0941A"),
                // In-canvas overlay (drawn over the Revit view, always dark) and its mark colours
                ["Cp.Mark.Green"]    = ("#4CC77F", "#4CC77F"),
                ["Cp.Mark.Blue"]     = ("#6B9BFF", "#6B9BFF"),
                ["Cp.Mark.Red"]      = ("#FF7B6B", "#FF7B6B"),
                ["Cp.Mark.Amber"]    = ("#E7A84A", "#E7A84A"),
                ["Cp.Overlay.Pill"]  = ("#EB0B0D12", "#EB0B0D12"),
                ["Cp.Overlay.Ground"] = ("#F20F1522", "#F20F1522"),
                ["Cp.Overlay.Raised"] = ("#1C2535", "#1C2535"),
                ["Cp.Overlay.Line"]  = ("#2F3B50", "#2F3B50"),
                ["Cp.Overlay.Ink"]   = ("#0F1522", "#0F1522"),
                ["Cp.Overlay.Text"]  = ("#F3F6FB", "#F3F6FB"),
                ["Cp.Overlay.Note"]  = ("#C9D2E0", "#C9D2E0"),
                ["Cp.Overlay.Muted"] = ("#9AA5B8", "#9AA5B8"),
                ["Cp.Overlay.Faint"] = ("#6F7A8C", "#6F7A8C"),
                ["Cp.Overlay.Link"]  = ("#8FB4FF", "#8FB4FF"),
                ["Cp.Overlay.Dot"]   = ("#fbbf24", "#fbbf24"),
                ["Cp.Overlay.Btn"]   = ("#1AFFFFFF", "#1AFFFFFF"),
                // Blocked screen padlock illustration
                ["Cp.Padlock.ShackleBack"] = ("#3f74d6", "#3f74d6"),
                ["Cp.Padlock.ShackleHi"] = ("#9cc6ff", "#9cc6ff"),
                ["Cp.Padlock.ShackleLo"] = ("#4f8ef0", "#4f8ef0"),
                ["Cp.Padlock.BodyHi"] = ("#7cb4ff", "#7cb4ff"),
                ["Cp.Padlock.BodyMid"] = ("#4a8bf0", "#4a8bf0"),
                ["Cp.Padlock.BodyLo"] = ("#2766d6", "#2766d6"),
                ["Cp.Padlock.Key"] = ("#dbe9ff", "#dbe9ff"),
                ["Cp.Padlock.Glow"] = ("#3b82f6", "#3b82f6"),
            };

        /// <summary>Tokens that read the same in both themes (not shifted for dark).</summary>
        public static readonly IReadOnlyCollection<string> ThemeIndependent = new[]
        {
            "Cp.Tool.Det", "Cp.Tool.Ai", "Cp.Tool.Rep", "Cp.Tool.DetBg", "Cp.Tool.AiBg", "Cp.Tool.RepBg", "Cp.Pin",
            "Cp.System.Supply", "Cp.System.Return", "Cp.System.Exhaust", "Cp.System.None",
            "Cp.Brand", "Cp.BrandGreen", "Cp.BrandTint10", "Cp.BrandTint12", "Cp.BrandLine35", "Cp.ChipOnBg",
            "Cp.RowLine", "Cp.RowHover", "Cp.ConfirmHover", "Cp.WaitBg", "Cp.WaitFg", "Cp.IndexingVeil",
            "Cp.Scrim", "Cp.ScrimSoft", "Cp.WhatsApp", "Cp.Chart.Cyan",
            "Cp.Rating.Fill", "Cp.Rating.Hi", "Cp.Rating.Mid", "Cp.Rating.Lo", "Cp.Rating.Text",
            "Cp.Mark.Green", "Cp.Mark.Blue", "Cp.Mark.Red", "Cp.Mark.Amber",
            "Cp.Overlay.Pill", "Cp.Overlay.Ground", "Cp.Overlay.Raised", "Cp.Overlay.Line", "Cp.Overlay.Ink",
            "Cp.Overlay.Text", "Cp.Overlay.Note", "Cp.Overlay.Muted", "Cp.Overlay.Faint", "Cp.Overlay.Link",
            "Cp.Overlay.Dot", "Cp.Overlay.Btn",
            "Cp.Padlock.ShackleBack", "Cp.Padlock.ShackleHi", "Cp.Padlock.ShackleLo", "Cp.Padlock.BodyHi", "Cp.Padlock.BodyMid", "Cp.Padlock.BodyLo", "Cp.Padlock.Key", "Cp.Padlock.Glow",
        };

        // 135° two-stop gradients.
        public static (string From, string To) AccentGrad(bool dark) => dark ? ("#83b8fb", "#60a5fa") : ("#3b7ee0", "#2a69c6");
        public static (string From, string To) AccentGradHover(bool dark) => dark ? ("#95c3fc", "#74b0fb") : ("#4f8ee6", "#3b7ee0");

        /// <summary>The v6 brand diamond: blue, jade, gold at 135°. Same in both themes.</summary>
        public static readonly IReadOnlyList<(string Hex, double Offset)> LogoStops = new[]
        {
            ("#3b7ee0", 0.0), ("#2fbf9a", 0.6), ("#e0a53b", 1.0),
        };

        /// <summary>The assistant avatar's star (design gstarLogo) at 135°. Same in both themes.</summary>
        public static readonly IReadOnlyList<(string Hex, double Offset)> StarLogoStops = new[]
        {
            ("#7dd6ff", 0.0), ("#3b8ef7", 0.45), ("#1d5fe0", 1.0),
        };

        /// <summary>The token's LIGHT hex, for model data that carries a colour as a string
        /// (catalog tiles, highlight marks). Throws on an unknown key so a typo fails the tests.</summary>
        public static string LightHex(string key) =>
            Solid.TryGetValue(key, out var v) ? v.Light : throw new KeyNotFoundException("No colour token " + key);

        /// <summary>The token's hex for a theme; null when the key is not a token.</summary>
        public static string? Hex(string key, bool dark) =>
            Solid.TryGetValue(key, out var v) ? (dark ? v.Dark : v.Light) : null;

        /// <summary>#RGB / #RRGGBB / #AARRGGBB.</summary>
        public static bool IsHex(string? s) =>
            !string.IsNullOrEmpty(s) && s![0] == '#' && (s.Length == 4 || s.Length == 7 || s.Length == 9)
            && s.Skip(1).All(Uri.IsHexDigit);
    }
}
