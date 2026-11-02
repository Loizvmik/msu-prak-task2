namespace TspGenetic;

public sealed class Route
{
    private readonly int[] _order;

    public IReadOnlyList<int> Order => Array.AsReadOnly(_order);

    public double Length { get; }

    internal int[] Genes => _order;

    internal Route(int[] order, double length)
    {
        _order = order;
        Length = length;
    }

    public static Route Create(DistanceMatrix matrix, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(order);
        if (!IsPermutation(order, matrix.Size))
            throw new ArgumentException("Маршрут должен быть перестановкой номеров всех городов.", nameof(order));
        var copy = order.ToArray();
        return new Route(copy, matrix.RouteLength(copy));
    }

    public static bool IsPermutation(IReadOnlyList<int> order, int n)
    {
        if (order.Count != n) return false;
        var seen = new bool[n];
        foreach (int c in order)
        {
            if (c < 0 || c >= n || seen[c]) return false;
            seen[c] = true;
        }
        return true;
    }

    public IReadOnlyList<int> StartingFrom(int start)
    {
        int idx = Array.IndexOf(_order, start);
        if (idx < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var result = new int[_order.Length];
        for (int i = 0; i < _order.Length; i++)
            result[i] = _order[(idx + i) % _order.Length];
        return result;
    }

    public IReadOnlyList<(int From, int To, double Distance)> Legs(DistanceMatrix matrix, int start = 0)
    {
        var order = StartingFrom(start);
        var legs = new (int, int, double)[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            int from = order[i], to = order[(i + 1) % order.Count];
            legs[i] = (from, to, matrix[from, to]);
        }
        return legs;
    }
}
