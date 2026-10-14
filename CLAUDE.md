# CLAUDE.md

## What this is

STB Editor — a standalone replacement for the Skyrim Creation Kit (placement, cells, landscape,
navmesh, record editing, script properties without compiling). Plan and reasoning:
`docs/PLAN.md`, teardown of the SkyrimMapEditor concept and of CK: `docs/ANALYSIS.md`.
Status (v0.70.0, 2026-10-14) — the table in `README.md` is the source of truth. Done: data, NIF,
D3D11 view incl. particles/billboards/shader animation, NPCs from race/outfit/FaceGen in bind pose;
ref edits with undo (`Core/EditDocument`), CK-style drags + ImGuizmo (`Panels/RenderWindow.Editing.cs`),
lossless saving into an active plugin (`Core/PluginWriter` + `Data/Raw/RawSplice`,
`App/Services/SaveController`), drag & drop placement, thumbnails; ref properties, field editor for
any record, Duplicate, scripts + properties; new interior cells / worldspaces / exterior cells
(`Core/NewRecords.cs`), landscape sculpt + paint; manual navmesh editing; going through doors
(`Data/DoorIndex.cs`, `Panels/RenderWindow.Doors.cs`), Cell View object list, Help window (F1),
Russian / English UI (`Ui/Loc.cs`, `Ui/LangRu.cs`). .NET 10 (`global.json`). v0.5.0: CK-like record
forms (`Panels/RecordForm.cs`, `FormView.cs`, `FormWindows.cs`), plugin selection in Data (`Workspace.Open(only)`).
v0.6.0: xEdit-like comparison (`Ui/RecordCompare.cs` builds the aligned field tree and colours,
`Panels/CompareWindows.cs`; Object Window Ctrl/Shift multi-pick), colour themes (`Ui/Theme.cs`
`Palettes`, `Theme.Use`; use `Theme.*` colours, never hard-coded ones), record header fields hidden
unless `Settings.ShowRecordHeader` (`RecordFields.IsRecordHeader`).
v0.7.0: New record of any type (`NewRecords.RecordClasses` / `Blank`, `Panels/NewFormDialog.cs`), removing a record or
override from the active file (`RemovalDraft` / `RemoveRecordCommand` in `Core/Overrides.cs`, `Panels/RecordActions.cs`),
reference clipboard with record copies (`RenderWindow.Editing.cs` `CopySelection` / `Paste`), Object Window plugin and
conflict filters (`Ui/ConflictScan.cs`, time-sliced), master edits (`EditDocument.Masters` / `MasterEdit`, writer
`UsedMasters`; `CompareUntouched` maps every old master index to the new one).
v0.8.0: Use Info (`Data/UseIndex.cs`: raw 4-byte FormID matches per subrecord, winners only, per-plugin disk cache keyed
by the plugin and its masters; placed references use exact link offsets and a real VMAD parse; other records are
confirmed at view time with Mutagen `EnumerateFormLinks` in `Panels/UseInfoWindows.cs`; `RenderWindow.GoToReference`).
v0.9.0: condition editor (`Core/Conditions.cs`: function = Mutagen data class, Use Global = ConditionGlobal class;
`Ui/ConditionsEditor.cs`, hooked into `FormView` for every condition list), reference «More properties» (`ReferenceProperties.DrawMore`).
v0.10.0: quest editor (`Panels/QuestEditor.cs`: Stages / Objectives / Aliases tabs as list + detail, drawn from
`FormView.DrawTab` for QUST; parts drawn with `FormView.DrawObject`; `ConditionsEditor.Aliases` names a quest's aliases).
v0.11.0: Papyrus (`Data/Papyrus.cs` compiler + MO2 source imports incl. disabled mods; `Core/QuestFragments.cs` CK fragment
script format; `Panels/QuestEditor.Fragments.cs`, `Panels/ScriptSourceWindows.cs`), SEQ on save (`Data/Raw/SeqFile.cs`).
Compiled output goes next to the active file — tests must use a scratch active file.
v0.12.0: dialogue (`Data/DialogueIndex.cs`, `Panels/DialogueEditor.cs`; INFO drafts are `RecordPlace.DialogInfo` with the topic
in `Cell`, written under a header-only topic; `InfoFragments` for TIF__ scripts).
v0.13.0: cell / worldspace header edits (`RecordPlace.Header`, `HeaderCopies`, `Panels/PlacePropertiesWindows.cs`).
v0.14.0: navmesh Finalize (`Core/NavmeshFinalize.cs`: door triangles + XNDP, edge links by overlap one-to-one, NAVI
entries only for the plugin's navmeshes; `Panels/RenderWindow.NavmeshFinalize.cs`; verified against CK data by `finalizetest`).
v0.15.0: Check and clean (`Core/PluginChecks.cs`: ITM vs the nearest master version, GenderedItem-safe compare; errors;
UDR via the writer's SafeDisable; `Panels/PluginCheckWindow.cs`); writer removes references (RemovalDraft for REFR/ACHR…).
v0.16.0: BSA packing (`Assets/BsaWriter.cs` v105 uncompressed, hash checked on vanilla archives; `Panels/ArchiveWindow.cs`).
v0.17.0: Compact FormIDs / light (`Core/FormIdCompactor.cs`, `PluginWriter.Compact` with RawSplice, `Panels/CompactWindow.cs`).
v0.18.0: copy a field between compare columns (`Core/MemberCopy.cs`, `CompareView.CopyValue`).
v0.19.0: Find text / replace (`Core/TextSearch.cs`, `Panels/FindTextWindow.cs`; replace only display strings).
v0.20.0: render Show/Hide groups + current cell only (`RenderOptions.Hides`), stale FaceGen warning (`PluginChecks.LooksChanged`).
v0.21.0: reference primitives drawn with markers (`SceneObject.Primitive`, `SceneRenderer.DrawPrimitives`).
v0.22.0: AI packages (`Core/Packages.cs`: template inputs by name, `UseTemplate`, procedure tree from the flat list;
`Panels/PackageEditor.cs`; `NewRecords.PackageDefaults` — vanilla defaults; `RecordFields.FlagBits` fixes Mutagen's WearSleepOutfit).
v0.23.0: scenes (`Core/Scenes.cs`: actors / phases / actions with vanilla defaults, phase insert-remove shifting actions;
`Panels/SceneEditor.cs` timeline; dialogue actions reuse `DialogueEditor.DrawTopic`).
v0.24.0: package / scene fragments (`Core/RecordFragments.cs`: slots Begin / End / Change / Phase{i}Start|End;
`Panels/FragmentsPanel.cs` generic compile flow, `Update` each frame). v0.27.0: perk fragments (slots Entry{i} for Activate entries).
v0.25.0: alias Use Report (`Core/AliasUses.cs`, `Panels/QuestEditor.AliasUses.cs`, background task per quest + alias).
v0.26.0: texts for translation (`Core/TextTranslation.cs` JSON export / import, `TextSearch.DisplayTexts`; `Services/TextEdits.cs`
puts many text edits where the writer expects them — INFO under the topic, CELL / WRLD as Header — used by Find text too).
v0.28.0: navmesh find by index, move selection to a new navmesh (`NavmeshTools.MoveTriangles`, `RenderWindow.MoveNavmeshSelectionToNew`).
v0.29.0: compare with another file (`Core/PluginDiff.cs`: raw bodies by FormKey via each file's masters, fields of changed ones; `Panels/CompareFileWindow.cs`).
v0.30.0: leveled list merge (`Core/LeveledMerge.cs`: per item, the last plugin that changed it wins; `Panels/LeveledMergeWindow.cs`).
v0.31.0: NPC body tint check + fix (`Core/BodyTint.cs`: floor(127.5 + (skin - 127.5) x interpolation), matches all vanilla NPCs; CheckIssue.Fix "BodyTint").
v0.32.0: heightmap export / import of the loaded cells (`Core/Heightmap.cs` .r16 + .json; `RenderWindow.ExportHeightmap` / `ImportHeightmap` as a LandStrokeCommand).
v0.33.0: vertex colour brush (`LandBrushMode.Color`, `LandscapeBrush.Color`; LandscapeData.Colors → VCLR).
v0.34.0: object palette (`Panels/RenderWindow.Palette.cs` EditTool.Palette, `Panels/PaletteWindow.cs`).
v0.35.0: layers (`Panels/LayersWindow.cs`, layers.json in %LOCALAPPDATA%; RenderOptions.HiddenReferences / FrozenReferences, `Picking.Pick(selecting:)`).
v0.36.0: Story Manager tree (`Core/StoryManager.cs`: children by Parent, ordered by the PreviousSibling chain; `Panels/StoryManagerWindow.cs`).
v0.36.1: Inspector of references made in this session (`RecordForm.Load` starts them from `ReferenceProperties.NewRecord`),
forms re-read after a save (`_loadedIn` session); `--autotest-place` drops a base like the Object Window and checks it.
v0.36.2: left drag on the selection follows the tool (Rotate turns, Scale sizes; `_dragByLeft`), move drags are capped at 4x the
cursor's speed at the object (a level view threw objects far away); Preview «Spin» popped an unpushed colour (native abort) —
push/pop by a local captured before the button, never by the field the button flips.
v0.36.3: `edit:` log lines for drags (removed again in 0.36.5 once the bug was found).
v0.36.4: CK-style turns pivot on the drag-start position (`RotateSelection`; the live position fed rounding back and objects flew away past 60°); `mathtest` replays the reported case.
v0.36.6: `Rings` leaves edge-on rotation rings out of the gizmo (grabbable but dead); the viewport stays hovered over a gizmo handle for RMB / MMB / wheel.
v0.37.0: FaceGen export (`Render/FaceGenExport.cs` parts / textures / colours, `Render/FaceGenMorphs.cs` TRI morphs, `Render/FaceGenTint.cs`,
`Nif/FaceGenNif.cs` + `Nif/NifDocument.cs` block-level NIF writer, `Nif/TriFile.cs`, `Render/TextureImage.cs` CPU DDS decode / DXT5 encode;
`App/Services/FaceGenExports.cs`, Object Window Ctrl+F4). Learned from the CK's own files (`stbedit-cli facegen*`): slider → morph
directions are not what Mutagen's names suggest (`FaceGenMorphs.Sliders`); race morph = MorphRace's; SkinnyMorph = 1 − weight/100;
TXST TX02..TX05 go to NIF slots 5, 2, 3, 4; head part partitions (BSDynamicTriShape) hold no positions — never patch them;
tint = QNAM, then each layer but SkinTone lerped by mask red × value; hair tint colour = hair colour × 2 / 255.
v0.38.0: Run Havok Sim (`Render/PhysicsDrop.cs`, BepuPhysics 2.4: metres inside, convex hull bodies, static meshes shared per model, triangles
turned clockwise for Bepu, contents of a moving piece and pieces starting inside each other left out; `RenderWindow.RunHavokSim` on a worker,
`FinishHavokSim` one undo step; Alt+H).
v0.39.0: face editor (`Panels/FaceEditor.cs`, drawn by `FormView.DrawTab` for the NPC «Appearance» tab: a working Npc copy, Moved → preview,
let go → one `EditTarget.Apply` with the whole appearance; preview = `FaceGenExport.Build` on a worker into `AssetFileSystem.AddMemory`
paths under meshes\stbeditor\facepreview, drawn by `ObjectPreview`).
v0.40.0: navmesh generation (`Core/NavmeshGenerator.cs`: DotRecast, Skyrim (x, y, z) → Recast (x, z, −y), heights from the detail
mesh, pieces kept by door seeds / cell edge; `Render/NavmeshGeometry.cs`: what counts as ground — STAT/MSTT/FURN/CONT/TREE/FLOR/SCOL
meshes with collision, no doors/activators/effects/alpha; `Panels/RenderWindow.NavmeshGenerate.cs`: worker, then into the cell's biggest
NAVM (neighbours' links into it dropped) + Finalize). Tuned against the game's navmeshes with `stbedit-cli navgen`.
v0.41.0: perk tree (`Panels/PerkTreeEditor.cs`, AVIF «Perk tree» tab via `FormView.DrawTab`): place = grid column/row + offsets, links =
ConnectionLineToIndices (node Index values, not list positions); the root's grid fields hold junk in vanilla — drawn under its children.
v0.42.0: idle tree (`Core/IdleTree.cs`: parent = RelatedIdles[0] (an IDLE, or an AACT action for roots), previous sibling = [1];
`IdleTree.Move` gives the (parent, previous) edits of a move; `Panels/IdleTreeWindow.cs`, World menu, one `CommitEdits` step per move).
v0.43.0: voice files (`Core/VoiceFiles.cs`: name = quest EditorID ≤ 10 + topic to 25 in all, FormID X8, response number — checked on
34k vanilla files by `voicecheck`; speakers from Speaker / subject conditions / aliases / scenes, faction or class as a fallback;
`Core/VoiceTools.cs`: CK LipGenerator wants mono 16-bit WAV and writes tmp16khz.wav into its working folder — run it in a temp folder;
.fuz = "FUZE", 1, lip size, lip, xwm; `Panels/VoiceFilesWindow.cs`).
v0.44.0: weather preview (`Render/WeatherLighting.cs`: colour sets blended by the climate's sunrise / sunset, sun arc east → south → west
from sunrise begin to sunset end; `Panels/RenderWindow.Sky.cs` sets `Scene.Lighting` and the clear colour, outdoors only).
v0.45.0: regions (`Panels/RenderWindow.Regions.cs`: EditTool.Region, borders into `OverlayLines` following `TerrainHeight`, corners as
P2Float in world units, one CommitEdit per change; `Panels/RegionsWindow.cs`).
v0.46.0: user scripts (`Services/ScriptHost.cs`: Roslyn CSharpScript with `ScriptGlobals` — must stay public — Records<T> via
`RecordLookup.SignatureOf`, edits collected as copies and committed on the UI thread after RecordRoundTrip; `Panels/ScriptsWindow.cs`,
scripts in Documents\STB Editor\Scripts).
v0.47.0: export data (`Core/DataExport.cs`, kinds from `TextSearch.DisplayTexts` + the plugin's GMSTs) and ModGroups (`ModGroups.Collapse`
on the winner-first versions in `Ui/ConflictScan`; groups count only when all their plugins are loaded).
v0.48.0: plugin assets (`Services/PluginAssets.cs`: Mutagen EnumerateListedAssetLinks per record + textures of the named NIFs, found
through `AssetFileSystem.Find`; `Panels/PluginAssetsWindow.cs`).
v0.49.0: Story Manager moves (`Core/SiblingChains.Move` shared with `IdleTree.Move`; `StoryManager.Build` takes the document's edits;
`Panels/StoryManagerWindow.MoveTo` writes Parent / PreviousSibling on `AStoryManagerNode` copies in one CommitEdits).
v0.50.0: dialogue line order = PNAM chain (`SiblingChains.Ordered` — walks with TryGetValue, items may be tuples), moves edit PNAM
(`DialogueEditor.MoveLine`), Delete line = RemovalDraft "INFO" (writer takes it out of the topic) for the active file's own lines only.
v0.51.0: Sort Masters (`MasterEdit.Sort`, the writer orders the final list by load order → full rewrite path) and Change FormID
(`CompactWindow.ChangePlan` → `PluginWriter.Compact` with a one-entry map).
v0.52.0: Check NavMeshes (`Core/NavmeshCheck.cs`; vanilla interiors come out clean — keep it that way) and deleted navmeshes in
Check and clean (`DeletedNavmesh`, fix = master copy with every vertex at Z −30000).
v0.53.0: merged patch for containers, NPCs, outfits, form lists (`LeveledMerge.Specs`: per type the lists, read as EntryKey + entry,
written back into the winner's copy; init-only Mutagen lists are cleared and refilled).
v0.54.0: procedure tree editing (`Packages.Insert / Remove / Move / SubtreeEnd / ParentOf` on the flat depth-first list, Root.BranchCount
kept in step; `PackageEditor.DrawTreeTools`, branch types gathered from the load order's packages).
v0.55.0: Filtered Dialogue (`Core/FilteredDialogue.cs`: subject conditions on ID / voice / race / sex / class / faction against the NPC,
OR groups, unknown = passing; ById only for «is this NPC») and Export Relationship Data.
v0.57.0: room / portal checks (`PluginChecks.RoomIssues`: RoomMarker 0x1F / PortalMarker 0x20 bases; vanilla rooms joined by a
portal do not list each other and linked rooms do not always link back — not checked).
v0.58.0: delta patch (`CompareFileWindow.DeltaPatch`: the compared plugin's own versions of added / changed records as overrides,
INFOs under their topic as DialogInfo, cell content left out).
v0.59.0: dialogue graph (`Panels/DialogueEditor.Graph.cs`: LinkTo edges, BFS columns from branch starts / unreached topics).
v0.60.0: Havok collision for falling objects (`Nif/NifFile.Collision.cs`: bhkCollisionObject → bhkRigidBody(T) → box / sphere /
capsule / convex vertices (grown by the convex radius) / list / transform / MOPP as convex pieces × 69.99124; triangle meshes mark the
model incomplete → render-mesh hull; `PhysicsDrop` builds a Bepu Compound with CompoundBuilder, mass 1).
v0.62.0: collision view (`GpuModel.CollisionLines`, `RenderWindow.CollisionOverlay`, F4) incl. bhkCompressedMeshShapeData (chunks: origin +
ushort × error, chunk transform; strips then a triangle list; big tris). Physics places bodies without the NIF root's own transform.
`NifFile.SafePiece`: Bepu's hull builder crashes the process (no exception) on nearly flat point sets — keep pieces through it.
(0.61.0 was tagged on the 0.60.0 commit by mistake and the tag removed; the release script now refuses to tag without a commit.)
v0.63.0: perk tree plates + «As in the game» view (`PerkTreeEditor.DrawGame`: taken ranks per AVIF in memory, a node can be taken when
a node linking to it is taken or it hangs from the root, rank perks = Next Perk chain, required level from conditions). LOD crossed out.
v0.64.0: SKSE framework configs (`Core/FrameworkIndex.cs` reads _DISTR / _KID / _SWAP / _FLM / _DESC / _SRD / OAR json / DAR txt
through `AssetFileSystem` loose files, `EditorSession.Frameworks`; `Core/SpidMatch.cs`, `Core/KidMatch.cs` evaluate filters;
`Panels/FrameworksView.cs`: Use Info tab, World → Framework configs, form header chip).
v0.65.0: perk tree «As in the game» = the game's menu (`Panels/PerkSky.cs`: INTPerkSkydome.nif node PointNN per skill slot,
star = ((width / 2 − column) × fStatsStarXIncrement, row × Y, ZInitialOffset − row × ZIncrement) in that node, width = max column
of perk nodes — from SkyrimSE.exe 1.5.97 StatsMenu; slot order Enchanting, Smithing, Heavy Armor, Block, Two-Handed, One-Handed,
Archery, Light Armor, Sneak, Lockpicking, Pickpocket, Speech, Alchemy, Illusion, Conjuration, Destruction, Restoration, Alteration;
camera at the dome's centre; `PerkTreeEditor.DrawSky` renders it through `ObjectPreview`, star drags = ray ∩ the tree's slope).
v0.66.0: what SPID gives an NPC (`FrameworksView.GiftsOf` / `DrawNpc`, drawn by `FormView.DrawTab` under the NPC «Spells and perks» tab;
each SPID line evaluated once per NPC with `SpidMatch`).
v0.67.0: grass (`Render/Grass.cs` `GrassField.Generate`: iMinGrassSize grid, dominant LTEX → its GRAS, density / slope / water / ranges;
`SceneBuilder.GrassOf` + INI [Grass] into `Scene.Grasses / GrassSpacing / GrassDistance`; `SceneRenderer.CollectGrass` two cells a frame,
cached per TerrainCell; models through MeshCache "grass:" — vertex alpha off, it is the wind weight); hide selection 1 / Alt+1
(`RenderOptions.HiddenByHand`); RecordForm no longer dereferences a missing catalog entry when a reference's cell unloads.
v0.68.0: view presets (`Panels/RenderWindow.Presets.cs` `ViewPreset`, Settings.ViewPresets / LastView — not written by autotests or
screenshots), Cell View camera cell + «Find placed objects» (UseIndex placed uses of matching bases, filtered by the cell's worldspace),
toolbar button IDs `###tool…` / `###show…` (same labels clashed); Release builds set `ConfigDebugHighlightIdConflicts = false`.
Translations can make two English labels the same Russian text — give buttons explicit `###` IDs where labels may repeat.
v0.69.0: key 1 cycles see-through → hidden → shown (`RenderOptions.FadedByHand`: every shape of the object goes to the blended pass with
`Blended.Fade` 0.5 — opacity and the alpha-test threshold scaled, shader flag F_FADE takes alpha from the fade alone for materials
without alpha). `docs/ROADMAP.md` is the short what-is-there / what-is-planned page for testers — keep it current too.
v0.70.0: BOS preview (`Core/BosRules.cs` from the BOS FrameworkRefs grouped by line; `RenderOptions.Swaps` SceneObject → SceneSwap
model + world, drawn tinted in `CollectObjects`; `Panels/RenderWindow.Bos.cs` builds them with `SceneBuilder.CreatePlaced` of the swap base)
and OAR / DAR on an NPC (`Core/AnimReplacers.cs` parse + Pass / Fail / Unknown evaluation, `EditorSession.AnimReplacers`,
`Panels/AnimReplacersView.cs` «Animations» tab added by `RecordForm`, replacers filtered by the race skeleton folder).
Next: what is still missing against CK and xEdit, with priorities and order — `docs/GAPS.md` (update it when a gap closes).
Developer docs: `docs/DEVELOPING.md`, `docs/RENDERING.md`, history in `CHANGELOG.md` —
**keep them, README and this file current with every feature** (several people work on this repo,
most of them with Claude Code).

## Repository and releases

GitHub: https://github.com/STB-Team/STB-Editor (branch `main`). Releases are built by GitHub Actions
(`.github/workflows/release.yml`): add a `## <version>` section to `CHANGELOG.md`, commit, then
`git tag v<version>` and `git push origin v<version>` — the workflow runs `package.ps1 -Version <version>`
and publishes `STBEditor-<version>.zip` with that changelog section as the notes. `dist/` and `build/`
are not committed.

## Build / run

```powershell
dotnet build STB-Editor.sln -c Debug
src\Editor.App\bin\Debug\net10.0-windows\win-x64\STBEditor.exe --game C:\Skyrim --open
```

`C:\Skyrim` here and below is the example game folder (the original author's: MO2 portable instance
`[STB] Mod Organizer`, profile `STB`); use your own. .NET SDK 10 usually lives in `C:\Program Files\dotnet`
(not always on PATH).

**Never write test output into the game or the mod folders.** Tests copy plugins into a temp folder;
in-app autotests change `%LOCALAPPDATA%\STBEditor\settings.json` (active file) — restore it afterwards
(`ActivePluginPath` back to null).

Self-check without a person at the screen:
`STBEditor.exe --game C:\Skyrim --open [--search X] [--select 0E01A348] --screenshot out.bmp`
loads, renders a few frames, writes the frame as BMP and exits. Convert to PNG with
System.Drawing to look at it. Render checks: `--cell <EditorID|FormID>` waits until every mesh
and texture of the cell has streamed in; `--camera dx,dy,dz,yawDeg,pitchDeg` (relative to the
scene focus), `--pick fx,fy` (select under that viewport fraction, logged as `pick:`),
`--no-markers`, `--render opaque=0,blended=0,markers=0,shading=N,logblended=1` to isolate
passes. In screenshot mode crashes never show a message box (they would land on the user's
screen). Good test cells: `RiverwoodSleepingGiantInn`, `BleakFallsBarrow01`, `WhiterunExterior10`.

Data layer diagnostics: `dotnet build src/Editor.Cli -c Release -o build/cli`, then
`build\cli\stbedit-cli.exe <command> C:\Skyrim …` with `plugins`, `catalog`, `find <text> [--plugin]`,
`assets <paths…>`, `extract <asset> <file>`, `nif [prefix]` (parse every NIF: 33k files, ~10 s),
`nifdump <mesh>`, `scene <cell EditorID> [--shaders]`, `sweep [n]` (build n random cells).

## Layout

- `src/Editor.Data` — no UI. `GameInstall` (SE/AE/VR/GOG, version from the exe *string*
  version — SkyrimSE 1.5.97 has 1.0.0.0 in the numeric fields, BEES detection → ESL limits),
  `Mo2Instance` + `LoadOrderReader` (MO2 profile or plain AppData plugins.txt), `Workspace`
  (Mutagen overlays, masters-first sort, FormID↔FormKey incl. FE slots), `Raw/` (TES4 header
  and record walker without Mutagen), `Catalog/` (merged record index with per-plugin disk cache,
  CK Object Window categories).
- `src/Editor.Assets` — `AssetFileSystem`: loose files by MO2 priority over all BSAs; BSAs in
  INI order then `<plugin>.bsa` / `<plugin> - Textures.bsa` per plugin.
- `src/Editor.Nif` — render-only NIF reader (20.2.0.7, BS 83 LE / 100 SSE): node tree, BSTriShape,
  NiTriShape/NiTriStrips, lighting/effect shaders, texture sets, alpha. Unknown blocks skipped by
  header block sizes. Skinned SSE shapes take their vertices from NiSkinPartition (bind pose, see
  Actors below); particle systems and controllers in `NifFile.Effects.cs`. Pre-Skyrim meshes (BS < 83)
  are reported, not drawn.
- `src/Editor.Render` — `Dds` (BC1–7, legacy RGB/L8), `MeshCache`/`TextureCache` (async; D3D11
  creation is free-threaded), `SceneBuilder` (merges each cell's refs over every plugin version,
  persistent world refs by position, LAND → `TerrainCell`, water, XCLL/LGTM lighting),
  `SceneRenderer` (instanced opaque, sorted blended, terrain base + alpha layers, overlays),
  `Picking` (CPU ray), `Camera` (reversed-Z infinite).
- `src/Editor.Data/RecordLookup` — typed record/cell access. **Never call untyped
  `LinkCache.TryResolve(key)` / `ResolveAllSimpleContexts(key)`**: it indexes every type incl. a
  million REFRs (19 s for one cell). Use the catalog signature → getter type.
- `src/Editor.Core` — `EditDocument` (pending ref states keyed by FormKey, original state kept
  so drag-back removes the change), `UndoStack`. Scenes re-apply it after every (re)load.
- `src/Editor.App` — Win32 window, D3D11 (Vortice), Dear ImGui docking (Hexa.NET.ImGui +
  official Win32/D3D11 backends). `Panels/` one class per dock window, `Ui/PropertyTree` —
  generic field tree over Mutagen getter interfaces. **Every visible string goes through
  `T("…")` / `TF($"…")` / `Title("…")` (`Ui/Loc.cs`) and gets a Russian entry in `Ui/LangRu.cs`;
  `python tools/loc.py missing` must print nothing** (see DEVELOPING.md, «Перевод интерфейса»).
- `src/Editor.Cli` — diagnostics.

## Rules learned the hard way

- **Text encoding is per string.** The build has UTF-8 plugins (STB.esp), cp1251 ones and
  plugins mixing both (Valtheim.esp). Reading uses strict UTF-8 with cp1251 fallback per string
  (`StringEncodingDetector.ReadEncoding`); writing uses the plugin's dominant encoding. Never
  read non-localized strings with one fixed encoding.
- **Localized masters inside MO2 mods** (cleaned Update / DLC masters): Mutagen looks for .strings only next to the
  plugin, so names came out empty. `Workspace.StringSources` points it at the winning mod's loose `Strings` or at
  Data's archives (`StringsFolderOverride` / `BsaFolderOverride`). Check with `stbedit-cli strtest C:\Skyrim <EditorID>`.
- FormID prefixes come from the list of *active* plugins, not of those Mutagen opened — a broken
  plugin must not shift everyone below it.
- MO2 `plugins.txt` and `loadorder.txt` can be in different encodings — `TextFiles.ReadLines`
  decodes line by line.
- Catalog skips placed refs/navmesh/land (most of Skyrim.esm); cells are catalogued down to the
  CELL record only. Cell contents are the cell loader's job.
- Hexa.NET.ImGui: `MenuItem(label, null, …)` is ambiguous — cast `(string?)null`; DockBuilder
  split takes `uint*`.
- Reference rotation = engine `NiMatrix3::SetEulerAnglesXYZ(x, y, z)` rows applied to column
  vectors (Z = heading, clockwise). NIF/terrain triangles are CCW-front → rasterizer
  `FrontCounterClockwise = true`.
- NIF "EditorMarker" nodes are culled by the game by name (their hidden flag is often unset):
  draw them only with Markers on.
- NiAlphaProperty blend factors: *_COLOR factors are illegal for the D3D11 alpha channel —
  map them to alpha equivalents (else E_INVALIDARG).
- **`<CETCompat>false</CETCompat>` must stay** in Editor.App.csproj: .NET 9+ marks exes
  CET-compatible, and under MO2 the usvfs hooks then crash the process at startup (0xC0000005,
  before any managed code — no log, no crash report). Portable build: `package.ps1` → `dist\`.
- UI look follows the STB build's in-game settings menu (STB SettingsMenu, `Mcm*.cpp`, not in this repo):
  monochrome, white-alpha highlights, STB_FuturaCondensed/Light from the build (`Ui/Theme.cs`
  finds them; never ship the fonts). Russian culture's U+202F digit separator is not in Futura —
  Program sets a plain space.
- Texture/mesh paths in NIFs may carry the author's build path (`skyrimhd\build\pc\data\textures\...`);
  `AssetFileSystem.TexturePath` cuts at `data\` / `\textures\` like the engine.
- **Saving:** Mutagen alone normalises untouched records (50+ diffs in STB.esp) and drops
  unused masters (renumbering own FormIDs). The writer keeps every original master in place,
  appends needed ones, writes with `MastersListContent/Ordering = NoCheck`, then `RawSplice`
  restores original bytes of every untouched record; `CompareUntouched` must report 0. Only when
  a new master is needed is the splice skipped (own index shifts) and the user asked.
  Test: `stbedit-cli savetest C:\Skyrim <interior EditorID> <out dir> [existing plugin]` (copies the
  plugin, never writes into the build), and in-app `--autotest-save <dir>` (restore settings.json after:
  it sets the active file).
- Refraction (heat-haze) shapes are not drawn: their diffuse slot holds a normal map.
- Game shader behaviour is checked against Community Shaders' HLSL in the build
  (`C:\Skyrim\[STB] Mod Organizer\mods\Community Shaders\Shaders\Effect.hlsl` etc.). Effect palettes are 2D:
  alpha = palette(tex.a, alpha), colour = emissiveMult × palette(tex.g, baseColour.r × vertex.r).
  Guessing these cost three rounds — read the HLSL first.
- NIF field layouts: niftools nif.xml (develop branch). Particle aspect/sub-texture data live in NiPSysData.
- Effect checks: `--object-shot <FormID> out.bmp [size yaw pitch zoom]` and `--psys <name>` (see DEVELOPING.md).
- New records are drafts: mutable Mutagen `Cell`/`Worldspace` held by `EditDocument` (`CellDraft`,
  `WorldDraft`, created by `Core/NewRecords.cs`), shown by `SceneBuilder { Document = … }`, written by
  `PluginWriter` before references. A reference landing where no exterior cell exists gets one (draft
  in the editor, auto cell in the writer). `PendingCount` (refs + drafts) is "unsaved", not `ChangedCount`.
  Tests: `stbedit-cli newtest <dir> [plugin]`, in-app `--autotest-new <dir>` (restore settings.json).
- Landscape: `Core/LandscapeData.cs` (heights in world units; VHGT = offset + sbyte steps ×8, clamped;
  normals recomputed only when heights were edited; ATXT layers ≤ 6 per quadrant, paint always blends
  as a layer), `LandDraft` + `LandStrokeCommand`, brush in `Panels/RenderWindow.Landscape.cs`. Scene
  terrain is rebuilt from drafts on `DraftsChanged`; during a stroke from the stroke's draft data (not the
  TerrainCell's original data — that bug made strokes invisible). Tests: `stbedit-cli landtest`,
  `--autotest-land <dir>` with `--cell`, `--autotest-landview <dir>`.
- Record edits: `Core/Overrides.cs` (`OverrideDraft`, `WorkingCopy` → mutate → `CommitEdit`). Writer puts
  top-level ones into their group with the group's `Set` (AddUntyped throws on an existing key), references
  replace their cell entry. Editable field tree: `Ui/PropertyTree.cs` with an `EditTarget` (path of
  property names / list indices applied to a fresh copy). Ref dialog: `Panels/ReferenceProperties.cs`;
  scripts: `Panels/ScriptsEditor.cs` + `Assets/PexScript.cs` (big-endian .pex, walks functions to skip them).
  New base records (Duplicate) are catalog transients (`RecordCatalog.AddTransient`, synced from drafts).
- Navmesh: `Core/NavmeshTools.cs` (neighbour links from shared edges, bounds, lookup grid = per cell
  uint count + ushort triangle indices; ours is a safe superset of the game's — navtest checks it),
  editing in `Panels/RenderWindow.Navmesh.cs` + `Panels/NavmeshPanel.cs`, NAVM drafts with
  `RecordPlace.Navmesh` (writer replaces the cell's NAVM). Deleting triangles renumbers: loaded neighbours'
  edge links are remapped in the same undo step; door links, cross-cell links and NAVI come from Finalize (v0.14.0).
- ImGui: numeric inputs assert on `EnterReturnsTrue` (native abort 0xC0000409, no crash report) — use
  `Ui/Fields.cs` (commit on deactivate). Futura has no "×"/"⊕"/"↔": use x, +, <->.
- Actors: `Render/ActorAppearance.cs` builds `actor:a.nif|b.nif?fallback,…` composite models (MeshCache).
  SSE skin partitions index the shared vertex buffer **directly** (mapping through the vertex map shreds
  multi-partition meshes). Skinned vertices go to the bind pose via the file's own bone nodes × NiSkinData
  bone transforms, blended per vertex (one bone is not enough: facegen bones disagree by a few units).
  Skin parts hide when their main slot (body, else lowest) is worn. `stbedit-cli npc <id>` / `npcscan`.
- **Record fields go through `Core/RecordFields`** (walk, editability, `Set` by path, add/remove rows,
  dictionaries, gendered pairs); the UI (`Panels/FormView`, `Ui/PropertyTree`, `Ui/FieldEditors`) only
  draws. `stbedit-cli formtest <dir> 5` edits every field of every type through it and must report
  0 unexpected / 0 set errors. Edits clear Mutagen's Break flags (`Unbreak`) — else fields past a short
  struct are silently not written. `RecordRoundTrip` (write one record, read back) drives the «!» format
  notes and refuses edits that would make a record unreadable. Floats are typed/shown with `FloatText`
  (round-trip), never "%.2f". Hide only `System.Type`-valued members, not members named "Type".
- **Memory: walk a plugin's groups once.** Mutagen's stream overlay reads a group's bytes on every walk
  and each record taken from it keeps that copy alive. Looking cells up by walking the worldspace per cell
  kept one copy of Skyrim.esm's Tamriel group per loaded cell — 5 GB for a 5×5 grid, and 16 GB PCs froze.
  Cells come from `RecordLookup.ModCells` (one index per plugin). Check with `stbedit-cli memprobe <cell>`
  and `--autotest-fly <n>` (memory must stay flat).
- Mesh/texture caches trim what was not drawn for 20 s beyond their share of the DXGI video budget
  (`RenderWindow.TrimCaches`); holders of a `GpuModel` check `IsDisposed`. Frame limit 60 FPS (15 in background).
- Hexa.NET.ImGui `InputText(ref string)` with `EnterReturnsTrue` hands back the typed text **only on Enter**
  (the form-reference search never saw what was typed). Read Enter from the key instead; keyboard nav is on,
  so a field that uses the arrows takes them with `ImGuiP.SetItemKeyOwner`. Test: `--autotest-picker`.
- ImGuizmo: it will not start a drag while any ImGui item is hovered — over a gizmo handle the viewport
  button is replaced by a Dummy. Its rotation is incremental on the matrix it is given: during a drag feed
  back its own last output. `ImGuizmo.SetImGuiContext(null)` before `DestroyContext` (else AV on exit).
  Gizmo changes are tested with `--autotest-gizmo [move]` (fake mouse via `ImGuiLayer.TestMouse`).
- Logs: `%LOCALAPPDATA%\STBEditor\logs\editor.log`, crash reports in `crashes\`, layout in
  `layout.ini` (delete it to get the default layout back).
