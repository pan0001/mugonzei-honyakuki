using System.Text;

namespace SteamVrTranslator;

internal sealed record OcrAccuracyResult(int ReferenceCharacters, int Substitutions, int Deletions, int Insertions)
{
    public int Errors => Substitutions + Deletions + Insertions;
    public double CharacterErrorRate => ReferenceCharacters == 0 ? (Errors == 0 ? 0 : 1) : (double)Errors / ReferenceCharacters;
    public bool ExactMatch => Errors == 0;
}

/// <summary>Offline scoring only. Reference answers must never enter a model request.</summary>
internal static class OcrAccuracy
{
    private readonly record struct Edits(int Substitutions, int Deletions, int Insertions)
    {
        public int Cost => Substitutions + Deletions + Insertions;
    }

    public static OcrAccuracyResult Score(string reference, string actual)
    {
        static Rune[] Characters(string text) => text.Normalize(NormalizationForm.FormC)
            .EnumerateRunes().Where(character => !Rune.IsWhiteSpace(character)).ToArray();
        var expected = Characters(reference);
        var recognized = Characters(actual);
        if (expected.Length > 4000 || recognized.Length > 32000)
            throw new ArgumentException("Accuracy samples exceed the bounded comparison length.");
        var previous = new Edits[recognized.Length + 1];
        var current = new Edits[recognized.Length + 1];
        for (int j = 1; j <= recognized.Length; j++) previous[j] = new(0, 0, j);
        for (int i = 1; i <= expected.Length; i++)
        {
            current[0] = new(0, i, 0);
            for (int j = 1; j <= recognized.Length; j++)
            {
                var best = previous[j - 1];
                if (expected[i - 1] != recognized[j - 1]) best = best with { Substitutions = best.Substitutions + 1 };
                var deletion = previous[j] with { Deletions = previous[j].Deletions + 1 };
                var insertion = current[j - 1] with { Insertions = current[j - 1].Insertions + 1 };
                if (deletion.Cost < best.Cost) best = deletion;
                if (insertion.Cost < best.Cost) best = insertion;
                current[j] = best;
            }
            (previous, current) = (current, previous);
        }
        var result = previous[recognized.Length];
        return new(expected.Length, result.Substitutions, result.Deletions, result.Insertions);
    }
}
