using TspGenetic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace TspDistributed.App;

public partial class MainWindow : Window
{
    private const int MaxEditableMatrixSize = 150;
    private const int HistoryCapacity = 5000;

    private const long MaxGenes = BatchProtocol.MaxGenes;
    private const long RefreshIntervalMs = 100;

    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#1E7B3C"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#B35C00"));

    private readonly object _historyLock = new();
    private readonly Queue<ChartView.Sample> _history = new();
    private readonly Stopwatch _stopwatch = new();

    private DistanceMatrix? _matrix;
    private string? _appliedMatrixText;
    private DistributedSolver? _solver;
    private CancellationTokenSource? _cts;
    private Route? _shownRoute;
    private int _startGeneration;
    private long _lastRefreshPostMs;
    private int _refreshPending;

    public MainWindow()
    {
        InitializeComponent();

        SetMatrix(DistanceMatrix.RandomEuclidean(6, seed: 1));
        Closing += (_, _) => _cts?.Cancel();
        ChartWindowBox.ValueChanged += (_, _) => Refresh();
    }

    private void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            int n = (int)(CityCountBox.Value ?? 30);
            SetMatrix(DistanceMatrix.RandomEuclidean(n, ParseSeed(CitySeedBox.Text, "Сид городов")));
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void OnGermanyClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        SetMatrix(DistanceMatrix.Germany14());
    }

    private void OnApplyMatrixClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            ApplyMatrixText();
        }
        catch (Exception ex)
        {
            MatrixStatus.Foreground = Brushes.Firebrick;
            MatrixStatus.Text = ex.Message;
        }
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        DistributedSolver solver;
        int maxGenerations;
        try
        {
            if (MatrixBox.IsEnabled && MatrixBox.Text != _appliedMatrixText)
                ApplyMatrixText();
            if (_matrix == null)
                throw new InvalidOperationException("Сначала задайте матрицу расстояний.");

            var settings = new GeneticSettings
            {
                PopulationSize = (int)(PopulationBox.Value ?? 0),
                EliteCount = (int)(EliteBox.Value ?? 0),
                TournamentSize = (int)(TournamentBox.Value ?? 0),
                CrossoverProbability = (double)(CrossoverBox.Value ?? 0),
                MutationProbability = (double)(MutationBox.Value ?? 0),
                InversionShare = (double)(InversionBox.Value ?? 0),
                MaxThreads = (int)(ThreadsBox.Value ?? 0),
                Seed = ParseSeed(GaSeedBox.Text, "Сид ГА"),
            };
            settings.Validate();
            maxGenerations = (int)(MaxGenerationsBox.Value ?? 0);
            long genes = (long)settings.PopulationSize * _matrix.Size;
            if (genes > MaxGenes)
                throw new ArgumentException($"Популяция × число городов = {genes:N0}, допускается не больше {MaxGenes:N0}.");
            var endpoints = ServiceEndpoints.Parse(ServicesBox.Text ?? "");
            solver = new DistributedSolver(_matrix, settings, endpoints,
                (int)(BatchGenerationsBox.Value ?? 50),
                TimeSpan.FromSeconds((double)(TimeoutBox.Value ?? 10)));
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            StateText.Text = "не запущен";
            return;
        }

        _solver?.Dispose();
        _solver = solver;
        _shownRoute = null;
        lock (_historyLock) _history.Clear();
        RouteCanvas.Show(_matrix, null);
        Chart.Show([]);
        RouteText.Text = "Ожидаем первый ответ сервисов.";
        GenerationText.Text = BestText.Text = AverageText.Text = SpeedText.Text = "—";
        ServiceStatusText.Text = "Отправляем задачи сервисам…";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true);
        StateText.Text = "идёт поиск";
        _startGeneration = 0;
        _lastRefreshPostMs = 0;
        _stopwatch.Restart();
        try
        {
            await Task.Run(() => solver.RunAsync(cts.Token, maxGenerations, OnProgress));
            StateText.Text = cts.IsCancellationRequested ? "остановлен" : "завершён";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StateText.Text = "остановлен";
        }
        catch (Exception ex)
        {
            StateText.Text = "ошибка";
            ErrorText.Text = ex.Message;
            if (solver.Best is null)
                RouteText.Text = "Результат не получен. Проверьте адреса и запустите вычислительные сервисы.";
        }
        finally
        {
            _stopwatch.Stop();
            _cts = null;
            Refresh();
            SetRunning(false);
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        StateText.Text = "останавливается…";
    }

    private void OnProgress(DistributedStats stats)
    {
        lock (_historyLock)
        {
            _history.Enqueue(new ChartView.Sample(stats.Generation, stats.Best.Length, stats.AverageLength));
            while (_history.Count > HistoryCapacity)
                _history.Dequeue();
        }

        long now = _stopwatch.ElapsedMilliseconds;
        if (now - _lastRefreshPostMs < RefreshIntervalMs || Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;
        _lastRefreshPostMs = now;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        var solver = _solver;
        if (solver == null) return;
        ServiceStatusText.Text = string.Join("\n", solver.Services.Select(s =>
            $"{s.Endpoint}: {(s.Available ? "доступен" : "отключён")}, блоков {s.CompletedBatches}. {s.Message}"));

        int window = (int)(ChartWindowBox.Value ?? 200);
        ChartView.Sample[] samples;
        lock (_historyLock)
        {
            samples = _history.Skip(Math.Max(0, _history.Count - window)).ToArray();
        }
        if (samples.Length == 0) return;

        var last = samples[^1];
        var best = solver.Best;
        if (best is null) return;
        GenerationText.Text = last.Generation.ToString("N0", CultureInfo.CurrentCulture);
        BestText.Text = best.Length.ToString("N2", CultureInfo.CurrentCulture);
        AverageText.Text = last.Average.ToString("N2", CultureInfo.CurrentCulture);
        double seconds = _stopwatch.Elapsed.TotalSeconds;
        SpeedText.Text = seconds > 0
            ? $"{(last.Generation - _startGeneration) / seconds:N0} пок./с"
            : "—";
        Chart.Show(samples);

        if (!ReferenceEquals(best, _shownRoute))
        {
            _shownRoute = best;
            RouteCanvas.Show(solver.Matrix, best);
            RouteText.Text = DescribeRoute(solver.Matrix, best);
        }
    }

    private static string DescribeRoute(DistanceMatrix matrix, Route route)
    {
        var legs = route.Legs(matrix);
        var names = matrix.Names;
        int width = Math.Max(6, names.Max(s => s.Length)) + 2;
        var sb = new StringBuilder();

        sb.Append("Порядок обхода: ");
        sb.AppendJoin(" → ", legs.Select(l => names[l.From]));
        sb.Append(" → ").AppendLine(names[legs[0].From]);
        sb.AppendLine();
        sb.Append("  №  ").Append("Откуда".PadRight(width)).Append("Куда".PadRight(width))
          .AppendLine("Расстояние    Накоплено");

        double total = 0;
        for (int i = 0; i < legs.Count; i++)
        {
            var (from, to, distance) = legs[i];
            total += distance;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("  ")
              .Append(names[from].PadRight(width))
              .Append(names[to].PadRight(width))
              .Append(distance.ToString("F2", CultureInfo.InvariantCulture).PadLeft(10))
              .Append(total.ToString("F2", CultureInfo.InvariantCulture).PadLeft(13))
              .AppendLine();
        }
        sb.AppendLine();
        sb.Append("Суммарное расстояние: ").AppendLine(route.Length.ToString("F2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private void SetMatrix(DistanceMatrix matrix)
    {
        _matrix = matrix;
        if (matrix.Size <= MaxEditableMatrixSize)
        {
            _appliedMatrixText = matrix.ToText();
            MatrixBox.Text = _appliedMatrixText;
            MatrixBox.IsEnabled = true;
            ApplyMatrixButton.IsEnabled = true;
        }
        else
        {
            _appliedMatrixText = null;
            MatrixBox.Text = $"Матрица {matrix.Size}×{matrix.Size} слишком велика для редактирования.";
            MatrixBox.IsEnabled = false;
            ApplyMatrixButton.IsEnabled = false;
        }
        UpdateMatrixStatus(matrix, generated: matrix.Points != null);
        ShowIdle(matrix);
    }

    private void ApplyMatrixText()
    {
        var parsed = DistanceMatrix.Parse(MatrixBox.Text ?? "");

        if (_matrix != null && MatrixBox.Text == _appliedMatrixText)
            return;
        _matrix = _matrix?.WithNamesFrom(parsed) ?? parsed;
        _appliedMatrixText = MatrixBox.Text;
        UpdateMatrixStatus(_matrix, generated: false);
        ShowIdle(_matrix);
    }

    private void UpdateMatrixStatus(DistanceMatrix matrix, bool generated)
    {
        string size = $"Матрица {matrix.Size}×{matrix.Size}.";
        if (generated)
        {
            MatrixStatus.Foreground = OkBrush;
            MatrixStatus.Text = $"{size} Расстояния рассчитаны по координатам городов.";
        }
        else if (matrix.SatisfiesTriangleInequality(0.02))
        {
            MatrixStatus.Foreground = OkBrush;
            MatrixStatus.Text = $"{size} Неравенство треугольника выполняется.";
        }
        else
        {
            MatrixStatus.Foreground = WarnBrush;
            MatrixStatus.Text = $"{size} Внимание: неравенство треугольника нарушено. Алгоритм всё равно отработает.";
        }
    }

    private void ShowIdle(DistanceMatrix matrix)
    {
        _solver?.Dispose();
        _solver = null;
        _shownRoute = null;
        lock (_historyLock) _history.Clear();
        RouteCanvas.Show(matrix, null);
        Chart.Show([]);
        StateText.Text = "не запущен";
        ServiceStatusText.Text = "Сервисы ещё не запущены.";
        GenerationText.Text = BestText.Text = AverageText.Text = SpeedText.Text = "—";
        RouteText.Text = "Маршрут появится после запуска поиска.";
    }

    private void SetRunning(bool running)
    {
        foreach (var c in new Control[]
                 {
                     CityCountBox, CitySeedBox, GenerateButton, GermanyButton, PopulationBox, EliteBox,
                     TournamentBox, CrossoverBox, MutationBox, InversionBox, ThreadsBox, MaxGenerationsBox,
                     GaSeedBox, StartButton, ServicesBox, BatchGenerationsBox, TimeoutBox,
                 })
            c.IsEnabled = !running;

        bool editable = _matrix is { Size: <= MaxEditableMatrixSize };
        MatrixBox.IsEnabled = !running && editable;
        ApplyMatrixButton.IsEnabled = !running && editable;
        StopButton.IsEnabled = running;
    }

    private static int? ParseSeed(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) return seed;
        throw new FormatException($"{what}: «{text}» не является целым числом.");
    }
}
