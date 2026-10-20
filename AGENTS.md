# STB Editor: engineering instructions

Apply these rules to new and changed code. Existing violations are not precedents.
Follow explicit task requirements; raise material conflicts instead of silently changing policy.
Read any more specific `AGENTS.md` for the area being changed. Preserve user edits and unrelated behavior.

## Start with the responsibility

- Read `README.md`, the relevant parts of `CLAUDE.md`, and `docs/DEVELOPING.md`.
  Use `docs/RENDERING.md` for graphics and `docs/CLAUDE-BRIDGE.md` for editor automation.
  Check the current implementation: historical plans, release notes, and stale examples are not current contracts.
- Keep the project-specific compatibility knowledge in `CLAUDE.md`; do not replace it with generic advice.
  These engineering rules apply even where existing examples are denser or use weaker error handling.
  Resolve material contradictions explicitly and update the affected guidance together.
- Before a substantial change, identify the owner of the rule and state, input boundary, failure outcomes,
  affected contracts, and smallest coherent implementation. A short plan is enough; routine fixes need no ceremony.

| Area | Responsibility |
| --- | --- |
| `src/Editor.Data` | Game/MO2 discovery, load order, Mutagen workspace/overlays, typed record lookup, catalogs/indexes, raw TES4 representation |
| `src/Editor.Assets` | Asset resolution and precedence, loose/BSA access, archive and script-asset handling |
| `src/Editor.Nif` | NIF/TRI representation, decoding and supported writing; preserve format-specific semantics |
| `src/Editor.Core` | Edit document, drafts, undo/redo commands, record-editing rules, validation, plugin writing and verification |
| `src/Editor.Render` | Scene construction, graphics resources/caches, rendering, picking, transforms and geometry-related processing |
| `src/Editor.App` | Win32/ImGui application, composition, panels, UI services, save lifecycle, scripting and local MCP integration |
| `src/Editor.Cli` | Command-line diagnostics, probes and regression scenarios using the shared implementations |
| `tools`, `docs`, root build files, `.github/workflows` | Localization tooling, documentation, build/package/release definitions |

STB Editor is a standalone Skyrim content editor. User plugins, assets, unsaved edits, and undo history
are primary data, not disposable caches. Protect them before optimizing convenience or implementation size.
Keep editing rules independent of panels and MCP transport. Core currently uses the Data/Mutagen model;
do not invent a new architecture or introduce another language just to imitate a different repository.

## Model valid domain states

- Express meaningful distinctions with precise C# types, enums, records, pattern matching and controlled
  construction. Prefer a small explicit outcome/state type over flags and nullable fields that permit
  contradictory combinations. Do not build a type-level framework for a simple invariant.
- Reuse `FormKey`, `ModKey`, `RecordPlace`, draft/command types and the owning conversion APIs.
  Keep load-order FormIDs, file-local FormIDs, object IDs and editor display strings distinct.
  Do not duplicate master-index or light-plugin conversion arithmetic in panels, CLI tools and services.
- Nullable values mean legitimate absence. Missing required input, malformed bytes, unsupported formats,
  failed resolution and I/O failure are not interchangeable with an absent optional field.
  Preserve explicit distinctions such as unchanged, cleared, pending, rejected, canceled and unavailable.
- Do not convert an arbitrary cast, `default`, `null!`, a foreign sentinel or unchecked deserialization
  into a trusted domain value. Account for default struct values when choosing a representation;
  `record`, `required` and nullable annotations alone do not establish every runtime invariant.
- Infallible internal operations return their value directly. Do not wrap every getter or transformation
  in an error result merely to repeat checks already established by its owner.
- Cover defined domain cases explicitly. Handle unknown external enum/flag values at their format boundary,
  preserving supported unknown data when round-trip compatibility requires it. Do not conflate
  “the preview cannot interpret this” with “the plugin is invalid and must be rewritten.”

## Validate at the owning boundary

