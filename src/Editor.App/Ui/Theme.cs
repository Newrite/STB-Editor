using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.Data;

namespace StbEditor.App.Ui;

/// <summary>
/// Fonts from the player's own build: the Futura Condensed of STB / Untarnished-style UIs and
/// Sovngarde for headings. They are not shipped with the editor (not ours to redistribute); when
/// the build has none the editor falls back to Segoe UI.
/// </summary>
internal sealed record UiFonts(string? Body, string? Header)
{
    private static readonly string[] BodyNames = ["STB_FuturaCondensed.ttf", "FuturaCondensed.ttf", "Futura Condensed.ttf"];
    private static readonly string[] HeaderNames = ["SovngardeBold.ttf", "Sovngarde.ttf", "SovngardeLight.ttf"];

    public static UiFonts Find(GameInstall? game)
    {
        if (game is null) return new UiFonts(null, null);

        var folders = new List<string>
        {
            Path.Combine(game.DataFolder, "SKSE", "Plugins", "fonts"),
            Path.Combine(game.DataFolder, "Interface", "fonts"),
        };

        if (game.Mo2 is { } mo2)
        {
            // EnabledModFolders runs from low to high priority; the winner is searched first.
            foreach (var mod in mo2.EnabledModFolders().Reverse())
            {
                folders.Add(Path.Combine(mod, "SKSE", "Plugins", "fonts"));
                folders.Add(Path.Combine(mod, "Interface", "fonts"));
            }
        }

        string? FindFirst(string[] names)
        {
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var name in names)
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        return new UiFonts(FindFirst(BodyNames), FindFirst(HeaderNames));
    }
}

/// <summary>
/// Monochrome Skyrim look, matching Untarnished UI and the STB settings menu (STB.dll McmUI):
/// near-black panels, light grey labels with white values, rows and buttons lit by translucent
/// white, hairline white separators, square corners.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 Accent = Rgb(235, 235, 235);
    public static readonly Vector4 Text = Rgb(214, 214, 214);
    public static readonly Vector4 TextDim = Rgb(150, 150, 152);
    public static readonly Vector4 Warning = Rgb(222, 180, 100);
    public static readonly Vector4 Error = Rgb(220, 105, 95);
    public static readonly Vector4 Good = Rgb(160, 200, 150);
    public static readonly Vector4 Override = Rgb(190, 175, 220);
    public static readonly Vector4 ButtonActive = White(0.24f);

    private static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    private static Vector4 White(float a) => new(1f, 1f, 1f, a);

    public static void Apply(float scale)
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.FrameRounding = 3;
        style.PopupRounding = 4;
        style.ScrollbarRounding = 2;
        style.GrabRounding = 2;
        style.TabRounding = 0;
        style.WindowBorderSize = 1;
        style.FrameBorderSize = 0;
        style.PopupBorderSize = 1;
        style.TabBorderSize = 0;
        style.WindowPadding = new Vector2(10, 8);
        style.FramePadding = new Vector2(8, 3);
        style.ItemSpacing = new Vector2(8, 5);
        style.ItemInnerSpacing = new Vector2(6, 4);
        style.IndentSpacing = 16;
        style.ScrollbarSize = 10;
        style.GrabMinSize = 9;
        style.CellPadding = new Vector2(6, 2);
        style.WindowTitleAlign = new Vector2(0f, 0.5f);
        style.SeparatorTextBorderSize = 1;
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.ScaleAllSizes(scale);

        var c = style.Colors;
        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = White(0.32f);
        c[(int)ImGuiCol.WindowBg] = Rgb(10, 10, 12, 0.97f);
        c[(int)ImGuiCol.ChildBg] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg] = Rgb(14, 14, 16, 0.98f);
        c[(int)ImGuiCol.Border] = White(0.13f);
        c[(int)ImGuiCol.BorderShadow] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = White(0.06f);
        c[(int)ImGuiCol.FrameBgHovered] = White(0.11f);
        c[(int)ImGuiCol.FrameBgActive] = White(0.16f);
        c[(int)ImGuiCol.TitleBg] = Rgb(6, 6, 7);
        c[(int)ImGuiCol.TitleBgActive] = Rgb(6, 6, 7);
        c[(int)ImGuiCol.TitleBgCollapsed] = Rgb(6, 6, 7);
        c[(int)ImGuiCol.MenuBarBg] = Rgb(6, 6, 7);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = White(0.16f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = White(0.30f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = White(0.55f);
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = White(0.75f);
        c[(int)ImGuiCol.SliderGrabActive] = White(0.95f);
        c[(int)ImGuiCol.Button] = White(0.07f);
        c[(int)ImGuiCol.ButtonHovered] = White(0.16f);
        c[(int)ImGuiCol.ButtonActive] = White(0.28f);
        c[(int)ImGuiCol.Header] = White(0.11f);
        c[(int)ImGuiCol.HeaderHovered] = White(0.063f);
        c[(int)ImGuiCol.HeaderActive] = White(0.16f);
        c[(int)ImGuiCol.Separator] = White(0.20f);
        c[(int)ImGuiCol.SeparatorHovered] = White(0.55f);
        c[(int)ImGuiCol.SeparatorActive] = White(0.80f);
        c[(int)ImGuiCol.ResizeGrip] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.ResizeGripHovered] = White(0.40f);
        c[(int)ImGuiCol.ResizeGripActive] = White(0.70f);
        c[(int)ImGuiCol.Tab] = Rgb(8, 8, 10);
        c[(int)ImGuiCol.TabHovered] = White(0.10f);
        c[(int)ImGuiCol.TabSelected] = Rgb(18, 18, 21);
        c[(int)ImGuiCol.TabSelectedOverline] = White(0.85f);
        c[(int)ImGuiCol.TabDimmed] = Rgb(8, 8, 10);
        c[(int)ImGuiCol.TabDimmedSelected] = Rgb(14, 14, 16);
        c[(int)ImGuiCol.TabDimmedSelectedOverline] = White(0.35f);
        c[(int)ImGuiCol.DockingPreview] = White(0.25f);
        c[(int)ImGuiCol.DockingEmptyBg] = Rgb(4, 4, 5);
        c[(int)ImGuiCol.TableHeaderBg] = White(0.05f);
        c[(int)ImGuiCol.TableBorderStrong] = White(0.13f);
        c[(int)ImGuiCol.TableBorderLight] = White(0.07f);
        c[(int)ImGuiCol.TableRowBg] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt] = White(0.018f);
        c[(int)ImGuiCol.TextSelectedBg] = White(0.25f);
        c[(int)ImGuiCol.NavCursor] = White(0.6f);
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0, 0, 0, 0.6f);
        c[(int)ImGuiCol.PlotHistogram] = White(0.8f);
    }
}
