using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Changes Claude proposes instead of making: the objects show where they would go (tinted), and the user applies them as
/// one undoable step or rejects them (Claude window, or Enter / Backspace over the view). One proposal at a time; a new one
/// replaces the pending one.
/// </summary>
internal sealed partial class RenderWindow
{
    public sealed class Proposal
    {
        public required int Id { get; init; }
        public required string Title { get; init; }
        public required List<(SceneObject Object, RefState Before, RefState After)> Changes { get; init; }
        public string Status { get; set; } = "pending";
        public TaskCompletionSource<string> Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private int _nextProposal = 1;
    public Proposal? PendingProposal { get; private set; }
    public List<Proposal> PastProposals { get; } = [];

    /// <summary>Shows the changes in the view (not recorded yet) and waits for the user.</summary>
    public Proposal Propose(string title, IReadOnlyList<(SceneObject Object, RefState After)> changes)
    {
        if (PendingProposal is not null) Decide(false, "replaced by a newer proposal");
        var proposal = new Proposal
        {
            Id = _nextProposal++, Title = title,
            Changes = changes.Select(c => (c.Object, c.Object.State, c.After)).ToList(),
        };
        foreach (var (obj, _, after) in proposal.Changes) obj.Apply(after);
        if (_renderer is not null) foreach (var c in proposal.Changes) _renderer.Options.Proposed.Add(c.Object);
        PendingProposal = proposal;
        return proposal;
    }

    /// <summary>Applies (one undo step) or rejects (puts everything back) the pending proposal.</summary>
    public void Decide(bool apply, string? why = null)
    {
        if (PendingProposal is not { } p) return;
        PendingProposal = null;
        _renderer?.Options.Proposed.Clear();
        var alive = _scene is not null && p.Changes.All(c => _scene.Objects.Contains(c.Object));
        if (!alive) p.Status = "lost: the cell was reloaded";
        else if (apply)
        {
            Commit($"Claude: {p.Title}", p.Changes.ToDictionary(c => c.Object, c => c.Before));
            p.Status = "applied";
        }
        else
        {
            foreach (var (obj, before, _) in p.Changes) obj.Apply(before);
            p.Status = why is null ? "rejected" : $"rejected: {why}";
        }
        PastProposals.Insert(0, p);
        if (PastProposals.Count > 20) PastProposals.RemoveAt(PastProposals.Count - 1);
        p.Decided.TrySetResult(p.Status);
    }

    /// <summary>The banner over the view while a proposal waits (and its keys).</summary>
    private void DrawProposalBanner(ImDrawListPtr draw, System.Numerics.Vector2 origin, System.Numerics.Vector2 size, bool hovered)
    {
        if (PendingProposal is not { } p) return;
        if (_scene is null || !p.Changes.All(c => _scene.Objects.Contains(c.Object))) { Decide(false); return; }
        var text = TF($"Claude proposes: {p.Title} ({p.Changes.Count} object(s), shown tinted) — Enter applies, Backspace rejects (or the Claude window)");
        var textSize = ImGui.CalcTextSize(text);
        var at = origin + new System.Numerics.Vector2((size.X - textSize.X) / 2, size.Y - textSize.Y - 14);
        draw.AddRectFilled(at - new System.Numerics.Vector2(10, 6), at + textSize + new System.Numerics.Vector2(10, 6), 0xE0000000u, 4);
        draw.AddRect(at - new System.Numerics.Vector2(10, 6), at + textSize + new System.Numerics.Vector2(10, 6), ImGui.GetColorU32(Theme.Accent), 4);
        draw.AddText(at, ImGui.GetColorU32(Theme.Accent), text);
        if (hovered && !ImGui.GetIO().WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) Decide(true);
            else if (ImGui.IsKeyPressed(ImGuiKey.Backspace, false)) Decide(false);
        }
    }
}
