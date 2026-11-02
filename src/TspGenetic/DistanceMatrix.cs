using System.Globalization;
using System.Text;

namespace TspGenetic;

public sealed class DistanceMatrix
{
    public const int MaxSize = 20000;

    public const double MaxDistance = 1e12;

    private readonly double[] _data;

    public int Size { get; }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<(double X, double Y)>? Points { get; }

    public double this[int i, int j] => _data[i * Size + j];

    public DistanceMatrix(double[,] distances, IReadOnlyList<string>? names = null,
        IReadOnlyList<(double X, double Y)>? points = null)
    {
        ArgumentNullException.ThrowIfNull(distances);
        int n = distances.GetLength(0);
        if (distances.GetLength(1) != n)
            throw new ArgumentException("Матрица расстояний должна быть квадратной.");
        if (n < 3)
            throw new ArgumentException("Нужно хотя бы 3 города.");
        if (n > MaxSize)
            throw new ArgumentException($"Слишком много городов: {n}, допускается не больше {MaxSize}.");
        if (names != null && names.Count != n)
            throw new ArgumentException("Число названий не совпадает с размером матрицы.");
        if (points != null && points.Count != n)
            throw new ArgumentException("Число точек не совпадает с размером матрицы.");

        Size = n;
        _data = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double d = distances[i, j];
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new ArgumentException($"d[{i + 1},{j + 1}] не является конечным числом.");
                if (i == j && d != 0)
                    throw new ArgumentException($"На диагонали должны стоять нули: d[{i + 1},{i + 1}] = {d}.");
                if (d > MaxDistance)
                    throw new ArgumentException($"Расстояние d[{i + 1},{j + 1}] = {d} больше допустимого {MaxDistance:G}.");
                if (i != j && d <= 0)
                    throw new ArgumentException($"Расстояние d[{i + 1},{j + 1}] = {d} должно быть положительным.");
                if (d != distances[j, i])
                    throw new ArgumentException($"Матрица не симметрична: d[{i + 1},{j + 1}] ≠ d[{j + 1},{i + 1}].");
                _data[i * n + j] = d;
            }
        }

        Names = names?.ToArray() ?? Enumerable.Range(1, n).Select(k => k.ToString(CultureInfo.InvariantCulture)).ToArray();
        Points = points?.ToArray();
    }

    public double RouteLength(IReadOnlyList<int> route)
    {
        double sum = 0;
        int n = route.Count;
        for (int i = 0; i < n; i++)
            sum += _data[route[i] * Size + route[(i + 1) % n]];
        return sum;
    }

    internal double RouteLength(int[] route)
    {
        double sum = 0;
        int n = route.Length;
        int prev = route[n - 1];
        for (int i = 0; i < n; i++)
        {
            sum += _data[prev * Size + route[i]];
            prev = route[i];
        }
        return sum;
    }

    public bool SatisfiesTriangleInequality(double tolerance = 1e-9)
    {
        int n = Size;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = 0; k < n; k++)
                    if (this[i, j] > this[i, k] + this[k, j] + tolerance)
                        return false;
        return true;
    }

    public static DistanceMatrix RandomEuclidean(int count, int? seed = null, double side = 100)
    {
        if (count < 3) throw new ArgumentOutOfRangeException(nameof(count), "Нужно хотя бы 3 города.");
        var rnd = seed.HasValue ? new Random(seed.Value) : new Random();
        var points = new (double X, double Y)[count];
        for (int i = 0; i < count; i++)
            points[i] = (rnd.NextDouble() * side, rnd.NextDouble() * side);

        var d = new double[count, count];
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                double dx = points[i].X - points[j].X, dy = points[i].Y - points[j].Y;

                d[i, j] = d[j, i] = Math.Max(Math.Round(Math.Sqrt(dx * dx + dy * dy), 2), 0.01);
            }
        return new DistanceMatrix(d, null, points);
    }

    public static DistanceMatrix Germany14()
    {
        (string Name, double Lat, double Lon)[] cities =
        [
            ("Берлин", 52.520, 13.405),
            ("Гамбург", 53.551, 9.994),
            ("Мюнхен", 48.137, 11.575),
            ("Кёльн", 50.938, 6.960),
            ("Франкфурт", 50.110, 8.682),
            ("Штутгарт", 48.776, 9.183),
            ("Дюссельдорф", 51.227, 6.774),
            ("Лейпциг", 51.340, 12.375),
            ("Дортмунд", 51.514, 7.466),
            ("Эссен", 51.456, 7.012),
            ("Бремен", 53.079, 8.802),
            ("Дрезден", 51.050, 13.738),
            ("Ганновер", 52.376, 9.732),
            ("Нюрнберг", 49.452, 11.077),
        ];
        int n = cities.Length;
        var d = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                d[i, j] = d[j, i] = Math.Round(Haversine(cities[i].Lat, cities[i].Lon, cities[j].Lat, cities[j].Lon), 2);

        double cosLat = Math.Cos(51.0 * Math.PI / 180);
        var points = cities.Select(c => (c.Lon * cosLat, -c.Lat)).ToArray();
        return new DistanceMatrix(d, cities.Select(c => c.Name).ToArray(), points);
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        double toRad = Math.PI / 180;
        double dLat = (lat2 - lat1) * toRad, dLon = (lon2 - lon1) * toRad;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * toRad) * Math.Cos(lat2 * toRad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static DistanceMatrix Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select((line, rowIndex) => line
                .Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(tok =>
                {
                    if (!double.TryParse(tok.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        throw new FormatException($"Строка {rowIndex + 1}: «{tok}» не является числом.");
                    return v;
                }).ToArray())
            .ToArray();

        int n = rows.Length;
        if (n == 0) throw new FormatException("Матрица пуста.");
        var d = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (rows[i].Length != n)
                throw new FormatException($"Строка {i + 1} содержит {rows[i].Length} чисел, ожидалось {n}.");
            for (int j = 0; j < n; j++) d[i, j] = rows[i][j];
        }
        return new DistanceMatrix(d);
    }

    public string ToText()
    {
        var cells = new string[Size, Size];
        int width = 1;
        for (int i = 0; i < Size; i++)
            for (int j = 0; j < Size; j++)
            {
                cells[i, j] = this[i, j].ToString("0.##", CultureInfo.InvariantCulture);
                width = Math.Max(width, cells[i, j].Length);
            }

        var sb = new StringBuilder();
        for (int i = 0; i < Size; i++)
        {
            for (int j = 0; j < Size; j++)
            {
                if (j > 0) sb.Append(' ');
                sb.Append(cells[i, j].PadLeft(width));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public DistanceMatrix WithNamesFrom(DistanceMatrix other) =>
        other.Size == Size ? new DistanceMatrix(other.ToArray(), Names) : other;

    public double[,] ToArray()
    {
        var d = new double[Size, Size];
        for (int i = 0; i < Size; i++)
            for (int j = 0; j < Size; j++)
                d[i, j] = this[i, j];
        return d;
    }
}
