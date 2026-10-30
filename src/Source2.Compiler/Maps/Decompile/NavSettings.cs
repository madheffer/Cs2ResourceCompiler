using System.Buffers.Binary;
using Source2.Compiler.Maps.NavGen;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// The nav settings a map was built with, back from its <c>maps/&lt;map&gt;.nav</c>. SaveNavMeshToBuffer
/// (rc 1818d1320, <see cref="NavWriter.Write"/>) writes the generation parameters navsystem ran with
/// (1818d0690) after the areas: the block resourcecompiler chose from gameinfo's NavSystem and the map's
/// CMapNavData (1818c95e0, <see cref="NavGenSettings.FromMap"/>). Those are what the compile reads, so a
/// CMapNavData holding them builds the same nav: with UseProjectDefaults, or a hull preset named, the
/// project's numbers are what the block shows and the map's own (which the build never read) are lost.
/// A map whose nav is the fixed empty one (<see cref="NavFile.EmptyDefault"/>) had no CMapNavData and no
/// walkable seed, and gets none.
/// </summary>
internal static class NavSettings
{
    /// <summary>The block's settings and its last byte (+0x59), or null with the reason the file is not read.</summary>
    public static (NavGenSettings? Settings, byte Byte59, string? Reason) FromNav(byte[] nav)
    {
        var p = 0;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(nav.AsSpan(p)); p += 4; return v; }
        float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(nav.AsSpan(p)); p += 4; return v; }
        byte U8() => nav[p++];
        string Str()
        {
            var end = Array.IndexOf(nav, (byte)0, p);
            var s = System.Text.Encoding.UTF8.GetString(nav, p, end - p);
            p = end + 1;
            return s;
        }
        // A binary KV3 block on an 8-byte boundary, closed by its 00 dd ee ff trailer.
        void Kv3()
        {
            p = (p + 7) & ~7;
            p = nav.AsSpan(p).IndexOf(new byte[] { 0, 0xdd, 0xee, 0xff }) + p + 4;
        }
        var stage = "header";
        try
        {
            if (U32() != 0xFEEDFACE || U32() is var version && version != 36)
                return (null, 0, "not a version 36 .nav");
            U32();
            U8();
            var names = BinaryPrimitives.ReadUInt16LittleEndian(nav.AsSpan(p));
            p += 2;
            for (var i = 0; i < names; i++)
                p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(nav.AsSpan(p));
            U8();
            Kv3();
            stage = "corners";
            var corners = (int)U32();
            p += 12 * corners;
            var polygons = new List<int>();
            for (var n = U32(); n > 0; n--)
            {
                int k = U8();
                polygons.Add(k);
                p += (4 * k) + 4;
            }
            if (U32() != 0)
                return (null, 0, "vertex attribute flags (not read)");
            // The movable meshes: each its id path and 3x4 matrix (NavWriter.MovableMesh, 1818e9510).
            for (var n = U32(); n > 0; n--)
            {
                Str();
                p += 48;
            }
            Kv3();
            stage = "areas";
            for (var n = U32(); n > 0; n--)
            {
                // Id, attributes, hull byte; the polygon; a float.
                p += 13;
                var polygon = (int)U32();
                p += 4;
                for (var e = 0; e < polygons[polygon]; e++)
                {
                    var links = (int)U32();
                    p += 8 * links;
                }
                if (U8() != 0 || U32() != 0)
                    return (null, 0, "hiding or encounter spots (not read)");
                var up = (int)U32();
                p += 4 * up;
                var down = (int)U32();
                p += 4 * down;
            }
            stage = "ladders";
            if (U32() != 0)
                return (null, 0, "ladders (not read)");
            if (U32() != 0)
                return (null, 0, "transformed bounds (not read)");
            if (U32() != 13)
                return (null, 0, "generation parameters of another version");
            var s = new NavGenSettings
            {
                UseProjectDefaults = U32() != 0,
                TileSize = F32(),
                CellSize = F32(),
                CellHeight = F32(),
                RegionMinSize = (int)U32(),
                RegionMergeSize = (int)U32(),
                DetailSampleDist = F32(),
                DetailSampleMaxError = F32(),
                EdgeMaxLen = (int)U32(),
                EdgeMaxError = F32(),
                VertsPerPoly = (int)U32(),
                SmallAreaOnEdgeRemovalSize = F32(),
                HullPreset = Str(),
                HullsVDataOverride = Str(),
            };
            for (var n = U32(); n > 0; n--)
                s.Hulls.Add(new NavHull
                {
                    Enabled = U8() != 0,
                    Radius = F32(),
                    Height = F32(),
                    ShortHeightEnabled = U8() != 0,
                    ShortHeight = F32(),
                    CrawlEnabled = U8() != 0,
                    CrawlHeight = F32(),
                    MaxClimb = F32(),
                    MaxSlope = (int)U32(),
                    MaxJumpDownDist = F32(),
                    MaxJumpHorizDistBase = F32(),
                    MaxJumpUpDist = F32(),
                    BorderErosion = (int)U32(),
                });
            return (s, U8(), null);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return (null, 0, $"the file ends early ({stage}, at {p} of {nav.Length})");
        }
    }

    /// <summary>The block as <see cref="NavWriter"/> writes it, for comparing two settings.</summary>
    public static byte[] Block(NavGenSettings settings) => NavWriter.GenerationParametersBytes(settings);
}
