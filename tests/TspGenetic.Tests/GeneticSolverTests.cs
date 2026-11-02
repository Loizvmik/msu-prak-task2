using TspGenetic;

namespace TspGenetic.Tests;

public class GeneticSolverTests
{
    [Fact]
    public void Solver_FindsKnownOptimum_ForGermany14()
    {
        var matrix = DistanceMatrix.Germany14();
        double optimum = HeldKarp(matrix);

        var solver = new GeneticSolver(matrix, new GeneticSettings { PopulationSize = 500, Seed = 1 });
        var best = solver.Run(CancellationToken.None, maxGenerations: 1000);

        Assert.Equal(optimum, best.Length, 6);
    }

    [Fact]
    public void Solver_GivesSameResultOnOneThreadAndOnMany()
    {
        var matrix = DistanceMatrix.RandomEuclidean(60, seed: 4);

        Route RunWith(int threads) =>
            new GeneticSolver(matrix, new GeneticSettings
            {
                PopulationSize = 500, Seed = 42, MaxThreads = threads, InversionShare = 0.5,
            }).Run(CancellationToken.None, maxGenerations: 300);

        var single = RunWith(1);
        var many = RunWith(8);

        Assert.Equal(single.Order, many.Order);
        Assert.Equal(single.Length, many.Length);
    }

    [Fact]
    public void Solver_ReducesRouteLength_OnUnknownProblem()
    {
        var matrix = DistanceMatrix.RandomEuclidean(100, seed: 17);
        var solver = new GeneticSolver(matrix, new GeneticSettings
        {
            PopulationSize = 500, Seed = 7, InversionShare = 0.5,
        });

        double start = solver.Best.Length;
        double previous = start;
        solver.Run(CancellationToken.None, maxGenerations: 2000, stats =>
        {
            Assert.True(stats.BestLength <= previous,
                $"поколение {stats.Generation}: длина выросла с {previous} до {stats.BestLength}");
            previous = stats.BestLength;
        });

        Assert.True(solver.Best.Length < start / 2,
            $"длина маршрута сократилась недостаточно: было {start}, стало {solver.Best.Length}");
    }

    private static double HeldKarp(DistanceMatrix m)
    {
        int n = m.Size;
        var dp = new double[1 << n, n];
        for (int s = 0; s < 1 << n; s++)
            for (int v = 0; v < n; v++)
                dp[s, v] = double.MaxValue;
        dp[1, 0] = 0;
        for (int s = 1; s < 1 << n; s += 2)
            for (int v = 0; v < n; v++)
            {
                if (dp[s, v] == double.MaxValue) continue;
                for (int u = 1; u < n; u++)
                {
                    if ((s & (1 << u)) != 0) continue;
                    int t = s | (1 << u);
                    dp[t, u] = Math.Min(dp[t, u], dp[s, v] + m[v, u]);
                }
            }
        double best = double.MaxValue;
        for (int v = 1; v < n; v++)
            best = Math.Min(best, dp[(1 << n) - 1, v] + m[v, 0]);
        return best;
    }
}
