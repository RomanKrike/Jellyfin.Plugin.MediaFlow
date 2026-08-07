namespace Jellyfin.Plugin.MediaFlow.Services;

public static class TitleSimilarity
{
    public static double Score(string left, string right)
    {
        var a = MediaParser.NormalizeForCompare(left);
        var b = MediaParser.NormalizeForCompare(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        var levenshtein = 1.0 - ((double)Levenshtein(a, b) / Math.Max(a.Length, b.Length));
        var token = TokenJaccard(a, b);
        return Math.Clamp((levenshtein * 0.65) + (token * 0.35), 0, 1);
    }

    private static double TokenJaccard(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Count(x => right.Contains(x));
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
