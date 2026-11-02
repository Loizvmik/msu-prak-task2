using TspGenetic;

namespace TspDistributed;

public sealed record BatchRequest(
    double[][] Distances, GeneticSettings Settings, int Generations,
    int[][]? Population = null, int[]? Migrant = null);

public sealed record BatchSample(int Generation, double BestLength, double AverageLength);

public sealed record BatchResponse(
    int Generations, int[][] Population, int[] BestOrder, double BestLength,
    BatchSample[] History);

public static class BatchProtocol
{
    public const int MaxCities = 512;
    public const int MaxPopulation = 10_000;
    public const int MaxGenerations = 1_000;
    public const long MaxGenes = 2_000_000;
    public const long MaxWork = 100_000_000;

    public static double[][] ToArray(DistanceMatrix matrix) =>
        Enumerable.Range(0, matrix.Size)
            .Select(i => Enumerable.Range(0, matrix.Size).Select(j => matrix[i, j]).ToArray()).ToArray();

    public static DistanceMatrix Validate(BatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Distances is null || request.Distances.Length is < 3 or > MaxCities)
            throw new ArgumentException($"Число городов должно быть от 3 до {MaxCities}.");
        ArgumentNullException.ThrowIfNull(request.Settings);
        request.Settings.Validate();
        int n = request.Distances.Length;
        if (request.Settings.PopulationSize > MaxPopulation ||
            (long)n * request.Settings.PopulationSize > MaxGenes)
            throw new ArgumentException("Популяция превышает лимит сервиса.");
        if (request.Settings.MaxThreads > 256)
            throw new ArgumentException("Допускается не больше 256 потоков.");
        if (request.Generations is < 1 or > MaxGenerations ||
            ((long)n + 2L * request.Settings.TournamentSize) *
            request.Settings.PopulationSize * request.Generations > MaxWork)
            throw new ArgumentException("Размер блока вычислений превышает лимит сервиса. Уменьшите число поколений в блоке.");
        var data = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (request.Distances[i] is null || request.Distances[i].Length != n)
                throw new ArgumentException("Матрица расстояний должна быть квадратной.");
            for (int j = 0; j < n; j++) data[i, j] = request.Distances[i][j];
        }
        var matrix = new DistanceMatrix(data);
        if (request.Population is not null &&
            (request.Population.Length != request.Settings.PopulationSize ||
             request.Population.Any(p => p is null || !Route.IsPermutation(p, n))))
            throw new ArgumentException("Популяция должна содержать заданное число перестановок городов.");
        if (request.Migrant is not null && !Route.IsPermutation(request.Migrant, n))
            throw new ArgumentException("Мигрант должен быть перестановкой городов.");
        return matrix;
    }
}
