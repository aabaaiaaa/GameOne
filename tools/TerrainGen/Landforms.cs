using System.Numerics;

namespace TerrainGen;

/// <summary>A land outline, tested in latitude/longitude (unwrapped round its own middle, so it may cross ±180°).</summary>
internal sealed class Outline
{
    private readonly float[] _lat;
    private readonly float[] _lon;
    private readonly float _middle, _minLat, _maxLat, _minLon, _maxLon;

    public Outline(LandMass land)
    {
        _lat = land.Outline.Select(p => p[0]).ToArray();
        _lon = land.Outline.Select(p => p[1]).ToArray();
        _minLat = _lat.Min();
        _maxLat = _lat.Max();
        _minLon = _lon.Min();
        _maxLon = _lon.Max();
        _middle = (_minLon + _maxLon) * 0.5f;
        if (_maxLon - _minLon >= 360f) throw new InvalidDataException($"{land.Name} goes all the way round");
        if (_minLat <= -90f || _maxLat >= 90f) throw new InvalidDataException($"{land.Name} reaches a pole");
    }

    /// <summary>Whether a latitude and longitude (degrees) is inside (even-odd).</summary>
    public bool Contains(float lat, float lon)
    {
        if (lat < _minLat || lat > _maxLat) return false;
        lon = _middle + ((lon - _middle) % 360f + 540f) % 360f - 180f;
        if (lon < _minLon || lon > _maxLon) return false;
        var inside = false;
        for (int a = 0, b = _lat.Length - 1; a < _lat.Length; b = a++)
        {
            if ((_lat[a] > lat) != (_lat[b] > lat) &&
                lon < _lon[a] + (lat - _lat[a]) / (_lat[b] - _lat[a]) * (_lon[b] - _lon[a]))
                inside = !inside;
        }

        return inside;
    }
}

/// <summary>A mountain range's crest line, as great-circle segments.</summary>
internal sealed class Crest
{
    private readonly Vector3[] _points;
    private readonly Vector3[] _normals;
    private readonly float[] _start;   // metres along the crest where each segment starts
    private readonly Vector3 _middle;
    private readonly float _reach;     // cos of the angle round the middle beyond which nothing is near

    public Crest(RangeLayout range, float radius)
    {
        Range = range;
        _points = range.Path.Select(Degrees.Direction).ToArray();
        _normals = new Vector3[_points.Length - 1];
        _start = new float[_points.Length];
        for (var i = 0; i < _normals.Length; i++)
        {
            _normals[i] = Vector3.Normalize(Vector3.Cross(_points[i], _points[i + 1]));
            _start[i + 1] = _start[i] + radius * Angle(_points[i], _points[i + 1]);
        }

        Length = _start[^1];
        _middle = Vector3.Normalize(_points.Aggregate(Vector3.Zero, (s, p) => s + p));
        var far = _points.Max(p => Angle(p, _middle)) + range.Width * 2.5f / radius;
        _reach = far >= MathF.PI ? -2f : MathF.Cos(far);
    }

    public RangeLayout Range { get; }
    public float Length { get; }

    private static float Angle(Vector3 a, Vector3 b) => 2f * MathF.Asin(MathF.Min(Vector3.Distance(a, b) * 0.5f, 1f));

    /// <summary>The angle (radians) from a direction to the crest, and how far along it (metres) its nearest point is; null if far off.</summary>
    public (float Angle, float Along)? Nearest(Vector3 p, float radius)
    {
        if (Vector3.Dot(p, _middle) < _reach) return null;
        var best = float.MaxValue;
        var along = 0f;
        for (var i = 0; i < _normals.Length; i++)
        {
            var n = _normals[i];
            var onPlane = p - n * Vector3.Dot(p, n);
            float angle, at;
            if (Vector3.Dot(Vector3.Cross(_points[i], onPlane), n) >= 0f && Vector3.Dot(Vector3.Cross(onPlane, _points[i + 1]), n) >= 0f)
            {
                angle = MathF.Asin(Math.Clamp(MathF.Abs(Vector3.Dot(p, n)), 0f, 1f));
                at = _start[i] + radius * Angle(_points[i], Vector3.Normalize(onPlane));
            }
            else
            {
                var a = Angle(p, _points[i]);
                var b = Angle(p, _points[i + 1]);
                (angle, at) = a < b ? (a, _start[i]) : (b, _start[i + 1]);
            }

            if (angle < best)
            {
                best = angle;
                along = at;
            }
        }

        return (best, along);
    }
}
