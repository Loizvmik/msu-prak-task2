using Microsoft.AspNetCore.Mvc.Testing;
using TspDistributed;
using TspGenetic;
using Xunit;

namespace TspWorker.Tests;

public sealed class DistributedScenarios
{
    private sealed class RoutingHandler(HttpClient first, HttpClient second)
        : HttpMessageHandler
    {
        private int _firstRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            bool isFirst = request.RequestUri!.Host == "worker1";
            if (isFirst && Interlocked.Increment(ref _firstRequests) > 1)
                throw new HttpRequestException("Первый сервис отключён после первого блока.");

            using var forwarded = new HttpRequestMessage(request.Method, "/api/batches")
            {
                Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync(cancellationToken))
            };
            forwarded.Content.Headers.ContentType = request.Content.Headers.ContentType;
            return await (isFirst ? first : second).SendAsync(forwarded, cancellationToken);
        }
    }

    [Fact]
    public async Task CoordinatorFinishesWhenOneOfTwoServicesFails()
    {
        await using var firstApp = new WebApplicationFactory<Program>();
        await using var secondApp = new WebApplicationFactory<Program>();
        using var first = firstApp.CreateClient();
        using var second = secondApp.CreateClient();
        using var client = new HttpClient(new RoutingHandler(first, second));
        var matrix = DistanceMatrix.Germany14();
        using var solver = new DistributedSolver(matrix,
            new GeneticSettings { PopulationSize = 64, EliteCount = 2, Seed = 7,
                InversionShare = 0.5, MaxThreads = 2 },
            [new Uri("http://worker1:5101"), new Uri("http://worker2:5102")],
            batchGenerations: 10, httpClient: client);
        var progress = new List<DistributedStats>();

        var best = await solver.RunAsync(CancellationToken.None, 35, progress.Add);

        Assert.NotNull(best);
        Assert.True(Route.IsPermutation(best.Order, matrix.Size));
        Assert.Equal(matrix.RouteLength(best.Order), best.Length, 8);
        Assert.Equal(35, solver.Generation);
        Assert.All(progress.Zip(progress.Skip(1)), pair =>
            Assert.True(pair.Second.Best.Length <= pair.First.Best.Length));
        Assert.False(solver.Services[0].Available);
        Assert.Equal(1, solver.Services[0].CompletedBatches);
        Assert.True(solver.Services[1].Available);
        Assert.Equal(7, solver.Services[1].CompletedBatches);
        Assert.Equal(8, solver.Services.Sum(s => s.CompletedBatches));
    }
}
