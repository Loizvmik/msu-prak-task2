namespace TspGenetic;

public sealed record GenerationStats(
    int Generation, Route Best, double GenerationBestLength, double AverageLength, double WorstLength)
{
    public double BestLength => Best.Length;
}

public sealed class GeneticSolver
{
    private const int MaxChunks = 64;
    private const int MinChunkSize = 16;

    private readonly DistanceMatrix _matrix;
    private readonly GeneticSettings _settings;
    private readonly ulong _baseSeed;
    private readonly ParallelOptions _parallelOptions;

    private int[][] _genes;
    private int[][] _nextGenes;
    private double[] _lengths;
    private double[] _nextLengths;

    private int[] _ranked;
    private int[] _nextRanked;
    private double[] _sortKeys;

    private volatile Route _best;
    private volatile int _generation;

    public GeneticSolver(DistanceMatrix matrix, GeneticSettings settings,
        IReadOnlyList<int[]>? initialPopulation = null, IReadOnlyList<int>? migrant = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _matrix = matrix;
        _settings = settings;
        _baseSeed = (ulong)(settings.Seed ?? Random.Shared.Next());
        _parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.MaxThreads > 0 ? settings.MaxThreads : Environment.ProcessorCount,
            CancellationToken = cancellationToken,
        };

        int size = settings.PopulationSize, n = matrix.Size;
        _genes = NewBuffer(size, n);
        _nextGenes = NewBuffer(size, n);
        _lengths = new double[size];
        _nextLengths = new double[size];
        _ranked = new int[size];
        _nextRanked = new int[size];
        _sortKeys = new double[size];

        if (initialPopulation is null)
            CreateInitialPopulation();
        else
        {
            if (initialPopulation.Count != size)
                throw new ArgumentException("Размер входной популяции не совпадает с параметрами.");
            for (int i = 0; i < size; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (initialPopulation[i] is null || !Route.IsPermutation(initialPopulation[i], n))
                    throw new ArgumentException("Популяция содержит недопустимый маршрут.");
                Array.Copy(initialPopulation[i], _genes[i], n);
                _lengths[i] = _matrix.RouteLength(_genes[i]);
            }
            RankPopulation();
        }

