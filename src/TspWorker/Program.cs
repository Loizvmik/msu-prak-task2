using TspDistributed;
using TspGenetic;

namespace TspWorker;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
        builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/api/batches", async (BatchRequest request, SemaphoreSlim gate, HttpContext context) =>
        {
            DistanceMatrix matrix;
            try
            {
                matrix = BatchProtocol.Validate(request);
            }
            catch (ArgumentException error)
            {
                return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректный запрос");
            }

            var cancellation = context.RequestAborted;
            if (!await gate.WaitAsync(0, cancellation))
                return Results.Problem("Сервис занят вычислением другого блока.",
                    statusCode: StatusCodes.Status503ServiceUnavailable, title: "Сервис занят");
            try
            {
                var response = await Task.Run(() =>
                {
                    var solver = new GeneticSolver(matrix, request.Settings, request.Population,
                        request.Migrant, cancellation);
                    var history = new BatchSample[request.Generations];
                    for (int i = 0; i < history.Length; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var stats = solver.NextGeneration();
                        history[i] = new BatchSample(stats.Generation, stats.BestLength, stats.AverageLength);
                    }
                    return new BatchResponse(solver.Generation,
                        solver.Population.Select(route => route.Order.ToArray()).ToArray(),
                        solver.Best.Order.ToArray(), solver.Best.Length, history);
                }, cancellation);
                return Results.Ok(response);
            }
            finally
            {
                gate.Release();
            }
        });
        app.Run();
    }
}
