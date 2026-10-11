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
    case "reflect":
    {
        // reflect <game> <Mutagen getter interface, e.g. INpcGetter>: properties, to write code against.
        var type = typeof(Mutagen.Bethesda.Skyrim.ISkyrimModGetter).Assembly.GetTypes().FirstOrDefault(x => x.Name == args[2] || x.FullName?.EndsWith("." + args[2]) == true);
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
