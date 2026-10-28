namespace FFMoGuang.Core;

public sealed class InputTiming(Func<int, int, int>? random = null)
{
    private readonly Func<int, int, int> next = random ?? Random.Shared.Next;
    public int SubmitDelay(AnswerOptions options)
    {
        options = options.Clamp();
        if (!options.RandomizeSubmitDelay) return options.FillDelayMs;
        return next(options.SubmitDelayMinMs, options.SubmitDelayMaxMs + 1);
    }
    public int Gap(AnswerOptions options)
    {
        options = options.Clamp();
        if (!options.RandomizeDelays) return options.CharacterDelayMs;
        var delay = next(options.RandomMinMs, options.RandomMaxMs + 1);
        return next(0, 100) < 12 ? delay + next(180, 421) : delay;
    }
    public int Hold(AnswerOptions options) => options.RandomizeDelays ? next(35, 81) : 50;
}
