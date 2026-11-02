using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using TspDistributed;
using TspGenetic;
using TspWorker;
using Xunit;

namespace TspWorker.Tests;

public sealed class BatchScenarios(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static BatchRequest Request(int seed = 17) => new(
        [[0, 1, 8, 1], [1, 0, 1, 8], [8, 1, 0, 1], [1, 8, 1, 0]],
        new GeneticSettings { PopulationSize = 32, EliteCount = 2, TournamentSize = 3,
            Seed = seed, MaxThreads = 2 }, 8);

    [Fact]
    public async Task HealthReportsReady()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BatchReturnsValidRoutesAndMonotonicHistory()
    {
        var request = Request();
        var result = await Solve(request);
        Assert.Equal(request.Generations, result.Generations);
        Assert.Equal(request.Settings.PopulationSize, result.Population.Length);
        Assert.All(result.Population, order => Assert.True(Route.IsPermutation(order, 4)));
        Assert.True(Route.IsPermutation(result.BestOrder, 4));
        Assert.Equal(Length(request.Distances, result.BestOrder), result.BestLength, 10);
        Assert.Equal(Enumerable.Range(1, request.Generations), result.History.Select(h => h.Generation));
        Assert.All(result.History, h => Assert.True(double.IsFinite(h.AverageLength)));
        Assert.Equal(result.BestLength, result.History[^1].BestLength);
        Assert.All(result.History.Zip(result.History.Skip(1)), pair =>
            Assert.True(pair.Second.BestLength <= pair.First.BestLength));
    }

    [Fact]
    public async Task ContinuationAcceptsPopulationAndPreservesGoodMigrant()
    {
        var firstRequest = Request();
        var first = await Solve(firstRequest);
        var continued = await Solve(firstRequest with { Population = first.Population,
            Migrant = [0, 1, 2, 3], Generations = 3 });
        Assert.Equal(4, continued.BestLength);
        Assert.True(continued.BestLength <= first.BestLength);
        Assert.Equal(3, continued.History.Length);
        Assert.All(continued.Population, route => Assert.True(Route.IsPermutation(route, 4)));
    }

    [Fact]
    public async Task FixedSeedProducesSameResult()
    {
        var first = await Solve(Request());
        var second = await Solve(Request());
        Assert.Equal(first.BestOrder, second.BestOrder);
        Assert.Equal(first.BestLength, second.BestLength);
        Assert.Equal(first.History, second.History);
        for (int i = 0; i < first.Population.Length; i++)
            Assert.Equal(first.Population[i], second.Population[i]);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        var request = Request();
        yield return [request with { Generations = 0 }];
        yield return [request with { Settings = request.Settings with { PopulationSize = 1 } }];
        yield return [request with { Settings = request.Settings with { MutationProbability = 2 } }];
        yield return [request with { Settings = request.Settings with { MaxThreads = 257 } }];
        yield return [request with { Distances = [[0, 1], [1, 0]] }];
        yield return [request with { Distances = [[0, 1, 2], [1, 0], [2, 1, 0]] }];
        yield return [request with { Distances = [[0, -1, 2], [-1, 0, 1], [2, 1, 0]] }];
        yield return [request with { Population = [[0, 1, 2, 3]] }];
        yield return [request with { Population = Enumerable.Repeat(new[] { 0, 0, 2, 3 }, 32).ToArray() }];
        yield return [request with { Migrant = [0, 0, 2, 3] }];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidInputReturnsBadRequest(BatchRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/batches", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MalformedJsonReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/batches",
            new StringContent("{broken", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsKeepTheirOwnMatrices()
    {
        var firstRequest = Request(1);
        var secondRequest = Request(2) with {
            Distances = [[0, 3, 24, 3], [3, 0, 3, 24], [24, 3, 0, 3], [3, 24, 3, 0]] };
        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/batches", firstRequest),
            _client.PostAsJsonAsync("/api/batches", secondRequest));
        var requests = new[] { firstRequest, secondRequest };
        for (int i = 0; i < responses.Length; i++)
        {
            Assert.True(responses[i].StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
            var result = responses[i].StatusCode == HttpStatusCode.ServiceUnavailable
                ? await Solve(requests[i])
                : (await responses[i].Content.ReadFromJsonAsync<BatchResponse>())!;
            Assert.Equal(Length(requests[i].Distances, result.BestOrder), result.BestLength, 10);
        }
    }

    private async Task<BatchResponse> Solve(BatchRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/batches", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BatchResponse>())!;
    }

    private static double Length(double[][] matrix, int[] order) =>
        Enumerable.Range(0, order.Length).Sum(i => matrix[order[i]][order[(i + 1) % order.Length]]);
}