- Parse external bytes, CLI/MCP arguments, UI text, configuration and script/import outputs into checked
  representations at the boundary responsible for the invariant. Reuse that API instead of copying
  validation into every panel, command and writer.
- Keep record-field traversal, editability, path setting and collection edits in `Editor.Core/RecordFields`.
  UI field editors draw and dispatch through that owner; do not add another reflection-based editing engine
  in a panel or MCP tool. Preserve `RecordRoundTrip` checks and Mutagen `Unbreak` handling where required.
- Separate stable value invariants from current-state preconditions. Recheck the active document/plugin,
  session identity, edit permission, pending operation and target existence when applying a command.
  Perform these checks with the owning mutation; validation before background work may be stale.
- Preserve game detection, load-order and encoding contracts. Active-plugin slots must not shift because
  a plugin failed to open. Respect SE/AE/VR/GOG/BEES distinctions through the existing detection APIs.
  Do not replace per-string strict UTF-8/cp1251 fallback or localized-string source resolution with one
  fixed decoding assumption. Keep the existing write-encoding policy unless the task explicitly changes it.
- Resolve assets through `AssetFileSystem`, including MO2/loose/BSA precedence and path normalization.
  Do not bypass it with direct game-folder reads or invent a second asset-priority policy.
- Validate lengths, offsets, counts, relationships and allocation sizes at binary/interop boundaries.
  Use format limits and actual resource budgets, with named units and purpose. Do not invent arbitrary
  caps, silently truncate records, or “repair” modded values without an established product/format rule.
- Preserve supported skipping of unknown NIF blocks by declared size and other explicit compatibility
  behavior. A rendering limitation must not silently remove content from a saved plugin or exported asset.

## Own edits, undo and save transactions

- `EditDocument` and its command/history model own pending edits. Use working copies, drafts and the
  appropriate commit/command path; never mutate the loaded winning/master record as an editing shortcut.
  Keep render previews, document state, catalog transients and dirty-state accounting consistent.
- One user operation must have the intended undo boundary. Preserve composite edits, related references,
  navmesh links and other dependent changes in the same logical operation. Do not create one undo step
  per frame of a drag or bypass undo from scripts, automation, batch edits or a new panel.
- Keep temporary previews/proposals separate from committed edits. Applying, canceling, undoing, redoing
  and reloading must have explicit effects. Preserve `PendingCount` semantics, including drafts and masters;
  a changed-reference count alone is not a reliable “nothing to save” test.
- Preserve the save lifecycle: prepare a temporary file, verify it, obtain any required user confirmation,
  release the workspace's file handles, back up/replace through the save owner, and reload appropriately.
  A prepared file is not a committed save. Replacement failure must not be reported as success or clear
  unsaved edits merely because preparation succeeded. Track post-replacement failures separately.
- `PluginWriter` and `Editor.Data/Raw/RawSplice` own lossless persistence. When master mapping is unchanged,
  retain untouched record bytes and require the applicable `CompareUntouched` checks to stay clean.
  Adding/removing/reordering masters or remapping FormIDs requires the existing full-rewrite/remapping
  and verification path; never splice bytes under incompatible master indices.
- Preserve default master retention, explicit master-edit/compaction behavior, reference-placement groups,
  header-only CELL/WRLD/topic copies, and safe handling of deleted master references. Do not normalize
  all records, drop unused masters, or renumber identities as an incidental effect of another edit.
- Keep backup, temporary-file and sidecar ownership explicit. Save/compile/export failures must distinguish
  what actually committed from what remains pending; preserve recoverable data and report its location.
  Do not silently delete a usable temporary result or retry a partially completed write.

## Own mutation, asynchronous work and resources

- Owner-confined mutable state is allowed and often appropriate. Keep document/history, panel state,
  ImGui interaction and application-level scene mutations in their owning UI/session context.
  Do not introduce actors, immutable copies of the entire workspace per frame, or global locks without need.
