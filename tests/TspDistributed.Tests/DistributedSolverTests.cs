using System.Net;
using System.Net.Http.Json;
using TspDistributed;
using TspGenetic;
using Xunit;

public class DistributedSolverTests
{
    private static readonly DistanceMatrix Matrix = new(new double[,] { { 0, 1, 2 }, { 1, 0, 3 }, { 2, 3, 0 } });
    private static readonly GeneticSettings Settings = new() { PopulationSize = 2, EliteCount = 1, TournamentSize = 2, Seed = 42 };
    private static readonly Uri[] Endpoints = [new("http://a:8000"), new("http://b:8001")];
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private static HttpResponseMessage Good(BatchRequest request) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new BatchResponse(request.Generations, [[0, 1, 2], [2, 1, 0]], [0, 1, 2], 6,
            Enumerable.Range(1, request.Generations).Select(i => new BatchSample(i, 6, 6)).ToArray()))
    };
    private static DistributedSolver Solver(HttpClient client, TimeSpan? timeout = null) => new(Matrix, Settings, Endpoints, 2, timeout, client);

    [Fact]
    public async Task ParallelEndpointsCarryPopulationMigrantAndPartialFinalBlock()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<BatchRequest>();
        int active = 0, maximum = 0;
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, now);
            var request = (await message.Content!.ReadFromJsonAsync<BatchRequest>(cancellationToken: ct))!;
            calls.Add(request);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref active);
            return Good(request);
        }));
        using var solver = Solver(client);
        var progress = new List<int>();
        Assert.Equal(6, (await solver.RunAsync(default, 5, s => progress.Add(s.Generation)))!.Length);
        Assert.Equal(2, maximum);
        Assert.Equal(new[] { 2, 4, 5 }, progress);
        Assert.Equal(2, calls.Count(x => x.Generations == 1));
        Assert.Equal(4, calls.Count(x => x.Population is not null && x.Migrant is not null));
        Assert.Equal(6, calls.Select(x => x.Settings.Seed).Distinct().Count());
    }

    [Fact]
    public async Task FailureTransfersIslandAndSerializesRequestsToSurvivor()
    {
        int active = 0, maximum = 0, failedCalls = 0;
        var seeds = new List<int?>();
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<BatchRequest>(cancellationToken: ct))!;
            if (message.RequestUri!.Host == "a") { Interlocked.Increment(ref failedCalls); return new(HttpStatusCode.ServiceUnavailable); }
            int now = Interlocked.Increment(ref active); InterlockedExtensions.Max(ref maximum, now);
            seeds.Add(request.Settings.Seed);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref active);
            return Good(request);
        }));
        using var solver = Solver(client);
        ServiceStatus[]? statuses = null;
        await solver.RunAsync(default, 4, x => statuses = x.Services.ToArray());
        Assert.Equal(4, solver.Generation);
        Assert.Equal(1, failedCalls);
        Assert.Equal(1, maximum);
        Assert.Contains(42, seeds);
        Assert.False(statuses![0].Available);
        Assert.Equal(4, statuses[1].CompletedBatches);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("permutation")]
    [InlineData("timeout")]
    [InlineData("connection")]
    public async Task InvalidOrUnreachableServicesFailClearly(string failure)
    {
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            if (failure == "timeout") await Task.Delay(Timeout.Infinite, ct);
            if (failure == "connection") throw new HttpRequestException("Нет соединения");
            if (failure == "json") return new(HttpStatusCode.OK) { Content = new StringContent("{") };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new BatchResponse(2, [[0, 0, 1], [0, 1, 2]], [0, 1, 2], 6, [new(1, 6, 6), new(2, 6, 6)])) };
        }));
        using var solver = Solver(client, TimeSpan.FromMilliseconds(30));
        Assert.Contains("Все", (await Assert.ThrowsAsync<InvalidOperationException>(() => solver.RunAsync(default, 2))).Message);
        Assert.Equal(0, solver.Generation);
        Assert.Null(solver.Best);
    }

    [Fact]
    public async Task CancellationPreservesSuccessfulIslandAndDoesNotDisableService()
    {
        using var cts = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<BatchRequest>(cancellationToken: ct))!;
            if (message.RequestUri!.Host == "b") { await Task.Delay(50, ct); cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return Good(request);
        }));
        using var solver = Solver(client);
        DistributedStats? progress = null;
        var best = await solver.RunAsync(cts.Token, 4, x => progress = x);
        Assert.NotNull(best);
        Assert.Equal(0, solver.Generation);
        Assert.All(progress!.Services, s => Assert.True(s.Available));
        Assert.Equal(1, progress.Services[0].CompletedBatches);
    }

    [Fact]
    public async Task ReassignmentRetainsPopulationAndDeterministicSeed()
    {
        var transferred = new List<BatchRequest>();
        int aCalls = 0;
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<BatchRequest>(cancellationToken: ct))!;
            if (message.RequestUri!.Host == "a" && ++aCalls == 2) return new(HttpStatusCode.ServiceUnavailable);
            if (message.RequestUri.Host == "b") transferred.Add(request);
            return Good(request);
        }));
        using var solver = Solver(client);
        await solver.RunAsync(default, 6);
        var replay = Assert.Single(transferred, x => x.Settings.Seed == 42 + 7919);
        Assert.NotNull(replay.Population);
        Assert.NotNull(replay.Migrant);
        Assert.Equal(6, solver.Generation);
        Assert.Equal(6, solver.Best!.Length);
    }

    [Fact]
    public async Task AllServicesFailingAfterFirstRoundPreserveBestRoute()
    {
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<BatchRequest>(cancellationToken: ct))!;
            return request.Population is null ? Good(request) : new(HttpStatusCode.InternalServerError);
        }));
        using var solver = Solver(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => solver.RunAsync(default, 6));
        Assert.Contains("сохранён", error.Message);
        Assert.Equal(6, solver.Best!.Length);
        Assert.Equal(2, solver.Generation);
        Assert.All(solver.Services, status => Assert.False(status.Available));
    }

    [Theory]
    [InlineData("population")]
    [InlineData("history")]
    [InlineData("order")]
    [InlineData("sample")]
    public async Task NullResponseMembersDisableEndpoint(string member)
    {
        using var client = new HttpClient(new Handler((message, ct) =>
        {
            var result = new BatchResponse(2, [[0, 1, 2], [0, 1, 2]], [0, 1, 2], 6, [new(1, 6, 6), new(2, 6, 6)]);
            result = member switch
            {
                "population" => result with { Population = null! },
                "history" => result with { History = null! },
                "order" => result with { BestOrder = null! },
                _ => result with { History = [null!, new(2, 6, 6)] }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }));
        using var solver = Solver(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => solver.RunAsync(default, 2));
        Assert.All(solver.Services, x => Assert.False(x.Available));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host:0")]
    [InlineData("http://host:80/path")]
    [InlineData("host:80\nhost:80")]
    [InlineData("http://user@host:80")]
    [InlineData("http://host:80?x=1")]
    public void RejectInvalidEndpoints(string text) => Assert.Throws<ArgumentException>(() => ServiceEndpoints.Parse(text));

    [Fact]
    public void AcceptExplicitDefaultPortAndIpv6() => Assert.Equal(2, ServiceEndpoints.Parse("http://host:80\n[::1]:1234").Count);

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int observed;
            do { observed = Volatile.Read(ref target); if (observed >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, observed) != observed);
        }
    }
}
