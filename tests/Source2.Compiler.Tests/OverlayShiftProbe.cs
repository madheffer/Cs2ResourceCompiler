using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// OPEN 1.a5 (ledger 278): do overlays take HammerMesh_ShiftTexcoordIslands (no: 0 of 297 on atixref)? <c>TEXSHIFT=&lt;addon&gt;|&lt;map&gt;|&lt;jsonl&gt;</c>
/// (tools/re/capture_texshift.py output): for each overlay of the map, whether its raw .vmap texcoords occur as
/// one captured island call's input, and how that call's output compares with our NodeOverlays faces.
/// </summary>
public class OverlayShiftProbe(ITestOutputHelper output)
{
    [Fact]
    public void OverlaysAgainstIslandCalls()
    {
        if (Environment.GetEnvironmentVariable("TEXSHIFT") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var path])
            return;
        var calls = new List<(Vector2[] Before, Vector2[] After)>();
        foreach (var line in File.ReadLines(path))
        {
            using var j = JsonDocument.Parse(line);
            var e = j.RootElement;
            if (e.GetProperty("ev").GetString() != "recentre" || e.GetProperty("before").ValueKind != JsonValueKind.Array)
                continue;
            static Vector2[] Uvs(JsonElement a) => [.. a.EnumerateArray().Select(p => new Vector2(p[0].GetSingle(), p[1].GetSingle()))];
            calls.Add((Uvs(e.GetProperty("before")), Uvs(e.GetProperty("after"))));
        }
        var doc = MapFixtures.ReadAsCompiled(MapFixtures.VmapSource(addon, map)!);
        var overlays = MapMeshes.ReadOverlays(doc);
        output.WriteLine($"{calls.Count} island calls, {overlays.Count} overlays, {MapMeshes.Read(doc).Count} meshes");
        var byFirst = calls.GroupBy(c => c.Before.Length > 0 ? c.Before[0] : Vector2.Zero).ToDictionary(g => g.Key, g => g.ToList());
        int found = 0, shifted = 0;
        foreach (var o in overlays)
        {
            if (o.Element?.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var uvs = (data.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams") ?? [])
                .FirstOrDefault(s => s.Name.Split(':')[0] == "texcoord")?.Get<object?[]>("data")?.OfType<Vector2>().ToArray() ?? [];
            if (uvs.Length == 0)
                continue;
            var set = uvs.ToHashSet();
            var match = calls.FirstOrDefault(c => c.Before.Length > 0 && c.Before.All(set.Contains) && set.All(c.Before.Contains));
            if (match.Before == null)
                continue;
            found++;
            if (!match.Before.SequenceEqual(match.After))
                shifted++;
        }
        output.WriteLine($"overlays whose texcoords are one island call's input: {found}; of them shifted by it: {shifted}");
    }

    /// <summary>
    /// <c>TEXSHIFT_CACHE=&lt;addon&gt;|&lt;map&gt;|&lt;jsonl&gt;</c> (tools/re/capture_overlaycache.py output): each
    /// projection cache face, as the projector leaves it, matched to our NodeOverlays face by its world positions;
    /// counts the faces whose cached texcoords equal ours (Recentre applied) and the ones equal to the raw .vmap
    /// texcoords (no shift).
    /// </summary>
    [Fact]
    public void CacheAgainstOurFaces()
    {
        if (Environment.GetEnvironmentVariable("TEXSHIFT_CACHE") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var path])
            return;
        var cached = new List<(Vector3[] Pos, Vector2[] Uv)>();
        int records = 0, unread = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var j = JsonDocument.Parse(line);
            var e = j.RootElement;
            records++;
            if (!e.TryGetProperty("cache", out var c) || c.ValueKind != JsonValueKind.Object)
            {
                unread++;
                continue;
            }
            foreach (var mat in c.GetProperty("mats").EnumerateArray())
                foreach (var f in mat.EnumerateArray())
                    cached.Add(([.. f.GetProperty("pos").EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()))],
                                [.. f.GetProperty("uv").EnumerateArray().Select(p => new Vector2(p[0].GetSingle(), p[1].GetSingle()))]));
        }
        output.WriteLine($"{records} projector calls ({unread} records not walked), {cached.Count} cached faces");
        var doc = MapFixtures.ReadAsCompiled(MapFixtures.VmapSource(addon, map)!);
        var ours = NodeOverlays.FromWorld(doc).SelectMany(d => d.Faces).ToList();
        static bool Near(Vector3[] a, Vector3[] b) => a.Length == b.Length && a.All(p => b.Any(q => Vector3.DistanceSquared(p, q) < 1e-4f));
        int matched = 0, equalOurs = 0, equalOursAsSet = 0;
        var shown = 0;
        foreach (var face in ours)
        {
            var hit = cached.FirstOrDefault(c => Near(c.Pos, face.Positions));
            if (hit.Pos == null)
                continue;
            matched++;
            if (hit.Uv.SequenceEqual(face.Texcoords))
                equalOurs++;
            else if (hit.Uv.Length == face.Texcoords.Length && hit.Uv.All(face.Texcoords.Contains))
                equalOursAsSet++;
            else if (shown++ < 6)
                output.WriteLine($"  differs: ours [{string.Join(" ", face.Texcoords)}] cache [{string.Join(" ", hit.Uv)}]");
        }
        output.WriteLine($"our faces {ours.Count}, matched to a cached face {matched}: texcoords equal {equalOurs}, equal in another order {equalOursAsSet}");
    }
}
