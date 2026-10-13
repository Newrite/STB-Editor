using System.Text.RegularExpressions;
using System.Diagnostics;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using StbEditor.Assets;
using StbEditor.Nif;
using StbEditor.Render;
using StbEditor.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Diagnostics for the data layer without the UI: stbedit-cli <command> [game folder]
var command = args.Length > 0 ? args[0] : "plugins";
var gameFolder = args.Length > 1 ? args[1] : GameInstall.FindInstalledGame() ?? @"C:\Skyrim";

if (command == "mathtest")
{
    var rng = new Random(1);
    var worst = 0f;
    for (var i = 0; i < 1000; i++)
    {
        var rot = new System.Numerics.Vector3((float)rng.NextDouble() * 6.28f, (float)(rng.NextDouble() - 0.5) * 3.1f, (float)rng.NextDouble() * 6.28f);
        var pos = new System.Numerics.Vector3(rng.Next(-5000, 5000), rng.Next(-5000, 5000), rng.Next(-500, 500));
        var scale = 0.5f + (float)rng.NextDouble();
        var m = SceneBuilder.ReferenceTransform(pos, rot, scale).ToMatrix();
        var (p2, r2, s2) = TransformMath.Decompose(m);
        var m2 = SceneBuilder.ReferenceTransform(p2, r2, s2).ToMatrix();
        for (var k = 0; k < 16; k++) worst = Math.Max(worst, Math.Abs(m[k / 4, k % 4] - m2[k / 4, k % 4]) / (k >= 12 ? 1000f : 1f));
    }
    Console.WriteLine($"decompose round trip: worst error {worst:E2}");

    // A right-button turn held past 60°, from a reported case (Embershard Mine, snap 15°): the pivot must be the
    // drag-start position. Turning about the live position fed each frame's rounding error back, and it grew.
    foreach (var live in new[] { true, false })
    {
        var start = new System.Numerics.Vector3(-3056.295f, 3423.1162f, 8663.646f);
        var current = start;
        var currentRot = System.Numerics.Vector3.Zero;
        var drift = 0f;
        var away = -1;
        for (var frame = 0; frame < 420; frame++)
        {
            var mouse = Math.Min(frame, 360) * 1f; // 0.5° a pixel: up to 180°, then held
            var degrees = MathF.Round(mouse * 0.5f / 15f) * 15f;
            var radians = degrees * MathF.PI / 180f;
            var turn = System.Numerics.Matrix4x4.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, -radians);
            if (live)
            {
                var livePivot = current;
                var rotation = System.Numerics.Matrix4x4.CreateTranslation(-livePivot) * turn * System.Numerics.Matrix4x4.CreateTranslation(livePivot);
                var world = SceneBuilder.ReferenceTransform(start, System.Numerics.Vector3.Zero, 1f).ToMatrix() * rotation;
                (current, currentRot, _) = TransformMath.Decompose(world);
            }
            else
            {
                var (_, euler, _) = TransformMath.Decompose(SceneBuilder.ReferenceTransform(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 1f).ToMatrix() * turn);
                current = start + System.Numerics.Vector3.Transform(start - start, turn);
                currentRot = euler;
            }
            var d = System.Numerics.Vector3.Distance(current, start);
            if (!float.IsFinite(d)) d = float.PositiveInfinity;
            if (d > 100 && away < 0) away = frame;
            drift = Math.Max(drift, d);
        }
        Console.WriteLine($"turn held at 180° for 60 frames, pivot {(live ? "live position (old)" : "drag start")}: worst drift {(float.IsInfinity(drift) ? "infinite" : drift.ToString("0.###"))} units" +
            (away >= 0 ? $", over 100 units at frame {away} ({MathF.Round(Math.Min(away, 360) * 0.5f / 15f) * 15f}°)" : "") + (live ? "" : $", rotation {currentRot.Z * 180 / MathF.PI:0.#}°"));
    }

    // Orbit about a pivot beside the camera (not in front of it): it must turn, not jump.
    var cam = new Camera { Position = new System.Numerics.Vector3(0, 0, 0), Yaw = 0, Pitch = 0 };
    var pivot = new System.Numerics.Vector3(500, 0, 0);
    var before = cam.Position;
    cam.Orbit(pivot, 0.01f, 0.005f);
    var moved = System.Numerics.Vector3.Distance(before, cam.Position);
    Console.WriteLine($"orbit 0.01 rad about a pivot 500 units to the side: camera moved {moved:0.0} units (expected ~5), distance to pivot {System.Numerics.Vector3.Distance(cam.Position, pivot):0.0}");
    return;
}

var game = GameInstall.Detect(gameFolder);
Console.WriteLine($"{game.DisplayName} | {game.Release} | lang {game.Language} | BEES {game.HasBees} | header {game.MaxHeaderVersion:0.00}");
Console.WriteLine($"MO2: {game.Mo2?.Folder ?? "-"} profile {game.Mo2?.ProfileName ?? "-"} | ini {game.IniFolder}");

