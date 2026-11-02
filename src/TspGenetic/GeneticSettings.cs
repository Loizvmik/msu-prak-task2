namespace TspGenetic;

public sealed record GeneticSettings
{
    public int PopulationSize { get; init; } = 300;

    public int EliteCount { get; init; } = 4;

    public int TournamentSize { get; init; } = 4;

    public double CrossoverProbability { get; init; } = 0.9;

    public double MutationProbability { get; init; } = 0.3;

    public double InversionShare { get; init; }

    public int MaxThreads { get; init; }

    public int? Seed { get; init; }

    public void Validate()
    {
        if (PopulationSize < 2)
            throw new ArgumentException("Размер популяции должен быть не меньше 2.");
        if (EliteCount < 0 || EliteCount >= PopulationSize)
            throw new ArgumentException("Число элитных экземпляров должно быть от 0 до размера популяции − 1.");
        if (TournamentSize < 1 || TournamentSize > PopulationSize)
            throw new ArgumentException("Размер турнира должен быть от 1 до размера популяции.");
        if (CrossoverProbability is < 0 or > 1 || double.IsNaN(CrossoverProbability))
            throw new ArgumentException("Вероятность скрещивания должна лежать в [0, 1].");
        if (MutationProbability is < 0 or > 1 || double.IsNaN(MutationProbability))
            throw new ArgumentException("Вероятность мутации должна лежать в [0, 1].");
        if (InversionShare is < 0 or > 1 || double.IsNaN(InversionShare))
            throw new ArgumentException("Доля инверсий должна лежать в [0, 1].");
        if (MaxThreads < 0)
            throw new ArgumentException("Число потоков не может быть отрицательным.");
    }
}
