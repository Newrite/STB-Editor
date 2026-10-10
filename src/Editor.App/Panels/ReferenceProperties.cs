using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The Creation Kit's reference dialog, in the Inspector: flags, enable parent, ownership, lock,
/// teleport (with linking both doors), linked references, count/radius. Every change is one undo
/// step on an edited copy of the reference (<see cref="OverrideDraft"/>).
/// </summary>
internal static class ReferenceProperties
{
    private static readonly (string Label, uint Bit, string Tip)[] Flags =
    [
        (T("Persistent"), 0x400, T("Always loaded (needed for scripts, quests, linked refs across cells)")),
        (T("Initially disabled"), 0x800, T("Hidden until something enables it")),
        (T("Visible when distant"), 0x8000, T("Shown in distant LOD")),
        (T("No AI acquire"), 0x2000000, T("NPCs will not pick it up")),
        (T("Don't havok settle"), 0x20000000, T("Stays where placed instead of dropping when the cell loads")),
        (T("No respawn"), 0x40000000, T("Containers/items do not respawn")),
    ];

    /// <summary>A door waiting for its destination to be picked (may be in another cell).</summary>
    private static (SceneObject Door, RefContext Context)? _linking;

    public static bool IsLinking => _linking is not null;

    /// <summary>Called every frame (also with nothing selected): completes a pending door link.</summary>
    public static void Update(EditorApp app)
    {
        if (_linking is not { } link) return;
        if (app.RenderWindow.TakePick(LinkPickId) is { } target)
        {
            _linking = null;
            LinkDoors(app, link.Door, link.Context, target);
            return;
        }
        if (app.RenderWindow.PickRequestId != LinkPickId) _linking = null; // cancelled (Esc)
    }

    private const string LinkPickId = "##teleport-link";

    public static void Draw(EditorApp app, SceneObject obj)
    {
        if (app.Session is not { } session) return;
        var document = app.Document;
        var key = obj.Reference;
        var context = app.RenderWindow.RefContextOf(obj);
        document.RegisterContext(key, context);

        var current = document.Override(key)?.Record as IPlacedGetter ?? obj.Record as IPlacedGetter ?? NewRecord(session, obj);
        if (current is null) return;

        var edited = document.Override(key) is not null;
        ImGui.SeparatorText(edited ? T("Reference  (changed)") : T("Reference"));
        if (app.ActivePlugin is null) ImGui.TextColored(Theme.TextDim, T("Changes are saved into the active file (Ctrl+S asks for one)."));

        void Edit(string name, Action<IPlaced> change)
        {
            var copy = (IPlaced)document.WorkingCopy(key, current);
            change(copy);
            document.CommitEdit(name, copy, RecordPlace.Reference, context);
        }

        DrawFlags(current, Edit);
        DrawEnableParent(app, current, Edit);
        DrawOwnership(app, current, Edit);
        if (current is IPlacedObjectGetter placed)
        {
            if (obj.BaseSignature is "DOOR" or "CONT" or "ACTI" or "FURN") DrawLock(app, placed, Edit);
            if (obj.BaseSignature == "DOOR") DrawTeleport(app, obj, context, placed, Edit);
            DrawExtra(placed, obj, Edit);
        }
        DrawLinkedRefs(app, current, Edit);
        if (ScriptsEditor.Supports(current))
            ScriptsEditor.Draw(app, current, (name, change) => Edit(name, r => change(r)));

        if (edited && ImGui.SmallButton(T("Revert reference properties")))
        {
            var before = document.Override(key)!;
            document.Record(new RevertCommand(before));
        }
        ImGui.Spacing();
    }

    /// <summary>A reference made in this session has no record yet: start from an empty one with its base.</summary>
    private static IPlacedGetter? NewRecord(EditorSession session, SceneObject obj)
    {
        if (obj.BaseSignature is "NPC_" or "LVLN")
        {
            var npc = new PlacedNpc(obj.Reference, session.Game.Release);
            npc.Base.SetTo(obj.Base);
            return npc;
        }
        var placed = new PlacedObject(obj.Reference, session.Game.Release);
        placed.Base.SetTo(obj.Base);
        return placed;
    }

