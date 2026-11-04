using SteamVrTranslator;

internal static class OcrAccuracyTests
{
    public static void Run()
    {
        var exact = OcrAccuracy.Score("が\nありがとう", "か\u3099 ありがとう");
        Check(exact.ExactMatch && exact.ReferenceCharacters == 6, "Layout and canonical dakuten do not count as recognition errors");
        Check(OcrAccuracy.Score("おはよう", "おはよ").Deletions == 1, "Missing final kana counts as a deletion");
        Check(OcrAccuracy.Score("ありがとう", "ありかとう").Substitutions == 1, "Missing dakuten changes a character");
        Check(OcrAccuracy.Score("かな", "かなA").Insertions == 1, "Background text counts as extra characters");
        Check(OcrAccuracy.Score("𠮷", "吉").Errors == 1, "Supplementary-plane characters count as one character");
        Check(!OcrAccuracy.Score("っ", "つ").ExactMatch, "Small kana must not be normalized into full-size kana");
        Check(!OcrAccuracy.Score("読む", "[无法辨认]").ExactMatch, "Abstention is not reported as correct transcription");
        Check(OcrAccuracy.Score("", "余分").Insertions == 2, "Text hallucinated on an empty reference counts");
        Console.WriteLine("PASS offline OCR error counts for omitted kana, dakuten, inserted background text and Unicode");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
