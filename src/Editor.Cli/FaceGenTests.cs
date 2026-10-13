using System.Numerics;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Assets;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using StbEditor.Nif;
using StbEditor.Render;

/// <summary>FaceGen research and checks against the Creation Kit's own FaceGen files.</summary>
internal static class FaceGenTests
{
    /// <summary>
    /// facegensliders &lt;game&gt; #count [part type]: over many vanilla NPCs, a free least-squares fit of every slider morph
    /// of one head part type against the CK FaceGen; per slider, how the fitted weights of its two morphs follow the value.
    /// </summary>
    public static void Sliders(Workspace ws, string[] args)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var partsByName = catalog.BySignature["HDPT"].ToDictionary(r => r.EditorId ?? "", r => r.FormKey, StringComparer.OrdinalIgnoreCase);
        var wantType = args.Length > 3 ? args[3] : "Face";
        var samples = new Dictionary<string, List<(float Value, double Plus, double Minus)>>();
        var others = new Dictionary<string, List<double>>();

        foreach (var rec in catalog.BySignature["NPC_"].Where(r => r.FormKey.ModKey.FileName.String == "Skyrim.esm").Take(int.Parse(args[2].TrimStart('#'))))
        {
            var key = rec.FormKey;
            var facegen = ActorAppearance.FaceGenPath(key);
            if (vfs.ReadAllBytes(facegen) is not { } fgBytes) continue;
            var npc = ws.LinkCache.ResolveAllSimpleContexts<INpcGetter>(key).Last().Record;
            var race = lookup.Resolve<IRaceGetter>(npc.Race.FormKey);
            if (race is null || npc.FaceMorph is null) continue;
            NifModel fg;
            try { fg = NifFile.Load(fgBytes, facegen, raw: true); } catch { continue; }

            foreach (var shape in fg.Shapes)
            {
                if (!partsByName.TryGetValue(shape.Name, out var partKey) || lookup.Resolve<IHeadPartGetter>(partKey) is not { } part || part.Type?.ToString() != wantType) continue;
                var file = part.Model?.File.GivenPath;
                var source = file is null || vfs.ReadAllBytes(AssetFileSystem.MeshPath(file)) is not { } nifBytes ? null : NifFile.Load(nifBytes, file, raw: true).Shapes.FirstOrDefault();
                if (source is null || source.Positions.Length != shape.Positions.Length) continue;
                var n = source.Positions.Length;

                var morphs = new List<(string Name, Vector3[] D)>();
                foreach (var p in part.Parts)
                {
                    if (p.FileName?.GivenPath is not { } tri || vfs.ReadAllBytes(AssetFileSystem.MeshPath(tri)) is not { } bytes) continue;
                    var t = TriFile.Load(bytes);
                    if (t.Vertices.Length != n) continue;
                    foreach (var m in t.Morphs)
                    {
                        var keep = p.PartType switch
                        {
                            Part.PartTypeEnum.RaceMorph => m.Name == race.EditorID,
                            Part.PartTypeEnum.Tri => m.Name == "SkinnyMorph",
                            _ => (!m.Name.Contains("Type") && m.Name != "VampireMorph")
                                 || m.Name == $"NoseType{npc.FaceParts?.Nose}" || m.Name == $"EyesType{npc.FaceParts?.Eyes}" || m.Name == $"LipType{npc.FaceParts?.Mouth}",
                        };
                        if (keep && m.Deltas.Any(d => d.LengthSquared() > 1e-10f)) morphs.Add((m.Name, m.Deltas));
                    }
                }

                var target = new Vector3[n];
                for (var i = 0; i < n; i++) target[i] = shape.Positions[i] - source.Positions[i];
                var weights = Fit(morphs, target);

                foreach (var (slider, plus, minus) in FaceGenMorphs.Sliders)
                {
                    var value = (float)(typeof(INpcFaceMorphGetter).GetProperty(slider)!.GetValue(npc.FaceMorph)!);
                    if (!samples.TryGetValue(slider, out var list)) samples[slider] = list = [];
                    list.Add((value, weights.GetValueOrDefault(plus, double.NaN), weights.GetValueOrDefault(minus, double.NaN)));
                }
                foreach (var (name, weight) in weights)
                {
                    string? label = name == race.EditorID ? "race morph" : name == "SkinnyMorph" ? "SkinnyMorph" : name.Contains("Type") ? name.TrimEnd("0123456789".ToCharArray()) : null;
                    if (label is null) continue;
                    if (!others.TryGetValue(label, out var list)) others[label] = list = [];
                    list.Add(name == "SkinnyMorph" ? weight - (1 - npc.Weight / 100.0) : weight - 1);
                }
            }
        }