- A record containing mutable members, `IReadOnlyDictionary` over live storage, or a lazy LINQ sequence
  is not an immutable snapshot. Background work needs detached inputs or an explicit stable read lease/
  freeze contract covering the exact document/session version and the lifetime of every borrowed resource.
  Same-owner synchronous reads do not require pointless copying.
- Publish worker results through the existing owner/poll/queue boundary. Check session, document revision,
  target and operation identity before applying late results. Observe cancellation and faults; do not let
  an old save, scene load, script, thumbnail or analysis result mutate a newly opened session.
- Keep D3D11 immediate-context operations and ImGui/Win32 interaction on their established owning thread.
  Preserve the existing supported background resource-creation paths; do not assume every graphics API
  operation is interchangeable across threads or serialize all creation onto the UI thread by default.
- Dispose workspace overlays, streams, native/COM objects, GPU buffers, handles and external processes
  deterministically. Account for partial construction, cancellation, cache eviction and shutdown.
  Preserve cache/scene `IsDisposed` handling and do not dispose shared resources still in use by workers.
- Keep unsafe layouts, pinning, spans, pooled arrays and native callback lifetimes explicit. A helper
  extraction must not shorten a borrow, move disposal earlier or allow an exception across an unmanaged
  callback boundary. Do not modify native/shader layouts, matrix conventions or ABI details by guesswork.
- Bound queues, outstanding work, retained results and resource usage where they can grow independently.
  Define cancellation/saturation behavior without silently dropping edits or control commands.
  Do not block the UI thread on work that requires the UI thread to complete. A timeout does not establish
  that an admitted script, write or external operation never executed.

## Control allocation without weakening the model

- Treat frame rendering, picking, scene streaming, catalog/filter scans and repeated record lookup as
  allocation-sensitive. Look for captured delegates, boxing, temporary LINQ chains/collections, repeated
  reflection, path/string conversion, full-data scans and unnecessary serialization.
- Preserve typed/signature-directed `RecordLookup` access. Do not introduce untyped
  `LinkCache.TryResolve(key)` / `ResolveAllSimpleContexts(key)` lookups that index unrelated record types.
  Reuse `RecordLookup.ModCells` and existing indexes instead of repeatedly walking a plugin's large groups.
- Use C# generics and small shared helpers when they express a real common contract and reduce present
  duplication or overhead. Keep reflection where the generic Mutagen field editor actually needs it;
  cache metadata at the owning layer when justified rather than replacing it with duplicated type tables.
- Choose readonly/value representations, direct loops, static/noncapturing delegates and buffer reuse
  deliberately. Consider copying, boxing, capture lifetime and pooled-buffer ownership. Do not turn every
  class into a struct or assume an inlining annotation proves a delegate allocation was removed.
- Keep memory/VRAM accounting and cache invalidation coherent. Bound loading and eviction against the
  relevant workload/budget, not arbitrary small constants that make normal mod collections unusable.
- Support material performance claims with a focused measurement or generated-code inspection. Use the
  existing CLI probes and flight/streaming scenarios where relevant; record the workload and limitations.
  Do not require benchmarks for ordinary layout edits or claim improvements from a successful build.

## Errors are values; exceptions stay at designated boundaries

- Project-authored throwing is forbidden as production control flow in application, core, data, rendering,
  assets and CLI operations. Do not introduce `throw`, `throw new`, throw expressions or throwing guard
  helpers for ordinary invalid input, missing assets, rejected edits, failed saves or unsupported content.
  Existing throwing APIs and broad catches are not precedents for new code.
- Represent expected outcomes through appropriate Try-style APIs or small typed result/outcome objects.
  Reuse and improve the relevant owning contract; do not assume a repository-wide `Result<T, E>` exists.
  Keep success/error combinations valid and error categories separate from display text. Do not classify
  failures by substring matching or manufacture `null`, an empty collection or success after a failure.
