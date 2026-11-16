using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using GameOne;

namespace TerrainGen;

/// <summary>
/// Pictures of a map for judging its look without starting the game (<c>--preview</c>): the whole
/// world (equirectangular), the launch site from above, and each place in <c>views</c> from above
/// (the icy seas' ice), coloured by surface kind and depth and shaded by the sun from the north-west.
/// </summary>
internal static class Preview
{
    public static void Write(TerrainMap map, string folder, IEnumerable<(string File, Vector3 Centre, float Across)> views)
    {
        Directory.CreateDirectory(folder);
        const int w = 2048, h = 1024;
        Png(Path.Combine(folder, "world.png"), w, h, (x, y) =>
        {
            var lat = (0.5f - (y + 0.5f) / h) * MathF.PI;
            var lon = ((x + 0.5f) / w - 0.5f) * 2f * MathF.PI;
            return Shade(map, CubeSphere.FromLatLon(lat, lon), 30f);
        });

        // The launch site, 20 km across, from straight above the pad.
        Above(map, Path.Combine(folder, "launch-site.png"), Vector3.UnitY, 20000f);
        foreach (var (file, centre, across) in views)
            Above(map, Path.Combine(folder, file), centre, across);
    }

    /// <summary>
    /// Pictures of the moon's map, lit as the game lights it (each kind's albedo, the sun's slant on
    /// the ground and the ground's own shadows, <see cref="MoonGround.SunSeenOver"/>): its near side
    /// as the planet sees it, north up, with the sun low from the east (<c>near-side-east.png</c>)
    /// and from the west (<c>near-side-west.png</c>); its far side from straight out beyond it, north
    /// up and east to the right, the sun low from the east (<c>far-side.png</c>); and the landing
    /// spot, 3 km across from straight above with its near-side meridian (towards the planet) down
    /// (<c>landing-spot.png</c>).
    /// </summary>
    public static void WriteMoon(TerrainMap map, string folder)
    {
        Directory.CreateDirectory(folder);
        const int s = 1024;
        const float low = 8f * MathF.PI / 180f; // the sun's height over the middle of the side shown
        // Which side (1 the near side, lat 0 lon 0 facing the planet along +y; -1 the far side,
        // lon 180), and which way the sun lies (1 from the east, -1 from the west).
        foreach (var (file, side, east) in new[] { ("near-side-east.png", 1f, 1f), ("near-side-west.png", 1f, -1f), ("far-side.png", -1f, 1f) })
        {
            var facing = Vector3.UnitY * side;
            var eastward = Vector3.UnitX * side; // east at the middle of the side shown
            var sun = Vector3.Normalize(eastward * east * MathF.Cos(low) + facing * MathF.Sin(low));
            Png(Path.Combine(folder, file), s, s, (x, y) =>
            {
                var px = ((x + 0.5f) / s - 0.5f) * 2.1f;
                var py = (0.5f - (y + 0.5f) / s) * 2.1f;
                var outward = 1f - px * px - py * py;
                if (outward <= 0f) return Vector3.Zero;
                var dir = eastward * px + facing * MathF.Sqrt(outward) - Vector3.UnitZ * py; // north, up the picture, is -z
                return Vector3.One * MoonLight(map, dir, sun);
            });
        }

        // The landing spot from above, lit from low in the south (the near side's way), so its
        // levelled plains and the small craters round it show.
        var spot = CubeSphere.FromLatLon(Scene.MoonLandingSpot.Latitude * MathF.PI / 180f, Scene.MoonLandingSpot.Longitude * MathF.PI / 180f);
        var spotEast = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), spot));
        var spotNorth = Vector3.Cross(spot, spotEast);
        var spotSun = Vector3.Normalize(-spotNorth * MathF.Cos(3f * low) + spotEast * 0.4f + spot * MathF.Sin(3f * low));
        const float across = 3000f;
        Png(Path.Combine(folder, "landing-spot.png"), s, s, (x, y) =>
        {
            var e = ((x + 0.5f) / s - 0.5f) * across;
            var n = (0.5f - (y + 0.5f) / s) * across;
            var dist = MathF.Sqrt(e * e + n * n);
            var dir = spot;
            if (dist > 0f)
            {
                var angle = dist / map.Radius;
                dir = spot * MathF.Cos(angle) + Vector3.Normalize(spotEast * e + spotNorth * n) * MathF.Sin(angle);
            }

            var c = MoonLight(map, dir, spotSun);
            // The levelled spot's edge, faintly, so it can be found.
            var edge = MathF.Abs(dist - Scene.MoonLandingSpotRadius) < 3f ? 0.15f : 0f;
            return new Vector3(c + edge, c, c);
        });
    }

    /// <summary>How bright the moon's ground looks in <paramref name="dir"/> lit from <paramref name="sun"/>, as a display value.</summary>
    private static float MoonLight(TerrainMap map, Vector3 dir, Vector3 sun)
    {
        var lit = MathF.Max(Vector3.Dot(MoonGround.Normal(map, dir), sun), 0f) * MoonGround.SunSeenOver(map, dir, sun);
        var linear = MoonGround.Albedo(map, dir) * (6f * lit + 0.04f);
        return MathF.Pow(MathF.Min(linear, 1f), 1f / 2.2f);
    }

    /// <summary>A square picture <paramref name="across"/> metres wide, north up, from straight above <paramref name="centre"/> (a unit direction, not a pole).</summary>
    private static void Above(TerrainMap map, string path, Vector3 centre, float across)
    {
        const int s = 1280;
        var eastward = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), centre)); // as Planet.East
        var northward = Vector3.Cross(centre, eastward);
        Png(path, s, s, (x, y) =>
        {
            var east = ((x + 0.5f) / s - 0.5f) * across;
            var north = (0.5f - (y + 0.5f) / s) * across;
            var dist = MathF.Sqrt(east * east + north * north);
            var dir = centre;
            if (dist > 0f)
            {
                var heading = Vector3.Normalize(eastward * east + northward * north);
                var angle = dist / map.Radius;
                dir = centre * MathF.Cos(angle) + heading * MathF.Sin(angle);
            }

            return Shade(map, dir, across / s);
        });
    }

    private static Vector3 Shade(TerrainMap map, Vector3 dir, float spacing)
    {
        float At(Vector3 d)
        {
            var (f, u, v) = CubeSphere.ToFace(Vector3.Normalize(d));
            return map.HeightAt(f, u, v);
        }

        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), dir));
        if (!float.IsFinite(east.X)) east = Vector3.UnitX;
        var north = Vector3.Cross(dir, east);
        var step = spacing / map.Radius;
        var height = At(dir);
        var de = (At(dir + east * step) - At(dir - east * step)) / (2f * spacing);
        var dn = (At(dir + north * step) - At(dir - north * step)) / (2f * spacing);
        var normal = Vector3.Normalize(new Vector3(-de, -dn, 1f));
        var light = Vector3.Normalize(new Vector3(-1f, 1f, 1.2f));
        var lit = Math.Clamp(Vector3.Dot(normal, light) / light.Z * 0.85f, 0.25f, 1.3f);

        var (face, fu, fv) = CubeSphere.ToFace(dir);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        map.Texels(face, fu, fv, index, weight);
        if (map.RegionAt(face, fu, fv) != 0)
        {
            var depth = -height; // the map is above the sea
            var shallow = new Vector3(0.35f, 0.75f, 0.78f);
            var deep = new Vector3(0.04f, 0.14f, 0.32f);
            var c = Vector3.Lerp(shallow, deep, MathF.Min(MathF.Sqrt(MathF.Max(depth, 0f) / 400f), 1f));
            // Among the floes, the water is paled by the ice floating in it.
            var floes = 0f;
            for (var i = 0; i < 4; i++)
                if ((SurfaceKind)map.Kinds[index[i]] == SurfaceKind.FloeField) floes += weight[i];
            c = Vector3.Lerp(c, new Vector3(0.80f, 0.86f, 0.90f), 0.45f * floes);
            return c * (0.85f + 0.15f * lit);
        }

        var colour = Vector3.Zero;
        for (var i = 0; i < 4; i++) colour += Colour((SurfaceKind)map.Kinds[index[i]]) * weight[i];
        return colour * lit;
    }

    private static Vector3 Colour(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Scrub => new Vector3(0.52f, 0.52f, 0.33f),
        SurfaceKind.Grass => new Vector3(0.36f, 0.50f, 0.25f),
        SurfaceKind.Desert => new Vector3(0.82f, 0.70f, 0.48f),
        SurfaceKind.Rock => new Vector3(0.45f, 0.42f, 0.40f),
        SurfaceKind.Snow => new Vector3(0.93f, 0.94f, 0.97f),
        SurfaceKind.Beach => new Vector3(0.88f, 0.82f, 0.62f),
        SurfaceKind.Site => new Vector3(0.75f, 0.30f, 0.30f),
        SurfaceKind.PackIce => new Vector3(0.86f, 0.91f, 0.95f),
        _ => new Vector3(0.5f, 0.5f, 0.5f),
    };

    private static void Png(string path, int width, int height, Func<int, int, Vector3> pixel)
    {
        var raw = new byte[height * (width * 3 + 1)];
        Parallel.For(0, height, y =>
        {
            var row = y * (width * 3 + 1);
            for (var x = 0; x < width; x++)
            {
                var c = pixel(x, y);
                raw[row + 1 + x * 3] = (byte)Math.Clamp(c.X * 255f, 0f, 255f);
                raw[row + 2 + x * 3] = (byte)Math.Clamp(c.Y * 255f, 0f, 255f);
                raw[row + 3 + x * 3] = (byte)Math.Clamp(c.Z * 255f, 0f, 255f);
            }
        });

        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(file, "IHDR", header);
        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
            Chunk(file, "IDAT", packed.ToArray());
        }

        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
        s.Write(four);
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }
}