var sw = Stopwatch.StartNew();
// --only a.esp,b.esm: load just these (and their masters), as the editor's Data dialog does.
var onlyIndex = Array.IndexOf(args, "--only");
var only = onlyIndex >= 0 && onlyIndex + 1 < args.Length ? args[onlyIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
using var ws = Workspace.Open(game, new Progress<string>(m => Console.Error.WriteLine(m)), only: only);
Console.WriteLine($"opened {ws.Active.Count} active of {ws.Plugins.Count} in {sw.ElapsedMilliseconds} ms, default text {ws.DefaultEncoding}");

switch (command)
{
    case "plugins":
        Console.WriteLine($"loaded {ws.Active.Count(p => p.Mod is not null)}, skipped {ws.Active.Count(p => p.Skipped)}");
        foreach (var p in ws.Plugins)
            Console.WriteLine($"{p.IndexLabel,-6} {(p.IsMaster ? "M" : " ")}{(p.IsLight ? "L" : " ")} {p.StringEncoding,-9} {p.Header?.Version,5:0.00} {p.Header?.RecordCount,7} {p.Name}  <{p.Location.Source}> {p.Error}");
        break;
    case "catalog":
    case "find":
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir, new Progress<string>(m => Console.Error.WriteLine(m)));
        Console.WriteLine($"catalog: {catalog.ByKey.Count} records, {catalog.BySignature.Count} types, {catalog.CachedPlugins} plugins from cache, {catalog.BuildTime.TotalMilliseconds:0} ms, {catalog.Errors.Count} errors");
        foreach (var e in catalog.Errors.Take(20)) Console.WriteLine("  ! " + e);
        if (command == "find" && args.Length > 3 && args[3] == "--plugin")
        {
            foreach (var r in catalog.ByKey.Values.Where(r => r.Plugins.Any(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrEmpty(r.Name)).Take(15))
                Console.WriteLine($"  {ws.FormIdText(r.FormKey)} {r.Signature} {r.EditorId} \"{r.Name}\" win={r.WinningPlugin.Name}");
        }
        else if (command == "catalog")
        {
            foreach (var (sig, list) in catalog.BySignature.OrderByDescending(x => x.Value.Count).Take(25))
                Console.WriteLine($"  {sig} {list.Count}");
        }
        else
        {
            var text = args[2];
            var onlyType = args.SkipWhile(x => x != "--type").Skip(1).FirstOrDefault();
            foreach (var r in catalog.ByKey.Values.Where(r => (onlyType is null || r.Signature == onlyType)
                         && ((r.EditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))).Take(30))
                Console.WriteLine($"  {ws.FormIdText(r.FormKey)} {r.Signature} {r.EditorId} \"{r.Name}\" [{string.Join(" > ", r.Plugins.Select(p => p.Name))}]");
        }
        break;
    }
    case "assets":
    {
        var t = Stopwatch.StartNew();
        var vfs = AssetFileSystem.Build(ws, new Progress<string>(m => Console.Error.WriteLine(m)));
        Console.WriteLine($"vfs: {vfs.LooseCount} loose, {vfs.ArchivedCount} archived in {vfs.LoadedArchives.Count} BSA, {t.ElapsedMilliseconds} ms, {vfs.Errors.Count} errors");
        foreach (var e in vfs.Errors.Take(10)) Console.WriteLine("  ! " + e);
        foreach (var path in args.Skip(2))
        {
            var src = vfs.Find(path);
            Console.WriteLine($"  {path} -> {src?.ToString() ?? "NOT FOUND"} {vfs.ReadAllBytes(path)?.Length} bytes");
        }
        break;
    }
    case "nif":
    {
        var vfs = AssetFileSystem.Build(ws);
        var prefix = args.Length > 2 ? args[2] : "meshes\\";
        var limit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
        var files = vfs.Enumerate(prefix, ".nif").Take(limit).ToList();
        int ok = 0, failed = 0, shapes = 0, withTexSet = 0, lighting = 0, weirdGloss = 0, weirdUv = 0, warned = 0, empty = 0;
        int particleFiles = 0, particleSystems = 0, billboards = 0, animated = 0, weirdParticles = 0;
        var failures = new Dictionary<string, int>();
        var t = Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                var model = NifFile.Load(vfs.ReadAllBytes(file)!, file);
                ok++;
                if (model.IsEmpty) { empty++; if (args.Contains("--empty") && empty % 400 == 1) Console.WriteLine($"  empty {file}"); }
                if (model.Warnings.Count > 0) { warned++; if (warned <= 8) Console.WriteLine($"  warn {file}: {model.Warnings[0]}"); }
                if (model.Particles.Count > 0) particleFiles++;
                foreach (var ps in model.Particles)
                {
                    particleSystems++;
                    var sane = ps.LifeSpan is > 0 and < 120 && float.IsFinite(ps.Speed) && Math.Abs(ps.Speed) < 1e5f && ps.Radius is > 0 and < 1e4f
                        && ps.BirthRate is >= 0 and < 1e5f && float.IsFinite(ps.Emitter.Translation.X);
                    if (!sane && ++weirdParticles <= 8) Console.WriteLine($"  odd particles {file} {ps.Name}: life {ps.LifeSpan} speed {ps.Speed} radius {ps.Radius} rate {ps.BirthRate}");
                }
                billboards += model.Shapes.Count(x => x.Billboard is not null);
                animated += model.Shapes.Count(x => x.Material.Animations.Count > 0);
                foreach (var shape in model.Shapes)
                {
                    shapes++;
                    var m = shape.Material;
                    if (m.Kind == ShaderKind.Lighting)
                    {
                        lighting++;
                        if (m.Textures.Length > 0) withTexSet++;
                        if (m.Glossiness < 0 || m.Glossiness > 2000 || float.IsNaN(m.Glossiness)) weirdGloss++;
                        if (m.UvScale.X <= 0 || m.UvScale.X > 100) weirdUv++;
                    }
                    if (shape.Indices.Any(i => i >= shape.Positions.Length)) Console.WriteLine($"  BAD INDEX {file} {shape.Name}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                var key = ex.GetType().Name + ": " + ex.Message;
                failures[key] = failures.GetValueOrDefault(key) + 1;
                if (failed <= 10) Console.WriteLine($"  FAIL {file}: {ex.Message}");
            }
        }
        Console.WriteLine($"nif: {files.Count} files in {t.ElapsedMilliseconds} ms: ok {ok}, failed {failed}, empty {empty}, with warnings {warned}");
        Console.WriteLine($"  shapes {shapes}, lighting {lighting}, with texture set {withTexSet}, weird gloss {weirdGloss}, weird uv scale {weirdUv}");
        Console.WriteLine($"  particles: {particleSystems} systems in {particleFiles} files ({weirdParticles} odd), billboard shapes {billboards}, animated shapes {animated}");
        foreach (var (k, v) in failures.OrderByDescending(x => x.Value).Take(10)) Console.WriteLine($"  {v}x {k}");
        break;
    }
    case "nifdump":
    {
        var vfs = AssetFileSystem.Build(ws);
        var model = NifFile.Load(vfs.ReadAllBytes(args[2])!, args[2]);
        Console.WriteLine($"{args[2]}: bsver {model.BsVersion}, {model.Shapes.Count} shapes, bounds {model.BoundsMin} .. {model.BoundsMax}");
        foreach (var w in model.Warnings) Console.WriteLine("  warn " + w);
        foreach (var sh in model.Shapes)
        {
            var m = sh.Material;
            Console.WriteLine($"  {sh.Name}: [{sh.BoundsMin} .. {sh.BoundsMax}] {sh.Positions.Length} v, {sh.Indices.Length / 3} t, hidden {sh.IsHidden}, marker {sh.IsEditorMarker}, uv {sh.UVs is not null}, n {sh.Normals is not null}, t {sh.Tangents is not null}, col {sh.Colors is not null}");
            Console.WriteLine($"     {m.Kind} {m.LightingType} f1 {m.Flags1} f2 {m.Flags2} alpha 0x{m.AlphaFlags:X} thr {m.AlphaThreshold} gloss {m.Glossiness} uv {m.UvOffset}/{m.UvScale} emissive {m.EmissiveColor} x{m.EmissiveMultiple} alpha {m.Alpha}");
            for (var i = 0; i < m.Textures.Length; i++) if (m.Textures[i].Length > 0) Console.WriteLine($"     tex{i}: {m.Textures[i]}");
            if (sh.Colors is not null) Console.WriteLine($"     colors: {string.Join(" ", sh.Colors.Take(6).Select(c => c.ToString("X8")))}");
            if (sh.UVs is not null) Console.WriteLine($"     uv: {string.Join(" ", sh.UVs.Take(3))}  pos: {string.Join(" ", sh.Positions.Take(2))}  n: {sh.Normals?[0]}");
            if (sh.Billboard is not null) Console.WriteLine($"     billboard {sh.Billboard} at {sh.BillboardOrigin}");
            if (m.GreyscaleTexture is not null) Console.WriteLine($"     palette: {m.GreyscaleTexture}");
            foreach (var a in m.Animations) Console.WriteLine($"     anim {a.Target}: {a.Keys.Times.Length} keys type {a.Keys.Type} {a.Timing} first {a.Keys.Values[0]} last {a.Keys.Values[^1]}");
        }
        foreach (var ps in model.Particles)
        {
            Console.WriteLine($"  particles {ps.Name}: max {ps.MaxParticles}, rate {ps.BirthRate}{(ps.BirthRateKeys is null ? "" : " keyed")}, life {ps.LifeSpan}±{ps.LifeSpanVariation}, speed {ps.Speed}±{ps.SpeedVariation}, radius {ps.Radius}±{ps.RadiusVariation}, world {ps.WorldSpace}, hidden {ps.IsHidden}");
            Console.WriteLine($"     emitter {ps.Shape} {ps.Size} at {ps.Emitter.Translation} scale {ps.Emitter.Scale}; decl {ps.Declination}±{ps.DeclinationVariation} planar {ps.PlanarAngle}±{ps.PlanarAngleVariation}; color {ps.InitialColor}");
            Console.WriteLine($"     gravity {string.Join("; ", ps.Gravity)}; grow {ps.GrowTime} fade {ps.FadeTime} base {ps.BaseScale}; scales {ps.Scales?.Length}; simple color {ps.SimpleColor}; color keys {ps.ColorKeys?.Times.Length}; rot {ps.Rotates} {ps.RotationSpeed}; atlas {ps.SubTextures.Length} anim {ps.AnimatesSubTexture}");
            Console.WriteLine($"     {ps.Material.Kind} tex {ps.Material.Diffuse} palette {ps.Material.GreyscaleTexture} alpha 0x{ps.Material.AlphaFlags:X} f1 {ps.Material.Flags1} f2 {ps.Material.Flags2} emissive {ps.Material.EmissiveColor} x{ps.Material.EmissiveMultiple}; bounds {ps.BoundsMin}..{ps.BoundsMax}");
        }
        break;
    }
    case "npc":
    {
        // npc <game> <FormID|EditorID>: what the editor puts together for an actor base.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var key = ws.ParseFormReference(args[2]) ?? catalog.BySignature["NPC_"].FirstOrDefault(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase))?.FormKey;
        if (key is null) { Console.WriteLine("no such NPC"); break; }
        var builder = new SceneBuilder(ws, catalog);
        var look = builder.Appearance.Resolve(key.Value);
        if (look is null) { Console.WriteLine("no look (unresolved race/template)"); break; }
        var vfs = AssetFileSystem.Build(ws);
        Console.WriteLine($"{key}: race {look.Race}, traits {look.Traits}, female {look.Female}, scale {look.Scale}");
        foreach (var m in look.Models) Console.WriteLine($"  {m} {(vfs.Find(m) is { } src ? src.ToString() : "MISSING")}");
        foreach (var n in look.Notes) Console.WriteLine($"    {n}");
        break;
    }
    case "npcscan":
    {
        // npcscan <game> [n]: resolve every NPC_ (or the first n), report unresolved looks and missing meshes.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var builder = new SceneBuilder(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var limit = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;
        int total = 0, noLook = 0, missingHead = 0, missingOther = 0;
        var t = Stopwatch.StartNew();
        foreach (var record in catalog.BySignature["NPC_"].Take(limit))
        {
            total++;
            var look = builder.Appearance.Resolve(record.FormKey);
            if (look is null) { if (++noLook <= 5) Console.WriteLine($"  no look {ws.FormIdText(record.FormKey)} {record.EditorId}"); continue; }
            foreach (var m in look.Models)
            {
                var primary = m.Split(ActorAppearance.FallbackSeparator)[0];
                if (vfs.Find(primary) is not null) continue;
                if (primary.Contains("facegeom")) { if (++missingHead <= 5) Console.WriteLine($"  no facegen {ws.FormIdText(record.FormKey)} {record.EditorId}: {primary}"); }
                else if (++missingOther <= 5) Console.WriteLine($"  missing {ws.FormIdText(record.FormKey)} {record.EditorId}: {primary}");
            }
        }
        Console.WriteLine($"npcscan: {total} NPCs in {t.ElapsedMilliseconds} ms: no look {noLook}, missing facegen {missingHead}, missing other meshes {missingOther}");
        break;
    }
    case "landtest":
    {
        // landtest <game> [n]: LAND → LandscapeData → new LAND → LandscapeData for n exterior cells; heights must match.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var limit = args.Length > 2 ? int.Parse(args[2]) : 200;
        var cells = catalog.BySignature["CELL"].Where(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false }).OrderBy(c => c.FormKey.GetHashCode()).Take(limit * 3).ToList();
        int tested = 0, heightMismatch = 0, layerMismatch = 0, reported = 0, borderCount = 0, innerCount = 0;
        double borderSum = 0, innerSum = 0;
        double worstNormal = 0;
        foreach (var record in cells)
        {
            var land = lookup.CellVersions(record.FormKey).LastOrDefault(v => v.Landscape is not null)?.Landscape;
            if (land?.VertexHeightMap is null) continue;
            var data = LandscapeData.From(land);
            var written = new Mutagen.Bethesda.Skyrim.Landscape(land.FormKey, game.Release);
            data.Clone().WriteTo(written);
            var back = LandscapeData.From(written);
            var maxDiff = data.Heights.Zip(back.Heights).Max(p => Math.Abs(p.First - p.Second));
            if (maxDiff > 0.01f && ++heightMismatch <= 5) Console.WriteLine($"  heights differ by {maxDiff} in {record.EditorId ?? record.FormKey.ToString()}");
            for (var i = 0; i < data.Normals.Length; i++)
            {
                var angle = Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(data.Normals[i], back.Normals[i]), -1, 1)) * 180 / Math.PI;
                worstNormal = Math.Max(worstNormal, angle);
                var border = i % 33 == 0 || i % 33 == 32 || i / 33 == 0 || i / 33 == 32;
                if (border) { borderSum += angle; borderCount++; } else { innerSum += angle; innerCount++; }
                if (!border && angle > 30 && ++reported <= 4) Console.WriteLine($"  inner normal {data.Normals[i]} vs {back.Normals[i]} at {i % 33},{i / 33}");
            }
            var layersBefore = data.Quadrants.Sum(q => q.Layers.Count(l => l.Opacity.Any(o => o > 0.001f)) + (q.BaseTexture is null ? 0 : 1));
            var layersAfter = back.Quadrants.Sum(q => q.Layers.Count + (q.BaseTexture is null ? 0 : 1));
            if (layersBefore != layersAfter && ++layerMismatch <= 5) Console.WriteLine($"  layers {layersBefore} -> {layersAfter} in {record.EditorId ?? record.FormKey.ToString()}");
            if (++tested >= limit) break;
        }
        Console.WriteLine($"landtest: {tested} LAND records: height mismatches {heightMismatch}, layer mismatches {layerMismatch}, worst normal difference {worstNormal:0.0}° (stored vs recomputed); mean inner {innerSum / Math.Max(innerCount, 1):0.00}°, border {borderSum / Math.Max(borderCount, 1):0.00}°");
        break;
    }
    case "pex":
    {
        // pex <game> [name]: read every compiled script (or one, with its properties).
        var vfs = AssetFileSystem.Build(ws);
        var library = new ScriptLibrary(vfs);
        if (args.Length > 2)
        {
            foreach (var (prop, owner) in library.AllProperties(args[2]))
                Console.WriteLine($"  {prop.Type} {prop.Name}{(prop.IsAuto ? " auto" : "")}{(prop.IsConditional ? " conditional" : "")}{(prop.IsHidden ? " hidden" : "")}  [{owner}] {prop.Documentation}");
            Console.WriteLine($"  parent {library.Get(args[2])?.Parent}");
            break;
        }
        int ok = 0, failed = 0, properties = 0;
        var t = Stopwatch.StartNew();
        foreach (var name in library.Names)
        {
            try
            {
                var script = PexScript.Read(vfs.ReadAllBytes($@"scripts\{name}.pex")!);
                properties += script.Properties.Count;
                ok++;
            }
            catch (Exception ex)
            {
                if (++failed <= 10) Console.WriteLine($"  FAIL {name}: {ex.Message}");
            }
        }
        Console.WriteLine($"pex: {library.Names.Count} scripts in {t.ElapsedMilliseconds} ms: ok {ok}, failed {failed}, properties {properties}");
        break;
    }
    case "vmadfind":
    {
        // vmadfind <game> <signature>: base records of a type that carry scripts.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var shown = 0;
        foreach (var r in catalog.BySignature[args[2]])
        {
            var record = lookup.Resolve(r.FormKey);
            if (record?.GetType().GetProperties().FirstOrDefault(p => p.Name == "VirtualMachineAdapter")?.GetValue(record) is Mutagen.Bethesda.Skyrim.IVirtualMachineAdapterGetter { Scripts.Count: > 0 } vmad)
            {
                Console.WriteLine($"  {ws.FormIdText(r.FormKey)} {r.EditorId}: {string.Join(", ", vmad.Scripts.Select(x => $"{x.Name}({x.Properties.Count})"))}");
                if (++shown >= 8) break;
            }
        }
        break;
    }
    case "findtest":
    {
        // findtest <game> <plugin> <text>: Find Text over a plugin's records (every text field) and the catalog; time.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hits = StbEditor.Core.TextSearch.InPlugin(plugin, catalog, args[3], false);
        Console.WriteLine($"{plugin.Name}: {hits.Count} hit(s) in {clock.Elapsed.TotalSeconds:0.0} s; by type {string.Join(", ", hits.GroupBy(h => h.Signature).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var h in hits.Take(6)) Console.WriteLine($"  {h.Signature} {h.EditorId} {h.PathText}: {(h.Text.Length > 70 ? h.Text[..70] : h.Text)}{(h.Topic is { } t ? $" (topic {t})" : "")}");
        clock.Restart();
        var quick = StbEditor.Core.TextSearch.InCatalog(catalog, args[3], false);
        Console.WriteLine($"catalog: {quick.Count} hit(s) in {clock.Elapsed.TotalMilliseconds:0} ms");
        break;
    }
    case "deepcopyprobe":
    {
        var asm = typeof(Mutagen.Bethesda.Skyrim.Armor).Assembly;
        foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("Armor") && t.IsAbstract && t.IsSealed))
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(m => m.Name.StartsWith("DeepCopy")))
                Console.WriteLine($"{t.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        break;
    }
    case "copytest":
    {
        // copytest <game>: every top-level field of overridden records (several types) copied from the oldest version onto
        // a copy of the winner: that field must equal the oldest version's, every other field stay the winner's.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        int ok = 0, wrong = 0, failed = 0;
        var failures = new Dictionary<string, int>();
        // Mutable and read-only Mutagen objects list links and empty lists differently: compare what they hold.
        string Leaves(object? o) => o is null || (o is System.Collections.IEnumerable e and not string && !e.Cast<object?>().Any()) ? ""
            : string.Join("|", StbEditor.Core.RecordFields.Leaves(o, 16, int.MaxValue).Where(l => !l.PathText.EndsWith("FormKeyNullable", StringComparison.Ordinal))
                .Select(l => l.PathText + "=" + StbEditor.Core.RecordFields.Repr(l.Value)));
        foreach (var sig in new[] { "ARMO", "WEAP", "NPC_", "LVLI", "COBJ", "PERK", "RACE", "MGEF", "SPEL", "ALCH", "CONT", "FACT", "QUST", "LCTN" })
        {
            var type = StbEditor.Data.RecordLookup.GetterType(sig)!;
            foreach (var entry in catalog.BySignature[sig].Where(r => r.Plugins.Count >= 2).Take(6))
            {
                var versions = ws.LinkCache.ResolveAllSimpleContexts(entry.FormKey, type).Select(c => c.Record).ToList();
                var winner = versions[0];
                var oldest = versions[^1];
                foreach (var member in StbEditor.Core.RecordFields.PropertiesOf(winner).Select(p => p.Name).Where(n => !StbEditor.Core.RecordFields.IsRecordHeader(n) && n != "FormKey"))
                {
                    var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(winner);
                    try
                    {
                        StbEditor.Core.MemberCopy.Copy(copy, oldest, member);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures[$"{sig}.{member}: {ex.GetType().Name} {ex.Message}"[..Math.Min(120, $"{sig}.{member}: {ex.GetType().Name} {ex.Message}".Length)]] = 1;
                        continue;
                    }
                    var prop = StbEditor.Core.RecordFields.PropertiesOf(copy).First(p => p.Name == member);
                    var got = Leaves(prop.GetValue(copy));
                    var want = Leaves(StbEditor.Core.RecordFields.PropertiesOf(oldest).FirstOrDefault(p => p.Name == member)?.GetValue(oldest));
                    var baseline = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(winner);
                    var othersSame = StbEditor.Core.RecordFields.PropertiesOf(copy).Where(p => p.Name != member && !StbEditor.Core.RecordFields.IsRecordHeader(p.Name) && p.Name != "FormKey")
                        .All(p => Leaves(p.GetValue(copy)) == Leaves(StbEditor.Core.RecordFields.PropertiesOf(baseline).First(w => w.Name == p.Name).GetValue(baseline)));
                    var scalarSame = got == want;
                    if ((got == want && (got.Length > 0 || scalarSame)) && othersSame) ok++;
                    else
                    {
                        wrong++;
                        if (wrong < 6)
                        {
                            var changed = StbEditor.Core.RecordFields.PropertiesOf(copy).Where(p => p.Name != member && !StbEditor.Core.RecordFields.IsRecordHeader(p.Name) && p.Name != "FormKey")
                                .Where(p => Leaves(p.GetValue(copy)) != Leaves(StbEditor.Core.RecordFields.PropertiesOf(baseline).First(w => w.Name == p.Name).GetValue(baseline))).Select(p => p.Name).Take(4);
                            static string Cut(string t) => t.Length > 90 ? t[..90] : t;
                            foreach (var name in changed.Take(1))
                                Console.WriteLine($"    {name}: copy '{Cut(Leaves(StbEditor.Core.RecordFields.PropertiesOf(copy).First(p => p.Name == name).GetValue(copy)))}' baseline '{Cut(Leaves(StbEditor.Core.RecordFields.PropertiesOf(baseline).First(p => p.Name == name).GetValue(baseline)))}'");
                            var gotText = got.Length > 0 ? got : StbEditor.Core.RecordFields.Repr(prop.GetValue(copy));
                            var wantText = want.Length > 0 ? want : StbEditor.Core.RecordFields.Repr(StbEditor.Core.RecordFields.PropertiesOf(oldest).FirstOrDefault(p => p.Name == member)?.GetValue(oldest));
                            Console.WriteLine($"  wrong {sig} {entry.EditorId}.{member}: others changed [{string.Join(", ", changed)}]; got '{Cut(gotText)}' want '{Cut(wantText)}'");
                        }
                    }
                }
            }
        }
        Console.WriteLine($"member copies: {ok} correct, {wrong} wrong, {failed} not copyable");
        foreach (var f in failures.Keys.Take(12)) Console.WriteLine("  " + f);
        break;
    }
    case "compacttest":
    {
        // compacttest <game> <plugin> <out folder>: Compact FormIDs on a copy: every own record in 0x800-0xFFF, same
        // record count, every link to an own record resolves, and each record equals the original with links remapped.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        var outDir = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(outDir);
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var plan = StbEditor.Core.FormIdCompactor.Plan(plugin.Mod!, plugin.ModKey, ws.Active);
        Console.WriteLine($"{plugin.Name}: {plan.Own} own record(s), {plan.Moved} to renumber, fits ESL {plan.Fits}, dependents [{string.Join(", ", plan.Dependents)}]");
        var writer = new PluginWriter(ws, catalog);
        var temp = writer.Compact(active, plan.Map, light: plan.Fits);
        Console.WriteLine($"{writer.LastCompactRestored} untouched record(s) kept byte for byte");
        using var after = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(new Mutagen.Bethesda.Plugins.ModPath(plugin.ModKey, temp), game.Release);
        var afterRecords = after.EnumerateMajorRecords().ToList();
        var own = afterRecords.Where(r => r.FormKey.ModKey == plugin.ModKey).ToList();
        var ownKeys = own.Select(r => r.FormKey).ToHashSet();
        var beforeRecords = plugin.Mod!.EnumerateMajorRecords().ToList();
        var outOfRange = own.Count(r => r.FormKey.ID is < 0x800 or > 0xFFF);
        var dangling = afterRecords.SelectMany(r => r.EnumerateFormLinks()).Count(l => l.FormKey.ModKey == plugin.ModKey && !l.FormKey.IsNull && !ownKeys.Contains(l.FormKey));
        Console.WriteLine($"after: {afterRecords.Count} records (before {beforeRecords.Count}), own {own.Count}, out of range {outOfRange}, links to missing own records {dangling}, light flag {after.ModHeader.Flags}");
        var byKey = afterRecords.GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
        int same = 0, differ = 0;
        foreach (var record in beforeRecords.Where(r => r.Registration.Name is not ("Cell" or "Worldspace" or "DialogTopic")).Take(3000))
        {
            var target = plan.Map.TryGetValue(record.FormKey, out var moved) ? moved : record.FormKey;
            if (!byKey.TryGetValue(target, out var now)) { differ++; continue; }
            var expected = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.Duplicate(record, target);
            expected.RemapLinks(plan.Map);
            if (StbEditor.Core.PluginChecks.Identical(expected, now)) same++;
            else
            {
                differ++;
                if (differ < 4)
                {
                    var a = StbEditor.Core.RecordFields.Leaves(expected, 16, int.MaxValue).Select(l => (l.PathText, StbEditor.Core.RecordFields.Repr(l.Value))).ToList();
                    var b = StbEditor.Core.RecordFields.Leaves(now, 16, int.MaxValue).Select(l => (l.PathText, StbEditor.Core.RecordFields.Repr(l.Value))).ToList();
                    var diffs = a.Except(b).Take(3).Select(x => $"{x.Item1}={x.Item2}").Concat(b.Except(a).Take(3).Select(x => $"now {x.Item1}={x.Item2}"));
                    Console.WriteLine($"  differs: {record.FormKey} -> {target} {record.EditorID}: {string.Join("; ", diffs)}");
                }
            }
        }
        Console.WriteLine($"content: {same} identical after remapping, {differ} differ");
        break;
    }
    case "bsatest":
    {
        // bsatest <game> <vanilla .bsa> <out folder>: our name hash against every folder and file of a vanilla archive;
        // then an archive written from a few hundred of its files, read back by Mutagen, byte for byte.
        var path = args[2];
        var bytes = File.ReadAllBytes(path);
        var folderCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var totalFileNames = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        var fileCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
        Console.WriteLine($"{Path.GetFileName(path)}: version {version}, flags 0x{flags:X}, {folderCount} folders, {fileCount} files");
        var pos = 36;
        var folders = new List<(ulong Hash, int Count)>();
        for (var i = 0; i < folderCount; i++)
        {
            folders.Add((System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos)), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 8))));
            pos += version == 105 ? 24 : 16;
        }
        var fileHashes = new List<(string Folder, ulong Hash)>();
        int folderOk = 0, folderBad = 0;
        foreach (var (hash, count) in folders)
        {
            var len = bytes[pos];
            var name = System.Text.Encoding.ASCII.GetString(bytes, pos + 1, len - 1);
            pos += 1 + len;
            if (StbEditor.Assets.BsaWriter.Hash(name, true) == hash) folderOk++; else { folderBad++; if (folderBad < 4) Console.WriteLine($"  folder {name}: ours {StbEditor.Assets.BsaWriter.Hash(name, true):X16}, archive {hash:X16}"); }
            for (var f = 0; f < count; f++) { fileHashes.Add((name, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos)))); pos += 16; }
        }
        var names = System.Text.Encoding.ASCII.GetString(bytes, pos, totalFileNames).Split('\0');
        int fileOk = 0, fileBad = 0;
        for (var i = 0; i < fileHashes.Count; i++)
        {
            if (StbEditor.Assets.BsaWriter.Hash(names[i], false) == fileHashes[i].Hash) fileOk++;
            else { fileBad++; if (fileBad < 4) Console.WriteLine($"  file {fileHashes[i].Folder}\\{names[i]}: ours {StbEditor.Assets.BsaWriter.Hash(names[i], false):X16}, archive {fileHashes[i].Hash:X16}"); }
        }
        Console.WriteLine($"hashes: folders {folderOk} ok / {folderBad} wrong, files {fileOk} ok / {fileBad} wrong");

        // Round trip: extract some files, pack them, read back.
        var outDir = Path.GetFullPath(args[3]);
        var loose = Path.Combine(outDir, "loose");
        Directory.CreateDirectory(loose);
        var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(ws.Game.GameRelease, new Noggog.FilePath(path));
        var picked = reader.Files.Take(300).ToList();
        var map = new Dictionary<string, string>();
        foreach (var file in picked)
        {
            var target = Path.Combine(loose, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.GetBytes());
            map[file.Path] = target;
        }
        var archive = Path.Combine(outDir, "STBEditorTest.bsa");
        var written = StbEditor.Assets.BsaWriter.Write(archive, map);
        var back = Mutagen.Bethesda.Archives.Archive.CreateReader(ws.Game.GameRelease, new Noggog.FilePath(archive));
        var backFiles = back.Files.ToDictionary(f => f.Path.ToLowerInvariant(), f => f);
        var same = map.Count(kv => backFiles.TryGetValue(kv.Key.ToLowerInvariant(), out var f) && f.GetBytes().AsSpan().SequenceEqual(File.ReadAllBytes(kv.Value)));
        Console.WriteLine($"round trip: {written} written ({new FileInfo(archive).Length:N0} bytes), {backFiles.Count} read back, {same} byte for byte");
        break;
    }
    case "udrtest":
    {
        // udrtest <game> <out folder>: a plugin that deletes a vanilla reference (as old CK plugins do) is found by the
        // clean check, and the fix (undelete and disable) writes it back undeleted, disabled, sunk, enable parent player.
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var baseCatalog = RecordCatalog.Build(ws, cacheDir);
        var baseLookup = new StbEditor.Data.RecordLookup(ws, baseCatalog);
        var cellRec = baseCatalog.BySignature["CELL"].First(c => c.EditorId == "RiverwoodSleepingGiantInn");
        var cellVersion = baseLookup.CellVersionsByPlugin(cellRec.FormKey).First(v => v.Plugin.Name == "Skyrim.esm");
        var victim = cellVersion.Cell.Temporary.OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().First();
        var path = Path.Combine(outDir, "STBEditorUdrTest.esp");
        var mod = new Mutagen.Bethesda.Skyrim.SkyrimMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("STBEditorUdrTest.esp"), game.Release);
        var cell = Mutagen.Bethesda.Skyrim.CellMixIn.DeepCopy(cellVersion.Cell, new Mutagen.Bethesda.Skyrim.Cell.TranslationMask(true) { Persistent = false, Temporary = false, Landscape = false, NavigationMeshes = false });
        var deleted = Mutagen.Bethesda.Skyrim.PlacedObjectMixIn.DeepCopy(victim);
        deleted.IsDeleted = true;
        cell.Temporary.Add(deleted);
        Mutagen.Bethesda.Skyrim.CellGroupExt.AddInteriorCell(mod.Cells, cell);
        ((Mutagen.Bethesda.Plugins.Records.IModGetter)mod).WriteToBinary(path, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters { MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(ws.Active.Select(p => p.ModKey)) });
        Console.WriteLine($"made {path}: deleted override of {victim.FormKey} at {victim.Placement?.Position}");

        using var test = Workspace.Open(game, null, default, path);
        var catalog = RecordCatalog.Build(test, Path.Combine(outDir, "catalog"));
        var plugin = test.Active.First(p => p.Name == "STBEditorUdrTest.esp");
        var report = StbEditor.Core.PluginChecks.Clean(test, catalog, plugin);
        Console.WriteLine($"found: {report.DeletedReferences.Count} deleted reference(s): {string.Join(", ", report.DeletedReferences.Select(d => $"{d.Key} override {d.Override}"))}");

        var doc = new EditDocument();
        var active = ActivePlugin.ForExisting(plugin, game);
        var d = report.DeletedReferences[0];
        var context = new RefContext(d.Cell, null, d.Persistent, true, victim, victim.Base.FormKey, "STAT");
        doc.RegisterContext(d.Key, context);
        var state = new RefState(new System.Numerics.Vector3(victim.Placement!.Position.X, victim.Placement.Position.Y, victim.Placement.Position.Z),
            new System.Numerics.Vector3(victim.Placement.Rotation.X, victim.Placement.Rotation.Y, victim.Placement.Rotation.Z), victim.Scale ?? 1f, false);
        doc.Execute("udr", [(d.Key, state, state, state with { Deleted = true })], [new OverrideDraft((Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Skyrim.PlacedObjectMixIn.DeepCopy(victim), RecordPlace.Reference, context)]);
        var writer = new PluginWriter(test, catalog);
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; disabled {result.Disabled}");
        if (!result.Success) break;
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(new Mutagen.Bethesda.Plugins.ModPath(active.ModKey, result.TempPath!), game.Release);
        var fixedRef = check.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells).SelectMany(c => c.Temporary.Concat(c.Persistent)).OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().First(r => r.FormKey == victim.FormKey);
        Console.WriteLine($"written: deleted {fixedRef.IsDeleted}, flags 0x{fixedRef.MajorRecordFlagsRaw:X}, z {fixedRef.Placement?.Position.Z}, base {fixedRef.Base.FormKey}, enable parent {fixedRef.EnableParent?.Reference.FormKey} {fixedRef.EnableParent?.Flags}");
        break;
    }
    case "looksprobe":
    {
        // looksprobe <game> <npc EditorID>: the looks fields of each version of an NPC, to see what differs.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var npcKey = catalog.BySignature["NPC_"].First(r => r.EditorId == args[2]).FormKey;
        foreach (var version in ws.LinkCache.ResolveAllSimpleContexts<Mutagen.Bethesda.Skyrim.INpcGetter>(npcKey).Reverse())
        {
            Console.WriteLine($"== {version.ModKey}");
            foreach (var name in new[] { "Race", "HeadParts", "HairColor", "HeadTexture", "FaceMorph", "FaceParts", "TintLayers", "Weight", "TextureLighting" })
            {
                var value = StbEditor.Core.RecordFields.PropertiesOf(version.Record).FirstOrDefault(p => p.Name == name)?.GetValue(version.Record);
                var text = value is null ? "null" : StbEditor.Core.RecordFields.IsComposite(value)
                    ? string.Join(",", StbEditor.Core.RecordFields.Leaves(value, 8, int.MaxValue).Select(l => l.PathText + "=" + StbEditor.Core.RecordFields.Repr(l.Value)))
                    : StbEditor.Core.RecordFields.Repr(value);
                Console.WriteLine($"  {name}: {(text.Length > 150 ? text[..150] : text)}");
            }
        }
        break;
    }
    case "checktest":
    {
        // checktest <game> <plugin>: Check for Errors over one plugin, issues grouped by message kind.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var assets = AssetFileSystem.Build(ws);
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var facegen = assets.Enumerate($@"meshes\actors\character\facegendata\facegeom\{plugin.Name}\", ".nif").ToList();
        Console.WriteLine($"facegen meshes of {plugin.Name}: {facegen.Count}, e.g. {facegen.FirstOrDefault()}");
        var issues = StbEditor.Core.PluginChecks.Errors(ws, catalog, plugin, assets.Exists, assetOrigin: path => assets.Find(path) is { } source ? source.LoosePath ?? source.ArchivePath : null);
        Console.WriteLine($"{plugin.Name}: {issues.Count(i => i.Error)} error(s), {issues.Count(i => !i.Error)} warning(s) in {clock.Elapsed.TotalSeconds:0.0} s");
        foreach (var group in issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"[0-9A-F]{6}:\S+|\S+ \[\w{4}\]", "X")).OrderByDescending(g => g.Count()).Take(10))
            Console.WriteLine($"  {group.Count(),5} x {group.Key}   e.g. {group.First().Signature} {group.First().EditorId ?? group.First().Key.ToString()}: {group.First().Message}");
        break;
    }
    case "itmtest":
    {
        // itmtest <game> <plugin>: overrides identical to the version they override from the plugin's masters (ITM), and
        // deleted references (UDR) — xEdit's Quick Auto Clean targets — counted and timed.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        // The comparison must call a record identical to itself (and to a copy read from another plugin's view).
        var selfLookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var selfTypes = catalog.BySignature.Where(kv => kv.Key is not ("CELL" or "WRLD" or "NAVI")).SelectMany(kv => kv.Value.Take(3)).Take(300).ToList();
        var selfSame = selfTypes.Count(r => selfLookup.Resolve(r.FormKey) is { } x && StbEditor.Core.PluginChecks.Identical(x, x));
        Console.WriteLine($"self-check: {selfSame}/{selfTypes.Count} records identical to themselves");
        foreach (var r in selfTypes.Where(r => selfLookup.Resolve(r.FormKey) is { } x && !StbEditor.Core.PluginChecks.Identical(x, x)).Take(12))
        {
            var x = selfLookup.Resolve(r.FormKey)!;
            var diff = StbEditor.Core.RecordFields.Leaves(x).Where(l => l.Value is float f && float.IsNaN(f) || l.Value is double d && double.IsNaN(d)).Select(l => l.PathText).Take(3);
            Console.WriteLine($"  not self-identical: {r.Signature} {r.EditorId}: {string.Join(", ", diff)}");
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = StbEditor.Core.PluginChecks.Clean(ws, catalog, plugin, new Progress<string>(m => Console.Error.WriteLine(m)));
        Console.WriteLine($"{plugin.Name}: {report.Itm.Count} ITM, {report.DeletedReferences.Count} UDR, {report.Compared} overrides compared in {clock.Elapsed.TotalSeconds:0.0} s");
        foreach (var itm in report.Itm.Take(12)) Console.WriteLine($"  ITM {itm.Signature} {itm.EditorId} {itm.Key} (same as {itm.Master})");
        foreach (var udr in report.DeletedReferences.Take(5)) Console.WriteLine($"  UDR {udr.Signature} {udr.Key} in {udr.Cell}");
        break;
    }
    case "finalizetest":
    {
        // finalizetest <game> <interior EditorID> <world EditorID> <x> <y>: Finalize run on copies of vanilla navmeshes must
        // give what the CK wrote: door triangles of the interior's doors, and the exterior grid's edge links.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var doorIndex = StbEditor.Data.DoorIndex.Build(ws);
        List<Mutagen.Bethesda.Skyrim.INavigationMeshGetter> MeshesOf(Mutagen.Bethesda.Plugins.FormKey cell) => lookup.CellVersions(cell).SelectMany(v => v.NavigationMeshes)
            .Where(n => n.Data is not null).GroupBy(n => n.FormKey).Select(g => g.Last()).ToList();
        List<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter> DoorsOf(Mutagen.Bethesda.Plugins.FormKey cell) => lookup.CellVersions(cell).SelectMany(v => v.Persistent.Concat(v.Temporary))
            .OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().GroupBy(p => p.FormKey).Select(g => g.Last()).Where(p => p.TeleportDestination is { Door.IsNull: false }).ToList();
        System.Numerics.Vector3? Arrival(Mutagen.Bethesda.Skyrim.IPlacedObjectGetter door)
        {
            var other = door.TeleportDestination!.Door.FormKey;
            if (doorIndex.CellOf(other) is not { } otherCell) return null;
            var otherDoor = lookup.CellVersions(otherCell).SelectMany(v => v.Persistent.Concat(v.Temporary)).OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().LastOrDefault(p => p.FormKey == other);
            return otherDoor?.TeleportDestination?.Position is { } p ? new System.Numerics.Vector3(p.X, p.Y, p.Z) : null;
        }

        // Interiors: door triangles and XNDP.
        int same = 0, differ = 0, xndpSame = 0, xndpDiffer = 0;
        foreach (var name in args[2].Split(','))
        {
            var interior = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, name, StringComparison.OrdinalIgnoreCase)).FormKey;
            var doors = DoorsOf(interior).Select(d => (Door: d, At: Arrival(d))).Where(d => d.At is not null).ToList();
            foreach (var nav in MeshesOf(interior))
            {
                var copy = Mutagen.Bethesda.Skyrim.NavigationMeshMixIn.DeepCopy(nav);
                var got = StbEditor.Core.NavmeshFinalize.LinkDoors(copy, doors.Select(d => new StbEditor.Core.NavmeshFinalize.Door(d.Door.FormKey, d.At!.Value)).Where(d => StbEditor.Core.NavmeshFinalize.TriangleAt(copy.Data!, d.Arrival, 64) >= 0));
                foreach (var original in nav.Data!.DoorTriangles)
                {
                    var ok = got.TryGetValue(original.Door.FormKey, out var t) && t == original.TriangleBeforeDoor;
                    if (ok) same++; else { differ++; Console.WriteLine($"  {name}: door {original.Door.FormKey} CK triangle {original.TriangleBeforeDoor}, ours {(got.TryGetValue(original.Door.FormKey, out var x) ? x : -1)}"); }
                }
                foreach (var (door, _) in doors.Where(d => d.Door.NavigationDoorLink?.NavMesh.FormKey == nav.FormKey))
                {
                    if (got.TryGetValue(door.FormKey, out var t) && t == door.NavigationDoorLink!.TeleportMarkerTriangle) xndpSame++;
                    else { xndpDiffer++; Console.WriteLine($"  {name}: door {door.FormKey} XNDP {door.NavigationDoorLink!.TeleportMarkerTriangle}, ours {(got.TryGetValue(door.FormKey, out var x) ? x : -1)}"); }
                }
            }
        }
        Console.WriteLine($"door triangles: {same} as the CK, {differ} differ; door XNDP triangles: {xndpSame} as the CK, {xndpDiffer} differ");

        // Exteriors: edge links of the centre cell, recomputed after clearing them, against the CK's.
        var world = catalog.BySignature["WRLD"].First(w => string.Equals(w.EditorId, args[3], StringComparison.OrdinalIgnoreCase)).FormKey;
        int cx = int.Parse(args[4]), cy = int.Parse(args[5]);
        var meshes = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Skyrim.NavigationMesh>();
        var originals = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Skyrim.INavigationMeshGetter>();
        Mutagen.Bethesda.Plugins.FormKey? centre = null;
        foreach (var c in catalog.BySignature["CELL"].Where(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p && p.Worldspace == world && Math.Abs(p.GridX - cx) <= 1 && Math.Abs(p.GridY - cy) <= 1))
        {
            foreach (var nav in MeshesOf(c.FormKey))
            {
                meshes[nav.FormKey] = Mutagen.Bethesda.Skyrim.NavigationMeshMixIn.DeepCopy(nav);
                originals[nav.FormKey] = nav;
            }
            if (c.Winner.Cell!.GridX == cx && c.Winner.Cell.GridY == cy) centre = c.FormKey;
        }
        var finalize = centre is { } cc ? MeshesOf(cc).Select(n => n.FormKey).ToHashSet() : [];
        // Forget the centre meshes' links to other meshes (as if they were edited), then finalize them.
        foreach (var key in finalize)
        {
            var data = meshes[key].Data!;
            foreach (var tri in data.Triangles)
                for (var e = 0; e < 3; e++)
                    if (tri.Flags.HasFlag((Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag)(1 << e)) && data.EdgeLinks[e switch { 0 => tri.EdgeLink_0_1, 1 => tri.EdgeLink_1_2, _ => tri.EdgeLink_2_0 }].Unknown == 0)
                    {
                        tri.Flags &= ~(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag)(1 << e);
                        switch (e) { case 0: tri.EdgeLink_0_1 = -1; break; case 1: tri.EdgeLink_1_2 = -1; break; default: tri.EdgeLink_2_0 = -1; break; }
                    }
        }
        var changedMeshes = StbEditor.Core.NavmeshFinalize.LinkEdges(meshes, finalize);
        int linkSame = 0, linkMissing = 0, linkExtra = 0;
        foreach (var key in finalize)
        {
            var before = originals[key].Data!;
            var after = meshes[key].Data!;
            for (var t = 0; t < before.Triangles.Count; t++)
                for (var e = 0; e < 3; e++)
                {
                    var flag = (Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag)(1 << e);
                    short Edge(Mutagen.Bethesda.Skyrim.INavmeshTriangleGetter x) => e switch { 0 => x.EdgeLink_0_1, 1 => x.EdgeLink_1_2, _ => x.EdgeLink_2_0 };
                    var ck = before.Triangles[t].Flags.HasFlag(flag) && before.EdgeLinks[Edge(before.Triangles[t])].Unknown == 0 ? before.EdgeLinks[Edge(before.Triangles[t])] : null;
                    var ours = after.Triangles[t].Flags.HasFlag(flag) ? after.EdgeLinks[Edge(after.Triangles[t])] : null;
                    if (ck is null && ours is null) continue;
                    if (ck is not null && ours is not null && ck.Mesh.FormKey == ours.Mesh.FormKey && ck.TriangleIndex == ours.TriangleIndex) linkSame++;
                    else if (ck is not null && ours is null)
                    {
                        linkMissing++;
                        if (linkMissing < 6)
                        {
                            var v = before.Triangles[t].Vertices;
                            int[] cs = [v.X, v.Y, v.Z];
                            var a = before.Vertices[cs[e]]; var b = before.Vertices[cs[(e + 1) % 3]];
                            var target = originals.TryGetValue(ck.Mesh.FormKey, out var om) ? om.Data! : null;
                            var tv = target?.Triangles[ck.TriangleIndex].Vertices;
                            var tp = tv is { } q ? string.Join(" ", new[] { q.X, q.Y, q.Z }.Select(i => $"({target!.Vertices[i].X:0},{target.Vertices[i].Y:0},{target.Vertices[i].Z:0})")) : "not loaded";
                            Console.WriteLine($"  missing {key} t{t}e{e} ({a.X:0},{a.Y:0},{a.Z:0})-({b.X:0},{b.Y:0},{b.Z:0}) -> {ck.Mesh.FormKey}/{ck.TriangleIndex}: {tp}");
                        }
                    }
                    else
                    {
                        linkExtra++;
                        if (linkExtra < 6)
                        {
                            var v = before.Triangles[t].Vertices;
                            int[] cs = [v.X, v.Y, v.Z];
                            var a = before.Vertices[cs[e]]; var b = before.Vertices[cs[(e + 1) % 3]];
                            var raw = e switch { 0 => before.Triangles[t].EdgeLink_0_1, 1 => before.Triangles[t].EdgeLink_1_2, _ => before.Triangles[t].EdgeLink_2_0 };
                            Console.WriteLine($"  edge {key} t{t}e{e} ({a.X:0},{a.Y:0},{a.Z:0})-({b.X:0},{b.Y:0},{b.Z:0}) CK raw {raw} flags {before.Triangles[t].Flags}: CK {ck?.Mesh.FormKey}/{ck?.TriangleIndex}, ours {ours?.Mesh.FormKey}/{ours?.TriangleIndex}");
                        }
                    }
                }
        }
        Console.WriteLine($"exterior {args[3]} ({cx},{cy}): {finalize.Count} centre navmesh(es) among {meshes.Count}; edge links: {linkSame} as the CK, {linkMissing} missing, {linkExtra} different or extra; meshes changed {changedMeshes.Count}");
        break;
    }
    case "navprobe":
    {
        // navprobe <game> <cell EditorID>: how the game data ties navmeshes to doors and NAVI (door triangles, door XNDP,
        // NAVI entries), to check what Finalize must write.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var cellRec = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var cellVersions = lookup.CellVersions(cellRec.FormKey);
        System.Numerics.Vector3 Center(Mutagen.Bethesda.Skyrim.INavigationMeshDataGetter d, int t)
        {
            var tri = d.Triangles[t].Vertices;
            var a = d.Vertices[tri.X]; var b = d.Vertices[tri.Y]; var c = d.Vertices[tri.Z];
            return new((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
        }
        var meshes = cellVersions.SelectMany(v => v.NavigationMeshes).Where(n => n.Data is not null).GroupBy(n => n.FormKey).Select(g => g.Last()).ToList();
        foreach (var nav in meshes)
        {
            var d = nav.Data!;
            Console.WriteLine($"NAVM {nav.FormKey}: {d.Triangles.Count} tris, {d.EdgeLinks.Count} edge links (unknown values {string.Join(",", d.EdgeLinks.Select(e => e.Unknown).Distinct())}), door flag tris {d.Triangles.Count(t => t.Flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Door))}, parent {d.Parent?.GetType().Name}");
            foreach (var dt in d.DoorTriangles)
                Console.WriteLine($"  door triangle {dt.TriangleBeforeDoor} at {Center(d, dt.TriangleBeforeDoor)} unknown {dt.Unknown:X} door {dt.Door.FormKey} flags {d.Triangles[dt.TriangleBeforeDoor].Flags}");
        }
        foreach (var placed in cellVersions.SelectMany(v => v.Persistent.Concat(v.Temporary)).OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().GroupBy(p => p.FormKey).Select(g => g.Last()).Where(p => p.TeleportDestination is not null))
        {
            var tel = placed.TeleportDestination!;
            Console.WriteLine($"door {placed.FormKey} at {placed.Placement?.Position} -> {tel.Door.FormKey} marker {tel.Position}; XNDP {placed.NavigationDoorLink?.NavMesh.FormKey} tri {placed.NavigationDoorLink?.TeleportMarkerTriangle}");
            if (placed.NavigationDoorLink is { } link && meshes.FirstOrDefault(m => m.FormKey == link.NavMesh.FormKey)?.Data is { } own && link.TeleportMarkerTriangle < own.Triangles.Count)
                Console.WriteLine($"  XNDP triangle is in this cell, centre {Center(own, link.TeleportMarkerTriangle)}");
            else if (placed.NavigationDoorLink is { } far)
                Console.WriteLine($"  XNDP navmesh is elsewhere ({catalog.Get(far.NavMesh.FormKey)?.Signature ?? "not catalogued"})");
            var other = lookup.Resolve<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>(tel.Door.FormKey);
            Console.WriteLine($"  other door {tel.Door.FormKey}: at {other?.Placement?.Position}, its marker {other?.TeleportDestination?.Position}, its XNDP {other?.NavigationDoorLink?.NavMesh.FormKey} tri {other?.NavigationDoorLink?.TeleportMarkerTriangle}");
        }
        // Constants or hashes? Distinct values over the whole of Skyrim.esm.
        var skyrim = ws.Active.First(p => p.Name == "Skyrim.esm").Mod!;
        var naviAll = skyrim.NavigationMeshInfoMaps.First();
        Console.WriteLine("NAVI unknown2 top: " + string.Join(", ", naviAll.MapInfos.GroupBy(i => i.Unknown2).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key:X}x{g.Count()}")));
        Console.WriteLine("NAVI unknown top: " + string.Join(", ", naviAll.MapInfos.GroupBy(i => i.Unknown).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key:X}x{g.Count()}")));
        Console.WriteLine("NAVI linked door unknown top: " + string.Join(", ", naviAll.MapInfos.SelectMany(i => i.LinkedDoors).GroupBy(l => l.Unknown).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key:X}x{g.Count()}")));
        Console.WriteLine("NAVI parents: " + string.Join(", ", naviAll.MapInfos.GroupBy(i => i.Parent?.GetType().Name).Select(g => $"{g.Key}x{g.Count()}")) + $"; islands {naviAll.MapInfos.Count(i => i.Island is not null)}; merged {naviAll.MapInfos.Count(i => i.MergedTo.Count > 0)}; preferred flag {string.Join(",", naviAll.MapInfos.Select(i => i.PreferredMergesFlag).Distinct().Take(5))}");
        var worldInfo = naviAll.MapInfos.First(i => i.Parent is Mutagen.Bethesda.Skyrim.INavigationMapInfoWorldParentGetter);
        Console.WriteLine($"world info {worldInfo.NavigationMesh.FormKey}: point {worldInfo.Point}, parent {((Mutagen.Bethesda.Skyrim.INavigationMapInfoWorldParentGetter)worldInfo.Parent!).ParentWorldspace.FormKey} {((Mutagen.Bethesda.Skyrim.INavigationMapInfoWorldParentGetter)worldInfo.Parent!).ParentWorldspaceCoord}, unknown2 {worldInfo.Unknown2:X}");
        var doorUnknown = new Dictionary<int, int>();
        var crcs = new HashSet<uint>();
        var edgeUnknown = new Dictionary<int, int>();
        var sampled = 0;
        foreach (var block in skyrim.Cells.Records)
            foreach (var sub in block.SubBlocks)
                foreach (var c in sub.Cells)
                    foreach (var n in c.NavigationMeshes)
                    {
                        if (n.Data is not { } d || sampled++ > 300) continue;
                        foreach (var dt in d.DoorTriangles) doorUnknown[dt.Unknown] = doorUnknown.GetValueOrDefault(dt.Unknown) + 1;
                        crcs.Add(d.CrcHash);
                        foreach (var e in d.EdgeLinks) edgeUnknown[e.Unknown] = edgeUnknown.GetValueOrDefault(e.Unknown) + 1;
                    }
        Console.WriteLine($"interior navmeshes sampled {sampled}: door triangle unknown {string.Join(", ", doorUnknown.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key:X}x{k.Value}"))}; crc values {crcs.Count} ({string.Join(",", crcs.Take(3).Select(x => x.ToString("X")))}); edge link unknown {string.Join(", ", edgeUnknown.Select(k => $"{k.Key:X}x{k.Value}"))}");
        var extEdge = new Dictionary<int, int>();
        var extSampled = 0;
        foreach (var w in skyrim.Worldspaces.Where(w => w.EditorID == "Tamriel"))
            foreach (var block in w.SubCells)
                foreach (var sub in block.Items)
                    foreach (var c in sub.Items)
                        foreach (var n in c.NavigationMeshes)
                        {
                            if (n.Data is not { } d || extSampled++ > 200) continue;
                            foreach (var e in d.EdgeLinks) extEdge[e.Unknown] = extEdge.GetValueOrDefault(e.Unknown) + 1;
                        }
        Console.WriteLine($"exterior navmeshes sampled {extSampled}: edge link unknown {string.Join(", ", extEdge.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key:X}x{k.Value}"))}");

        foreach (var naviRec in catalog.BySignature.GetValueOrDefault("NAVI") ?? [])
        {
            foreach (var plugin in naviRec.Plugins.Take(3))
            {
                var navi = plugin.Mod?.NavigationMeshInfoMaps.FirstOrDefault();
                if (navi is null) continue;
                Console.WriteLine($"NAVI {naviRec.FormKey} in {plugin.Name}: {navi.MapInfos.Count} infos, version {navi.NavMeshVersion}");
                foreach (var info in navi.MapInfos.Where(i => meshes.Any(m => m.FormKey == i.NavigationMesh.FormKey)))
                    Console.WriteLine($"  info {info.NavigationMesh.FormKey}: point {info.Point} unknown {info.Unknown:X} flag {info.PreferredMergesFlag:X} merged {info.MergedTo.Count} preferred {info.PreferredMerges.Count} doors [{string.Join(", ", info.LinkedDoors.Select(l => $"{l.Unknown:X}:{l.Door.FormKey}"))}] island {info.Island is not null} unknown2 {info.Unknown2:X} parent {info.Parent?.GetType().Name}");
            }
        }
        break;
    }
    case "navtest":
    {
        // navtest <game> [n]: for n navmeshes, rebuild the lookup grid and neighbour links like the editor does and compare with the file.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var limit = args.Length > 2 ? int.Parse(args[2]) : 300;
        int meshes = 0, gridSame = 0, linksSame = 0, gridSizeSame = 0, gridSuperset = 0;
        static List<HashSet<ushort>> GridCells(byte[] grid)
        {
            var cells = new List<HashSet<ushort>>();
            for (var p = 0; p + 4 <= grid.Length;)
            {
                var n = BitConverter.ToInt32(grid, p);
                p += 4;
                var set = new HashSet<ushort>();
                for (var k = 0; k < n && p + 2 <= grid.Length; k++, p += 2) set.Add(BitConverter.ToUInt16(grid, p));
                cells.Add(set);
            }
            return cells;
        }
        foreach (var cellRec in catalog.BySignature["CELL"].OrderBy(c => c.FormKey.GetHashCode()))
        {
            foreach (var nav in lookup.CellVersions(cellRec.FormKey).LastOrDefault()?.NavigationMeshes ?? [])
            {
                if (nav.Data is not { } d || d.Triangles.Count == 0) continue;
                meshes++;
                var grid = NavmeshTools.BuildGrid(d.Vertices, d.Triangles, d.NavmeshGridDivisor, new System.Numerics.Vector2(d.Min.X, d.Min.Y), d.MaxDistanceX, d.MaxDistanceY);
                var original = d.NavmeshGrid.ToArray();
                if (grid.AsSpan().SequenceEqual(original)) gridSame++;
                if (GridCells(grid).Zip(GridCells(original)).All(p => p.Second.IsSubsetOf(p.First))) gridSuperset++;
                else if (grid.Length == original.Length) gridSizeSame++;
                else if (meshes - gridSame <= 3) Console.WriteLine($"  grid differs {nav.FormKey}: {grid.Length} vs {original.Length} bytes, divisor {d.NavmeshGridDivisor}, tris {d.Triangles.Count}");

                var copy = (Mutagen.Bethesda.Skyrim.NavigationMesh)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(nav);
                NavmeshTools.Rebuild(copy);
                var same = copy.Data!.Triangles.Count == d.Triangles.Count && copy.Data.Triangles.Zip(d.Triangles).All(p =>
                    p.First.EdgeLink_0_1 == p.Second.EdgeLink_0_1 && p.First.EdgeLink_1_2 == p.Second.EdgeLink_1_2 && p.First.EdgeLink_2_0 == p.Second.EdgeLink_2_0 && p.First.Flags == p.Second.Flags);
                if (same) linksSame++;
                if (meshes >= limit) break;
            }
            if (meshes >= limit) break;
        }
        Console.WriteLine($"navtest: {meshes} navmeshes: grid identical {gridSame}, same size {gridSizeSame}, ours contains the game's {gridSuperset}; neighbour links reproduced {linksSame}");
        break;
    }
    case "facegenprobe":
    {
        // facegenprobe <game> <NPC FormID> [plugin]: an NPC's head parts with their TRI morphs, and the CK FaceGen head.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var key = ws.ParseFormReference(args[2])!.Value;
        var contexts = ws.LinkCache.ResolveAllSimpleContexts<Mutagen.Bethesda.Skyrim.INpcGetter>(key).ToList();
        var chosen = args.Length > 3 ? contexts.First(c => c.ModKey.FileName.String.Equals(args[3], StringComparison.OrdinalIgnoreCase)) : contexts[0];
        var npc = chosen.Record;
        var race = lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(npc.Race.FormKey)!;
        var female = npc.Configuration.Flags.HasFlag(Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female);
        Console.WriteLine($"{npc.EditorID} from {chosen.ModKey}: race {race.EditorID}, female {female}, weight {npc.Weight}, clamps main {race.FacegenMainClamp} face {race.FacegenFaceClamp}");
        if (npc.FaceMorph is { } fm) Console.WriteLine($"  morph: {string.Join(", ", fm.GetType().GetProperties().Where(p => p.PropertyType == typeof(float)).Select(p => $"{p.Name}={p.GetValue(fm)}"))}");
        if (npc.FaceParts is { } fp) Console.WriteLine($"  parts: nose {fp.Nose} eyes {fp.Eyes} mouth {fp.Mouth} unknown {fp.Unknown}");
        var head = female ? race.HeadData?.Female : race.HeadData?.Male;
        Console.WriteLine($"  race head parts: {string.Join(", ", (head?.HeadParts ?? []).Select(h => $"{h.Number}:{lookup.Resolve<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(h.Head.FormKey)?.EditorID}"))}");
        var parts = new List<Mutagen.Bethesda.Skyrim.IHeadPartGetter>();
        var queue = new Queue<Mutagen.Bethesda.Plugins.FormKey>(npc.HeadParts.Select(h => h.FormKey));
        foreach (var h in head?.HeadParts ?? []) if (!h.Head.IsNull) queue.Enqueue(h.Head.FormKey);
        var seenParts = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            if (!seenParts.Add(k) || lookup.Resolve<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(k) is not { } part) continue;
            parts.Add(part);
            foreach (var extra in part.ExtraParts) queue.Enqueue(extra.FormKey);
        }
        foreach (var part in parts)
        {
            Console.WriteLine($"  part {part.EditorID} [{part.Type}] flags {part.Flags} model {part.Model?.File.GivenPath}");
            if (part.Model?.File.GivenPath is { } file && vfs.ReadAllBytes(AssetFileSystem.MeshPath(file)) is { } nif)
                foreach (var sh in NifFile.Load(nif, file, raw: true).Shapes) Console.WriteLine($"     shape {sh.Name}: {sh.Positions.Length} v, first {sh.Positions[0]}");
            foreach (var p in part.Parts)
            {
                var tri = p.FileName?.GivenPath;
                var bytes = tri is null ? null : vfs.ReadAllBytes(AssetFileSystem.MeshPath(tri));
                if (bytes is null) { Console.WriteLine($"     {p.PartType} {tri}: MISSING"); continue; }
                var t = TriFile.Load(bytes);
                Console.WriteLine($"     {p.PartType} {tri}: {t.Vertices.Length} v, {t.Morphs.Count} morphs, {t.StatMorphs.Count} stat; {string.Join(" ", t.Morphs.Take(args.Contains("--all") ? 400 : 30).Select(m => $"{m.Name}({m.Scale:G3})"))}");
            }
        }
        var facegen = StbEditor.Render.ActorAppearance.FaceGenPath(key);
        if (vfs.ReadAllBytes(facegen) is { } fg)
            foreach (var sh in NifFile.Load(fg, facegen, raw: true).Shapes) Console.WriteLine($"  facegen shape {sh.Name}: {sh.Positions.Length} v, first {sh.Positions[0]}, tex {string.Join(" | ", sh.Material.Textures.Where(t => t.Length > 0))}");
        else Console.WriteLine($"  no facegen at {facegen}");
        break;
    }
    case "facegencheck":
    {
        // facegencheck <game> <NPC FormID|#count> [--plugin X] [--winner] [--bad] [--faces]: the FaceGen head computed from the head parts' TRI morphs against
        // the CK's file, shape by shape (rms and worst vertex error). A count checks that many vanilla NPCs.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var parts = catalog.BySignature["HDPT"].ToDictionary(r => r.EditorId ?? "", r => r.FormKey, StringComparer.OrdinalIgnoreCase);
        var keys = !args[2].StartsWith('#') && ws.ParseFormReference(args[2]) is { } one ? [one]
            : catalog.BySignature["NPC_"].Where(r => r.FormKey.ModKey.FileName.String.Equals(args.SkipWhile(x => x != "--plugin").Skip(1).FirstOrDefault() ?? "Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.FormKey).Take(int.Parse(args[2].TrimStart('#'))).ToList();
        var verbose = keys.Count == 1;
        var stats = new Dictionary<string, (int Shapes, int Good, double Worst)>();
        foreach (var key in keys)
        {
            var facegen = StbEditor.Render.ActorAppearance.FaceGenPath(key);
            if (vfs.ReadAllBytes(facegen) is not { } fgBytes) continue;
            // The version the CK file was made from: the defining plugin's (vanilla), or the winner (--winner).
            var npcVersions = ws.LinkCache.ResolveAllSimpleContexts<Mutagen.Bethesda.Skyrim.INpcGetter>(key).ToList();
            var npc = (args.Contains("--winner") ? npcVersions.First() : npcVersions.Last()).Record;
            var race = lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(npc.Race.FormKey);
            if (race is null) continue;
            NifModel fg;
            try { fg = NifFile.Load(fgBytes, facegen, raw: true); } catch { continue; }
            foreach (var shape in fg.Shapes)
            {
                if (!parts.TryGetValue(shape.Name, out var partKey) || lookup.Resolve<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(partKey) is not { } part) continue;
                var file = part.Model?.File.GivenPath;
                var source = file is null || vfs.ReadAllBytes(AssetFileSystem.MeshPath(file)) is not { } nifBytes ? null : NifFile.Load(nifBytes, file, raw: true).Shapes.FirstOrDefault();
                if (source is null || source.Positions.Length != shape.Positions.Length) continue;
                var predicted = StbEditor.Render.FaceGenMorphs.Apply(source.Positions, npc, (race.MorphRace.IsNull ? race : lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(race.MorphRace.FormKey) ?? race).EditorID,
                    (race.Keywords ?? []).Any(k => lookup.Resolve<Mutagen.Bethesda.Skyrim.IKeywordGetter>(k.FormKey)?.EditorID == "Vampire"), part, p => (args.Contains("--archived-tri") ? vfs.ReadArchived(p) : null) ?? vfs.ReadAllBytes(p), out var used);
                double sum = 0, worst = 0;
                var worstAt = 0;
                for (var i = 0; i < predicted.Length; i++)
                {
                    var e = (predicted[i] - shape.Positions[i]).Length();
                    sum += e * e;
                    if (e > worst) { worst = e; worstAt = i; }
                }
                var rms = Math.Sqrt(sum / predicted.Length);
                var type = part.Type?.ToString() ?? "?";
                var st = stats.GetValueOrDefault(type);
                stats[type] = (st.Shapes + 1, st.Good + (worst < 0.01 ? 1 : 0), Math.Max(st.Worst, worst));
                if (args.Contains("--faces") && type == "Face")
                {
                    // A FaceGen made from other head parts than the record names is stale (the record changed after it).
                    var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pending = new Queue<Mutagen.Bethesda.Plugins.FormKey>(npc.HeadParts.Select(h => h.FormKey));
                    while (pending.Count > 0 && named.Count < 40)
                        if (lookup.Resolve<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(pending.Dequeue()) is { } hp && named.Add(hp.EditorID ?? ""))
                            foreach (var extra in hp.ExtraParts) pending.Enqueue(extra.FormKey);
                    var stale = npc.HeadParts.Count > 0 && fg.Shapes.Any(x => x.Name.Contains("Hair", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Eyes", StringComparison.OrdinalIgnoreCase)) && fg.Shapes.Count(x => named.Contains(x.Name)) < Math.Min(named.Count, fg.Shapes.Count) - 1;
                    Console.WriteLine($"{(worst < 0.06 ? "OK " : "BAD")} {(stale ? "stale" : "     ")} {npc.EditorID,-28} {race.EditorID,-22} w {npc.Weight,3:0} worst {worst:0.000} parts {npc.FaceParts?.Nose}/{npc.FaceParts?.Eyes}/{npc.FaceParts?.Mouth}/{npc.FaceParts?.Unknown} {used}");
                }
                if (verbose || worst > 0.05 && args.Contains("--bad"))
                    Console.WriteLine($"{npc.EditorID} {shape.Name} [{type}]: rms {rms:0.00000}, worst {worst:0.00000} at {worstAt} ({shape.Positions[worstAt]} vs {predicted[worstAt]}); {used}");
            }
        }
        foreach (var (type, st) in stats.OrderBy(x => x.Key)) Console.WriteLine($"{type}: {st.Good}/{st.Shapes} shapes within 0.01, worst {st.Worst:0.0000}");
        break;
    }
    case "nifblocks":
    {
        // nifblocks <game> <mesh>: block list (type, size, name) and header strings.
        var vfs = AssetFileSystem.Build(ws);
        var bytes = File.Exists(args[2]) ? File.ReadAllBytes(args[2]) : vfs.ReadAllBytes(args[2])!;
        var (blocks, strings) = NifFile.Blocks(bytes);
        for (var i = 0; i < blocks.Count; i++) Console.WriteLine($"  {i,3} {blocks[i].Type,-32} {blocks[i].Size,7} {blocks[i].Name}");
        Console.WriteLine($"  strings: {string.Join(" | ", strings)}");
        break;
    }
    case "facegentint":
        FaceGenTests.Tint(ws, args);
        break;
    case "facegenbuild":
        FaceGenTests.Build(ws, args);
        break;
    case "navgen":
        FaceGenTests.NavGen(ws, args);
        break;
    case "facegensliders":
        FaceGenTests.Sliders(ws, args);
        break;
    case "reflect":
    {
        // reflect <game> <Mutagen getter interface, e.g. INpcGetter>: properties, to write code against.
        var type = typeof(Mutagen.Bethesda.Skyrim.ISkyrimModGetter).Assembly.GetTypes().FirstOrDefault(x => x.Name == args[2] || x.FullName?.EndsWith("." + args[2]) == true);
        if (type is null && args.Contains("--recast"))
        {
            // Navmesh generation library: constructors and methods too.
            type = typeof(DotRecast.Recast.RcConfig).Assembly.GetTypes().Concat(typeof(DotRecast.Core.RcContext).Assembly.GetTypes())
                .FirstOrDefault(x => x.Name == args[2]);
            if (type is null) { Console.WriteLine("no such type"); break; }
            foreach (var c in type.GetConstructors()) Console.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            foreach (var f in type.GetFields()) Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
            foreach (var pr in type.GetProperties()) Console.WriteLine($"  prop {pr.PropertyType.Name} {pr.Name}");
            break;
        }
        if (type is null) { Console.WriteLine("no such type"); break; }
        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type)) Console.WriteLine($"  {value} = 0x{Convert.ToUInt64(value):X}");
            break;
        }
        foreach (var t in new[] { type }.Concat(type.GetInterfaces()))
            foreach (var prop in t.GetProperties())
                Console.WriteLine($"  {t.Name}.{prop.Name}: {prop.PropertyType.ToString().Replace("Mutagen.Bethesda.Skyrim.", "").Replace("Mutagen.Bethesda.Plugins.", "")}");
        break;
    }
    case "bsaextract":
    {
        // bsaextract <game> <archive.bsa> <path inside> <out file>: straight from one archive, ignoring loose overrides.
        var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(ws.Game.GameRelease, new Noggog.FilePath(args[2]));
        var wanted = args[3].Replace('/', '\\');
        var file = reader.Files.FirstOrDefault(f => string.Equals(f.Path, wanted, StringComparison.OrdinalIgnoreCase));
        if (file is null) { Console.WriteLine($"{args[3]} not in {args[2]}"); break; }
        File.WriteAllBytes(args[4], file.GetBytes());
        Console.WriteLine($"{file.Path} -> {args[4]}");
        break;
    }
    case "extract":
    {
        var vfs = AssetFileSystem.Build(ws);
        File.WriteAllBytes(args[3], vfs.ReadAllBytes(args[2])!);
        Console.WriteLine($"{args[2]} <- {vfs.Find(args[2])} -> {args[3]}");
        break;
    }
    case "memprobe":
    {
        // memprobe <exterior cell EditorID>: GC heap after each step of loading an exterior grid.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        long Mb() => GC.GetTotalMemory(true) >> 20;
        Console.WriteLine($"after catalog: {Mb()} MB");
        var cell = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var p = cell.Winner.Cell!;
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var world = catalog.Get(p.Worldspace!.Value)!;
        Console.WriteLine($"world in {world.Plugins.Count} plugins");
        var persistent = lookup.PersistentCellVersions(p.Worldspace!.Value);
        Console.WriteLine($"persistent cell versions {persistent.Count}: {Mb()} MB");
        var refs = persistent.Sum(c => c.Persistent.Count + c.Temporary.Count);
        Console.WriteLine($"persistent refs {refs}: {Mb()} MB");
        var versions = lookup.CellVersions(cell.FormKey);
        Console.WriteLine($"one cell, {versions.Count} versions: {Mb()} MB");
        var n = versions.Sum(c => c.Temporary.Count + c.Persistent.Count);
        Console.WriteLine($"its refs {n}: {Mb()} MB");
        var again = lookup.CellVersions(cell.FormKey);
        Console.WriteLine($"same cell again (both kept): {Mb()} MB");
        GC.KeepAlive(again); GC.KeepAlive(versions);
        var builder = new SceneBuilder(ws, catalog);
        var scene = builder.BuildExterior(p.Worldspace!.Value, p.GridX, p.GridY, 2);
        Console.WriteLine($"BuildExterior r2 {scene.Objects.Count} objects: {Mb()} MB");
        scene.Navmeshes.Clear();
        Console.WriteLine($"navmeshes cleared: {Mb()} MB");
        scene.Terrain.Clear();
        Console.WriteLine($"terrain cleared: {Mb()} MB");
        scene.Objects.Clear();
        Console.WriteLine($"objects cleared: {Mb()} MB");
        scene = null;
        Console.WriteLine($"scene dropped: {Mb()} MB");
        break;
    }
    case "scene":
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var cell = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var builder = new SceneBuilder(ws, catalog);
        var t = Stopwatch.StartNew();
        var p = cell.Winner.Cell!;
        var scene = p.IsInterior ? builder.BuildInterior(cell.FormKey) : builder.BuildExterior(p.Worldspace!.Value, p.GridX, p.GridY, 0);
        Console.WriteLine($"{scene.Title}: {scene.Objects.Count} objects, {scene.Terrain.Count} terrain, {t.ElapsedMilliseconds} ms, focus {scene.Focus}");
        foreach (var tc in scene.Terrain)
            Console.WriteLine($"  land {tc.X},{tc.Y}: h min {tc.Heights.Min():0} max {tc.Heights.Max():0} corner {tc.HeightAt(0,0):0} center {tc.HeightAt(16,16):0}; layers {string.Join(" | ", tc.Quadrants.Select(q => q.Count + ":" + Path.GetFileName(q[0].TexturePath)))}");
        var zs = scene.Objects.Select(o => o.Position.Z).Order().ToList();
        if (zs.Count > 0) Console.WriteLine($"  ref z: min {zs[0]:0} median {zs[zs.Count / 2]:0} max {zs[^1]:0}");
        var grep = args.SkipWhile(x => x != "--grep").Skip(1).FirstOrDefault();
        var listed = grep is null ? scene.Objects.Take(8)
            : scene.Objects.Where(o => (o.ModelPath ?? "").Contains(grep, StringComparison.OrdinalIgnoreCase) || (o.BaseEditorId ?? "").Contains(grep, StringComparison.OrdinalIgnoreCase));
        foreach (var o in listed) Console.WriteLine($"  {o.BaseEditorId} {o.BaseSignature} {o.Position} rot {o.Rotation} {o.ModelPath} ref {o.Reference}");
        Console.WriteLine($"  focus {scene.Focus}");
        if (args.Contains("--scripts"))
            foreach (var o in scene.Objects.Where(o => o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter { VirtualMachineAdapter: { } }).Take(10))
                Console.WriteLine($"  scripted ref {ws.FormIdText(o.Reference)} {o.BaseEditorId}: {string.Join(", ", ((Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)o.Record!).VirtualMachineAdapter!.Scripts.Select(x => $"{x.Name}({x.Properties.Count})"))}");
        if (args.Length > 3 && args[3] == "--shaders")
        {
            var vfs = AssetFileSystem.Build(ws);
            foreach (var path in scene.Objects.Where(o => o.ModelPath is not null).Select(o => o.ModelPath!).Distinct())
            {
                var bytes = vfs.ReadAllBytes(path);
                if (bytes is null) { Console.WriteLine($"  MISSING {path}"); continue; }
                var model = NifFile.Load(bytes, path);
                foreach (var sh in model.Shapes)
                {
                    var m = sh.Material;
                    var interesting = m.Kind != ShaderKind.Lighting || (m.Diffuse is null) || sh.IsEditorMarker || sh.IsHidden;
                    if (interesting) Console.WriteLine($"  {path} :: {sh.Name} {m.Kind} hidden={sh.IsHidden} marker={sh.IsEditorMarker} diffuse={m.Diffuse} emis={m.EmissiveColor} x{m.EmissiveMultiple} alpha=0x{m.AlphaFlags:X} col={sh.Colors is not null}");
                }
            }
        }
        break;
    }
    case "sweep":
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var builder = new SceneBuilder(ws, catalog);
        var cells = catalog.BySignature["CELL"].Where(c => c.Winner.Cell is { IsPersistentCell: false }).ToList();
        var rng = new Random(7);
        var sample = cells.OrderBy(_ => rng.Next()).Take(int.Parse(args.Length > 2 ? args[2] : "200")).ToList();
        int ok = 0, failed = 0, objects = 0, warnings = 0, noModel = 0;
        var t = Stopwatch.StartNew();
        foreach (var c in sample)
        {
            try
            {
                var p = c.Winner.Cell!;
                var scene = p.IsInterior ? builder.BuildInterior(c.FormKey) : builder.BuildExterior(p.Worldspace!.Value, p.GridX, p.GridY, 0);
                ok++; objects += scene.Objects.Count; warnings += scene.Warnings.Count;
                noModel += scene.Objects.Count(o => o.Kind == SceneObjectKind.Marker);
                if (scene.Warnings.Count > 0 && warnings < 10) Console.WriteLine($"  warn {c.EditorId}: {scene.Warnings[0]}");
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 10) Console.WriteLine($"  FAIL {c.EditorId} {ws.FormIdText(c.FormKey)}: {ex.GetType().Name} {ex.Message}");
            }
        }
        Console.WriteLine($"sweep: {sample.Count} cells in {t.ElapsedMilliseconds} ms: ok {ok}, failed {failed}, objects {objects}, markers {noModel}, warnings {warnings}");
        break;
    }
    case "formtest":
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var perType = args.Length > 3 && int.TryParse(args[3], out var n) ? n : 3;
        FormTest.Run(ws, game, catalog, args[2], perType, args.Skip(4).ToArray());
        break;
    }
    case "proptest":
    {
        // proptest <game> <out folder> [existing plugin]: reference property edits (lock, owner, linked ref,
        // flags, teleport) on Sleeping Giant Inn references, saved and read back.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = args[2];
        Directory.CreateDirectory(outDir);
        ActivePlugin active;
        if (args.Length > 3)
        {
            var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
            var copy = Path.Combine(outDir, plugin.Name);
            File.Copy(plugin.Location.Path, copy, overwrite: true);
            active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        }
        else active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorPropTest.esp"), light: true, master: false, game, ws.DefaultEncoding);

        var cellRec = catalog.BySignature["CELL"].First(c => c.EditorId == "RiverwoodSleepingGiantInn");
        var scene = new SceneBuilder(ws, catalog).BuildInterior(cellRec.FormKey);
        var doc = new EditDocument();
        var door = scene.Objects.First(o => o.BaseSignature == "DOOR" && o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter);
        var container = scene.Objects.First(o => o.BaseSignature == "CONT" && o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter);
        RefContext Ctx(SceneObject o) => new(o.Cell, null, o.IsPersistent, true, o.Record, o.Base, o.BaseSignature);
        var key = catalog.BySignature["KEYM"].First();
        var owner = catalog.BySignature["FACT"].First();

        var c = (Mutagen.Bethesda.Skyrim.PlacedObject)doc.WorkingCopy(container.Reference, container.Record!);
        c.Lock = new Mutagen.Bethesda.Skyrim.LockData { Level = Mutagen.Bethesda.Skyrim.LockLevel.Expert };
        c.Lock.Key.SetTo(key.FormKey);
        c.Owner.SetTo(owner.FormKey);
        c.FactionRank = 1;
        c.LinkedReferences.Add(new Mutagen.Bethesda.Skyrim.LinkedReferences());
        c.LinkedReferences[^1].Reference.SetTo(door.Reference);
        c.MajorRecordFlagsRaw |= 0x800;
        c.VirtualMachineAdapter = new Mutagen.Bethesda.Skyrim.VirtualMachineAdapter { Version = 5, ObjectFormat = 2 };
        var entry = new Mutagen.Bethesda.Skyrim.ScriptEntry { Name = "defaultSetStageTrigSCRIPT" };
        entry.Properties.Add(new Mutagen.Bethesda.Skyrim.ScriptIntProperty { Name = "stage", Data = 20, Flags = Mutagen.Bethesda.Skyrim.ScriptProperty.Flag.Edited });
        var questProp = new Mutagen.Bethesda.Skyrim.ScriptObjectProperty { Name = "myQuest", Alias = -1, Flags = Mutagen.Bethesda.Skyrim.ScriptProperty.Flag.Edited };
        questProp.Object.SetTo(catalog.BySignature["QUST"].First().FormKey);
        entry.Properties.Add(questProp);
        c.VirtualMachineAdapter.Scripts.Add(entry);
        doc.CommitEdit("container", c, RecordPlace.Reference, Ctx(container));
        doc.Undo();
        Console.WriteLine($"after undo: override {doc.Override(container.Reference) is not null}");
        doc.Redo();

        var d = (Mutagen.Bethesda.Skyrim.PlacedObject)doc.WorkingCopy(door.Reference, door.Record!);
        d.TeleportDestination ??= new Mutagen.Bethesda.Skyrim.TeleportDestination();
        d.TeleportDestination.Door.SetTo(container.Reference);
        d.TeleportDestination.Position = new Noggog.P3Float(1, 2, 3);
        doc.CommitEdit("door", d, RecordPlace.Reference, Ctx(door));

        // Base records: a vanilla STAT (new override) and, when testing into a plugin, one of its own records.
        var lookupT = new RecordLookup(ws, catalog);
        var stat = catalog.BySignature["STAT"].First(r => r.FormKey.ModKey.FileName == "Skyrim.esm" && r.Plugins.Count == 1);
        var statCopy = (Mutagen.Bethesda.Skyrim.Static)doc.WorkingCopy(stat.FormKey, lookupT.Resolve(stat.FormKey)!);
        statCopy.EditorID += "_STBTest";
        doc.CommitEdit("stat", statCopy, RecordPlace.TopLevel);
        CatalogRecord? own = args.Length > 3 ? catalog.BySignature["MISC"].FirstOrDefault(r => r.FormKey.ModKey.FileName.String.Equals(args[3], StringComparison.OrdinalIgnoreCase) && r.WinningPlugin.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase)) : null;
        if (own is not null)
        {
            var ownCopy = (Mutagen.Bethesda.Skyrim.MiscItem)doc.WorkingCopy(own.FormKey, lookupT.Resolve(own.FormKey)!);
            ownCopy.Value = 12345;
            doc.CommitEdit("misc", ownCopy, RecordPlace.TopLevel);
        }

        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; overridden {result.Overridden}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(5)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;
        File.Move(result.TempPath!, active.Path, overwrite: true);
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
        foreach (var r in check.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().Where(r => r.FormKey == door.Reference || r.FormKey == container.Reference))
            Console.WriteLine($"  {r.FormKey}: scripts {string.Join(", ", r.VirtualMachineAdapter?.Scripts.Select(x => $"{x.Name}[{string.Join(" ", x.Properties.Select(p => p switch { Mutagen.Bethesda.Skyrim.IScriptIntPropertyGetter i => $"{p.Name}={i.Data}", Mutagen.Bethesda.Skyrim.IScriptObjectPropertyGetter o => $"{p.Name}={o.Object.FormKey}", _ => p.Name }))}]") ?? [])} flags 0x{(uint)r.MajorRecordFlagsRaw:X} lock {r.Lock?.Level} key {r.Lock?.Key.FormKey} owner {r.Owner.FormKeyNullable} rank {r.FactionRank} links {r.LinkedReferences.Count} teleport {r.TeleportDestination?.Door.FormKey} @ {r.TeleportDestination?.Position} pos {r.Placement?.Position}");
        Console.WriteLine($"  original door pos {door.Position}, container pos {container.Position}");
        var statBack = check.Statics.Where(x => x.FormKey == stat.FormKey).ToList();
        Console.WriteLine($"  STAT {stat.FormKey}: {statBack.Count} copy, editor id {statBack.FirstOrDefault()?.EditorID}");
        if (own is not null)
        {
            var miscBack = check.MiscItems.Where(x => x.FormKey == own.FormKey).ToList();
            Console.WriteLine($"  own MISC {own.FormKey}: {miscBack.Count} copy, value {miscBack.FirstOrDefault()?.Value}");
        }
        break;
    }
    case "newtest":
    {
        // newtest <game> <out folder>: new interior cell + new worldspace (with a draft cell and an
        // auto-created one), references in each, saved into a new light plugin in the folder.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = args[2];
        Directory.CreateDirectory(outDir);
        ActivePlugin active;
        if (args.Length > 3)
        {
            // Into a copy of an existing plugin: its untouched records must survive byte for byte.
            var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
            var copy = Path.Combine(outDir, plugin.Name);
            File.Copy(plugin.Location.Path, copy, overwrite: true);
            active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        }
        else
        {
            active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorNewTest.esp"), light: true, master: false, game, ws.DefaultEncoding);
        }
        var doc = new EditDocument();
        var release = game.Release;

        var barrel = catalog.BySignature["STAT"].First(r => r.EditorId == "FarmhouseBarrel01" || r.EditorId?.StartsWith("Barrel", StringComparison.OrdinalIgnoreCase) == true);
        var template = catalog.BySignature.GetValueOrDefault("LGTM")?.FirstOrDefault(t => t.FormKey.ModKey.FileName == "Skyrim.esm");
        var lookup = new RecordLookup(ws, catalog);
        var interior = NewRecords.InteriorCell(active.Allocate(), release, "STBTestInterior", "Тестовая комната", LightingSpec.Default,
            template is null ? null : lookup.Resolve<Mutagen.Bethesda.Skyrim.ILightingTemplateGetter>(template.FormKey));
        doc.AddDraft("New cell", interior);

        var climate = catalog.BySignature.GetValueOrDefault("CLMT")?.FirstOrDefault()?.FormKey;
        var water = catalog.BySignature.GetValueOrDefault("WATR")?.FirstOrDefault()?.FormKey;
        var world = NewRecords.Worldspace(active.Allocate(), active.Allocate(), release, "STBTestWorld", "Тестовый мир", climate, water, 0f, 2048f, smallWorld: true);
        doc.AddDraft("New worldspace", world);
        var cell00 = NewRecords.ExteriorCell(active.Allocate(), release, world.Key, 0, 0);
        doc.AddDraft("New cell", cell00);

        void Place(Mutagen.Bethesda.Plugins.FormKey cell, Mutagen.Bethesda.Plugins.FormKey? worldKey, bool interiorCell, System.Numerics.Vector3 position, bool persistent = false)
        {
            var key = active.Allocate();
            doc.RegisterContext(key, new RefContext(cell, worldKey, persistent, interiorCell, null, barrel.FormKey, barrel.Signature));
            doc.Execute("place", [(key, EditDocument.Nonexistent, EditDocument.Nonexistent, new RefState(position, default, 1f, false))]);
        }

        Place(interior.Key, null, true, new(0, 0, 0));
        Place(cell00.Key, world.Key, false, new(100, 100, 0));
        Place(cell00.Key, world.Key, false, new(5000, 100, 0)); // lands in (1, 0): no cell yet
        Place(world.PersistentCell, world.Key, false, new(200, 200, 0), persistent: true);

        var writer = new PluginWriter(ws, catalog);
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; created {result.Created}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(10)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;
        File.Move(result.TempPath!, active.Path, overwrite: true);

        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, release);
        foreach (var c in check.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).Where(c => c.FormKey.ModKey == active.ModKey && c.FormKey.ID >= 0x800 && doc.Drafts.ContainsKey(c.FormKey)))
            Console.WriteLine($"  interior CELL {c.FormKey} {c.EditorID} \"{c.Name?.String}\" flags {c.Flags} template {c.LightingTemplate.FormKey} ambient {c.Lighting?.AmbientColor} refs {c.Temporary.Count}+{c.Persistent.Count}");
        foreach (var w in check.Worldspaces.Where(w => doc.Drafts.ContainsKey(w.FormKey)))
        {
            Console.WriteLine($"  WRLD {w.FormKey} {w.EditorID} \"{w.Name?.String}\" flags {w.Flags} climate {w.Climate.FormKey} water {w.Water.FormKey} land {w.LandDefaults?.DefaultLandHeight}/{w.LandDefaults?.DefaultWaterHeight} persistent cell {w.TopCell?.FormKey} refs {w.TopCell?.Persistent.Count}");
            foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                Console.WriteLine($"    CELL {c.FormKey} grid {c.Grid?.Point} refs {c.Temporary.Count}+{c.Persistent.Count}");
        }
        break;
    }
    case "dialtest":
    {
        // dialtest <game> <out folder> [existing plugin]: a vanilla topic's line edited, a new line, the topic's priority
        // changed; the plugin must hold the topic with exactly those two lines (not the topic's other lines).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        ActivePlugin active;
        if (args.Length > 3)
        {
            var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
            var copy = Path.Combine(outDir, plugin.Name);
            File.Copy(plugin.Location.Path, copy, overwrite: true);
            active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        }
        else active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorDialTest.esp"), light: false, master: false, game, ws.DefaultEncoding);

        var topic = catalog.BySignature["DIAL"].Where(r => r.Plugins.Count == 1 && r.Plugins[0].Name == "Skyrim.esm")
            .Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IDialogTopicGetter>(r.FormKey)).First(t => t?.Responses.Count >= 3)!;
        Console.WriteLine($"topic {topic.EditorID} {topic.FormKey}: {topic.Responses.Count} lines, priority {topic.Priority}");
        var doc = new EditDocument();
        var first = topic.Responses[0];
        var line = (Mutagen.Bethesda.Skyrim.DialogResponses)doc.WorkingCopy(first.FormKey, first);
        if (line.Responses.Count == 0) line.Responses.Add(new Mutagen.Bethesda.Skyrim.DialogResponse { ResponseNumber = 1 });
        line.Responses[0].Text = "STB dialtest edited line";
        doc.CommitEdit("line", line, RecordPlace.DialogInfo, cell: topic.FormKey);
        var newKey = active.Allocate();
        var added = new Mutagen.Bethesda.Skyrim.DialogResponses(newKey, game.Release);
        added.Responses.Add(new Mutagen.Bethesda.Skyrim.DialogResponse { ResponseNumber = 1, Text = "STB dialtest new line", Emotion = Mutagen.Bethesda.Skyrim.Emotion.Happy, EmotionValue = 50 });
        added.PreviousDialog.SetTo(topic.Responses[^1].FormKey);
        doc.CommitEdit("new line", added, RecordPlace.DialogInfo, cell: topic.FormKey);
        var topicCopy = (Mutagen.Bethesda.Skyrim.DialogTopic)doc.WorkingCopy(topic.FormKey, topic);
        topicCopy.Priority = 77;
        doc.CommitEdit("topic", topicCopy, RecordPlace.TopLevel);

        var writer = new PluginWriter(ws, catalog);
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; overridden {result.Overridden}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings.Take(5)) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(10)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;
        File.Move(result.TempPath!, active.Path, overwrite: true);
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
        var written = check.DialogTopics.FirstOrDefault(t => t.FormKey == topic.FormKey);
        Console.WriteLine(written is null ? "topic missing!" :
            $"written topic: priority {written.Priority}, {written.Responses.Count} line(s): {string.Join(" | ", written.Responses.Select(r => $"{r.FormKey.ID:X6} '{r.Responses.FirstOrDefault()?.Text?.String}' {r.Responses.FirstOrDefault()?.Emotion}"))}");
        break;
    }
    case "frameworks":
    {
        // frameworks <game> [EditorID…]: what SPID, KID, BOS, FLM, DESC, SRD, OAR and DAR configs do with records; with EditorIDs, their lines
        // (and for SPID lines how many NPCs match).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var ids = catalog.ByKey.Values.Where(r => r.EditorId is { Length: > 0 }).GroupBy(r => r.EditorId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().FormKey, StringComparer.OrdinalIgnoreCase);
        Mutagen.Bethesda.Plugins.FormKey? ById(string id) => ids.TryGetValue(id, out var k) ? k : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var files = new[] { ".ini", ".yaml", ".yml", ".json", ".txt" }.SelectMany(e => vfs.Enumerate("", e)).Where(p => StbEditor.Core.FrameworkIndex.FrameworkOf(p) is not null)
            .Select(p => (p, vfs.Find(p)?.Container ?? "?", (Func<string?>)(() => vfs.ReadAllBytes(p) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null))).ToList();
        var index = StbEditor.Core.FrameworkIndex.Build(files, ById);
        Console.WriteLine($"{index.All.Count} references to {index.ByForm.Count} records in {string.Join(", ", index.Files.Select(f => $"{f.Value} {f.Key}"))} file(s), {clock.ElapsedMilliseconds} ms");
        foreach (var g in index.All.GroupBy(r => (r.Framework, r.Role))) Console.WriteLine($"  {g.Key.Framework} {g.Key.Role}: {g.Count()}");
        var npcs = new Lazy<List<StbEditor.Core.SpidMatch.Npc>>(() => (catalog.BySignature.GetValueOrDefault("NPC_") ?? []).Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(r.FormKey)).OfType<Mutagen.Bethesda.Skyrim.INpcGetter>().Select(n => StbEditor.Core.SpidMatch.Npc.Of(n, lookup)).ToList());
        IEnumerable<Mutagen.Bethesda.Plugins.FormKey> ListItems(Mutagen.Bethesda.Plugins.FormKey k) => lookup.Resolve<Mutagen.Bethesda.Skyrim.IFormListGetter>(k)?.Items.Select(i => i.FormKey) ?? [];
        foreach (var id in args.Skip(2))
        {
            if (ById(id) is not { } key) { Console.WriteLine($"{id}: no such record"); continue; }
            foreach (var r in index.Of(key))
            {
                var line = $"{id}: {r.Framework} {r.Role} {r.Kind} — {r.Mod} {Path.GetFileName(r.File)}:{r.Line}: {r.Text}";
                if (r.Framework == "SPID" && r.Role == "gives")
                {
                    var results = npcs.Value.Select(n => StbEditor.Core.SpidMatch.Matches(r.Fields, n, ById, ListItems)).ToList();
                    line += $"  => {results.Count(x => x == true)} NPC(s) match, {results.Count(x => x is null)} maybe, chance {StbEditor.Core.SpidMatch.Chance(r.Fields)}%";
                }
                Console.WriteLine(line);
            }
        }
        break;
    }
    case "hulltest":
    {
        // hulltest <game> <interior EditorID>: Bepu hulls of every collision piece of the cell's meshes, one by one (finds a piece that breaks it).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var vfs = AssetFileSystem.Build(ws);
        var cell = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var scene = new SceneBuilder(ws, catalog).BuildInterior(cell.FormKey);
        var pool = new BepuUtilities.Memory.BufferPool();
        foreach (var path in scene.Objects.Select(o => o.ModelPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (vfs.ReadAllBytes(path) is not { } bytes) continue;
            NifModel model;
            try { model = NifFile.Load(bytes, path); } catch { continue; }
            for (var i = 0; i < model.CollisionPieces.Count; i++)
            {
                var piece = model.CollisionPieces[i];
                Console.Out.WriteLine($"{path} piece {i}: {piece.Length} points, finite {piece.All(p => float.IsFinite(p.X + p.Y + p.Z))}");
                Console.Out.Flush();
                BepuPhysics.Collidables.ConvexHullHelper.ComputeHull(piece.Where(p => float.IsFinite(p.X + p.Y + p.Z)).Distinct().ToArray().AsSpan(), pool, out var hull);
                hull.Dispose(pool);
            }
        }
        Console.WriteLine("all hulls built");
        break;
    }
    case "nifcollision":
    {
        // nifcollision <game> <mesh…>: the Havok collision read as convex pieces (count, complete, bounds against the render mesh).
        var vfs = AssetFileSystem.Build(ws);
        var nifCatalog = RecordCatalog.Build(ws, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog"));
        var nifLookup = new RecordLookup(ws, nifCatalog);
        foreach (var arg in args.Skip(2))
        {
            // A record's EditorID stands for its model.
            var mesh = arg.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ? arg
                : nifCatalog.ByKey.Values.FirstOrDefault(r => string.Equals(r.EditorId, arg, StringComparison.OrdinalIgnoreCase)) is { } rec
                    && nifLookup.Resolve(rec.FormKey) is Mutagen.Bethesda.Skyrim.IModeledGetter { Model: { } m } ? AssetFileSystem.MeshPath(m.File.DataRelativePath.Path) : arg;
            var bytes = vfs.ReadAllBytes(mesh);
            if (bytes is null) { Console.WriteLine($"{mesh}: not found"); continue; }
            var model = NifFile.Load(bytes, mesh);
            var points = model.CollisionPieces.SelectMany(p => p).ToList();
            var low = points.Count > 0 ? points.Aggregate(new System.Numerics.Vector3(float.MaxValue), System.Numerics.Vector3.Min) : default;
            var high = points.Count > 0 ? points.Aggregate(new System.Numerics.Vector3(float.MinValue), System.Numerics.Vector3.Max) : default;
            var types = NifFile.Blocks(bytes).Blocks.Select(b => b.Type).Where(t => t.StartsWith("bhk")).Distinct();
            Console.WriteLine($"{mesh}: {model.CollisionPieces.Count} piece(s), complete {model.CollisionComplete}; collision {low} .. {high}; render {model.BoundsMin} .. {model.BoundsMax}; [{string.Join(", ", types)}]");
            if (model.CollisionIndices.Count > 0)
            {
                var tl = model.CollisionPositions.Aggregate(new System.Numerics.Vector3(float.MaxValue), System.Numerics.Vector3.Min);
                var th = model.CollisionPositions.Aggregate(new System.Numerics.Vector3(float.MinValue), System.Numerics.Vector3.Max);
                Console.WriteLine($"  triangle collision: {model.CollisionIndices.Count / 3} triangles, {model.CollisionPositions.Count} vertices, {tl} .. {th}");
            }
        }
        break;
    }
    case "roomcheck":
    {
        // roomcheck <game> [plugin]: room / portal problems in the interiors of a plugin (default Skyrim.esm), counted by kind.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var plugin = ws.Active.First(p => p.Name.Equals(args.Length > 2 ? args[2] : "Skyrim.esm", StringComparison.OrdinalIgnoreCase));
        var cells = catalog.BySignature["CELL"].Where(c => c.Plugins.Contains(plugin) && c.Winner.Cell?.IsInterior == true).ToList();
        var issues = cells.SelectMany(c => StbEditor.Core.PluginChecks.RoomIssues(lookup, c.FormKey).Select(i => (Cell: c.EditorId, Issue: i))).ToList();
        Console.WriteLine($"{cells.Count} interiors, {issues.Count} room / portal issue(s)");
        foreach (var g in issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Issue.Message, @"[0-9A-F]{6}:\S+|\S+ and \S+|room \S+", "X")))
            Console.WriteLine($"  {g.Count()} x {g.Key}  (e.g. {g.First().Cell})");
        break;
    }
    case "filtereddialogue":
    {
        // filtereddialogue <game> <NPC EditorID>: lines the NPC can say (CK: Filtered Dialogue), how many name it.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var npc = lookup.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(catalog.BySignature["NPC_"].First(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase)).FormKey)!;
        var actor = StbEditor.Core.FilteredDialogue.Actor.Of(npc, lookup);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lines = StbEditor.Core.FilteredDialogue.Lines(ws, lookup, DialogueIndex.Build(ws, catalog), actor);
        Console.WriteLine($"{npc.EditorID}: voice {actor.Voice}, {actor.Factions.Count} factions; {lines.Count} lines, {lines.Count(l => l.Named)} name it, in {clock.ElapsedMilliseconds} ms");
        foreach (var l in lines.Where(l => l.Named).Take(10)) Console.WriteLine($"  {l.QuestEditorId}/{l.TopicEditorId} {l.Info.ID:X6}: {(l.Text.Length > 70 ? l.Text[..70] : l.Text)}");
        break;
    }
    case "mergeplan":
    {
        // mergeplan <game> <signature> [n]: what the merged patch would add / remove for records of that type.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var keys = (catalog.BySignature.GetValueOrDefault(args[2]) ?? []).Where(r => r.Plugins.Count >= 3).Select(r => r.FormKey).ToList();
        var plan = StbEditor.Core.LeveledMerge.Plan(ws, keys, k => catalog.Get(k)!.Signature);
        Console.WriteLine($"{plan.Count} of {keys.Count} {args[2]} to merge");
        string Name(Mutagen.Bethesda.Plugins.FormKey k) => catalog.Get(k)?.EditorId ?? k.ToString();
        foreach (var r in plan.Take(args.Length > 3 ? int.Parse(args[3]) : 8))
            Console.WriteLine($"  {r.EditorId} [{string.Join(" > ", r.Plugins)}]: {string.Join(", ", r.AddedEntries.Select(a => "+" + Name(a.Item)).Concat(r.RemovedEntries.Select(a => "-" + Name(a.Item))))}");
        break;
    }
    case "navcheck":
    {
        // navcheck <game> <interior EditorID…>: Check NavMeshes on the cells' navmeshes (issue counts by kind).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var builder = new SceneBuilder(ws, catalog);
        foreach (var id in args.Skip(2))
        {
            var cell = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, id, StringComparison.OrdinalIgnoreCase));
            foreach (var view in builder.NavmeshesOf(cell.FormKey, null))
            {
                if (view.Record.Data is not { } data) continue;
                var issues = StbEditor.Core.NavmeshCheck.Check(data);
                Console.WriteLine($"{id} {view.Key}: {data.Triangles.Count} triangles, {data.Vertices.Count} vertices; {issues.Count} issue(s): "
                    + string.Join(", ", issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Problem, @"\d+", "N")).Select(g => $"{g.Key} x{g.Count()}")));
            }
        }
        break;
    }
    case "deletednav":
    {
        // deletednav <game>: plugins (but the base game's) with deleted navmeshes, and how many.
        foreach (var plugin in ws.Active.Where(p => p.Mod is not null && !p.Name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Unofficial", StringComparison.OrdinalIgnoreCase)))
        {
            var count = 0;
            try { count = plugin.Mod!.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.INavigationMeshGetter>().Count(n => n.IsDeleted); } catch { }
            if (count > 0) Console.WriteLine($"{plugin.Name}: {count} deleted navmesh(es)");
        }
        break;
    }
    case "formidtest":
    {
        // formidtest <game> <out folder> <plugin>: a copy of the plugin where one own record that another record points at gets
        // a new FormID (as Change FormID does); the record must move and the links to it follow.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        var mod = plugin.Mod!;
        var own = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == plugin.ModKey).ToList();
        var linked = own.SelectMany(r => r.EnumerateFormLinks().Select(l => (From: r, To: l.FormKey))).Where(x => x.To.ModKey == plugin.ModKey && x.To != x.From.FormKey).ToList();
        var target = linked.GroupBy(x => x.To).OrderByDescending(g => g.Count()).First();
        var free = Enumerable.Range(0x800, 0xFFF000).Select(i => (uint)i).First(i => own.All(r => r.FormKey.ID != i) && i > own.Max(r => r.FormKey.ID));
        var map = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey> { [target.Key] = new(plugin.ModKey, free) };
        Console.WriteLine($"{plugin.Name}: {target.Key} ({target.Count()} link(s) from own records) -> {free:X6}");
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var writer = new PluginWriter(ws, catalog);
        var temp = writer.Compact(active, map, false);
        File.Move(temp, copy, overwrite: true);
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(copy, game.Release);
        var records = check.EnumerateMajorRecords().ToList();
        var links = records.SelectMany(r => r.EnumerateFormLinks()).ToList();
        Console.WriteLine($"kept byte for byte {writer.LastCompactRestored}; old key present {records.Any(r => r.FormKey == target.Key)}, new present {records.Any(r => r.FormKey == map[target.Key])}; links to old {links.Count(l => l.FormKey == target.Key)}, to new {links.Count(l => l.FormKey == map[target.Key])}; records {own.Count} own before, {records.Count(r => r.FormKey.ModKey == plugin.ModKey)} after");
        break;
    }
    case "mastersorttest":
    {
        // mastersorttest <game> <out folder> [plugin]: a copy of a plugin whose masters are out of load order (the first such one
        // if none is named) saved with Sort Masters; the masters must come out in load order and every record survive.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        var order = ws.Active.Select(p => p.Name.ToLowerInvariant()).ToList();
        bool OutOfOrder(PluginInfo p) => p.Header?.Masters is { Count: > 1 } m && !m.Select(x => order.IndexOf(x.ToLowerInvariant())).SequenceEqual(m.Select(x => order.IndexOf(x.ToLowerInvariant())).OrderBy(i => i));
        var plugin = args.Length > 3 ? ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase))
            : ws.Active.Where(p => p.Mod is not null && new FileInfo(p.Location.Path).Length < 5_000_000).First(OutOfOrder);
        Console.WriteLine($"{plugin.Name}: masters {string.Join(", ", plugin.Header!.Masters)} (out of order: {OutOfOrder(plugin)})");
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var doc = new EditDocument();
        doc.EditMasters("sort", new MasterEdit([], false, true));
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings.Take(6)) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(6)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;
        File.Move(result.TempPath!, active.Path, overwrite: true);
        Console.WriteLine($"written masters: {string.Join(", ", StbEditor.Data.Raw.PluginHeader.Read(active.Path).Masters)}");
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
        Console.WriteLine($"records: {plugin.Mod!.EnumerateMajorRecords().Count()} -> {check.EnumerateMajorRecords().Count()}");
        break;
    }
    case "dialremovetest":
    {
        // dialremovetest <game> <out folder> <plugin>: a copy of the plugin with one of its own dialogue lines removed (as Delete
        // line does); the topic must lose exactly that line, everything else stay.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var topic = plugin.Mod!.DialogTopics.First(t => t.Responses.Count(r => r.FormKey.ModKey == plugin.ModKey) >= 2);
        var doomed = topic.Responses.Last(r => r.FormKey.ModKey == plugin.ModKey);
        Console.WriteLine($"topic {topic.EditorID} {topic.FormKey}: {topic.Responses.Count} line(s) in {plugin.Name}; removing {doomed.FormKey}");
        var doc = new EditDocument();
        doc.Record(new RemoveRecordCommand("delete line", null, new RemovalDraft(doomed.FormKey, "INFO", doomed.EditorID, true), doomed.FormKey));
        var writer = new PluginWriter(ws, catalog);
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings.Take(5)) Console.WriteLine("  warn " + w);
        if (!result.Success) break;
        File.Move(result.TempPath!, active.Path, overwrite: true);
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
        var written = check.DialogTopics.FirstOrDefault(t => t.FormKey == topic.FormKey);
        Console.WriteLine(written is null ? "topic missing!" : $"written topic: {written.Responses.Count} line(s) (was {topic.Responses.Count}); removed one still there: {written.Responses.Any(r => r.FormKey == doomed.FormKey)}");
        Console.WriteLine($"records: {plugin.Mod.EnumerateMajorRecords().Count()} -> {check.EnumerateMajorRecords().Count()}");
        break;
    }
    case "savetest":
    {
        // savetest <game> <cell EditorID> <out folder> [existing plugin name]
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var cellRec = catalog.BySignature["CELL"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var outDir = args[3];
        Directory.CreateDirectory(outDir);
        var builder = new SceneBuilder(ws, catalog);
        var scene = builder.BuildInterior(cellRec.FormKey);
        var doc = new EditDocument();

        ActivePlugin active;
        if (args.Length > 4)
        {
            var plugin = ws.Active.First(p => p.Name.Equals(args[4], StringComparison.OrdinalIgnoreCase));
            var copy = Path.Combine(outDir, plugin.Name);
            File.Copy(plugin.Location.Path, copy, overwrite: true);
            active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        }
        else
        {
            active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorTest.esp"), light: true, master: false, game, ws.DefaultEncoding);
        }

        var meshes = scene.Objects.Where(o => o.Kind == SceneObjectKind.Mesh && o.Record is not null).ToList();
        RefContext Ctx(SceneObject o) => new(o.Cell, null, o.IsPersistent, true, o.Record, o.Base, o.BaseSignature);

        var moved = meshes[0];
        doc.RegisterContext(moved.Reference, Ctx(moved));
        var mstate = moved.State with { Position = moved.Position + new System.Numerics.Vector3(100, 0, 0) };
        doc.Execute("move", [(moved.Reference, moved.State, moved.State, mstate)]);

        var deleted = meshes[1];
        doc.RegisterContext(deleted.Reference, Ctx(deleted));
        doc.Execute("delete", [(deleted.Reference, deleted.State, deleted.State, deleted.State with { Deleted = true })]);

        var newKey = active.Allocate();
        doc.RegisterContext(newKey, new RefContext(cellRec.FormKey, null, false, true, null, moved.Base, moved.BaseSignature));
        doc.Execute("place", [(newKey, EditDocument.Nonexistent, EditDocument.Nonexistent, new RefState(moved.Position + new System.Numerics.Vector3(0, 200, 0), moved.Rotation, 1f, false))]);

        var writer = new PluginWriter(ws, catalog);
        var t = Stopwatch.StartNew();
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error} in {t.ElapsedMilliseconds} ms; overridden {result.Overridden}, created {result.Created}, disabled {result.Disabled}, removed {result.Removed}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(10)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;

        File.Move(result.TempPath!, active.Path, overwrite: true);
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
        var hdr = StbEditor.Data.Raw.PluginHeader.Read(active.Path);
        Console.WriteLine($"written {active.Path}: {new FileInfo(active.Path).Length} bytes, flags {hdr.Flags}, header {hdr.Version:0.00}, next {hdr.NextObjectId:X}, masters [{string.Join(", ", hdr.Masters)}]");
        foreach (var rec in check.EnumerateMajorRecords().Where(r => r.FormKey == moved.Reference || r.FormKey == deleted.Reference || r.FormKey == newKey || r is Mutagen.Bethesda.Skyrim.ICellGetter c && c.FormKey == cellRec.FormKey))
        {
            var text = rec switch
            {
                Mutagen.Bethesda.Skyrim.IPlacedObjectGetter po => $"REFR base {po.Base.FormKey} pos {po.Placement?.Position} flags 0x{(uint)po.MajorRecordFlagsRaw:X} enableParent {po.EnableParent?.Reference.FormKey}",
                Mutagen.Bethesda.Skyrim.ICellGetter cell => $"CELL {cell.EditorID} temp {cell.Temporary.Count} persistent {cell.Persistent.Count}",
                _ => rec.ToString(),
            };
            Console.WriteLine($"  {rec.FormKey}: {text}");
        }
        Console.WriteLine($"  expected: moved {moved.Reference} to {mstate.Position}, disabled {deleted.Reference}, new {newKey}");
        break;
    }
    case "usetest":
    {
        // usetest <game> <EditorID|FormID>...: build the Use Info index (cold or from cache), list who uses
        // each record, and check every non-placed hit with Mutagen's own link list.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var useDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "uses");
        var before = GC.GetTotalMemory(true);
        var index = StbEditor.Data.UseIndex.Build(ws, useDir, new Progress<string>(m => Console.Error.WriteLine(m)));
        Console.WriteLine($"use index: {index.Count:N0} links in {index.BuildTime.TotalSeconds:0.0} s ({index.CachedPlugins} plugins from cache), memory +{(GC.GetTotalMemory(true) - before) / 1048576} MB");
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        foreach (var text in args.Skip(2))
        {
            var key = ws.ParseFormReference(text) ?? catalog.ByKey.Values.FirstOrDefault(r => string.Equals(r.EditorId, text, StringComparison.OrdinalIgnoreCase))?.FormKey;
            if (key is not { } target) { Console.WriteLine($"{text}: not found"); continue; }
            var uses = index.UsesOf(target);
            int verified = 0, rejected = 0, unchecked_ = 0;
            var lines = new List<string>();
            foreach (var use in uses)
            {
                string verdict;
                if (use.IsPlaced) { verdict = "ref"; unchecked_++; }
                else if (catalog.Get(use.Source) is null && !(use.Type == "INFO" && use.Parent is { } t && use.Plugin.Mod is { } m && m.DialogTopics.ContainsKey(t)))
                { verdict = "?"; unchecked_++; }
                else
                {
                    // Dialogue lines: from the plugin whose version wins (later topic overrides hold only their own lines).
                    var record = catalog.Get(use.Source) is not null
                        ? lookup.Resolve(use.Source)
                        : use.Plugin.Mod!.DialogTopics[use.Parent!.Value].Responses.FirstOrDefault(r => r.FormKey == use.Source);
                    var ok = record?.EnumerateFormLinks().Any(l => l.FormKey == target) == true;
                    if (ok) verified++; else rejected++;
                    verdict = ok ? "ok" : "FALSE";
                }
                if (lines.Count < 25 || verdict == "FALSE")
                    lines.Add($"    {verdict,-5} {use.Type} {catalog.Get(use.Source)?.EditorId ?? ws.FormIdText(use.Source)} via {use.Subrecord} in {use.Plugin.Name}{(use.Parent is { } p ? $" (in {catalog.Get(p)?.EditorId ?? ws.FormIdText(p)})" : "")}");
            }
            Console.WriteLine($"{catalog.Get(target)?.EditorId ?? text}: {uses.Count} use(s): {verified} verified, {rejected} FALSE, {unchecked_} placed/unchecked");
            Console.WriteLine("    placed by subrecord: " + string.Join(", ", uses.Where(u => u.IsPlaced).GroupBy(u => u.Type + "." + u.Subrecord).Select(g => $"{g.Key} {g.Count()}")));
            foreach (var line in lines.Take(40)) Console.WriteLine(line);
        }
        break;
    }
    case "condtest":
    {
        // condtest <game>: every condition of PERK/MGEF/PACK/QUST/IDLE/COBJ/SPEL/LSCR described as a CK line (no
        // exceptions); then each function set on a perk's condition, plus Use Global, written and read back.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        string Name(Mutagen.Bethesda.Plugins.FormKey? k) => k is not { IsNull: false } key ? "NONE" : catalog.Get(key)?.EditorId ?? ws.FormIdText(key);
        int described = 0, failed = 0;
        var samples = new List<string>();
        foreach (var sig in new[] { "PERK", "MGEF", "PACK", "QUST", "IDLE", "COBJ", "SPEL", "LSCR" })
        {
            foreach (var entry in catalog.BySignature[sig].Take(400))
            {
                if (lookup.Resolve(entry.FormKey) is not { } rec) continue;
                foreach (var leaf in new[] { rec })
                {
                    foreach (var list in Walk(rec))
                    {
                        foreach (var c in list)
                        {
                            try
                            {
                                var line = StbEditor.Core.Conditions.Describe(c, Name);
                                described++;
                                if (samples.Count < 12 && samples.All(s => !s.StartsWith(sig))) samples.Add($"{sig} {entry.EditorId}: {line}");
                            }
                            catch (Exception ex) { failed++; if (failed < 5) Console.WriteLine($"  describe failed {entry.EditorId}: {ex.Message}"); }
                        }
                    }
                }
            }
        }
        Console.WriteLine($"described {described} conditions, {failed} failed");
        foreach (var s in samples) Console.WriteLine("  " + s);

        static IEnumerable<IEnumerable<Mutagen.Bethesda.Skyrim.IConditionGetter>> Walk(object root)
        {
            var stack = new Stack<(object Value, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (value, depth) = stack.Pop();
                if (StbEditor.Core.Conditions.IsConditionList(value)) { yield return ((System.Collections.IEnumerable)value).Cast<Mutagen.Bethesda.Skyrim.IConditionGetter>(); continue; }
                if (depth > 5) continue;
                if (value is System.Collections.IEnumerable e and not string)
                {
                    foreach (var item in e.Cast<object?>().Take(64)) if (item is not null && StbEditor.Core.RecordFields.IsComposite(item)) stack.Push((item, depth + 1));
                    continue;
                }
                foreach (var p in StbEditor.Core.RecordFields.PropertiesOf(value))
                {
                    object? v;
                    try { v = p.GetValue(value); } catch { continue; }
                    if (v is not null && StbEditor.Core.RecordFields.IsComposite(v)) stack.Push((v, depth + 1));
                }
            }
        }

        // Every function written and read back on a copy of a perk with conditions.
        var perk = catalog.BySignature["PERK"].Select(e => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(e.FormKey)).First(p => p?.Conditions.Count > 0)!;
        var encoding = System.Text.Encoding.UTF8;
        int ok = 0, bad = 0;
        foreach (var function in StbEditor.Core.Conditions.Functions)
        {
            var copy = Mutagen.Bethesda.Skyrim.PerkMixIn.DeepCopy(perk);
            StbEditor.Core.Conditions.SetFunction(copy.Conditions[0], function);
            copy.Conditions[0] = StbEditor.Core.Conditions.WithGlobal(copy.Conditions[0], function.GetHashCode() % 3 == 0);
            copy.Conditions[0].CompareOperator = Mutagen.Bethesda.Skyrim.CompareOperator.GreaterThanOrEqualTo;
            var back = StbEditor.Core.RecordRoundTrip.Read(copy, ws.Game.Release, encoding) as Mutagen.Bethesda.Skyrim.IPerkGetter;
            var got = back?.Conditions[0];
            var wantGlobal = function.GetHashCode() % 3 == 0;
            if (got is not null && StbEditor.Core.Conditions.FunctionName(got.Data) == function.ToString() && (got is Mutagen.Bethesda.Skyrim.IConditionGlobalGetter) == wantGlobal
                && got.CompareOperator == Mutagen.Bethesda.Skyrim.CompareOperator.GreaterThanOrEqualTo) ok++;
            else { bad++; if (bad < 8) Console.WriteLine($"  {function}: read back {(got is null ? StbEditor.Core.RecordRoundTrip.LastError : StbEditor.Core.Conditions.FunctionName(got.Data) + " global=" + (got is Mutagen.Bethesda.Skyrim.IConditionGlobalGetter))}"); }
        }
        Console.WriteLine($"functions: {ok} written and read back, {bad} wrong (of {StbEditor.Core.Conditions.Functions.Count})");
        break;
    }
    case "papyrustest":
    {
        // papyrustest <game> <out folder>: compiler, imports and flags found; every QF_*.psc of the load order parsed and
        // written back (same fragments and code); a real quest's fragment script changed and compiled into <out>.
        var setup = StbEditor.Data.PapyrusSetup.Find(ws.Game);
        Console.WriteLine($"compiler: {setup.Compiler ?? "none"}; {setup.SourceFolders.Count} source folders; flags in {setup.FlagsFolder ?? "none"}; problem: {setup.Problem ?? "none"}");
        foreach (var f in setup.SourceFolders.Take(4)) Console.WriteLine($"  import {f}");
        int parsed = 0, same = 0;
        var firstBad = new List<string>();
        foreach (var file in setup.SourceFolders.SelectMany(f => Directory.EnumerateFiles(f, "QF_*.psc").Concat(Directory.EnumerateFiles(f, "TIF__*.psc")).Concat(Directory.EnumerateFiles(f, "PF_*.psc")).Concat(Directory.EnumerateFiles(f, "SF_*.psc"))).DistinctBy(Path.GetFileName).Take(12000))
        {
            var text = File.ReadAllText(file);
            var a = StbEditor.Core.FragmentScript.Parse(text);
            var b = StbEditor.Core.FragmentScript.Parse(a.Write([]));
            parsed++;
            var ok = a.Name == b.Name && a.Fragments.Count == b.Fragments.Count && a.Tail.Trim() == b.Tail.Trim()
                && a.Fragments.Zip(b.Fragments).All(p => p.First.Name == p.Second.Name && p.First.Code == p.Second.Code && p.First.Prologue == p.Second.Prologue && p.First.Header == p.Second.Header);
            if (ok) same++; else if (firstBad.Count < 5) firstBad.Add(Path.GetFileName(file));
        }
        Console.WriteLine($"fragment scripts: {parsed} parsed, {same} written back the same{(firstBad.Count > 0 ? "; differ: " + string.Join(", ", firstBad) : "")}");

        // How vanilla dialogue lines store their fragments.
        foreach (var t in ws.Active.First(p => p.Name == "Skyrim.esm").Mod!.DialogTopics.Take(3000))
        {
            var info = t.Responses.FirstOrDefault(r => r.VirtualMachineAdapter?.ScriptFragments?.OnBegin is not null || r.VirtualMachineAdapter?.ScriptFragments?.OnEnd is not null);
            if (info?.VirtualMachineAdapter?.ScriptFragments is not { } f) continue;
            Console.WriteLine($"INFO {info.FormKey}: file {f.FileName} bind {f.ExtraBindDataVersion}; begin {f.OnBegin?.FragmentName} bind {f.OnBegin?.ExtraBindDataVersion}; end {f.OnEnd?.FragmentName} bind {f.OnEnd?.ExtraBindDataVersion}; scripts [{string.Join(", ", info.VirtualMachineAdapter.Scripts.Select(x => x.Name))}]");
            break;
        }
        var outDir = Path.GetFullPath(args[2]);
        var sourceDir = Path.Combine(outDir, "Source", "Scripts");
        Directory.CreateDirectory(sourceDir);
        var questName = args.Length > 3 ? args[3] : "QF_DA01_00028AD6";
        if (setup.SourcePath(questName) is { } original)
        {
            var script = StbEditor.Core.FragmentScript.Parse(File.ReadAllText(original));
            var aliases = Regex.Matches(File.ReadAllText(original), @";BEGIN ALIAS PROPERTY (\w+)\r?\n;ALIAS PROPERTY TYPE (\w+)")
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value == "LocationAlias")).ToList();
            script.Fragments[0].Code += "\nDebug.Trace(\"STB Editor papyrustest\")";
            var psc = Path.Combine(sourceDir, questName + ".psc");
            File.WriteAllText(psc, script.Write(aliases));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = setup.Compile(psc, Path.Combine(outDir, "Scripts"));
            Console.WriteLine($"compile {questName}: {(result.Success ? "ok" : "FAILED")} in {clock.Elapsed.TotalSeconds:0.0} s, pex {result.PexPath ?? "none"}, {result.Errors.Count} error(s)");
            foreach (var e in result.Errors.Take(5)) Console.WriteLine($"  {Path.GetFileName(e.File)}({e.Line},{e.Column}): {e.Message}");
            if (!result.Success) Console.WriteLine(result.Output[..Math.Min(result.Output.Length, 1500)]);
            // A broken one must report its error with the line.
            File.WriteAllText(psc, script.Write(aliases).Replace("Debug.Trace(\"STB Editor papyrustest\")", "Debug.Trace(UndefinedThing)"));
            var broken = setup.Compile(psc, Path.Combine(outDir, "Scripts"));
            Console.WriteLine($"broken: success {broken.Success}, errors: {string.Join(" | ", broken.Errors.Take(3).Select(e => $"({e.Line},{e.Column}) {e.Message}"))}");
        }
        break;
    }
    case "enums":
    {
        // enums <game> <Mutagen type name...>: the values of the enums nested in those types (flags of quests, aliases…).
        var asm = typeof(Mutagen.Bethesda.Skyrim.Condition).Assembly;
        foreach (var name in args.Skip(2))
        {
            var type = asm.GetTypes().FirstOrDefault(t => t.Name == name && t.Namespace == "Mutagen.Bethesda.Skyrim");
            if (type is null) { Console.WriteLine($"{name}: no such type"); continue; }
            foreach (var nested in type.GetNestedTypes().Where(t => t.IsEnum))
                Console.WriteLine($"{name}.{nested.Name}: " + string.Join(", ", Enum.GetValues(nested).Cast<object>().Select(v => $"{v}={Convert.ToInt64(v)}")));
        }
        break;
    }
    case "condprobe":
    {
        // condprobe <game>: how Mutagen models conditions (enums, data classes, link-or-index members).
        var asm = typeof(Mutagen.Bethesda.Skyrim.Condition).Assembly;
        Console.WriteLine("Flag: " + string.Join(", ", Enum.GetValues<Mutagen.Bethesda.Skyrim.Condition.Flag>().Select(v => $"{v}={(int)v}")));
        Console.WriteLine("RunOnType: " + string.Join(", ", Enum.GetValues<Mutagen.Bethesda.Skyrim.Condition.RunOnType>().Select(v => $"{v}={(int)v}")));
        Console.WriteLine("CompareOperator: " + string.Join(", ", Enum.GetValues<Mutagen.Bethesda.Skyrim.CompareOperator>().Select(v => $"{v}={(int)v}")));
        var functions = Enum.GetValues<Mutagen.Bethesda.Skyrim.Condition.Function>();
        var dataTypes = asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Mutagen.Bethesda.Skyrim.ConditionData).IsAssignableFrom(t)).ToList();
        Console.WriteLine($"functions {functions.Length}, data classes {dataTypes.Count}");
        var missing = functions.Where(f => asm.GetType($"Mutagen.Bethesda.Skyrim.{f}ConditionData") is null).ToList();
        Console.WriteLine($"functions without a {{name}}ConditionData class: {missing.Count}: {string.Join(", ", missing.Take(30))}");
        var orIndex = asm.GetType("Mutagen.Bethesda.Skyrim.GetIsIDConditionData")!.GetProperty("Object")!.PropertyType;
        Console.WriteLine($"Object type {orIndex}: " + string.Join(", ", orIndex.GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}{(p.CanWrite ? " rw" : "")}")));
        var getId = (Mutagen.Bethesda.Skyrim.ConditionData)Activator.CreateInstance(asm.GetType("Mutagen.Bethesda.Skyrim.GetIsIDConditionData")!)!;
        Console.WriteLine($"new GetIsID: Function={((Mutagen.Bethesda.Skyrim.IConditionDataGetter)getId).GetType().GetProperties().Where(p => p.Name == "Function").Select(p => p.GetValue(getId)).FirstOrDefault()} RunOn={getId.RunOnType}");
        Console.WriteLine("ConditionData members: " + string.Join(", ", typeof(Mutagen.Bethesda.Skyrim.ConditionData).GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}{(p.CanWrite ? " rw" : "")}")));
        Console.WriteLine("extra data classes: " + string.Join(", ", dataTypes.Where(t => !Enum.TryParse<Mutagen.Bethesda.Skyrim.Condition.Function>(t.Name.Replace("ConditionData", ""), out _)).Select(t => t.Name)));
        var param = dataTypes.Select(t => (t.Name, Props: t.GetProperties().Where(p => p.DeclaringType == t && p.CanWrite).Select(p => p.PropertyType.Name).ToList()))
            .GroupBy(x => string.Join(",", x.Props)).OrderByDescending(g => g.Count()).Take(25);
        foreach (var g in param) Console.WriteLine($"  [{g.Key}] x{g.Count()}: {string.Join(" ", g.Take(4).Select(x => x.Name))}");
        break;
    }
    case "packprobe":
    {
        // packprobe <game> [EditorID...]: AI package classes and, for each named package, its template chain,
        // data inputs (key, class, name, flags), procedure tree and schedule.
        var asm = typeof(Mutagen.Bethesda.Skyrim.Package).Assembly;
        foreach (var t in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Mutagen.Bethesda.Skyrim.APackageData).IsAssignableFrom(t)))
            Console.WriteLine($"data class {t.Name}: " + string.Join(", ", t.GetProperties().Where(p => p.DeclaringType != typeof(object)).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
        foreach (var basis in new[] { typeof(Mutagen.Bethesda.Skyrim.APackageTarget), typeof(Mutagen.Bethesda.Skyrim.ALocationTarget) })
            foreach (var t in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract && basis.IsAssignableFrom(t) && !t.Name.Contains("Overlay")))
                Console.WriteLine($"{basis.Name} {t.Name}: " + string.Join(", ", StbEditor.Core.RecordFields.PropertiesOf(Activator.CreateInstance(t)!).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
        foreach (var t in new[] { typeof(Mutagen.Bethesda.Skyrim.PackageDataTarget), typeof(Mutagen.Bethesda.Skyrim.PackageTargetObjectType), typeof(Mutagen.Bethesda.Skyrim.LocationTargetRadius), typeof(Mutagen.Bethesda.Skyrim.PackageIdles) })
            foreach (var p in t.GetProperties().Where(p => p.PropertyType.IsEnum))
                Console.WriteLine($"{t.Name}.{p.Name}: " + string.Join(", ", Enum.GetValues(p.PropertyType).Cast<object>().Select(v => $"{v}={Convert.ToInt64(v)}")));
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var all = catalog.ByKey.Values.Where(r => r.Signature == "PACK").ToList();
        int templated = 0, templates = 0, named = 0, entries = 0;
        var procedures = new Dictionary<string, int>();
        foreach (var r in all)
        {
            if (!ws.LinkCache.TryResolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey, out var p)) continue;
            if (!p.PackageTemplate.IsNull) templated++;
            if (p.Type == Mutagen.Bethesda.Skyrim.Package.Types.PackageTemplate) templates++;
            foreach (var d in p.Data.Values) { entries++; if (!string.IsNullOrEmpty(d.Name)) named++; }
            foreach (var b in p.ProcedureTree) procedures[b.ProcedureType ?? b.BranchType ?? "?"] = procedures.GetValueOrDefault(b.ProcedureType ?? b.BranchType ?? "?") + 1;
        }
        Console.WriteLine($"{all.Count} packages, {templated} with a template, {templates} templates; data entries {entries}, named {named}");
        Console.WriteLine("procedures: " + string.Join(", ", procedures.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var id in args.Skip(2))
        {
            var rec = all.FirstOrDefault(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase));
            if (rec is null || !ws.LinkCache.TryResolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(rec.FormKey, out var pack)) { Console.WriteLine($"{id}: not found"); continue; }
            Console.WriteLine($"== {pack.EditorID} {pack.FormKey} type {pack.Type} flags {pack.Flags} speed {pack.PreferredSpeed} interrupt {pack.InterruptOverride} iflags {pack.InterruptFlags}");
            Console.WriteLine($"   schedule month {pack.ScheduleMonth} day {pack.ScheduleDayOfWeek} date {pack.ScheduleDate} hour {pack.ScheduleHour} minute {pack.ScheduleMinute} duration {pack.ScheduleDurationInMinutes}");
            Console.WriteLine($"   template {pack.PackageTemplate.FormKey} dataver {pack.DataInputVersion} xnam {pack.XnamMarker.Length} owner {pack.OwnerQuest.FormKeyNullable} idles {pack.IdleAnimations?.Animations.Count} conds {pack.Conditions.Count}");
            foreach (var (key, d) in pack.Data)
                Console.WriteLine($"   data {key}: {d.GetType().Name.Replace("BinaryOverlay", "")} name '{d.Name}' flags {d.Flags}");
            for (var i = 0; i < pack.ProcedureTree.Count; i++)
            {
                var b = pack.ProcedureTree[i];
                Console.WriteLine($"   branch {i}: {b.BranchType} proc '{b.ProcedureType}' root {b.Root?.BranchCount}/{b.Root?.Flags} flags {b.Flags} inputs [{string.Join(",", b.DataInputIndices)}] conds {b.Conditions.Count} override {(b.FlagsOverride is null ? "-" : "yes")} unknown {b.Unknown.Count}");
            }
            foreach (var (name, e) in new[] { ("begin", pack.OnBegin), ("end", pack.OnEnd), ("change", pack.OnChange) })
                if (e is not null) Console.WriteLine($"   on {name}: idle {e.Idle.FormKey} topics {e.Topics.Count}");
            if (pack.VirtualMachineAdapter is { } vm) Console.WriteLine($"   scripts {vm.Scripts.Count} fragments {(vm.ScriptFragments is null ? "-" : vm.ScriptFragments.FileName)}");
            if (!pack.PackageTemplate.IsNull && ws.LinkCache.TryResolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(pack.PackageTemplate.FormKey, out var tpl))
            {
                Console.WriteLine($"   template {tpl.EditorID}: type {tpl.Type}, own template {tpl.PackageTemplate.FormKey}");
                foreach (var (key, d) in tpl.Data) Console.WriteLine($"     tdata {key}: {d.GetType().Name.Replace("BinaryOverlay", "")} name '{d.Name}' flags {d.Flags}");
            }
        }
        break;
    }
    case "packtest":
    {
        // packtest <game> <out folder>: a new package from the Sandbox template (as the editor's template picker
        // does), saved into a new plugin; its subrecords must follow a vanilla Sandbox package's. Also: every
        // template's procedure tree must parse into one tree that uses every branch, and the flag bit fix.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var failures = 0;
        var outDir = args[2];
        Directory.CreateDirectory(outDir);

        var bits = StbEditor.Core.RecordFields.FlagBits(typeof(Mutagen.Bethesda.Skyrim.Package.Flag));
        var wear = bits.FirstOrDefault(b => b.Name == "WearSleepOutfit").Bit;
        Console.WriteLine($"flag bits {bits.Count}, WearSleepOutfit 0x{wear:X}");
        if (wear != 0x20000000) failures++;

        // Procedure trees of every template (and of packages that have their own).
        int trees = 0, odd = 0;
        foreach (var r in catalog.BySignature["PACK"])
        {
            if (lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey) is not { ProcedureTree.Count: > 0 } p) continue;
            trees++;
            var nodes = StbEditor.Core.Packages.Tree(p.ProcedureTree);
            var roots = nodes.Count(n => n.Depth == 0);
            if (nodes.Count != p.ProcedureTree.Count || roots != 1)
            {
                odd++;
                if (odd <= 5) Console.WriteLine($"  tree {p.EditorID}: {p.ProcedureTree.Count} branches, {nodes.Count} nodes, {roots} roots");
            }
        }
        Console.WriteLine($"procedure trees {trees}, not a single tree {odd}");

        var template = lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory("01C254:Skyrim.esm"))!;
        var vanilla = lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory("0956B8:Skyrim.esm"))!;
        var active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorPackTest.esp"), light: true, master: false, game, ws.DefaultEncoding);
        var doc = new EditDocument();
        var pack = (Mutagen.Bethesda.Skyrim.Package)NewRecords.Blank(typeof(Mutagen.Bethesda.Skyrim.Package), active.Allocate(), game.Release, "STBTestSandboxPackage");
        StbEditor.Core.Packages.UseTemplate(pack, template);
        // As the form would: one input changed, the location made «near self».
        ((Mutagen.Bethesda.Skyrim.PackageDataBool)pack.Data[1]).Data = true;
        var location = ((Mutagen.Bethesda.Skyrim.PackageDataLocation)pack.Data[0]).Location;
        Console.WriteLine($"inputs {pack.Data.Count}: " + string.Join(", ", StbEditor.Core.Packages.Inputs(pack, template).Select(i => $"{i.Key} {i.Name} {StbEditor.Core.Packages.KindOf(i.Data)}")));
        Console.WriteLine($"location kind {location.Target.GetType().Name}, radius {location.Radius}");
        if (pack.Data.Count != vanilla.Data.Count) { Console.WriteLine($"  inputs: {pack.Data.Count} vs vanilla {vanilla.Data.Count}"); failures++; }
        foreach (var (key, data) in vanilla.Data)
            if (!pack.Data.TryGetValue(key, out var mine) || StbEditor.Core.Packages.KindOf(mine) != StbEditor.Core.Packages.KindOf(data))
            { Console.WriteLine($"  input {key}: {StbEditor.Core.Packages.KindOf(data)} in vanilla, {(mine is null ? "missing" : StbEditor.Core.Packages.KindOf(mine))} here"); failures++; }
        doc.CommitEdit("New package", pack, RecordPlace.TopLevel);
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}");
        if (!result.Success) { Console.WriteLine("FAILED"); break; }
        File.Move(result.TempPath!, active.Path, overwrite: true);

        // Subrecord sequence against the vanilla package (Skyrim.esm as it is on disk).
        static IEnumerable<string> Sequence(ReadOnlyMemory<byte> body) => StbEditor.Data.Raw.RawFormat.Subrecords(body).Select(s => s.Type);
        var written = StbEditor.Data.Raw.RawFormat.Records(File.ReadAllBytes(active.Path), t => t == "PACK").Single();
        var esm = ws.Active.First(p => p.Name.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)).Location.Path;
        var original = StbEditor.Data.Raw.RawFormat.Records(File.ReadAllBytes(esm), t => t == "PACK").First(r => (r.FormId & 0xFFFFFF) == 0x0956B8);
        var mineSeq = string.Join(" ", Sequence(written.Data).Where(t => t != "EDID"));
        var theirs = string.Join(" ", Sequence(original.Data).Where(t => t != "EDID"));
        // Bytes of each subrecord against vanilla: only what the test changed may differ (PKDT interrupt flags, Allow Eating, Location).
        var mineSubs = StbEditor.Data.Raw.RawFormat.Subrecords(written.Data).Where(s => s.Type != "EDID").ToList();
        var theirSubs = StbEditor.Data.Raw.RawFormat.Subrecords(original.Data).Where(s => s.Type != "EDID").ToList();
        for (var i = 0; i < Math.Min(mineSubs.Count, theirSubs.Count); i++)
            if (!mineSubs[i].Data.Span.SequenceEqual(theirSubs[i].Data.Span))
                Console.WriteLine($"  {i} {mineSubs[i].Type}: new {Convert.ToHexString(mineSubs[i].Data.Span)} vanilla {Convert.ToHexString(theirSubs[i].Data.Span)}");
        // Expected differences: PKDT (vanilla's unknown byte C4, interrupt flags), PLDT (the package's own location),
        // one CNAM (vanilla allows wandering) and XNAM (a marker that varies among vanilla packages of one template:
        // 0x20, the template's own, is the most common and what a new one gets).
        Console.WriteLine($"new:     {mineSeq}");
        Console.WriteLine($"vanilla: {theirs}");
        if (mineSeq != theirs) { Console.WriteLine("  subrecords differ"); failures++; }

        // Read back.
        var back = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release).Packages.Single();
        Console.WriteLine($"read back: template {back.PackageTemplate.FormKey}, inputs {back.Data.Count}, allow eating {((Mutagen.Bethesda.Skyrim.IPackageDataBoolGetter)back.Data[1]).Data}");
        if (back.PackageTemplate.FormKey != template.FormKey || back.Data.Count != pack.Data.Count || !((Mutagen.Bethesda.Skyrim.IPackageDataBoolGetter)back.Data[1]).Data) failures++;
        Console.WriteLine(failures == 0 ? "OK" : $"FAILED {failures}");
        break;
    }
    case "sceneprobe":
    {
        // sceneprobe <game> [EditorID]: what vanilla scenes hold (phase widths, unused data, action kinds) and
        // one scene's subrecords as Skyrim.esm has them.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var widths = new Dictionary<uint, int>();
        int scenes = 0, phaseUnused = 0, phases = 0, sceneUnused = 0, vnam = 0, noQuest = 0, actorsNotInQuest = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var r in catalog.BySignature["SCEN"])
        {
            if (lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey) is not { } s) continue;
            scenes++;
            if (s.Unused is not null) sceneUnused++;
            if (s.VNAM is not null) vnam++;
            if (s.Quest.IsNull) noQuest++;
            foreach (var p in s.Phases) { phases++; if (p.Unused is not null || p.Unused2 is not null) phaseUnused++; widths[p.EditorWidth ?? 0] = widths.GetValueOrDefault(p.EditorWidth ?? 0) + 1; }
            foreach (var a in s.Actions) { var k = $"{a.Type} flags {a.Flags} unused {(a.Unused is null ? 0 : 1)}"; kinds[k] = kinds.GetValueOrDefault(k) + 1; }
            if (!s.Quest.IsNull && lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(s.Quest.FormKey) is { } q)
                actorsNotInQuest += s.Actors.Count(a => q.Aliases.All(al => al.ID != a.ID));
        }
        Console.WriteLine($"{scenes} scenes, {phases} phases ({phaseUnused} with unused data), scene unused {sceneUnused}, VNAM {vnam}, no quest {noQuest}, actors not in the quest {actorsNotInQuest}");
        Console.WriteLine("phase widths: " + string.Join(", ", widths.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")));
        var all = catalog.BySignature["SCEN"].Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey)).OfType<Mutagen.Bethesda.Skyrim.ISceneGetter>().ToList();
        Console.WriteLine("actors: " + string.Join("; ", all.SelectMany(x => x.Actors).GroupBy(a => $"{a.Flags}/{a.BehaviorFlags}").OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}")));
        Console.WriteLine("dialog: " + string.Join("; ", all.SelectMany(x => x.Actions).Where(a => a.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Dialog)
            .GroupBy(a => $"max {a.LoopingMax} min {a.LoopingMin} head {a.HeadtrackActorID} emo {a.Emotion}/{a.EmotionValue}").OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}")));
        Console.WriteLine("timer: " + string.Join("; ", all.SelectMany(x => x.Actions).Where(a => a.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Timer)
            .GroupBy(a => $"sec {a.TimerSeconds} actor {a.ActorID}").OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}")));
        Console.WriteLine("vnam: " + string.Join("; ", all.Where(x => x.VNAM is not null).GroupBy(x => x.VNAM!.Value.Length == 16 && Enumerable.Range(0, 4).All(i => BitConverter.ToUInt32(x.VNAM.Value.Span[(i * 4)..]) == x.LastActionIndex) ? "4x last" : $"other {x.VNAM.Value.Length}").Select(g => $"{g.Key} x{g.Count()}")));
        Console.WriteLine("actions: " + string.Join("; ", kinds.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}")));
        if (args.Length > 2)
        {
            var rec = catalog.BySignature["SCEN"].FirstOrDefault(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase))
                ?? catalog.BySignature["SCEN"].First(r => r.FormKey.ModKey.FileName == "Skyrim.esm" && lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey) is { Phases.Count: 2, Actions.Count: >= 3 } s && s.Actions.Any(a => a.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Package) && s.Actions.Any(a => a.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Timer));
            Console.WriteLine($"{rec.EditorId} {rec.FormKey}");
            var esm = ws.Active.First(p => p.Name.Equals(rec.FormKey.ModKey.FileName, StringComparison.OrdinalIgnoreCase)).Location.Path;
            var raw = StbEditor.Data.Raw.RawFormat.Records(File.ReadAllBytes(esm), t => t == "SCEN").First(x => (x.FormId & 0xFFFFFF) == rec.FormKey.ID);
            foreach (var sub in StbEditor.Data.Raw.RawFormat.Subrecords(raw.Data))
                Console.WriteLine($"  {sub.Type} {Convert.ToHexString(sub.Data.Span[..Math.Min(sub.Data.Length, 24)])}");
        }
        break;
    }
    case "scenetest":
    {
        // scenetest <game> <out folder>: dunFellglow_ReleaseDogs rebuilt through Core.Scenes (two phases, its actor, a
        // dialogue, a package and a timer action) into a new plugin; subrecords must follow the vanilla scene's. Then
        // phase insert / remove keep the actions on their phases.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var failures = 0;
        Directory.CreateDirectory(args[2]);
        var vanilla = lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory("08A372:Skyrim.esm"))!;
        var active = ActivePlugin.ForNew(Path.Combine(args[2], "STBEditorSceneTest.esp"), light: true, master: false, game, ws.DefaultEncoding);
        var scene = (Mutagen.Bethesda.Skyrim.Scene)NewRecords.Blank(typeof(Mutagen.Bethesda.Skyrim.Scene), active.Allocate(), game.Release, "STBTestScene");
        scene.Quest.SetTo(vanilla.Quest.FormKey);
        StbEditor.Core.Scenes.InsertPhase(scene, 0);
        StbEditor.Core.Scenes.InsertPhase(scene, 1);
        var actor = (int)vanilla.Actors[0].ID;
        StbEditor.Core.Scenes.AddActor(scene, actor);
        foreach (var source in vanilla.Actions)
        {
            var action = StbEditor.Core.Scenes.AddAction(scene, source.Type, source.ActorID ?? actor, (int)(source.StartPhase ?? 0));
            action.EndPhase = source.EndPhase;
            if (source.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Dialog) action.Topic.SetTo(source.Topic.FormKey);
            if (source.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Package) foreach (var p in source.Packages) action.Packages.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IPackageGetter>(p.FormKey));
            if (source.Type == Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Timer) action.TimerSeconds = source.TimerSeconds;
        }
        var doc = new EditDocument();
        doc.CommitEdit("New scene", scene, RecordPlace.TopLevel);
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}");
        if (!result.Success) { Console.WriteLine("FAILED"); break; }
        File.Move(result.TempPath!, active.Path, overwrite: true);
        var esm = ws.Active.First(p => p.Name.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)).Location.Path;
        var original = StbEditor.Data.Raw.RawFormat.Records(File.ReadAllBytes(esm), t => t == "SCEN").First(r => (r.FormId & 0xFFFFFF) == 0x08A372);
        var written = StbEditor.Data.Raw.RawFormat.Records(File.ReadAllBytes(active.Path), t => t == "SCEN").Single();
        // VMAD (its fragment script) and VNAM (editor bookkeeping) are vanilla's own.
        var mine = StbEditor.Data.Raw.RawFormat.Subrecords(written.Data).Where(x => x.Type is not ("EDID" or "VMAD" or "VNAM")).ToList();
        var theirs = StbEditor.Data.Raw.RawFormat.Subrecords(original.Data).Where(x => x.Type is not ("EDID" or "VMAD" or "VNAM")).ToList();
        Console.WriteLine("new:     " + string.Join(" ", mine.Select(x => x.Type)));
        Console.WriteLine("vanilla: " + string.Join(" ", theirs.Select(x => x.Type)));
        if (!mine.Select(x => x.Type).SequenceEqual(theirs.Select(x => x.Type))) { Console.WriteLine("  subrecords differ"); failures++; }
        for (var i = 0; i < Math.Min(mine.Count, theirs.Count); i++)
            if (!mine[i].Data.Span.SequenceEqual(theirs[i].Data.Span))
                Console.WriteLine($"  {i} {mine[i].Type}: new {Convert.ToHexString(mine[i].Data.Span)} vanilla {Convert.ToHexString(theirs[i].Data.Span)}");

        // Phase edits: a phase in front moves every action right, removing it moves them back.
        string Phases(Mutagen.Bethesda.Skyrim.IScene s) => string.Join(",", s.Actions.Select(a => $"{a.StartPhase}-{a.EndPhase}"));
        var before = Phases(scene);
        StbEditor.Core.Scenes.InsertPhase(scene, 0);
        var moved = Phases(scene);
        StbEditor.Core.Scenes.RemovePhase(scene, 0);
        Console.WriteLine($"phases: {before} -> insert {moved} -> remove {Phases(scene)}");
        if (Phases(scene) != before || moved == before) failures++;
        StbEditor.Core.Scenes.AddAction(scene, Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Timer, actor, 1);
        var removed = StbEditor.Core.Scenes.RemovePhase(scene, 1);
        Console.WriteLine($"remove phase 1: {removed} action(s) gone, left {Phases(scene)}, phases {scene.Phases.Count}, last index {scene.LastActionIndex}");
        if (removed != 1 || scene.Phases.Count != 1) failures++;
        Console.WriteLine(failures == 0 ? "OK" : $"FAILED {failures}");
        break;
    }
    case "fragprobe":
    {
        // fragprobe <game>: package (PF_) and scene (SF_) fragment data as vanilla has it, and the source of one of each.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var papyrus = StbEditor.Data.PapyrusSetup.Find(ws.Game);
        void Source(string name)
        {
            if (papyrus.SourcePath(name) is { } path) Console.WriteLine(path + "\n" + string.Join("\n", File.ReadAllLines(path).Take(40).Select(l => "    | " + l)));
            else Console.WriteLine($"    (no source for {name})");
        }
        var packs = catalog.BySignature["PACK"].Where(r => r.FormKey.ModKey.FileName == "Skyrim.esm").Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey))
            .OfType<Mutagen.Bethesda.Skyrim.IPackageGetter>().Where(p => p.VirtualMachineAdapter?.ScriptFragments is not null).ToList();
        Console.WriteLine($"packages with fragments: {packs.Count}; bind " + string.Join(",", packs.Select(p => p.VirtualMachineAdapter!.ScriptFragments!.ExtraBindDataVersion).Distinct())
            + "; fragment bind " + string.Join(",", packs.SelectMany(p => new[] { p.VirtualMachineAdapter!.ScriptFragments!.OnBegin, p.VirtualMachineAdapter.ScriptFragments.OnEnd, p.VirtualMachineAdapter.ScriptFragments.OnChange }).OfType<Mutagen.Bethesda.Skyrim.IScriptFragmentGetter>().Select(f => f.ExtraBindDataVersion).Distinct())
            + "; vmad " + string.Join(",", packs.Select(p => $"{p.VirtualMachineAdapter!.Version}/{p.VirtualMachineAdapter.ObjectFormat}").Distinct()));
        if (packs.FirstOrDefault(p => p.VirtualMachineAdapter!.ScriptFragments!.OnBegin is not null && p.VirtualMachineAdapter.ScriptFragments.OnEnd is not null) is { } pk)
        {
            var f = pk.VirtualMachineAdapter!.ScriptFragments!;
            Console.WriteLine($"  {pk.EditorID}: file {f.FileName} begin {f.OnBegin?.ScriptName}/{f.OnBegin?.FragmentName} end {f.OnEnd?.FragmentName} change {f.OnChange?.FragmentName}; scripts {string.Join(",", pk.VirtualMachineAdapter.Scripts.Select(s => $"{s.Name}:{s.Flags}:{s.Properties.Count}"))}");
            Source(f.FileName);
        }
        var perks = catalog.BySignature["PERK"].Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(r.FormKey))
            .OfType<Mutagen.Bethesda.Skyrim.IPerkGetter>().Where(p => p.VirtualMachineAdapter?.ScriptFragments is not null).ToList();
        Console.WriteLine($"perks with fragments: {perks.Count}; bind " + string.Join(",", perks.Select(p => p.VirtualMachineAdapter!.ScriptFragments!.ExtraBindDataVersion).Distinct())
            + "; unknown " + string.Join(",", perks.SelectMany(p => p.VirtualMachineAdapter!.ScriptFragments!.Fragments).Select(f => $"{f.Unknown}/{f.Unknown2}").Distinct()));
        foreach (var perk in perks.Take(3))
        {
            var f = perk.VirtualMachineAdapter!.ScriptFragments!;
            Console.WriteLine($"  {perk.EditorID}: file {f.FileName}; fragments " + string.Join(", ", f.Fragments.Select(x => $"{x.FragmentIndex}:{x.FragmentName}"))
                + "; entries " + string.Join(", ", perk.Effects.Select((e, i) => $"{i}:{e.GetType().Name.Replace("BinaryOverlay", "")}{(e is Mutagen.Bethesda.Skyrim.IAPerkEntryPointEffectGetter ep ? "/" + ep.EntryPoint : "")}")));
        }
        var scenes = catalog.BySignature["SCEN"].Where(r => r.FormKey.ModKey.FileName == "Skyrim.esm").Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey))
            .OfType<Mutagen.Bethesda.Skyrim.ISceneGetter>().Where(s => s.VirtualMachineAdapter?.ScriptFragments is not null).ToList();
        Console.WriteLine($"scenes with fragments: {scenes.Count}; bind " + string.Join(",", scenes.Select(s => s.VirtualMachineAdapter!.ScriptFragments!.ExtraBindDataVersion).Distinct())
            + "; phase unknown " + string.Join(",", scenes.SelectMany(s => s.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments).Select(p => p.Unknown).Distinct())
            + "; vmad " + string.Join(",", scenes.Select(s => $"{s.VirtualMachineAdapter!.Version}/{s.VirtualMachineAdapter.ObjectFormat}").Distinct()));
        Console.WriteLine("phase index: max = count " + scenes.Count(s => s.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments.Any(p => p.Index == s.Phases.Count)) + ", index 0 used " + scenes.Count(s => s.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments.Any(p => p.Index == 0)) + ", beyond " + scenes.Count(s => s.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments.Any(p => p.Index > s.Phases.Count)));
        if (scenes.FirstOrDefault(s => s.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments.Count >= 2 && s.VirtualMachineAdapter.ScriptFragments.OnBegin is not null) is { } sc)
        {
            var f = sc.VirtualMachineAdapter!.ScriptFragments!;
            Console.WriteLine($"  {sc.EditorID}: file {f.FileName} begin {f.OnBegin?.FragmentName} end {f.OnEnd?.FragmentName}; phases " + string.Join(", ", f.PhaseFragments.Select(p => $"{p.Index}:{p.Flags}:{p.FragmentName}:{p.ScriptName}")) + $"; scripts {string.Join(",", sc.VirtualMachineAdapter.Scripts.Select(s => $"{s.Name}:{s.Flags}:{s.Properties.Count}"))}");
            Source(f.FileName);
        }
        break;
    }
    case "fragtest":
    {
        // fragtest <game> <out folder>: package and scene fragment tables of the load order point at fragments their
        // sources have; a vanilla package and a vanilla scene get new code (Begin, a phase completion) merged into
        // their scripts, compiled into <out>, applied to copies and read back; a phase inserted in front shifts the
        // scene's phase fragments.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var setup = StbEditor.Data.PapyrusSetup.Find(ws.Game);
        var failures = 0;
        int records = 0, withSource = 0, found = 0, missing = 0;
        var records2 = catalog.BySignature["PACK"].Concat(catalog.BySignature["SCEN"]).Concat(catalog.BySignature["PERK"]).ToList();
        foreach (var r in records2)
        {
            var record = r.Signature switch { "PACK" => (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey), "PERK" => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(r.FormKey), _ => lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey) };
            if (record is null) continue;
            var table = StbEditor.Core.RecordFragments.Table(record);
            if (table.Count == 0) continue;
            records++;
            var name = StbEditor.Core.RecordFragments.ScriptName(record, ws.FormIdText(record.FormKey));
            if (setup.SourcePath(name) is not { } path) continue;
            withSource++;
            var script = StbEditor.Core.FragmentScript.Parse(File.ReadAllText(path));
            foreach (var fragment in table.Values) if (script.Get(fragment) is not null) found++; else missing++;
        }
        Console.WriteLine($"records with fragments {records}, with source {withSource}; fragments in the source {found}, missing {missing}");

        var outDir = Path.GetFullPath(args[2]);
        var sources = Path.Combine(outDir, "Source", "Scripts");
        Directory.CreateDirectory(sources);
        void Check(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter record, Dictionary<string, string> edits, string expect)
        {
            var name = StbEditor.Core.RecordFragments.ScriptName(record, ws.FormIdText(record.FormKey));
            var script = setup.SourcePath(name) is { } path ? StbEditor.Core.FragmentScript.Parse(File.ReadAllText(path)) : new StbEditor.Core.FragmentScript { Name = name };
            script.Name = name;
            script.Extends = StbEditor.Core.RecordFragments.Extends(record);
            var before = StbEditor.Core.RecordFragments.Table(record);
            var table = StbEditor.Core.RecordFragments.Merge(record, script, edits);
            var psc = Path.Combine(sources, name + ".psc");
            File.WriteAllText(psc, script.Write([]));
            var result = setup.Compile(psc, Path.Combine(outDir, "Scripts"));
            Console.WriteLine($"{record.EditorID}: {name}, table [{string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value}"))}] -> [{string.Join(", ", table.Select(kv => $"{kv.Key}={kv.Value}"))}], compile {(result.Success ? "ok" : "FAILED")}");
            foreach (var e in result.Errors.Take(5)) Console.WriteLine($"  ({e.Line},{e.Column}) {e.Message}");
            if (!result.Success) { failures++; return; }
            var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(record);
            StbEditor.Core.RecordFragments.Apply(copy, name, table);
            var back = StbEditor.Core.RecordRoundTrip.Read(copy, game.Release, System.Text.Encoding.UTF8);
            var backTable = back is null ? null : StbEditor.Core.RecordFragments.Table(back);
            var text = backTable is null ? $"FAILED {StbEditor.Core.RecordRoundTrip.LastError}" : string.Join(", ", backTable.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"  read back: [{text}]");
            if (backTable is null || !backTable.ContainsKey(expect) || backTable.Count != table.Count) failures++;
        }
        // A package with a script source that has fragments, and a fresh one; a scene with phase fragments.
        var package = catalog.BySignature["PACK"].Select(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey)).OfType<Mutagen.Bethesda.Skyrim.IPackageGetter>()
            .First(p => p.EditorID == "AbeloneCarryFirewood11x2");
        Check(package, new() { ["End"] = "Debug.Trace(\"STB fragtest end \" + akActor)" }, "End");
        var scene = lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(catalog.BySignature["SCEN"].First(r => r.EditorId == "BardSongsInstrumentalBard2Drum01").FormKey)!;
        Check(scene, new() { ["Phase0End"] = "Debug.Trace(\"STB fragtest phase 1 done\")", ["Begin"] = "" }, "Phase0End");

        var perkRecord = lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(catalog.BySignature["PERK"].First(r => r.EditorId == "DA04BloodHarvestPerk").FormKey)!;
        Check(perkRecord, new() { ["Entry1"] = "Debug.Trace(\"STB fragtest perk \" + akTargetRef + \" by \" + akActor)" }, "Entry1");

        // Phases shift their fragments.
        var moved = (Mutagen.Bethesda.Skyrim.Scene)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(scene);
        var indices = string.Join(",", moved.VirtualMachineAdapter!.ScriptFragments!.PhaseFragments.Select(f => f.Index));
        StbEditor.Core.Scenes.InsertPhase(moved, 0);
        var shifted = string.Join(",", moved.VirtualMachineAdapter.ScriptFragments.PhaseFragments.Select(f => f.Index));
        StbEditor.Core.Scenes.RemovePhase(moved, 0);
        var restored = string.Join(",", moved.VirtualMachineAdapter.ScriptFragments.PhaseFragments.Select(f => f.Index));
        Console.WriteLine($"phase fragments {indices} -> insert {shifted} -> remove {restored}");
        if (restored != indices || shifted == indices) failures++;
        Console.WriteLine(failures == 0 ? "OK" : $"FAILED {failures}");
        break;
    }
    case "aliastest":
    {
        // aliastest <game> [quest EditorID] [alias ID]: the alias Use Report over the quest, its scenes, packages and
        // dialogue lines (default: the Fellglow guard of dunFellglow_ReleaseDogs, used by that scene).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var questKey = args.Length > 2 ? catalog.BySignature["QUST"].First(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase)).FormKey
            : lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory("08A372:Skyrim.esm"))!.Quest.FormKey;
        var quest = lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(questKey)!;
        if (args.Length > 3 && args[3] == "all")
        {
            // Every alias of the quest: how often each kind of use shows up.
            foreach (var a in quest.Aliases)
            {
                var found = StbEditor.Core.AliasUses.InQuest(quest, (int)a.ID).Select(u => u.Where).ToList();
                Console.WriteLine($"  {a.ID} {a.Name}: {found.Count}  {string.Join(" | ", found.Take(4))}");
            }
            break;
        }
        var alias = args.Length > 3 ? int.Parse(args[3]) : 19;
        Console.WriteLine($"{quest.EditorID}: alias {alias} {quest.Aliases.FirstOrDefault(a => a.ID == alias)?.Name}");
        var clock = Stopwatch.StartNew();
        var uses = StbEditor.Core.AliasUses.InQuest(quest, alias);
        foreach (var r in catalog.BySignature["SCEN"])
            if (lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(r.FormKey) is { } s && s.Quest.FormKeyNullable == quest.FormKey) uses.AddRange(StbEditor.Core.AliasUses.InScene(s, alias));
        foreach (var r in catalog.BySignature["PACK"])
            if (lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(r.FormKey) is { } p && p.OwnerQuest.FormKeyNullable == quest.FormKey) uses.AddRange(StbEditor.Core.AliasUses.InPackage(p, alias));
        var index = StbEditor.Data.DialogueIndex.Build(ws, catalog);
        foreach (var topic in index.TopicsOf(quest.FormKey))
            foreach (var (line, _) in StbEditor.Data.DialogueIndex.Lines(ws, topic))
                uses.AddRange(StbEditor.Core.AliasUses.InLine(line, alias));
        foreach (var use in uses) Console.WriteLine($"  {catalog.Get(use.Record)?.EditorId ?? ws.FormIdText(use.Record)} [{catalog.Get(use.Record)?.Signature ?? "INFO"}]: {use.Where}");
        Console.WriteLine($"{uses.Count} uses in {clock.Elapsed.TotalSeconds:0.0} s");
        break;
    }
    case "texttest":
    {
        // texttest <game> <plugin> <out folder>: the plugin's display texts exported to JSON, read back the same; every
        // 7th text "translated" and applied to a working copy of its record (dialogue lines found under their topic),
        // each record written and read back with the translation in place.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        var clock = Stopwatch.StartNew();
        var hits = StbEditor.Core.TextSearch.DisplayTexts(plugin, catalog);
        var file = StbEditor.Core.TextTranslation.FromHits(plugin.Name, hits);
        var path = Path.Combine(args[3], plugin.Name + ".texts.json");
        StbEditor.Core.TextTranslation.Write(path, file);
        var back = StbEditor.Core.TextTranslation.Read(path);
        Console.WriteLine($"{file.Strings.Count} texts in {clock.Elapsed.TotalSeconds:0.0} s ({new FileInfo(path).Length / 1024} KB): " +
            string.Join(", ", file.Strings.GroupBy(e => e.Signature).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}")));
        var failures = 0;
        var same = back.Strings.Count == file.Strings.Count && back.Strings.Zip(file.Strings).All(p => p.First.Source == p.Second.Source && p.First.Form == p.Second.Form
            && p.First.Path.Length == p.Second.Path.Length && p.First.Path.Zip(p.Second.Path).All(q => Equals(q.First, q.Second)));
        Console.WriteLine($"read back the same: {same}");
        if (!same) failures++;
        for (var i = 0; i < back.Strings.Count; i += 7) back.Strings[i].Translation = back.Strings[i].Source + " [перевод]";
        var outcomes = new Dictionary<StbEditor.Core.TextTranslation.Outcome, int>();
        Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>? own = null;
        int records = 0, checkedBack = 0;
        foreach (var group in StbEditor.Core.TextTranslation.Translated(back).GroupBy(e => e.Form))
        {
            var first = group.First();
            // The plugin's own version (the winner may be another plugin's, with other texts).
            own ??= plugin.Mod!.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
            var current = own.GetValueOrDefault(first.Key);
            if (current is null) { outcomes[StbEditor.Core.TextTranslation.Outcome.Missing] = outcomes.GetValueOrDefault(StbEditor.Core.TextTranslation.Outcome.Missing) + group.Count(); continue; }
            var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(current);
            var applied = new List<StbEditor.Core.TextEntry>();
            foreach (var entry in group)
            {
                var outcome = StbEditor.Core.TextTranslation.Apply(copy, entry);
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                if (outcome == StbEditor.Core.TextTranslation.Outcome.Applied) applied.Add(entry);
            }
            if (applied.Count == 0) continue;
            records++;
            // A dialogue line is only written under its topic, a cell or worldspace as a header: checked on the copy.
            var read = first.Signature is "INFO" or "CELL" or "WRLD" ? copy : StbEditor.Core.RecordRoundTrip.Read(copy, game.Release, System.Text.Encoding.UTF8);
            if (read is null) { Console.WriteLine($"  {first.EditorId} [{first.Signature}]: round trip FAILED {StbEditor.Core.RecordRoundTrip.LastError}"); failures++; continue; }
            foreach (var entry in applied)
            {
                var value = StbEditor.Core.RecordFields.Get(read, entry.Path);
                var text = value switch { Mutagen.Bethesda.Strings.ITranslatedStringGetter t => t.String, string s => s, _ => null };
                if (text == entry.Translation) checkedBack++;
                else { failures++; if (failures < 5) Console.WriteLine($"  {first.EditorId} {string.Join("/", entry.Path)}: read back '{text}'"); }
            }
        }
        Console.WriteLine($"import: {string.Join(", ", outcomes.Select(kv => $"{kv.Key} {kv.Value}"))}; {records} records written and read back, {checkedBack} translations in place");
        Console.WriteLine(failures == 0 ? "OK" : $"FAILED {failures}");
        break;
    }
    case "navsplittest":
    {
        // navsplittest <game> [n]: for n interior navmeshes, the first third of the triangles moved into a new navmesh
        // (MoveTriangles): counts add up, moved triangles keep their corner positions and flags, every link between
        // triangles of either mesh joins two triangles that share that edge (writing a NAVM is the writer's, under its cell).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var limit = args.Length > 2 ? int.Parse(args[2]) : 50;
        int meshes = 0, failures = 0;
        static System.Numerics.Vector3 Pt(Mutagen.Bethesda.Skyrim.NavigationMeshData d, int i) => new(d.Vertices[i].X, d.Vertices[i].Y, d.Vertices[i].Z);
        static int BadLinks(Mutagen.Bethesda.Skyrim.NavigationMeshData d)
        {
            var bad = 0;
            for (var t = 0; t < d.Triangles.Count; t++)
            {
                var tri = d.Triangles[t];
                int[] c = [tri.Vertices.X, tri.Vertices.Y, tri.Vertices.Z];
                short[] links = [tri.EdgeLink_0_1, tri.EdgeLink_1_2, tri.EdgeLink_2_0];
                for (var e = 0; e < 3; e++)
                {
                    if (tri.Flags.HasFlag((Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag)(1 << e)) || links[e] < 0) continue;
                    if (links[e] >= d.Triangles.Count) { bad++; continue; }
                    var other = d.Triangles[links[e]].Vertices;
                    var set = new HashSet<int> { other.X, other.Y, other.Z };
                    if (!set.Contains(c[e]) || !set.Contains(c[(e + 1) % 3])) bad++;
                }
            }
            return bad;
        }
        foreach (var cellRec in catalog.BySignature["CELL"].Where(c => c.Winner.Cell is { IsInterior: true }).OrderBy(c => c.FormKey.GetHashCode()))
        {
            foreach (var nav in lookup.CellVersions(cellRec.FormKey).LastOrDefault()?.NavigationMeshes ?? [])
            {
                if (nav.Data is not { Triangles.Count: >= 30 } || meshes >= limit) continue;
                meshes++;
                var source = (Mutagen.Bethesda.Skyrim.NavigationMesh)Mutagen.Bethesda.Plugins.Records.MajorRecordMixIn.DeepCopy(nav);
                var before = source.Data!.Triangles.Select(t => (A: Pt(source.Data, t.Vertices.X), B: Pt(source.Data, t.Vertices.Y), C: Pt(source.Data, t.Vertices.Z), t.Flags)).ToList();
                var moved = Enumerable.Range(0, before.Count / 3).ToHashSet();
                var target = new Mutagen.Bethesda.Skyrim.NavigationMesh(Mutagen.Bethesda.Plugins.FormKey.Factory("000800:STBNavSplitTest.esp"), game.Release)
                {
                    Data = new Mutagen.Bethesda.Skyrim.NavigationMeshData { NavmeshVersion = source.Data.NavmeshVersion, Parent = Mutagen.Bethesda.Skyrim.ANavmeshParentMixIn.DeepCopy(source.Data.Parent), NavmeshGridDivisor = 1 },
                };
                StbEditor.Core.NavmeshTools.MoveTriangles(source, moved, target);
                var problems = new List<string>();
                if (source.Data.Triangles.Count + target.Data.Triangles.Count != before.Count) problems.Add($"counts {source.Data.Triangles.Count}+{target.Data.Triangles.Count} != {before.Count}");
                for (var i = 0; i < target.Data.Triangles.Count; i++)
                {
                    var t = target.Data.Triangles[i];
                    var (a, b, c, flags) = before[i];
                    const Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag kept = Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Water | Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Preferred | Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.NoLargeCreatures | Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Overlapping;
                    if (Pt(target.Data, t.Vertices.X) != a || Pt(target.Data, t.Vertices.Y) != b || Pt(target.Data, t.Vertices.Z) != c || (t.Flags & kept) != (flags & kept)) { problems.Add($"moved triangle {i} differs"); break; }
                }
                var badSource = BadLinks(source.Data);
                var badTarget = BadLinks(target.Data);
                if (badSource + badTarget > 0) problems.Add($"bad links: source {badSource}, new {badTarget}");
                if (problems.Count > 0) { failures++; Console.WriteLine($"  {cellRec.EditorId} {nav.FormKey}: {string.Join("; ", problems)}"); }
            }
            if (meshes >= limit) break;
        }
        Console.WriteLine($"{meshes} navmeshes split; {failures} with problems");
        Console.WriteLine(failures == 0 ? "OK" : "FAILED");
        break;
    }
    case "difftest":
    {
        // difftest <game> <plugin> <other file>: the plugin compared with another version of the file (Compare to).
        var plugin = ws.Active.First(p => p.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        var clock = Stopwatch.StartNew();
        var diff = StbEditor.Core.PluginDiff.Compare(plugin.Location.Path, args[3], game.Release);
        Console.WriteLine($"{diff.Count} difference(s) in {clock.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", diff.GroupBy(d => d.Kind).Select(g => $"{g.Key} {g.Count()}")));
        foreach (var group in diff.GroupBy(d => d.Kind))
            foreach (var d in group.Take(6))
                Console.WriteLine($"  {d.Kind} {d.Signature} {d.EditorId} {d.Key}{(d.Fields.Count > 0 ? ": " + string.Join(", ", d.Fields) : "")}");
        break;
    }
    case "tintprobe":
    {
        // tintprobe <game> [plugin]: how the CK's body tint (QNAM, texture lighting) relates to the NPC's skin tone tint layer.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var plugin = args.Length > 2 ? args[2] : "Skyrim.esm";
        var stats = new Dictionary<string, int>();
        var samples = new List<string>();
        foreach (var r in catalog.BySignature["NPC_"].Where(r => r.Plugins.Any(p => p.Name == plugin)))
        {
            var npc = ws.LinkCache.ResolveAllSimpleContexts<Mutagen.Bethesda.Skyrim.INpcGetter>(r.FormKey).FirstOrDefault(c => c.ModKey.FileName == plugin)?.Record;
            if (npc is null || npc.TintLayers.Count == 0 || npc.TextureLighting is not { } light) continue;
            if (lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(npc.Race.FormKey) is not { } race) continue;
            var female = npc.Configuration.Flags.HasFlag(Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female);
            var head = female ? race.HeadData?.Female : race.HeadData?.Male;
            var skin = head?.TintMasks.FirstOrDefault(t => t.MaskType == Mutagen.Bethesda.Skyrim.TintAssets.TintMaskType.SkinTone);
            var layer = skin is null ? null : npc.TintLayers.FirstOrDefault(l => l.Index == skin.Index);
            if (layer?.Color is not { } c) { stats["no skin layer"] = stats.GetValueOrDefault("no skin layer") + 1; continue; }
            var i = layer.InterpolationValue ?? 1f;
            bool Near(System.Drawing.Color a, double r2, double g2, double b2) => Math.Abs(a.R - r2) <= 1 && Math.Abs(a.G - g2) <= 1 && Math.Abs(a.B - b2) <= 1;
            double Mix(byte v) => Math.Floor(127.5 + (v - 127.5) * i);
            var kind = Near(light, Mix(c.R), Mix(c.G), Mix(c.B)) ? "= grey mixed with the skin tone by interpolation" : "other";
            stats[kind] = stats.GetValueOrDefault(kind) + 1;
            if (kind == "other" && samples.Count < 5) samples.Add($"{npc.EditorID}: light {light.R},{light.G},{light.B} skin {c.R},{c.G},{c.B} a{c.A} i {i}");
        }
        Console.WriteLine(string.Join(", ", stats.Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var s in samples) Console.WriteLine("  " + s);
        break;
    }
    case "mergeshow":
    {
        // mergeshow <game> <leveled item EditorID>: every version's entries and the merged ones.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var rec = catalog.BySignature["LVLI"].First(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var versions = ws.LinkCache.ResolveAllSimpleContexts<Mutagen.Bethesda.Skyrim.ILeveledItemGetter>(rec.FormKey).Reverse().ToList();
        string Text(Mutagen.Bethesda.Skyrim.ILeveledItemGetter l) => string.Join(" ", (l.Entries ?? []).Select(e => $"{catalog.Get(e.Data!.Reference.FormKey)?.EditorId ?? e.Data.Reference.FormKey.ToString()}@{e.Data.Level}x{e.Data.Count}").OrderBy(x => x));
        foreach (var v in versions) Console.WriteLine($"{v.ModKey}: {Text(v.Record)}\n");
        if (StbEditor.Core.LeveledMerge.Merge(versions.Select(v => (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)v.Record).ToList()) is Mutagen.Bethesda.Skyrim.ILeveledItemGetter merged)
            Console.WriteLine($"merged: {Text(merged)}");
        break;
    }
    case "mergetest":
    {
        // mergetest <game> [patch plugin | -] [out folder]: leveled lists changed by two or more plugins merged over the whole load order
        // (the patch plugin, when given, left out as if regenerated); every merged list keeps each plugin's additions
        // against the original and reads back after writing.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var clock = Stopwatch.StartNew();
        var lists = new[] { "LVLI", "LVLN", "LVSP" }.SelectMany(s => catalog.BySignature.GetValueOrDefault(s) ?? []).ToList();
        var skip = args.Length > 2 && args[2] != "-" ? new HashSet<Mutagen.Bethesda.Plugins.ModKey> { Mutagen.Bethesda.Plugins.ModKey.FromFileName(args[2]) } : null;
        var results = StbEditor.Core.LeveledMerge.Plan(ws, lists.Select(l => l.FormKey), k => catalog.Get(k)!.Signature, skip);
        Console.WriteLine($"{lists.Count} leveled lists, {results.Count} to merge in {clock.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", results.GroupBy(r => r.Signature).Select(g => $"{g.Key} {g.Count()}"))
            + $"; entries added {results.Sum(r => r.Added)}, removed {results.Sum(r => r.Removed)}");
        foreach (var r in results.OrderByDescending(r => r.Added).Take(6))
            Console.WriteLine($"  {r.Signature} {r.EditorId}: +{r.Added} -{r.Removed} from {string.Join(" > ", r.Plugins)}: "
                + string.Join(", ", r.AddedEntries.Take(4).Select(a => $"+{catalog.Get(a.Item)?.EditorId}@{a.Level}x{a.Count}").Concat(r.RemovedEntries.Take(4).Select(a => $"-{catalog.Get(a.Item)?.EditorId}@{a.Level}x{a.Count}"))));
        var failures = 0;
        foreach (var r in results.Take(300))
            if (StbEditor.Core.RecordRoundTrip.Read(r.Merged, game.Release, System.Text.Encoding.UTF8) is null) { failures++; if (failures < 4) Console.WriteLine($"  round trip FAILED {r.EditorId}: {StbEditor.Core.RecordRoundTrip.LastError}"); }
        if (args.Length > 3)
        {
            // The patch written as the editor would: a new plugin in <out folder>, read back.
            Directory.CreateDirectory(args[3]);
            var active = ActivePlugin.ForNew(Path.Combine(args[3], "STBEditorMergedPatch.esp"), light: false, master: false, game, ws.DefaultEncoding);
            var doc = new EditDocument();
            doc.CommitEdits("merge", results.Select(r => (r.Merged, RecordPlace.TopLevel, (RefContext?)null)).ToList());
            var written = new PluginWriter(ws, catalog).Prepare(active, doc);
            Console.WriteLine($"patch: success={written.Success} error={written.Error}");
            if (written.Success)
            {
                File.Move(written.TempPath!, active.Path, overwrite: true);
                var back = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(active.Path, game.Release);
                var count = back.LeveledItems.Count + back.LeveledNpcs.Count + back.LeveledSpells.Count;
                Console.WriteLine($"patch read back: {count} list(s), masters {string.Join(", ", back.ModHeader.MasterReferences.Select(m => m.Master.FileName))}");
                if (count != results.Count) failures++;
            }
            else failures++;
        }
        Console.WriteLine(failures == 0 ? "OK" : $"FAILED {failures}");
        break;
    }
    case "members":
    {
        // members <game> <Mutagen getter type name, e.g. IPlacedObjectGetter>: the data members the field walk sees.
        var type = typeof(Mutagen.Bethesda.Skyrim.ISkyrimModGetter).Assembly.GetTypes().First(t => t.Name == args[2]);
        foreach (var p in StbEditor.Core.RecordFields.PropertiesOf(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            type.Assembly.GetType(type.Namespace + "." + type.Name[1..^6])!)))
            Console.WriteLine($"{p.Name}: {p.PropertyType.Name}");
        break;
    }
    case "voicecheck":
    {
        // voicecheck <game> [plugin] [quest filter]: voice file names and voice types the editor works out for a plugin's lines,
        // against the files the build has (name matches; voice types found / missed / extra).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var plugin = args.Length > 2 ? args[2] : "Skyrim.esm";
        var filter = args.Length > 3 ? args[3] : null;
        var prefix = $@"sound\voice\{plugin.ToLowerInvariant()}\";
        // FormID → (voice type folder, file base name)
        var files = new Dictionary<string, List<(string Voice, string Name)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in new[] { ".fuz", ".xwm", ".wav" })
            foreach (var file in vfs.Enumerate(prefix, ext))
            {
                var rest = file[prefix.Length..];
                var slash = rest.IndexOf('\\');
                if (slash < 0) continue;
                var name = Path.GetFileNameWithoutExtension(rest);
                var parts = name.Split('_');
                if (parts.Length < 3) continue;
                (files.TryGetValue(parts[^2], out var l) ? l : files[parts[^2]] = []).Add((rest[..slash], name));
            }
        var index = DialogueIndex.Build(ws, catalog);
        var quests = catalog.BySignature["QUST"].Where(q => q.FormKey.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase)
            && (filter is null || (q.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))).Select(q => q.FormKey).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lines = StbEditor.Core.VoiceFiles.Lines(ws, lookup, index, quests);
        Console.WriteLine($"{quests.Count} quests, {lines.Count:N0} line x voice rows in {clock.ElapsedMilliseconds} ms; {files.Count:N0} voiced FormIDs in {prefix}");
        int named = 0, misnamed = 0, found = 0, missed = 0, extra = 0, unresolved = 0, unresolvedVoiced = 0;
        var examples = new List<string>();
        foreach (var group in lines.GroupBy(l => (l.Info, l.Response)))
        {
            var id = group.Key.Info.ID.ToString("X8");
            var have = (files.GetValueOrDefault(id) ?? []).Where(f => f.Name.EndsWith("_" + group.Key.Response, StringComparison.Ordinal)).ToList();
            var first = group.First();
            var expected = StbEditor.Core.VoiceFiles.BaseName(first.QuestEditorId, first.TopicEditorId, first.Info, first.Response);
            if (have.Count > 0)
            {
                if (have.Any(h => h.Name.Equals(expected, StringComparison.OrdinalIgnoreCase))) named++;
                else { misnamed++; if (examples.Count < 12) examples.Add($"name: {expected} vs {have[0].Name}"); }
            }
            var predicted = group.Where(l => l.VoiceTypeEditorId is not null).Select(l => l.VoiceTypeEditorId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = have.Select(h => h.Voice).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (predicted.Count == 0)
            {
                unresolved++;
                if (actual.Count > 0) { unresolvedVoiced++; if (unresolvedVoiced <= 12) examples.Add($"no speaker {first.QuestEditorId}/{first.TopicEditorId} {id}: have [{string.Join(",", actual)}]"); }
                continue;
            }
            found += actual.Count(predicted.Contains);
            missed += actual.Count(a => !predicted.Contains(a));
            extra += predicted.Count(p => !actual.Contains(p));
            if (actual.Any(a => !predicted.Contains(a)) && examples.Count < 24) examples.Add($"voices {first.QuestEditorId}/{first.TopicEditorId} {id}: have [{string.Join(",", actual)}] predicted [{string.Join(",", predicted)}]");
        }
        Console.WriteLine($"names: {named} match, {misnamed} differ; voice types: {found} found, {missed} missed, {extra} extra; {unresolved} responses with no speaker ({unresolvedVoiced} of them voiced)");
        foreach (var e in examples) Console.WriteLine("  " + e);
        break;
    }
    case "voicenames":
    {
        // voicenames <game> <quest EditorID…>: voice files the build has for the quest's lines, to learn the naming.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in vfs.Enumerate(@"sound\voice\", ".fuz").Concat(vfs.Enumerate(@"sound\voice\", ".xwm")).Concat(vfs.Enumerate(@"sound\voice\", ".wav")))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length >= 3) (byId.TryGetValue(parts[^2], out var l) ? l : byId[parts[^2]] = []).Add(file);
        }
        Console.WriteLine($"{byId.Count} voiced line ids");
        var index = DialogueIndex.Build(ws, catalog);
        foreach (var questId in args.Skip(2))
        {
            var quest = catalog.BySignature["QUST"].First(q => string.Equals(q.EditorId, questId, StringComparison.OrdinalIgnoreCase));
            foreach (var topicKey in index.TopicsOf(quest.FormKey).Take(40))
            {
                var topic = lookup.Resolve<Mutagen.Bethesda.Skyrim.IDialogTopicGetter>(topicKey);
                foreach (var (line, _) in DialogueIndex.Lines(ws, topicKey).Take(3))
                {
                    var id = (line.FormKey.ID & 0xFFFFFF).ToString("X8");
                    var files = byId.GetValueOrDefault(id) ?? [];
                    Console.WriteLine($"{quest.EditorId} ({quest.EditorId?.Length}) | {topic?.EditorID} ({topic?.EditorID?.Length}) | {id} -> {(files.Count == 0 ? "-" : Path.GetFileName(files[0]) + $" x{files.Count} in {Path.GetDirectoryName(files[0])}")}");
                }
            }
        }
        break;
    }
    case "idletree":
    {
        // idletree <game> [depth] [filter]: the idle animation tree (as the CK's Animations window), counts and the top levels.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var idleWatch = System.Diagnostics.Stopwatch.StartNew();
        var roots = StbEditor.Core.IdleTree.Build(lookup, catalog);
        var depth = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 2;
        var filter = args.Length > 3 ? args[3] : null;
        int Count(StbEditor.Core.IdleNode n) => 1 + n.Children.Sum(Count);
        Console.WriteLine($"{roots.Sum(Count)} idles, {roots.Count} roots in {idleWatch.ElapsedMilliseconds} ms; parents: {string.Join(", ", roots.GroupBy(r => r.Parent is { } p ? catalog.Get(p)?.Signature ?? "missing" : "none").Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var group in roots.GroupBy(r => r.Behavior).OrderBy(g => g.Key))
            Console.WriteLine($"  graph '{group.Key}': {group.Count()} roots, {group.Sum(Count)} idles");
        void Print(StbEditor.Core.IdleNode n, int level)
        {
            if (level > depth) return;
            Console.WriteLine($"{new string(' ', 2 + level * 2)}{n.EditorId} event '{n.Event}' cond {n.Conditions} {n.Flags} [{Count(n) - 1}] {(n.Parent is { } p ? catalog.Get(p)?.Signature : "")}");
            foreach (var c in n.Children) Print(c, level + 1);
        }
        foreach (var r in roots.Where(r => filter is null || (r.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).Take(60)) Print(r, 0);
        break;
    }
    case "perktree":
    {
        // perktree <game> <AVIF EditorID>: the nodes of a skill's perk tree (grid, offsets, perk, links).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var entry = catalog.BySignature["AVIF"].First(c => string.Equals(c.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var avif = lookup.Resolve<Mutagen.Bethesda.Skyrim.IActorValueInformationGetter>(entry.FormKey)!;
        Console.WriteLine($"{avif.EditorID} '{avif.Name}' skill {avif.Skill is not null} CNAM {(avif.CNAM is { } c ? Convert.ToHexString(c) : "-")}, {avif.PerkTree.Count} nodes");
        foreach (var n in avif.PerkTree)
        {
            var perk = n.Perk.IsNull ? null : lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(n.Perk.FormKey);
            Console.WriteLine($"  #{n.Index} grid {n.PerkGridX},{n.PerkGridY} off {n.HorizontalPosition},{n.VerticalPosition} FNAM {(n.FNAM is { } f ? Convert.ToHexString(f) : "-")} skill {(n.AssociatedSkill.IsNull ? "-" : n.AssociatedSkill.FormKey.ToString())} perk {perk?.EditorID ?? n.Perk.FormKey.ToString()} -> [{string.Join(",", n.ConnectionLineToIndices)}]");
        }
        break;
    }
    case "strtest":
    {
        // strtest <game> <EditorID>: the record's Name in every plugin that has it (localized string lookup).
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var rec = catalog.ByKey.Values.First(r => string.Equals(r.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var type = StbEditor.Data.RecordLookup.GetterType(rec.Signature)!;
        foreach (var context in ws.LinkCache.ResolveAllSimpleContexts(rec.FormKey, type))
        {
            var named = context.Record as Mutagen.Bethesda.Plugins.Aspects.ITranslatedNamedGetter;
            var name = named?.Name;
            var langs = name is null ? "" : string.Join(", ", name.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"{context.ModKey}: '{name?.String}' [{langs}] lang {name?.TargetLanguage}");
        }
        break;
    }
    case "recordtest":
    {
        // recordtest <game> <out folder> <plugin>: a blank record of every creatable type must write and
        // read back; then, on a copy of the plugin: new records, one own record and one override removed.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = args[2];
        Directory.CreateDirectory(outDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var encoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(active.Encoding, game.Language);

        var doc = new EditDocument();
        int ok = 0, failed = 0;
        var created = new List<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var signature in StbEditor.Data.Catalog.RecordTypes.Signatures.Distinct())
        {
            if (NewRecords.NotCreatable.Contains(signature) || StbEditor.Data.RecordLookup.GetterType(signature) is not { } getter) continue;
            var classes = NewRecords.RecordClasses(getter);
            if (classes.Count == 0) { Console.WriteLine($"  {signature}: no class"); failed++; continue; }
            foreach (var cls in classes)
            {
                var id = (NewRecords.GameSettingPrefix(cls) ?? "") + "STBTest" + cls.Name;
                try
                {
                    var record = NewRecords.Blank(cls, active.Allocate(), game.Release, id);
                    if (RecordRoundTrip.Read(record, game.Release, encoding) is null)
                    {
                        Console.WriteLine($"  {signature} {cls.Name}: does not read back: {RecordRoundTrip.LastError}");
                        failed++;
                        continue;
                    }
                    doc.CommitEdit("new", record, RecordPlace.TopLevel);
                    created.Add(record.FormKey);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {signature} {cls.Name}: {ex.GetType().Name}: {ex.Message}");
                    failed++;
                }
            }
        }
        Console.WriteLine($"blank records: {ok} ok, {failed} failed");

        var own = catalog.ByKey.Values.FirstOrDefault(r => r.FormKey.ModKey == plugin.ModKey && r.Plugins.Count == 1 && r.Signature is "MISC" or "STAT");
        var over = catalog.ByKey.Values.FirstOrDefault(r => r.FormKey.ModKey != plugin.ModKey && r.WinningPlugin == plugin && r.Plugins.Count == 2 && r.Signature is "MISC" or "WEAP" or "ARMO");
        if (own is not null) doc.RemoveRecord("remove", own.FormKey, own.Signature, own.EditorId, inPlugin: true, ownRecord: true);
        if (over is not null) doc.RemoveRecord("remove", over.FormKey, over.Signature, over.EditorId, inPlugin: true, ownRecord: false);
        Console.WriteLine($"remove own {own?.EditorId} {own?.FormKey}, override {over?.EditorId} {over?.FormKey}");

        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; overridden {result.Overridden}, removed {result.Removed}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(10)) Console.WriteLine("  lossy " + l);
        if (!result.Success) break;
        using var back = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(new Mutagen.Bethesda.Plugins.ModPath(active.ModKey, result.TempPath!), game.Release);
        var present = back.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
        Console.WriteLine($"read back: new records present {created.Count(present.Contains)}/{created.Count}, own removed {(own is null ? "-" : (!present.Contains(own.FormKey)).ToString())}, override removed {(over is null ? "-" : (!present.Contains(over.FormKey)).ToString())}");
        break;
    }
    case "cleanmasters":
    {
        // cleanmasters <game> <out folder> <plugin> [add plugin]: on a copy, the masters nothing points at
        // (as xEdit's Clean Masters), then a save that drops them (and adds one) — the save check must stay clean.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        Directory.CreateDirectory(args[2]);
        var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        var copy = Path.Combine(args[2], plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var header = StbEditor.Data.Raw.PluginHeader.Read(copy);
        var used = PluginWriter.UsedMasters(plugin.Mod!, plugin.ModKey);
        Console.WriteLine($"masters of {plugin.Name}: {string.Join(", ", header.Masters.Select(m => used.Any(u => u.FileName.String.Equals(m, StringComparison.OrdinalIgnoreCase)) ? m : m + " (unused)"))}");
        var add = args.Length > 4 ? new List<Mutagen.Bethesda.Plugins.ModKey> { Mutagen.Bethesda.Plugins.ModKey.FromFileName(args[4]) } : [];
        var doc = new EditDocument();
        doc.EditMasters("masters", new MasterEdit(add, Clean: true));
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; added [{string.Join(", ", result.AddedMasters)}], removed [{string.Join(", ", result.RemovedMasters)}], lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        foreach (var l in result.LossyRecords.Take(10)) Console.WriteLine("  lossy " + l);
        if (result.Success) Console.WriteLine($"written masters: {string.Join(", ", StbEditor.Data.Raw.PluginHeader.Read(result.TempPath!).Masters)}");
        break;
    }
    case "mastertest":
    {
        // mastertest <game> <out folder> <plugin>: on a copy of the plugin, override a record of a
        // plugin that is not its master yet (the save must add a master) and report the save check.
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var outDir = args[2];
        Directory.CreateDirectory(outDir);
        var plugin = ws.Active.First(p => p.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
        var copy = Path.Combine(outDir, plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        var active = ActivePlugin.ForExisting(plugin, game).RetargetedTo(copy);
        var masters = StbEditor.Data.Raw.PluginHeader.Read(copy).Masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = ws.Active.Select(p => p.Name.ToLowerInvariant()).ToList();
        var target = catalog.BySignature["MISC"].Concat(catalog.BySignature["WEAP"])
            .FirstOrDefault(r => !masters.Contains(r.WinningPlugin.Name) && r.FormKey.ModKey.FileName.String is var origin
                && !masters.Contains(origin) && !origin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase)
                && order.IndexOf(origin.ToLowerInvariant()) is var at && at >= 0 && at < order.IndexOf(plugin.Name.ToLowerInvariant()));
        if (target is null) { Console.WriteLine("no record of a non-master plugin found"); break; }
        var lookup = new StbEditor.Data.RecordLookup(ws, catalog);
        var winning = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)lookup.Resolve<Mutagen.Bethesda.Skyrim.IMiscItemGetter>(target.FormKey)
            ?? lookup.Resolve<Mutagen.Bethesda.Skyrim.IWeaponGetter>(target.FormKey);
        var doc = new EditDocument();
        var edited = doc.WorkingCopy(target.FormKey, winning!);
        edited.EditorID = (edited.EditorID ?? "x") + "Edited";
        doc.CommitEdit("edit", edited, RecordPlace.TopLevel);
        Console.WriteLine($"override {target.EditorId} {target.FormKey} (from {target.FormKey.ModKey}, not a master of {plugin.Name})");

        var t = Stopwatch.StartNew();
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error} in {t.ElapsedMilliseconds} ms; lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        Console.WriteLine($"  written to {result.TempPath}");
        foreach (var l in result.LossyRecords.Take(20)) Console.WriteLine("  lossy " + l);
        break;
    }
    case "cellat":
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var world = catalog.BySignature["WRLD"].First(w => string.Equals(w.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        int x = int.Parse(args[3]), y = int.Parse(args[4]);
        var c = catalog.BySignature["CELL"].FirstOrDefault(c => c.Winner.Cell is { IsInterior: false } p && p.Worldspace == world.FormKey && p.GridX == x && p.GridY == y);
        Console.WriteLine(c is null ? "none" : $"{ws.FormIdText(c.FormKey)} {c.EditorId}");
        break;
    }
    case "errors":
        foreach (var p in ws.Plugins.Where(p => p.Error is not null))
            Console.WriteLine($"{p.Name}: {p.Error}");
        break;
}