- Prefer dependency status/Try APIs where available. Otherwise catch the relevant exceptions in a narrow
  adapter around the throwing .NET/Mutagen/archive/graphics/process operation. Translate once, retaining
  useful context. Do not wrap business callbacks or an entire editing transaction in that adapter.
- Framework task cancellation may surface as `OperationCanceledException`; preserve its ownership and
  distinguish requested cancellation from unrelated faults. Do not repurpose cancellation for validation.
- App/CLI top-level crash reporting, isolated worker lifecycles and the hosted user-script boundary may
  contain unexpected failures with an explicit disposition: fail/discard the operation, release ownership,
  preserve unsaved data, and stop/reconstruct the affected owner only when its state is safe.
  A script failure must not publish its collected edits as a successful command.
- Do not add catch/rethrow chains, blanket catch-and-continue loops or silent cleanup catches. Preserve the
  primary failure when cleanup/reporting also fails, and attempt independent owned cleanup as appropriate.
  Do not claim recovery from arbitrary corrupted state or native process failures.
- When replacing an existing throwing or broad-catching path, trace all callers and partial effects first;
  migrate the whole relevant contract and add regression coverage. Do not just remove the catch and make
  a normal user error process-fatal, or silently break public scripting APIs in a formatting-only change.

## Keep changes cohesive and source manually readable

- Put each rule and its transitions under one clear owner. Keep adapters thin and feature logic discoverable.
  Avoid generic `Manager`/`Utils` containers, forwarding layers and a growing `EditorApp` or CLI switch that
  absorbs unrelated behavior. A partial class is still one class; splitting files alone is not decomposition.
- Prefer changes local to the responsibility. A legitimate cross-boundary change may touch several projects;
  do not duplicate logic merely to reduce the diff's file count. Choose patterns for present needs, not
  speculative frameworks, extension points or compatibility layers.
- Separate independent methods/types and meaningful member groups with blank lines. Divide substantial
  method bodies into semantic paragraphs: preparation, lookup, computation, mutation, effects and result
  handling as appropriate. Keep tightly related statements together; do not add blank lines mechanically.
- Expand long calls, object/collection initializers, complex conditions, switch expressions, chained LINQ
  and semicolon-packed statements when compact layout hides their meaning. Name intermediate decisions
  when useful. Preserve concise expressions that are already clear.
- Reduce deep nesting with clear guards, explicit cases and cohesive named helpers. Split methods, types
  and files when responsibilities demand it, not at arbitrary line counts. Do not replace a wall of code
  with dozens of trivial helpers or scatter one state transition across unrelated files.
- Keep disposal/lock scopes, evaluation and mutation order, captured values, ImGui push/pop pairing and
  event subscription lifetimes unchanged unless an intentional behavior fix is part of the task.
  Balance UI stacks from the actual push decision, not a mutable value the intervening widget can change.
- Name meaningful limits, flags, masks, units and policy values near their owner. A coherent resource or
  constants module is welcome; a global dumping ground and names for obvious loop-zero values are not.
- Formatting tools handle syntax, not semantic grouping. Preserve established C# style and manual edits;
  no formatter, blanket whitespace script or regex reflow replaces reading and organizing the source.
  Do not introduce or run repository-wide automatic formatting during an unrelated change or manual pass.
- Fix confirmed nearby bugs and violations when that supports the task and stays reviewable. Preserve
  intended behavior, not demonstrated defects. Separate behavioral fixes from layout-only changes when
  practical; report significant out-of-scope findings with evidence and an incremental proposal.

### Acceptance for an explicitly requested readability/refactoring pass

- Review the whole agreed handwritten scope: libraries, UI, CLI checks, tooling and embedded shader code
  where relevant. Maintain a finite file inventory with reviewed areas, findings, dispositions and checks.
  A small selected batch or an empty initial task queue does not establish repository-wide completion.