        foreach (var (slider, list) in samples)
        {
            string Slope(Func<(float Value, double Plus, double Minus), double> weight, Func<float, double> part)
            {
                var pts = list.Where(t => !double.IsNaN(weight(t)) && part(t.Value) != 0).ToList();
                if (pts.Count < 3) return "      -      ";
                var slope = pts.Sum(t => weight(t) * part(t.Value)) / pts.Sum(t => part(t.Value) * part(t.Value));
                var err = Math.Sqrt(pts.Sum(t => Math.Pow(weight(t) - slope * part(t.Value), 2)) / pts.Count);
                return $"{slope,6:0.000}±{err:0.000}";
            }
            Console.WriteLine($"{slider,-24} n {list.Count,3}: plus-morph v>0 {Slope(t => t.Plus, v => Math.Max(v, 0))} v<0 {Slope(t => t.Plus, v => Math.Max(-v, 0))} | minus-morph v>0 {Slope(t => t.Minus, v => Math.Max(v, 0))} v<0 {Slope(t => t.Minus, v => Math.Max(-v, 0))}");
        }
        foreach (var (label, list) in others)
            Console.WriteLine($"{label,-24} n {list.Count,3}: fitted minus expected: mean {list.Average():0.000}, rms {Math.Sqrt(list.Average(x => x * x)):0.000}");
    }

    /// <summary>
    /// facegentint &lt;game&gt; #count [--plugin X] [--layers]: the tint texture baked by <see cref="FaceGenTint"/> against the CK's
    /// for NPCs of a plugin (mean and 99th percentile error, 0–255); --layers: per mask type, whether leaving a layer out
    /// matches the CK file better (a layer type the CK does not bake).
    /// </summary>
    public static void Tint(Workspace ws, string[] args)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var plugin = args.SkipWhile(x => x != "--plugin").Skip(1).FirstOrDefault() ?? "Skyrim.esm";
        var perType = new Dictionary<string, (int Applied, int Skipped)>();
        var means = new List<double>();
        foreach (var rec in catalog.BySignature["NPC_"].Where(r => r.FormKey.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase)).Take(int.Parse(args[2].TrimStart('#'))))
        {
            var key = rec.FormKey;
            var tintPath = $@"textures\actors\character\facegendata\facetint\{key.ModKey.FileName}\{key.ID:X8}.dds";
            if (vfs.ReadAllBytes(tintPath) is not { } ckBytes) continue;
            var npc = ws.LinkCache.ResolveAllSimpleContexts<INpcGetter>(key).Last().Record;
            // --race-from X: the race as that plugin has it (the winner by default).
            var raceFrom = args.SkipWhile(x => x != "--race-from").Skip(1).FirstOrDefault();
            var race = raceFrom is null ? lookup.Resolve<IRaceGetter>(npc.Race.FormKey)
                : ws.LinkCache.ResolveAllSimpleContexts<IRaceGetter>(npc.Race.FormKey).FirstOrDefault(c => c.ModKey.FileName.String.Equals(raceFrom, StringComparison.OrdinalIgnoreCase))?.Record;
            if (race is null || npc.TintLayers.Count == 0) continue;
            var head = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? race.HeadData?.Female : race.HeadData?.Male;
            TextureImage ck;
            try { ck = TextureImage.Decode(ckBytes).Resize(FaceGenTint.Size, FaceGenTint.Size); } catch { continue; }
            var ours = FaceGenTint.Compose(npc, head, vfs.ReadAllBytes);
            var errors = Errors(ours, ck);
            var sorted = errors.OrderBy(e => e).ToArray();
            means.Add(errors.Average());
            Console.WriteLine($"{npc.EditorID,-32} layers {npc.TintLayers.Count,2}: mean {errors.Average():0.00}, 99% {sorted[(int)(sorted.Length * 0.99)]:0}, max {sorted[^1]:0}");
            if (!args.Contains("--layers")) continue;
            foreach (var layer in npc.TintLayers.Where(l => (l.InterpolationValue ?? 0) > 0.05f))
            {
                var mask = head?.TintMasks.FirstOrDefault(m => m.Index == layer.Index);
                if (mask is null) continue;
                var without = FaceGenTint.Compose(npc, head, vfs.ReadAllBytes, m => m == mask);
                var errorsWithout = Errors(without, ck);
                // Only where the layer makes a visible difference.
                double with = 0, wo = 0;
                var count = 0;
                for (var i = 0; i < errors.Length; i++)
                {
                    var d = Math.Abs(ours.Pixels[i * 4] - without.Pixels[i * 4]) + Math.Abs(ours.Pixels[i * 4 + 1] - without.Pixels[i * 4 + 1]) + Math.Abs(ours.Pixels[i * 4 + 2] - without.Pixels[i * 4 + 2]);
                    if (d < 6) continue;
                    with += errors[i]; wo += errorsWithout[i]; count++;
                }
                if (count < 50) continue;
                var type = mask.MaskType?.ToString() ?? "none";
                var st = perType.GetValueOrDefault(type);
                perType[type] = with <= wo ? (st.Applied + 1, st.Skipped) : (st.Applied, st.Skipped + 1);
                if (with > wo) Console.WriteLine($"    layer {layer.Index} [{type}] value {layer.InterpolationValue:0.##} colour {layer.Color}: better left out ({with / count:0.0} vs {wo / count:0.0} over {count} px)");
            }
        }
        if (means.Count > 0) Console.WriteLine($"{means.Count} tints: mean error {means.Average():0.00}, NPCs under 3: {means.Count(m => m < 3)}");
        foreach (var (type, st) in perType.OrderBy(x => x.Key)) Console.WriteLine($"  {type,-18} applied better {st.Applied}, left out better {st.Skipped}");

        static double[] Errors(TextureImage a, TextureImage b)
        {
            var e = new double[a.Width * a.Height];
            for (var i = 0; i < e.Length; i++)
                e[i] = Math.Max(Math.Abs(a.Pixels[i * 4] - b.Pixels[i * 4]), Math.Max(Math.Abs(a.Pixels[i * 4 + 1] - b.Pixels[i * 4 + 1]), Math.Abs(a.Pixels[i * 4 + 2] - b.Pixels[i * 4 + 2])));
            return e;
        }
    }

    /// <summary>
    /// facegenbuild &lt;game&gt; &lt;NPC FormID&gt; &lt;folder&gt; [--winner]: export the NPC's FaceGen into the folder (never into the
    /// game) and compare it with the CK's files: shapes, positions, partition buffer, textures, tint colours, block sizes.
    /// </summary>
    public static void Build(Workspace ws, string[] args)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var vfs = AssetFileSystem.Build(ws);
        var key = ws.ParseFormReference(args[2])!.Value;
        var folder = args[3];
        Directory.CreateDirectory(folder);
        var versions = ws.LinkCache.ResolveAllSimpleContexts<INpcGetter>(key).ToList();
        var npc = (args.Contains("--winner") ? versions.First() : versions.Last()).Record;
        var race = lookup.Resolve<IRaceGetter>(npc.Race.FormKey);
        if (FaceGenExport.Unsupported(npc, race) is { } why) { Console.WriteLine($"{npc.EditorID}: {why}"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = FaceGenExport.Build(npc, lookup, vfs.ReadAllBytes);
        var nifOut = Path.Combine(folder, $"{key.ID:X8}.nif");
        var ddsOut = Path.Combine(folder, $"{key.ID:X8}.dds");
        File.WriteAllBytes(nifOut, files.Nif);
        File.WriteAllBytes(ddsOut, files.Tint);
        Console.WriteLine($"{npc.EditorID}: built in {sw.ElapsedMilliseconds} ms, nif {files.Nif.Length} bytes, tint {files.Tint.Length} bytes; parts {string.Join(", ", files.Parts)}");
        foreach (var note in files.Notes) Console.WriteLine("  note: " + note);

        // Our file must read back with our own reader.
        var ours = NifFile.Load(files.Nif, nifOut, raw: true);
        var oursDoc = NifDocument.Read(files.Nif);
        if (vfs.ReadAllBytes(FaceGenExport.NifPath(key)) is not { } ckBytes) { Console.WriteLine("  no CK FaceGen to compare with"); return; }
        var ck = NifFile.Load(ckBytes, "ck", raw: true);
        var ckDoc = NifDocument.Read(ckBytes);
        foreach (var shape in ck.Shapes)
        {
            var mine = ours.Shapes.FirstOrDefault(x => x.Name == shape.Name);
            if (mine is null) { Console.WriteLine($"  {shape.Name}: MISSING in ours"); continue; }
            var worst = mine.Positions.Length == shape.Positions.Length ? Enumerable.Range(0, mine.Positions.Length).Max(i => (mine.Positions[i] - shape.Positions[i]).Length()) : float.NaN;
            var tex = Enumerable.Range(0, Math.Max(mine.Material.Textures.Length, shape.Material.Textures.Length))
                .Where(i => !string.Equals(i < mine.Material.Textures.Length ? mine.Material.Textures[i] : "", i < shape.Material.Textures.Length ? shape.Material.Textures[i] : "", StringComparison.OrdinalIgnoreCase))
                .Select(i => $"tex{i} ours '{(i < mine.Material.Textures.Length ? mine.Material.Textures[i] : "")}' ck '{(i < shape.Material.Textures.Length ? shape.Material.Textures[i] : "")}'").ToList();
            var tint = Vector3.Distance(mine.Material.TintColor, shape.Material.TintColor);
            Console.WriteLine($"  {shape.Name}: {mine.Positions.Length}/{shape.Positions.Length} v, worst position {worst:0.0000}, shader {mine.Material.LightingType}/{shape.Material.LightingType}, tint {(tint < 0.01 ? "same" : $"ours {mine.Material.TintColor} ck {shape.Material.TintColor}")}{(tex.Count == 0 ? ", textures same" : "; " + string.Join("; ", tex))}");
        }
        foreach (var shape in ours.Shapes.Where(x => ck.Shapes.All(c => c.Name != x.Name))) Console.WriteLine($"  {shape.Name}: only in ours");
        // CK partition buffers: morphed or the mesh's own?
        for (var b = 0; b < ckDoc.Blocks.Count; b++)
        {
            if (ckDoc.Blocks[b].Type != "BSDynamicTriShape") continue;
            var name = ckDoc.StringAt(BitConverter.ToInt32(ckDoc.Blocks[b].Data, 0));
            var skinRef = BitConverter.ToInt32(ckDoc.Blocks[b].Data, 4 + 4 + 4 + 4 + 52 + 4 + 16 + (BitConverter.ToInt32(ckDoc.Blocks[b].Data, 4) * 4));
            var partitionRef = BitConverter.ToInt32(ckDoc.Blocks[skinRef].Data, 4);
            var partition = FaceGenNif.PartitionPositions(ckDoc.Blocks[partitionRef].Data, ckDoc.BsVersion);
            var shape = ck.Shapes.First(x => x.Name == name);
            var diff = partition.Length == shape.Positions.Length ? Enumerable.Range(0, partition.Length).Max(i => (partition[i] - shape.Positions[i]).Length()) : -1;
            Console.WriteLine($"  CK {name}: partition buffer {(partition.Length == 0 ? "has no positions" : $"positions vs dynamic: worst {diff:0.0000}")}");
        }
        Console.WriteLine($"  blocks ours {oursDoc.Blocks.Count}, ck {ckDoc.Blocks.Count}");
        for (var i = 0; i < Math.Max(oursDoc.Blocks.Count, ckDoc.Blocks.Count); i++)
        {
            var a = i < oursDoc.Blocks.Count ? $"{oursDoc.Blocks[i].Type} {oursDoc.Blocks[i].Data.Length}" : "-";
            var c = i < ckDoc.Blocks.Count ? $"{ckDoc.Blocks[i].Type} {ckDoc.Blocks[i].Data.Length}" : "-";
            Console.WriteLine($"    {i,3} {a,-36} {c,-36}{(a == c ? "" : "  <>")}");
        }
        // The CK's node flags and the first bytes of its shader and root, for reference.
        Console.WriteLine($"  CK root flags {BitConverter.ToUInt32(ckDoc.Blocks[0].Data, 12 + BitConverter.ToInt32(ckDoc.Blocks[0].Data, 4) * 4)}, header '{ckDoc.Author}' / '{ckDoc.ProcessScript}' / '{ckDoc.ExportScript}', user version {ckDoc.UserVersion}");
        foreach (var (type, data) in ckDoc.Blocks.Where(b => b.Type is "NiNode").Take(6))
            Console.WriteLine($"  CK node {ckDoc.StringAt(BitConverter.ToInt32(data, 0))}: flags {BitConverter.ToUInt32(data, 12 + BitConverter.ToInt32(data, 4) * 4)}");

        // The tint against the CK's.
        if (vfs.ReadAllBytes(FaceGenExport.TintPath(key)) is { } ckTint)
        {
            var a = TextureImage.Decode(files.Tint);
            var b = TextureImage.Decode(ckTint).Resize(a.Width, a.Height);
            double sum = 0;
            for (var i = 0; i < a.Width * a.Height; i++)
                sum += Math.Max(Math.Abs(a.Pixels[i * 4] - b.Pixels[i * 4]), Math.Max(Math.Abs(a.Pixels[i * 4 + 1] - b.Pixels[i * 4 + 1]), Math.Abs(a.Pixels[i * 4 + 2] - b.Pixels[i * 4 + 2])));
            Console.WriteLine($"  tint (our DXT5) vs CK: mean error {sum / (a.Width * a.Height):0.00}");
        }
    }

    /// <summary>Least-squares weights of <paramref name="morphs"/> that best give <paramref name="target"/>.</summary>
    public static Dictionary<string, double> Fit(List<(string Name, Vector3[] D)> morphs, Vector3[] target)
    {
        var k = morphs.Count;
        var n = target.Length;
        var a = new double[k, k];
        var b = new double[k];
        for (var x = 0; x < k; x++)
        {
            for (var y = x; y < k; y++)
            {
                double sum = 0;
                for (var i = 0; i < n; i++) sum += Vector3.Dot(morphs[x].D[i], morphs[y].D[i]);
                a[x, y] = a[y, x] = sum;
            }
            double sb = 0;
            for (var i = 0; i < n; i++) sb += Vector3.Dot(morphs[x].D[i], target[i]);
            b[x] = sb;
            a[x, x] *= 1 + 1e-9;
        }
        var w = Solve(a, b);
        var result = new Dictionary<string, double>();
        for (var x = 0; x < k; x++) result[morphs[x].Name] = w[x];
        return result;
    }

    private static double[] Solve(double[,] m, double[] rhs)
    {
        var size = rhs.Length;
        var aug = new double[size, size + 1];
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++) aug[r, c] = m[r, c];
            aug[r, size] = rhs[r];
        }
        for (var col = 0; col < size; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < size; r++) if (Math.Abs(aug[r, col]) > Math.Abs(aug[pivot, col])) pivot = r;
            if (Math.Abs(aug[pivot, col]) < 1e-30) continue;
            for (var c = 0; c <= size; c++) (aug[col, c], aug[pivot, c]) = (aug[pivot, c], aug[col, c]);
            for (var r = 0; r < size; r++)
            {
                if (r == col) continue;
                var f = aug[r, col] / aug[col, col];
                if (f == 0) continue;
                for (var c = col; c <= size; c++) aug[r, c] -= f * aug[col, c];
            }
        }
        var result = new double[size];
        for (var r = 0; r < size; r++) result[r] = Math.Abs(aug[r, r]) < 1e-30 ? 0 : aug[r, size] / aug[r, r];
        return result;
    }
}