        if (migrant is not null)
        {
            if (!Route.IsPermutation(migrant, n))
                throw new ArgumentException("Мигрант должен быть перестановкой городов.");
            var route = migrant.ToArray();
            double length = _matrix.RouteLength(route);
            int worst = _ranked[^1];
            if (length < _lengths[worst])
            {
                Array.Copy(route, _genes[worst], n);
                _lengths[worst] = length;
                RankPopulation();
            }
        }
        _best = Snapshot(_ranked[0]);
    }

    public DistanceMatrix Matrix => _matrix;

    public GeneticSettings Settings => _settings;

    public int Generation => _generation;

    public Route Best => _best;

    public IReadOnlyList<Route> Population => _ranked.Select(Snapshot).ToArray();

    public GenerationStats CurrentStats => MakeStats();

    public GenerationStats NextGeneration()
    {
        int size = _settings.PopulationSize;
        int elite = _settings.EliteCount;
        int generation = _generation + 1;
        int n = _matrix.Size;
        var oldGenes = _genes;
        var oldLengths = _lengths;
        var oldRanked = _ranked;
        var newGenes = _nextGenes;
        var newLengths = _nextLengths;

        for (int k = 0; k < elite; k++)
        {
            Array.Copy(oldGenes[oldRanked[k]], newGenes[k], n);
            newLengths[k] = oldLengths[oldRanked[k]];
        }

        int childCount = size - elite;
        int chunkSize = ChunkSizeFor(childCount);
        int chunks = (childCount + chunkSize - 1) / chunkSize;
        Parallel.For(0, chunks, _parallelOptions, chunk =>
        {
            var rnd = new FastRandom(ChunkSeed(generation, chunk));
            var used = new bool[n];
            int from = elite + chunk * chunkSize;
            int to = Math.Min(size, from + chunkSize);
            for (int k = from; k < to; k++)
            {
                var child = newGenes[k];
                var p1 = oldGenes[oldRanked[Tournament(rnd)]];
                if (rnd.NextDouble() < _settings.CrossoverProbability)
                {
                    var p2 = oldGenes[oldRanked[Tournament(rnd)]];
                    OrderCrossover(p1, p2, child, rnd, used);
                }
                else
                {
                    Array.Copy(p1, child, n);
                }

                if (rnd.NextDouble() < _settings.MutationProbability)
                {
                    if (rnd.NextDouble() < _settings.InversionShare)
                        InversionMutation(child, rnd);
                    else
                        SwapMutation(child, rnd);
                }

                newLengths[k] = _matrix.RouteLength(child);
            }
        });

        var ranked = _nextRanked;
        for (int i = 0; i < size; i++) ranked[i] = i;
        Array.Copy(newLengths, _sortKeys, size);
        Array.Sort(_sortKeys, ranked);

        (_genes, _nextGenes) = (newGenes, oldGenes);
        (_lengths, _nextLengths) = (newLengths, oldLengths);
        (_ranked, _nextRanked) = (ranked, oldRanked);

        if (_lengths[_ranked[0]] < _best.Length)
            _best = Snapshot(_ranked[0]);
        _generation = generation;
        return MakeStats();
    }

    public Route Run(CancellationToken cancellationToken, int maxGenerations = 0,
        Action<GenerationStats>? onGeneration = null)
    {
        while (!cancellationToken.IsCancellationRequested &&
               (maxGenerations <= 0 || _generation < maxGenerations))
        {
            var stats = NextGeneration();
            onGeneration?.Invoke(stats);
        }
        return _best;
    }

    public Task<Route> RunAsync(CancellationToken cancellationToken, int maxGenerations = 0,
        Action<GenerationStats>? onGeneration = null) =>
        Task.Run(() => Run(cancellationToken, maxGenerations, onGeneration), CancellationToken.None);

    private void CreateInitialPopulation()
    {
        int size = _settings.PopulationSize;
        int chunkSize = ChunkSizeFor(size);
        int chunks = (size + chunkSize - 1) / chunkSize;
        Parallel.For(0, chunks, _parallelOptions, chunk =>
        {
            var rnd = new FastRandom(ChunkSeed(0, chunk));
            int n = _matrix.Size;
            int to = Math.Min(size, (chunk + 1) * chunkSize);
            for (int k = chunk * chunkSize; k < to; k++)
            {
                var genes = _genes[k];
                for (int i = 0; i < n; i++) genes[i] = i;
                rnd.Shuffle(genes);
                _lengths[k] = _matrix.RouteLength(genes);
            }
        });
        RankPopulation();
    }

    private void RankPopulation()
    {
        for (int i = 0; i < _ranked.Length; i++) _ranked[i] = i;
        Array.Copy(_lengths, _sortKeys, _lengths.Length);
        Array.Sort(_sortKeys, _ranked);
    }

    private int Tournament(FastRandom rnd)
    {
        int size = _settings.PopulationSize;
        int best = rnd.Next(size);
        for (int i = 1; i < _settings.TournamentSize; i++)
            best = Math.Min(best, rnd.Next(size));
        return best;
    }

    internal static void OrderCrossover(int[] p1, int[] p2, int[] child, FastRandom rnd, bool[] used)
    {
        int n = p1.Length;
        int a = rnd.Next(n), b = rnd.Next(n);
        if (a > b) (a, b) = (b, a);

        Array.Clear(used);
        for (int i = a; i <= b; i++)
        {
            child[i] = p1[i];
            used[p1[i]] = true;
        }

        int pos = (b + 1) % n;
        for (int i = 0; i < n; i++)
        {
            int city = p2[(b + 1 + i) % n];
            if (used[city]) continue;
            child[pos] = city;
            pos = (pos + 1) % n;
        }
    }

    internal static void SwapMutation(int[] genes, FastRandom rnd)
    {
        var (i, j) = TwoDistinct(genes.Length, rnd);
        (genes[i], genes[j]) = (genes[j], genes[i]);
    }

    internal static void InversionMutation(int[] genes, FastRandom rnd)
    {
        var (i, j) = TwoDistinct(genes.Length, rnd);
        Array.Reverse(genes, i, j - i + 1);
    }

    private static (int Low, int High) TwoDistinct(int n, FastRandom rnd)
    {
        int i = rnd.Next(n);
        int j = rnd.Next(n - 1);
        if (j >= i) j++;
        return i < j ? (i, j) : (j, i);
    }

    private static int ChunkSizeFor(int count) => Math.Max(MinChunkSize, (count + MaxChunks - 1) / MaxChunks);

    private ulong ChunkSeed(int generation, int chunk)
    {
        ulong x = _baseSeed;
        ulong a = FastRandom.SplitMix(ref x);
        x = a ^ (ulong)generation;
        ulong b = FastRandom.SplitMix(ref x);
        x = b ^ (ulong)chunk;
        return FastRandom.SplitMix(ref x);
    }

    private static int[][] NewBuffer(int size, int n)
    {
        var buffer = new int[size][];
        for (int i = 0; i < size; i++) buffer[i] = new int[n];
        return buffer;
    }

    private Route Snapshot(int index) => new((int[])_genes[index].Clone(), _lengths[index]);

    private GenerationStats MakeStats()
    {
        double sum = 0;
        foreach (double len in _lengths) sum += len;
        return new GenerationStats(_generation, _best, _lengths[_ranked[0]], sum / _lengths.Length,
            _lengths[_ranked[^1]]);
    }
}