- Before delegating, review representative improvements. Each assignment specifies exact files, ownership,
  concrete issues, an approved example, invariants, allowed edits and verification. Use one writer per file
  and one integrator; serialize builds/operations that share outputs or application settings.
- Review every low-autonomy worker's changed method in its complete final form. Substantial structural
  changes need a different qualified reviewer when available; otherwise report the missing independent
  review. Resolve ambiguous control-flow, ownership and decomposition decisions with the responsible owner.
- Read final source after any syntax formatting. Accept based on semantic grouping, declaration separation,
  understandable branches and cohesive responsibilities, not test counts or formatter success alone.
  Show representative real before/after examples and state unresolved coverage explicitly.
- Do not impose a full-repository audit on every routine task. These inventory requirements apply to the
  requested broad pass; ordinary changes require review of their actual scope and affected contracts.

## Remove dead code and gate optional diagnostics

- Remove confirmed unused implementations, abandoned scaffolding, unreachable branches and obsolete
  temporary probes. Remove orphaned imports, settings, build entries and documentation with them.
  Do not keep commented-out code or speculative fallbacks “just in case.”
- Before deletion, check reflection, Mutagen/property traversal, public `.csx` scripting APIs, MCP dispatch,
  CLI command routing, event/native callbacks, localization keys and supported configurations. In particular,
  `ScriptGlobals` is a public scripting surface, not dead code because direct C# call sites are absent.
- Compile optional developer-only traces, dumps, profiling and test instrumentation only under explicit
  build flags, disabled in the normal production build. Reuse existing conventions; if a needed flag is
  absent, add a small documented project-level mechanism rather than pretending it already exists.
- Gate diagnostic implementation AND argument preparation, allocations, data capture, registrations and
  diagnostic-only dependencies. A runtime switch or disabled sink that still computes its arguments is
  insufficient. Remove obsolete probes rather than retaining all of them behind flags.
- Keep essential production logs, crash reporting, validation, save verification and user-visible errors.
  Do not classify normal editor features, the deliberate CLI tool, screenshots, script output or the
  user-enabled MCP bridge as disposable debug code. Isolate their test-only hooks from production behavior.
- Verify relevant flag-enabled and flag-disabled configurations. Record names, defaults and actual checks;
  do not claim production exclusion from a diagnostics-enabled build alone.

## Preserve UI, automation and external contracts

- Route visible text through `T`, `TF` and `Title` in `Ui/Loc.cs`, with Russian entries in `Ui/LangRu.cs`.
  Use `Theme` colors and explicit stable ImGui IDs where labels can collide or translations repeat them.
  Preserve `Ui/Fields` commit behavior and `FloatText` round-trip precision; do not invent per-panel variants.
- Keep UI/preview state separate from accepted document edits. All editing entry points—forms, viewport,
  scripts and MCP tools—must use the same owning validation and undo path.
- Preserve the bridge's default-off, loopback-only and Origin-check policies, edit permissions, UI-thread
  `Pump`/`OnUi` dispatch and user-approved proposals. Normal bridge operations do not save plugins;
  do not add automatic save or bypass the user's edit/confirmation controls.
- Treat hosted scripts and external processes as real execution, not inherently safe data. Do not run
  untrusted/generated scripts against a real user workspace merely to test a change. Preserve public
  script-host and MCP tool contracts, or migrate their callers, examples and documentation explicitly.
- Keep generated/vendor files tied to their real source of truth. Edit generators/schemas and regenerate
  where applicable; do not label handwritten reflection helpers or embedded HLSL as generated to skip them.
- Preserve `<CETCompat>false</CETCompat>` in `Editor.App.csproj` and the documented MO2 startup constraint.
  Do not change SDK, dependencies, target/runtime or rendering conventions as incidental cleanup.
  Keep packaging definitions synchronized when an authorized change actually requires it; never ship the
  user's game assets, proprietary fonts, credentials or local settings.

## Verify and finish the whole change

