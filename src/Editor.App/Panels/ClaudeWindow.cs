using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// View → Claude: the bridge's switch and status, the command that connects Claude Code, what Claude did lately, the
/// user's marks for Claude (K over the Render Window) with notes, and Claude's notes on the view.
/// </summary>
internal sealed class ClaudeWindow
{
    private bool _open;

    public void Show() => _open = true;

    public static string ConnectCommand(int port) => $"claude mcp add --transport http stb-editor http://127.0.0.1:{port}/mcp";

    /// <summary>The bridge's switches (also in View → Settings).</summary>
    public static void DrawSwitches(EditorApp app)
    {
        var settings = app.Settings;
        var on = settings.ClaudeBridge;
        if (ImGui.Checkbox(T("Let Claude Code work with the editor (local MCP server)"), ref on)) { settings.ClaudeBridge = on; settings.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Claude Code on this computer can look at the Render Window, move the camera, list, select and edit objects, read and change record fields, search and leave notes on the view. Only programs on this computer can connect."));
        ImGui.BeginDisabled(!on);
        var edits = settings.ClaudeBridgeEdits;
        if (ImGui.Checkbox(T("Claude may change things (each change can be undone; Claude never saves)"), ref edits)) { settings.ClaudeBridgeEdits = edits; settings.Save(); }
        var port = settings.ClaudeBridgePort;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        if (Fields.Int(T("Port") + "##claudeport", port, out var newPort) && newPort is >= 1024 and <= 65535) { settings.ClaudeBridgePort = newPort; settings.Save(); }
        ImGui.EndDisabled();
        var status = app.Bridge.Status;
        ImGui.TextColored(app.Bridge.Running ? Theme.Accent : status.StartsWith("error", StringComparison.Ordinal) ? Theme.Error : Theme.TextDim,
            app.Bridge.Running ? TF($"Listening: {status}") : status == "off" ? T("Off") : status);
        if (on)
        {
            var command = ConnectCommand(settings.ClaudeBridgePort);
            ImGui.TextColored(Theme.TextDim, T("Connect Claude Code once (in a terminal):"));
            ImGui.TextUnformatted(command);
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Copy") + "##claudecmd")) ImGui.SetClipboardText(command);
        }
    }

    public void Draw(EditorApp app)
    {
        if (!_open) return;
        ImGui.SetNextWindowSize(app.Scaled(620, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title("Claude") + "###claudewindow", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            DrawSwitches(app);

            var window = app.RenderWindow;
            ImGui.SeparatorText(TF($"Marks for Claude ({window.Marks.Count})"));
            ImGui.TextColored(Theme.TextDim, T("K over the Render Window marks the point under the cursor; write what you want there. Claude reads them with get_user_marks."));
            for (var i = 0; i < window.Marks.Count; i++)
            {
                var m = window.Marks[i];
                ImGui.PushID(m.Number);
                ImGui.TextColored(Theme.Accent, $"{m.Number}");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 10);
                ImGui.InputTextWithHint("##note", T("what to do here"), ref m.Note, 400);
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, m.ReferenceLabel ?? $"{m.Position.X:0} {m.Position.Y:0}");
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) { window.Marks.RemoveAt(i); ImGui.PopID(); break; }
                ImGui.PopID();
            }
            if (window.Marks.Count > 0 && ImGui.SmallButton(T("Remove all marks"))) window.Marks.Clear();

            ImGui.SeparatorText(TF($"Claude's notes on the view ({window.Annotations.Count})"));
            if (window.Annotations.Count > 0 && ImGui.SmallButton(T("Clear the notes"))) window.Annotations.Clear();

            ImGui.SeparatorText(T("What Claude did"));
            if (ImGui.BeginChild("##claudehistory", Vector2.Zero, ImGuiChildFlags.Borders))
            {
                foreach (var (at, tool, summary, error) in app.Bridge.History)
                {
                    ImGui.TextColored(Theme.TextDim, at.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextColored(error ? Theme.Error : Theme.Text, tool);
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.TextDim, summary);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }
}
