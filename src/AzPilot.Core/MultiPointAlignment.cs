namespace AzPilot.Core;

public sealed record AlignmentPoint(
    double MechanicalAzimuth,
    double MechanicalAltitude,
    double SkyAzimuth,
    double SkyAltitude,
    double RightAscension = 0,
    double Declination = 0);

/// <summary>
/// Session-only best-fit 3D rotation between mechanical Alt/Az encoder vectors and
/// observed sky Alt/Az vectors. Three or more separated points compensate rotation,
/// tripod tilt and similar rigid pointing errors; this is not a full flexure model.
/// </summary>
public sealed class MultiPointAlignment
{
    private readonly AlignmentPoint[] _points;
    private Rotation? _rotation;

    public static MultiPointAlignment Empty { get; } = new([]);
    public int PointCount => _points.Length;
    public bool IsActive => _rotation.HasValue;
    public double? ResidualDegrees { get; private set; }
    public IReadOnlyList<AlignmentPoint> Points => _points;

    private MultiPointAlignment(AlignmentPoint[] points)
    {
        _points = points;
        if (points.Length >= 3 && HasUsefulSeparation(points)) Fit();
    }

    public MultiPointAlignment AddPoint(AlignmentPoint point)
    {
        ValidateHorizontal(point.MechanicalAzimuth, point.MechanicalAltitude);
        ValidateHorizontal(point.SkyAzimuth, point.SkyAltitude);
        var points = _points
            .Where(existing => AngularSeparation(
                FromHorizontal(existing.MechanicalAzimuth, existing.MechanicalAltitude),
                FromHorizontal(point.MechanicalAzimuth, point.MechanicalAltitude)) >= 0.2)
            .Append(point)
            .ToArray();
        return new(points);
    }

    public (double Azimuth, double Altitude) ToSky(double azimuth, double altitude)
    {
        if (!_rotation.HasValue) throw new InvalidOperationException("The multi-point alignment model is not active.");
        return ToHorizontal(_rotation.Value.Rotate(FromHorizontal(azimuth, altitude)));
    }

    public (double Azimuth, double Altitude) ToMechanical(double azimuth, double altitude)
    {
        if (!_rotation.HasValue) throw new InvalidOperationException("The multi-point alignment model is not active.");
        return ToHorizontal(_rotation.Value.Inverse().Rotate(FromHorizontal(azimuth, altitude)));
    }

    private void Fit()
    {
        // Horn's absolute-orientation quaternion method, reduced to unit-vector pairs.
        // S = sum(mechanical * sky^T), so the selected rotation maps encoder to sky.
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        foreach (var point in _points)
        {
            var a = FromHorizontal(point.MechanicalAzimuth, point.MechanicalAltitude);
            var b = FromHorizontal(point.SkyAzimuth, point.SkyAltitude);
            sxx += a.X * b.X; sxy += a.X * b.Y; sxz += a.X * b.Z;
            syx += a.Y * b.X; syy += a.Y * b.Y; syz += a.Y * b.Z;
            szx += a.Z * b.X; szy += a.Z * b.Y; szz += a.Z * b.Z;
        }
        var n = new[,]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz }
        };

        // Shift the symmetric matrix to make its eigenvalues positive, then use power
        // iteration. The dominant shifted eigenvector is Horn's largest-eigenvalue quaternion.
        var shift = 1.0 + Enumerable.Range(0, 4)
            .Select(row => Enumerable.Range(0, 4).Sum(column => Math.Abs(n[row, column]))).Max();
        var q = Normalize(new[] { 1.0, 0.17, -0.11, 0.07 });
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var next = new double[4];
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++) next[row] += n[row, column] * q[column];
                next[row] += shift * q[row];
            }
            next = Normalize(next);
            if (next.Zip(q).Sum(pair => Math.Pow(pair.First - pair.Second, 2)) < 1e-28)
            {
                q = next;
                break;
            }
            q = next;
        }
        if (q[0] < 0) q = q.Select(value => -value).ToArray();
        var rotation = new Rotation(q[0], q[1], q[2], q[3]);
        _rotation = rotation;

        var squaredResidual = 0.0;
        foreach (var point in _points)
        {
            var predicted = rotation.Rotate(FromHorizontal(point.MechanicalAzimuth, point.MechanicalAltitude));
            var observed = FromHorizontal(point.SkyAzimuth, point.SkyAltitude);
            squaredResidual += Math.Pow(AngularSeparation(predicted, observed), 2);
        }
        ResidualDegrees = Math.Sqrt(squaredResidual / _points.Length);
    }

    private static bool HasUsefulSeparation(IEnumerable<AlignmentPoint> points)
    {
        var mechanical = points.Select(p => FromHorizontal(p.MechanicalAzimuth, p.MechanicalAltitude)).ToArray();
        var sky = points.Select(p => FromHorizontal(p.SkyAzimuth, p.SkyAltitude)).ToArray();
        static double Spread(Vector3[] vectors)
        {
            var maximum = 0.0;
            for (var i = 0; i < vectors.Length; i++)
            for (var j = i + 1; j < vectors.Length; j++)
                maximum = Math.Max(maximum, AngularSeparation(vectors[i], vectors[j]));
            return maximum;
        }
        return Spread(mechanical) >= 10 && Spread(sky) >= 10;
    }

    private static double AngularSeparation(Vector3 a, Vector3 b) =>
        Math.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / Math.PI;

    private static Vector3 FromHorizontal(double azimuth, double altitude)
    {
        var az = azimuth * Math.PI / 180;
        var alt = altitude * Math.PI / 180;
        var cosAlt = Math.Cos(alt);
        return new(cosAlt * Math.Sin(az), cosAlt * Math.Cos(az), Math.Sin(alt));
    }

    private static (double Azimuth, double Altitude) ToHorizontal(Vector3 vector)
    {
        var unit = vector.Normalize();
        var azimuth = Math.Atan2(unit.X, unit.Y) * 180 / Math.PI;
        var altitude = Math.Asin(Math.Clamp(unit.Z, -1, 1)) * 180 / Math.PI;
        return (Angle.Wrap360(azimuth), altitude);
    }

    private static void ValidateHorizontal(double azimuth, double altitude)
    {
        if (!double.IsFinite(azimuth) || !double.IsFinite(altitude) || altitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(altitude));
    }

    private static double[] Normalize(double[] values)
    {
        var length = Math.Sqrt(values.Sum(value => value * value));
        if (!double.IsFinite(length) || length < 1e-15) throw new InvalidOperationException("Alignment fit failed.");
        return values.Select(value => value / length).ToArray();
    }

    private readonly record struct Vector3(double X, double Y, double Z)
    {
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator *(double scale, Vector3 value) => new(scale * value.X, scale * value.Y, scale * value.Z);
        public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new(
            a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public Vector3 Normalize() => (1.0 / Math.Sqrt(Dot(this, this))) * this;
    }

    private readonly record struct Rotation(double W, double X, double Y, double Z)
    {
        public Vector3 Rotate(Vector3 value)
        {
            var axis = new Vector3(X, Y, Z);
            var twiceCross = 2.0 * Vector3.Cross(axis, value);
            return value + W * twiceCross + Vector3.Cross(axis, twiceCross);
        }
        public Rotation Inverse() => new(W, -X, -Y, -Z);
    }
}