Build settings live in `global.json`, `Directory.Build.props` and the project files: .NET 10, Windows x64.
The UI uses Win32/D3D11/ImGui. Current checks are CLI scenarios and in-app autotests; a successful
`dotnet test` invocation without an actual discovered test suite does not run them. CI's build is not
behavioral or visual verification. Consult `docs/DEVELOPING.md` and the current command dispatch for arguments.

| Purpose | Existing entry point |
| --- | --- |
| Development solution build | `dotnet build STB-Editor.sln -c Debug` |
| Release/CI build | `dotnet build STB-Editor.sln -c Release` |
| Build CLI checks/probes | `dotnet build src/Editor.Cli -c Release -o build/cli` |
| Transform diagnostic, no game dataset needed | `build\cli\stbedit-cli.exe mathtest` |
| Plugin save check on a copy | `build\cli\stbedit-cli.exe savetest <GameFolder> <InteriorEditorID> <ScratchDir> [ExistingPlugin]` |
| Feature-specific data/format checks | Relevant CLI `recordtest`, `formtest`, `landtest`, `navtest`, `finalizetest`, `compacttest`, etc.; arguments in the current docs/dispatcher |
| UI/rendering/edit lifecycle | Current `STBEditor.exe` with documented `--screenshot`, `--cell`, `--object-shot` or feature-specific `--autotest-*` arguments |
| Localization | `python tools/loc.py missing` — inspect the output; no missing visible strings |
| Diff hygiene | `git diff --check` |

- Use the smallest relevant checks first. Record the build/configuration, dataset, command, actual output
  and result. Diagnostic commands may print measurements or failures without a failing exit code:
  inspect their criteria and output; in particular, `mathtest` is not a pass/fail test suite by itself.
- Never write test output into the game, Data or MO2 mod folders. Use disposable copies and an explicit
  scratch active-plugin path for save, compile, compaction, FaceGen, voice, archive and export scenarios.
  External tools may write alongside inputs or into their working directory; isolate both.
- Back up and restore the actual previous `%LOCALAPPDATA%\STBEditor` settings affected by a scenario,
  especially `ActivePluginPath`, including failure paths. Do not blindly reset the user's existing active
  file to null. Isolate/restore layout and other changed persistent state as applicable; do not run
  settings-mutating autotests concurrently against the same profile.
- Add focused regression coverage for changed behavior or a bug. Test observable outcomes, meaningful
  failure paths, undo/redo, save/reload and unchanged-record preservation as relevant. For async changes,
  check cancellation, stale results and resource cleanup using causal synchronization and bounded waits.
  Do not create artificial behavior tests for pure whitespace edits.
- Review the tests too: establish the expected contract, meaningful assertions and reliable observation.
  Correct demonstrably wrong tests with an explanation; do not weaken assertions, hide failures, add skips,
  inflate timeouts or repeat runs until green. An empty selection is not successful coverage.
- Verify affected producers and consumers. Rendering/input changes need actual frame/interaction review
  where available; inspect screenshots, not just whether a file was produced. Save/format changes need
  suitable round-trip/untouched-data checks; building the editor does not prove game/CK compatibility.
- Do not run stale binaries after a failed build or use `--no-build` without a matching successful build.
  Missing Windows/D3D11, game fixtures, MO2, compilers or external tools are reported limitations, not passes.
- Update affected documentation in the same change: `README.md`, `CLAUDE.md`, `docs/DEVELOPING.md`,
  `docs/RENDERING.md`, `docs/CLAUDE-BRIDGE.md` and, when their stated status changes, `docs/GAPS.md`,
  `docs/ROADMAP.md` and `CHANGELOG.md`. Preserve each document's language and fix relevant stale examples.
  Keep stable engineering guidance here; do not fill it with release history or agent execution logs.
- Finish with what changed, why, what actually ran, results, and remaining verification or follow-up.
  Preserve local work. Do not push, tag, publish a release or modify a user's live installation unless
  explicitly authorized; release instructions in documentation are not that authorization.
