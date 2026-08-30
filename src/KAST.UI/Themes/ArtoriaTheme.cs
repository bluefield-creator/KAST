using MudBlazor;

namespace KAST.UI.Themes;

/// <summary>
/// The Artoria design system's MudBlazor adapter — Blue Kepi edition,
/// built from the visual-forensics dossier's region-mode pixel samples.
/// Single permanent theme; no light mode. Uniform indigo (#3B3A86 family,
/// violet-shifted) is the structure, tunic white carries actions, crimson
/// is lining revealed on state change, brass/cream trace the edges, and
/// the user accent is the staff ribbon — the one unissued color.
/// Canonical token values live in wwwroot/css/artoria-tokens.css — the two
/// files duplicate ~25 hex values and must be kept in sync.
/// Usage rules: docs/DESIGN.md.
/// </summary>
public static class ArtoriaTheme
{
    /// <summary>Inside the coat at night.</summary>
    public static PaletteDark Regulation => new()
    {
        Primary                  = "#FFFFFF",  // the tunic — white carries actions
        PrimaryContrastText      = "#1C1C4A",
        PrimaryDarken            = "#E4E4E4",  // fold
        PrimaryLighten           = "#FFFFFF",
        Secondary                = "#DDA8D6",  // the ribbon — overridden by user accent
        SecondaryContrastText    = "#131331",
        Tertiary                 = "#EBCB95",  // cream piping (trim grade of brass)
        TertiaryContrastText     = "#131331",
        Info                     = "#91BDE0",  // the cabochon stone
        InfoContrastText         = "#131331",
        Success                  = "#8FC3A8",  // her eye-green, text grade
        SuccessContrastText      = "#131331",
        Warning                  = "#DDA389",  // hair-tip apricot — warmth, not gold
        WarningContrastText      = "#131331",
        Error                    = "#DD4F4D",  // lit crimson lining
        ErrorContrastText        = "#131331",
        Background               = "#131331",
        BackgroundGray           = "#161638",
        Surface                  = "#191940",
        AppbarBackground         = "#0E0E24",
        AppbarText               = "#F7F6FA",
        DrawerBackground         = "#0E0E24",
        DrawerText               = "#F7F6FA",
        DrawerIcon               = "#AAA6C9",
        TextPrimary              = "#F7F6FA",
        TextSecondary            = "#AAA6C9",
        TextDisabled             = "#71738D",
        ActionDefault            = "#AAA6C9",
        ActionDisabled           = "#71738D",
        ActionDisabledBackground = "#22225866",
        LinesDefault             = "#34336E",
        TableLines               = "#34336E",
        Divider                  = "#34336E",
        DividerLight             = "#222258",
        OverlayDark              = "#0E0E2480",
        GrayLight                = "#3B3A86",
        GrayLighter              = "#222258",
    };

    private static readonly string[] Display = ["Cormorant Garamond", "Georgia", "Times New Roman", "serif"];
    private static readonly string[] Sans    = ["Source Sans 3", "Segoe UI", "system-ui", "sans-serif"];

    public static MudTheme Build() => new()
    {
        // The app renders with IsDarkMode always true — only PaletteDark matters.
        PaletteDark = Regulation,
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "3px" },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = Sans,
                FontSize   = ".875rem",
                FontWeight = "400",
                LineHeight = "1.45",
            },
            // Serif display faces — never below 1.25rem (Cormorant gets frail)
            H1 = new H1Typography { FontFamily = Display, FontSize = "2.75rem",  FontWeight = "600", LineHeight = "1.2",  LetterSpacing = "0" },
            H2 = new H2Typography { FontFamily = Display, FontSize = "2.375rem", FontWeight = "600", LineHeight = "1.2",  LetterSpacing = "0" },
            H3 = new H3Typography { FontFamily = Display, FontSize = "2.125rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "0" },
            H4 = new H4Typography { FontFamily = Display, FontSize = "1.625rem", FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "0" },
            // Sans below the serif floor
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "0" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "1rem",    FontWeight = "600", LineHeight = "1.4",  LetterSpacing = ".005em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "1rem",      FontWeight = "600", LineHeight = "1.4" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = ".875rem",   FontWeight = "600", LineHeight = "1.4" },
            Body1     = new Body1Typography     { FontFamily = Sans, FontSize = ".875rem",   FontWeight = "400", LineHeight = "1.45" },
            Body2     = new Body2Typography     { FontFamily = Sans, FontSize = ".8125rem",  FontWeight = "400", LineHeight = "1.45" },
            Button    = new ButtonTypography
            {
                FontFamily    = Sans,
                FontSize      = ".875rem",
                FontWeight    = "600",
                LetterSpacing = ".01em",
                TextTransform = "none",   // sentence case — no Material shouting
            },
            Caption  = new CaptionTypography  { FontFamily = Sans, FontSize = ".75rem", FontWeight = "400", LineHeight = "1.4" },
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = ".75rem", FontWeight = "600", LetterSpacing = ".06em", TextTransform = "uppercase" },
        },
    };
}
