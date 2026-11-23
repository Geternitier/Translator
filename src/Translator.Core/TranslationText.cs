namespace Translator.Core;

public static class TranslationText
{
    public static bool NeedsTranslation(string text, string? detectedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLanguage(detectedLanguage, "zh")) return false;
        if (IsLanguage(detectedLanguage, "ja")) return true;

        int han = 0;
        foreach (var character in text.EnumerateRunes())
        {
            int value = character.Value;
            if (value is >= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D)
                return true;
            if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
                or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x323AF)
                han++;
        }

        if (han == 0) return true;

        // Give a mixed string a chance to translate a foreign phrase without treating lone labels as prose.
        int words = 0, letters = 0, previousEnd = 0;
        bool hasProseCase = false;
        for (int index = 0; index < text.Length;)
        {
            if (!char.IsAsciiLetter(text[index])) { index++; continue; }
            int start = index;
            while (index < text.Length && char.IsAsciiLetter(text[index])) index++;
            int length = index - start;
            bool hasLowercase = HasLowercase(text.AsSpan(start, length));
            if (length < 2 || (length < 5 && !hasLowercase)) continue;
            if (!IsWordGap(text.AsSpan(previousEnd, start - previousEnd)))
            {
                words = letters = 0;
                hasProseCase = false;
            }
            previousEnd = index;
            words++;
            letters += length;
            hasProseCase |= char.IsAsciiLetterLower(text[start]) || !hasLowercase;
            if (words >= 2 && letters >= han && (words >= 3 || hasProseCase)) return true;
        }

        return false;
    }

    private static bool HasLowercase(ReadOnlySpan<char> word)
    {
        foreach (char character in word)
            if (char.IsAsciiLetterLower(character)) return true;
        return false;
    }

    private static bool IsWordGap(ReadOnlySpan<char> gap)
    {
        foreach (char character in gap)
            if (!char.IsWhiteSpace(character) && character is not (',' or '.' or '-' or '\'')) return false;
        return true;
    }

    private static bool IsLanguage(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        || actual?.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase) == true;
}
