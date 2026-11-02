using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TspGenetic;

namespace TspDistributed;

public sealed record DistributedStats(int Generation, Route Best, double AverageLength, IReadOnlyList<ServiceStatus> Services);
public sealed record ServiceStatus(string Endpoint, bool Available, int CompletedBatches, string Message);

public sealed class DistributedSolver : IDisposable
{
    private sealed class Island(int id, int endpoint)
    {
        public int Id = id, Endpoint = endpoint, Generation, Round;
        public int[][]? Population;
        public double Average;
    }
    private readonly GeneticSettings _settings;
    private readonly Uri[] _endpoints;
    private readonly ServiceStatus[] _statuses;
    private readonly Island[] _islands;
    private readonly double[][] _distances;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;
    private readonly int _batch, _seed;
    private int _running;
    private Route? _best;
    public DistanceMatrix Matrix { get; }
    public Route? Best => Volatile.Read(ref _best);
    public IReadOnlyList<ServiceStatus> Services => _statuses.ToArray();
    // Минимум выполненных поколений по островам; не включает незавершённые запросы.
    public int Generation { get; private set; }

    public DistributedSolver(DistanceMatrix matrix, GeneticSettings settings, IReadOnlyList<Uri> endpoints,
        int batchGenerations = 50, TimeSpan? requestTimeout = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = ServiceEndpoints.Parse(string.Join('\n', endpoints.Select(x => x.OriginalString))).ToArray();
        if (matrix.Size > BatchProtocol.MaxCities) throw new ArgumentException("Матрица превышает лимит сервиса.");
        Matrix = matrix; _settings = settings; _batch = batchGenerations;
        _distances = BatchProtocol.ToArray(matrix);
        BatchProtocol.Validate(new(_distances, settings, batchGenerations));
        _timeout = requestTimeout ?? TimeSpan.FromMinutes(2);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _seed = settings.Seed ?? Random.Shared.Next();
        _ownsClient = httpClient is null;
        _client = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _statuses = _endpoints.Select(x => new ServiceStatus(x.ToString(), true, 0, "Готов")).ToArray();
        _islands = _endpoints.Select((_, i) => new Island(i, i)).ToArray();
    }

    public async Task<Route?> RunAsync(CancellationToken ct, int maxGenerations = 0,
        Action<DistributedStats>? onProgress = null)
    {
        if (maxGenerations < 0) throw new ArgumentOutOfRangeException(nameof(maxGenerations));
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("Расчёт уже выполняется.");
        try
        {
            while (!ct.IsCancellationRequested && (maxGenerations == 0 || Generation < maxGenerations))
            {
                var live = Enumerable.Range(0, _statuses.Length).Where(i => _statuses[i].Available).ToArray();
                if (live.Length == 0)
                    throw new InvalidOperationException(Best is null
                        ? "Все вычислительные сервисы недоступны. Результат не получен."
                        : "Все вычислительные сервисы недоступны. Лучший найденный маршрут сохранён.");
                foreach (var island in _islands.Where(x => !_statuses[x.Endpoint].Available))
                    island.Endpoint = live[island.Id % live.Length];
                int[]? migrant = Best?.Order.ToArray();
                var groups = _islands.Where(x => maxGenerations == 0 || x.Generation < maxGenerations).GroupBy(x => x.Endpoint);
                await Task.WhenAll(groups.Select(async group =>
                {
                    foreach (var island in group)
                    {
                        if (ct.IsCancellationRequested || !_statuses[group.Key].Available) break;
                        int count = maxGenerations == 0 ? _batch : Math.Min(_batch, maxGenerations - island.Generation);
                        int seed = unchecked(_seed + island.Id * 1000003 + island.Round * 7919);
                        var request = new BatchRequest(_distances, _settings with { Seed = seed }, count, island.Population, migrant);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_timeout);
                        try
                        {
                            using var response = await _client.PostAsJsonAsync(new Uri(_endpoints[group.Key], "api/batches"), request, timeout.Token);
                            response.EnsureSuccessStatusCode();
                            var data = await response.Content.ReadFromJsonAsync<BatchResponse>(cancellationToken: timeout.Token);
                            ValidateResponse(data, count, island.Population, migrant);
                            island.Population = data!.Population; island.Generation += count; island.Round++;
                            island.Average = data.History[^1].AverageLength;
                            lock (_islands)
                            {
                                var route = Route.Create(Matrix, data.BestOrder);
                                if (Best is null || route.Length < Best.Length)
                                    Volatile.Write(ref _best, route);
                            }
                            var status = _statuses[group.Key];
                            _statuses[group.Key] = status with { CompletedBatches = status.CompletedBatches + 1, Message = "Блок выполнен" };
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or InvalidDataException or OperationCanceledException)
                        {
                            _statuses[group.Key] = _statuses[group.Key] with { Available = false,
                                Message = ex is OperationCanceledException ? "Истекло время ожидания" : ex.Message };
                            break;
                        }
                    }
                }));
                Generation = _islands.Min(x => x.Generation);
                if (Best is not null) onProgress?.Invoke(new(Generation, Best,
                    _islands.Where(x => x.Population is not null).Average(x => x.Average), _statuses.ToArray()));
            }
            return Best;
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private void ValidateResponse(BatchResponse? response, int generations, int[][]? inputPopulation, int[]? migrant)
    {
        void Require([DoesNotReturnIf(false)] bool valid) { if (!valid) throw new InvalidDataException("Сервис вернул некорректный результат."); }
        bool Close(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 1e-8 * Math.Max(1, Math.Abs(b));
        Require(response is not null);
        Require(response!.Generations == generations && response.Population is not null &&
            response.Population.Length == _settings.PopulationSize && response.BestOrder is not null &&
            Route.IsPermutation(response.BestOrder, Matrix.Size));
        Require(response.Population.All(p => p is not null && Route.IsPermutation(p, Matrix.Size)));
        Require(Close(response.BestLength, Matrix.RouteLength(response.BestOrder)));
        Require(response.History is not null && response.History.Length == generations);
        double min = double.PositiveInfinity;
        double previous = double.PositiveInfinity;
        for (int i = 0; i < generations; i++)
        {
            var sample = response.History[i];
            Require(sample is not null && sample.Generation == i + 1 && double.IsFinite(sample.BestLength) &&
                double.IsFinite(sample.AverageLength) && sample.BestLength > 0 && sample.AverageLength >= sample.BestLength - 1e-8);
            Require(sample.BestLength <= previous + 1e-8);
            previous = sample.BestLength;
            min = Math.Min(min, sample.BestLength);
        }
        var lengths = response.Population.Select(p => Matrix.RouteLength(p)).ToArray();
        Require(Close(response.History[^1].AverageLength, lengths.Average()));
        Require(response.History[^1].BestLength <= lengths.Min() + 1e-8);
        Require(Close(response.History[^1].BestLength, response.BestLength));
        if (inputPopulation is not null) Require(response.BestLength <= inputPopulation.Min(p => Matrix.RouteLength(p)) + 1e-8);
        if (migrant is not null) Require(response.BestLength <= Matrix.RouteLength(migrant) + 1e-8);
        Require(response.BestLength <= min + 1e-8 && response.BestLength <= lengths.Min() + 1e-8);
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