    private static void DrawFlags(IPlacedGetter current, Action<string, Action<IPlaced>> edit)
    {
        var raw = (uint)current.MajorRecordFlagsRaw;
        for (var i = 0; i < Flags.Length; i++)
        {
            var (label, bit, tip) = Flags[i];
            var on = (raw & bit) != 0;
            if (i % 2 == 1) ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.5f + ImGui.GetCursorPosX() * 0);
            if (ImGui.Checkbox(T(label), ref on))
                edit(on ? TF($"Set {T(label)}") : TF($"Clear {T(label)}"), r => r.MajorRecordFlagsRaw = (int)(on ? raw | bit : raw & ~bit));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T(tip));
        }
    }

    private static void DrawEnableParent(EditorApp app, IPlacedGetter current, Action<string, Action<IPlaced>> edit)
    {
        var parent = current switch
        {
            IPlacedObjectGetter o => o.EnableParent,
            IPlacedNpcGetter n => n.EnableParent,
            _ => null,
        };

        if (!ImGui.TreeNodeEx(T("Enable parent"), parent is null ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)) return;
        FormKey? reference = parent?.Reference.FormKeyNullable;
        if (FormField.Draw(app, "enableparent", ref reference, null, references: true))
        {
            edit(reference is null ? T("Remove enable parent") : T("Set enable parent"), r =>
            {
                var value = reference is null ? null : new EnableParent { Flags = parent?.Flags ?? 0 };
                value?.Reference.SetTo(reference!.Value);
                SetEnableParent(r, value);
            });
        }

        if (parent is not null)
        {
            var opposite = parent.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
            if (ImGui.Checkbox(T("Opposite of parent"), ref opposite))
                edit(T("Enable parent: opposite"), r => Mutate(r, p => p.Flags = opposite ? p.Flags | EnableParent.Flag.SetEnableStateToOppositeOfParent : p.Flags & ~EnableParent.Flag.SetEnableStateToOppositeOfParent));
            ImGui.SameLine();
            var popIn = parent.Flags.HasFlag(EnableParent.Flag.PopIn);
            if (ImGui.Checkbox(T("Pop in"), ref popIn))
                edit(T("Enable parent: pop in"), r => Mutate(r, p => p.Flags = popIn ? p.Flags | EnableParent.Flag.PopIn : p.Flags & ~EnableParent.Flag.PopIn));
        }
        ImGui.TreePop();

        static void Mutate(IPlaced r, Action<EnableParent> change)
        {
            var p = r switch { PlacedObject o => o.EnableParent, PlacedNpc n => n.EnableParent, _ => null };
            if (p is not null) change(p);
        }
    }

    private static void SetEnableParent(IPlaced record, EnableParent? value)
    {
        switch (record)
        {
            case PlacedObject o: o.EnableParent = value; break;
            case PlacedNpc n: n.EnableParent = value; break;
        }
    }

    private static void DrawOwnership(EditorApp app, IPlacedGetter current, Action<string, Action<IPlaced>> edit)
    {
        var (owner, rank) = current switch
        {
            IPlacedObjectGetter o => (o.Owner.FormKeyNullable, o.FactionRank),
            IPlacedNpcGetter n => (n.Owner.FormKeyNullable, n.FactionRank),
            _ => (null, null),
        };

        if (!ImGui.TreeNodeEx(T("Ownership"), owner is null ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)) return;
        FormKey? value = owner;
        if (FormField.Draw(app, "owner", ref value, ["NPC_", "FACT"]))
        {
            edit(value is null ? T("Clear owner") : T("Set owner"), r =>
            {
                switch (r)
                {
                    case PlacedObject o:
                        o.Owner.SetTo(value);
                        if (value is null) o.FactionRank = null;
                        break;
                    case PlacedNpc n:
                        n.Owner.SetTo(value);
                        if (value is null) n.FactionRank = null;
                        break;
                }
            });
        }

        if (owner is { } ownerKey && app.Session!.Catalog.Get(ownerKey)?.Signature == "FACT")
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
            if (Fields.Int(T("Faction rank"), rank ?? 0, out var faction))
                edit(T("Faction rank"), r =>
                {
                    if (r is PlacedObject o) o.FactionRank = faction;
                    else if (r is PlacedNpc n) n.FactionRank = faction;
                });
        }
        ImGui.TreePop();
    }

    private static void DrawLock(EditorApp app, IPlacedObjectGetter current, Action<string, Action<IPlaced>> edit)
    {
        var lockData = current.Lock;
        if (!ImGui.TreeNodeEx(T("Lock"), lockData is null ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)) return;

        var locked = lockData is not null;
        if (ImGui.Checkbox(T("Locked"), ref locked))
            edit(locked ? T("Lock") : T("Unlock"), r => ((PlacedObject)r).Lock = locked ? new LockData { Level = LockLevel.Novice } : null);

        if (lockData is not null)
        {
            var levels = Enum.GetValues<LockLevel>();
            var index = Array.IndexOf(levels, lockData.Level);
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
            if (ImGui.BeginCombo(T("Level"), lockData.Level.ToString()))
            {
                for (var i = 0; i < levels.Length; i++)
                {
                    var level = levels[i];
                    if (ImGui.Selectable(level.ToString(), i == index))
                        edit(TF($"Lock level {level}"), r => ((PlacedObject)r).Lock!.Level = level);
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            var leveled = lockData.Flags.HasFlag(LockData.Flag.LeveledLock);
            if (ImGui.Checkbox(T("Leveled"), ref leveled))
                edit(T("Leveled lock"), r => ((PlacedObject)r).Lock!.Flags = leveled ? LockData.Flag.LeveledLock : 0);

            FormKey? keyForm = lockData.Key.FormKeyNullable;
            ImGui.Text(T("Key"));
            if (FormField.Draw(app, "lockkey", ref keyForm, ["KEYM"]))
                edit(keyForm is null ? T("Clear key") : T("Set key"), r => ((PlacedObject)r).Lock!.Key.SetTo(keyForm));
        }
        ImGui.TreePop();
    }

    private static void DrawTeleport(EditorApp app, SceneObject obj, RefContext context, IPlacedObjectGetter current, Action<string, Action<IPlaced>> edit)
    {
        var teleport = current.TeleportDestination;
        if (!ImGui.TreeNodeEx(T("Teleport"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (teleport is null) ImGui.TextColored(Theme.TextDim, T("Not a load door."));
        else
        {
            if (ImGui.Button(app.RenderWindow.IsFindingDoor ? T("Finding the door…") : T("Go to linked door")) && !teleport.Door.IsNull)
                app.RenderWindow.GoToLinkedDoor(app, obj);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Opens the cell of the destination door, camera at its marker (or double-click the door)"));
            ImGui.Text(T("Destination door"));
            FormKey? door = teleport.Door.FormKeyNullable;
            if (FormField.Draw(app, "teleportdoor", ref door, null, references: true) && door is { } d)
                edit(T("Teleport door"), r => ((PlacedObject)r).TeleportDestination!.Door.SetTo(d));

            var position = new Vector3(teleport.Position.X, teleport.Position.Y, teleport.Position.Z);
            var rotation = new Vector3(teleport.Rotation.X, teleport.Rotation.Y, teleport.Rotation.Z) * (180f / MathF.PI);
            var width = ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 6;
            ImGui.SetNextItemWidth(width);
            if (Fields.Float3(T("Marker position"), position, out position, "%.1f"))
                edit(T("Teleport marker"), r => ((PlacedObject)r).TeleportDestination!.Position = new P3Float(position.X, position.Y, position.Z));
            ImGui.SetNextItemWidth(width);
            if (Fields.Float3(T("Marker rotation"), rotation, out rotation, "%.1f°"))
                edit(T("Teleport marker"), r => ((PlacedObject)r).TeleportDestination!.Rotation = new P3Float(rotation.X * MathF.PI / 180f, rotation.Y * MathF.PI / 180f, rotation.Z * MathF.PI / 180f));

            var noAlarm = teleport.Flags.HasFlag(TeleportDestination.Flag.NoAlarm);
            if (ImGui.Checkbox(T("No alarm"), ref noAlarm))
                edit(T("Teleport no alarm"), r => ((PlacedObject)r).TeleportDestination!.Flags = noAlarm ? TeleportDestination.Flag.NoAlarm : 0);

            if (ImGui.SmallButton(T("Remove teleport")))
                edit(T("Remove teleport"), r => ((PlacedObject)r).TeleportDestination = null);
        }

        var linking = _linking is { } l && l.Door == obj;
        if (linking) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button(linking ? T("Click the other door in the Render Window… (Esc cancels)") : T("Link with a door…")))
        {
            if (linking)
            {
                _linking = null;
                app.RenderWindow.RequestPick(LinkPickId);
            }
            else
            {
                _linking = (obj, context);
                app.RenderWindow.RequestPick(LinkPickId);
                Log.Info("teleport: click the destination door (you can open another cell first)");
            }
        }
        if (linking) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Both doors get a teleport to each other, the markers in front of each door (as the CK does)"));
        ImGui.TreePop();
    }

    /// <summary>Each door teleports to the other; markers stand 96 units in front of the target door, facing away from it.</summary>
    private static void LinkDoors(EditorApp app, SceneObject door, RefContext doorContext, SceneObject target)
    {
        if (target.BaseSignature != "DOOR" || target.Reference == door.Reference)
        {
            Log.Warn($"teleport: {target.BaseEditorId} is not another door");
            return;
        }

        var document = app.Document;
        var targetContext = app.RenderWindow.RefContextOf(target);
        document.RegisterContext(target.Reference, targetContext);
        var session = app.Session!;

        IPlaced Edited(SceneObject obj) =>
            (IPlaced)document.WorkingCopy(obj.Reference, document.Override(obj.Reference)?.Record ?? (IMajorRecordGetter?)obj.Record ?? NewRecord(session, obj)!);

        var a = (PlacedObject)Edited(door);
        var b = (PlacedObject)Edited(target);
        a.TeleportDestination = Destination(target);
        b.TeleportDestination = Destination(door);

        var before = new[] { document.Override(door.Reference), document.Override(target.Reference) };
        var after = new[]
        {
            before[0]?.With(a) ?? new OverrideDraft(a, RecordPlace.Reference, doorContext),
            before[1]?.With(b) ?? new OverrideDraft(b, RecordPlace.Reference, targetContext),
        };
        document.Record(new CompositeCommand(TF($"Link doors {door.BaseEditorId} <-> {target.BaseEditorId}"),
        [
            new RecordEditCommand("door", before[0]?.With((IMajorRecord)before[0]!.Record.DeepCopy()), after[0]),
            new RecordEditCommand("door", before[1]?.With((IMajorRecord)before[1]!.Record.DeepCopy()), after[1]),
        ]));
        Log.Info($"teleport: linked {door.Reference} and {target.Reference}");

        static TeleportDestination Destination(SceneObject to)
        {
            var heading = to.Rotation.Z;
            var forward = new Vector3(MathF.Sin(heading), MathF.Cos(heading), 0);
            var position = to.Position + forward * 96f;
            var value = new TeleportDestination
            {
                Position = new P3Float(position.X, position.Y, position.Z),
                Rotation = new P3Float(0, 0, TransformMath.Wrap(heading + MathF.PI)),
            };
            value.Door.SetTo(to.Reference);
            return value;
        }
    }

    private static void DrawExtra(IPlacedObjectGetter current, SceneObject obj, Action<string, Action<IPlaced>> edit)
    {
        if (obj.BaseSignature is "MISC" or "AMMO" or "INGR" or "ALCH" or "BOOK" or "SCRL" or "SLGM" or "KEYM" or "WEAP" or "ARMO")
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
            if (Fields.Int(T("Count"), current.ItemCount ?? 1, out var count) && count >= 1)
                edit("Count", r => ((PlacedObject)r).ItemCount = count == 1 ? null : count);
        }

        if (obj.BaseSignature == "LIGH")
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
            if (Fields.Float(T("Radius"), current.Radius ?? obj.Light?.Radius ?? 0, out var radius, "%.0f"))
                edit(T("Light radius"), r => ((PlacedObject)r).Radius = radius);
        }
    }

    private static void DrawLinkedRefs(EditorApp app, IPlacedGetter current, Action<string, Action<IPlaced>> edit)
    {
        var links = current switch
        {
            IPlacedObjectGetter o => o.LinkedReferences,
            IPlacedNpcGetter n => n.LinkedReferences,
            _ => [],
        };

        if (!ImGui.TreeNodeEx(TF($"Linked references ({links.Count})"), links.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) return;

        for (var i = 0; i < links.Count; i++)
        {
            var index = i;
            ImGui.PushID(i);
            ImGui.TextColored(Theme.TextDim, T("Keyword"));
            FormKey? keyword = links[i].KeywordOrReference.FormKeyNullable;
            if (FormField.Draw(app, $"lk{i}", ref keyword, ["KYWD"]))
                edit(T("Linked ref keyword"), r => List(r)[index].KeywordOrReference.SetTo(keyword));
            ImGui.TextColored(Theme.TextDim, T("Reference"));
            FormKey? reference = links[i].Reference.FormKeyNullable;
            if (FormField.Draw(app, $"lr{i}", ref reference, null, references: true) && reference is { } target)
                edit(T("Linked ref"), r => List(r)[index].Reference.SetTo(target));
            if (ImGui.SmallButton(T("Remove link")))
                edit(T("Remove linked ref"), r => List(r).RemoveAt(index));
            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.SmallButton(T("Add linked reference")))
            edit(T("Add linked ref"), r => List(r).Add(new LinkedReferences()));
        ImGui.TreePop();

        static ExtendedList<LinkedReferences> List(IPlaced r) => r switch
        {
            PlacedObject o => o.LinkedReferences,
            PlacedNpc n => n.LinkedReferences,
            _ => throw new InvalidOperationException("no linked references on this record type"),
        };
    }

    /// <summary>Drops the edited copy (back to the plugins' version) as an undoable step.</summary>
    private sealed class RevertCommand(OverrideDraft draft) : IEditCommand
    {
        public string Name => $"Revert {draft.EditorId}";
        public void Apply(EditDocument document) => document.Drop(draft.Key);
        public void Revert(EditDocument document) => document.Put(draft);
    }
}
