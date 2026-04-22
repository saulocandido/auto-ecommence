namespace AutoCommerce.StoreManagement.Services;

/// <summary>
/// Scores how well a candidate product matches the target product.
/// Uses fuzzy title matching, keyword relevance, and price proximity.
/// </summary>
public interface IProductMatchingEngine
{
    MatchResult Score(MatchTarget target, MatchCandidate candidate);
    MatchCandidate? SelectBest(MatchTarget target, IEnumerable<MatchCandidate> candidates, double threshold);
}

public record MatchTarget(
    string Title,
    string[] Keywords,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? SupplierKey,
    string? ImageUrl);

public record MatchCandidate(
    string Id,
    string Title,
    decimal Price,
    string? Vendor,
    string? ImageUrl,
    string? Description);

public record MatchResult(
    double TitleScore,
    double KeywordScore,
    double PriceScore,
    double VendorScore,
    double TotalScore,
    string Explanation);

public class ProductMatchingEngine : IProductMatchingEngine
{
    // Weights
    private const double TitleWeight = 0.45;
    private const double KeywordWeight = 0.25;
    private const double PriceWeight = 0.20;
    private const double VendorWeight = 0.10;

    public MatchResult Score(MatchTarget target, MatchCandidate candidate)
    {
        var titleScore = FuzzyTitleScore(target.Title, candidate.Title);
        var keywordScore = KeywordRelevanceScore(target.Keywords, candidate.Title, candidate.Description);
        var priceScore = PriceProximityScore(target.MinPrice, target.MaxPrice, candidate.Price);
        var vendorScore = VendorMatchScore(target.SupplierKey, candidate.Vendor);

        var total = titleScore * TitleWeight
                  + keywordScore * KeywordWeight
                  + priceScore * PriceWeight
                  + vendorScore * VendorWeight;

        return new MatchResult(
            Math.Round(titleScore, 3),
            Math.Round(keywordScore, 3),
            Math.Round(priceScore, 3),
            Math.Round(vendorScore, 3),
            Math.Round(total, 3),
            $"title={titleScore:F2} kw={keywordScore:F2} price={priceScore:F2} vendor={vendorScore:F2}");
    }

    public MatchCandidate? SelectBest(MatchTarget target, IEnumerable<MatchCandidate> candidates, double threshold)
    {
        MatchCandidate? best = null;
        double bestScore = 0;

        foreach (var c in candidates)
        {
            var result = Score(target, c);
            if (result.TotalScore > bestScore)
            {
                bestScore = result.TotalScore;
                best = c;
            }
        }

        return bestScore >= threshold ? best : null;
    }

    // ── Fuzzy title matching ──

    private static double FuzzyTitleScore(string target, string candidate)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(candidate)) return 0;

        var t = Normalize(target);
        var c = Normalize(candidate);

        // Exact match
        if (t == c) return 1.0;

        // Contains
        if (c.Contains(t) || t.Contains(c))
            return 0.9;

        // Word overlap (Jaccard)
        var tWords = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var cWords = c.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (tWords.Count == 0 || cWords.Count == 0) return 0;

        var intersection = tWords.Intersect(cWords).Count();
        var union = tWords.Union(cWords).Count();
        var jaccard = (double)intersection / union;

        // Levenshtein distance normalized
        var lev = LevenshteinDistance(t, c);
        var maxLen = Math.Max(t.Length, c.Length);
        var levScore = maxLen > 0 ? 1.0 - (double)lev / maxLen : 0;

        return Math.Max(jaccard, levScore);
    }

    private static double KeywordRelevanceScore(string[] keywords, string title, string? description)
    {
        if (keywords.Length == 0) return 0.5; // neutral if no keywords

        var text = Normalize($"{title} {description ?? ""}");
        var hits = keywords.Count(kw => text.Contains(Normalize(kw)));
        return (double)hits / keywords.Length;
    }

    private static double PriceProximityScore(decimal? minPrice, decimal? maxPrice, decimal candidatePrice)
    {
        if (!minPrice.HasValue && !maxPrice.HasValue) return 0.5; // neutral

        var mid = ((minPrice ?? 0) + (maxPrice ?? candidatePrice * 2)) / 2;
        if (mid == 0) return 0.5;

        var diff = Math.Abs(candidatePrice - mid);
        var range = maxPrice.HasValue && minPrice.HasValue
            ? maxPrice.Value - minPrice.Value
            : mid * 0.5m;

        if (range == 0) range = 1;

        var score = 1.0 - (double)(diff / range);
        return Math.Clamp(score, 0, 1);
    }

    private static double VendorMatchScore(string? targetVendor, string? candidateVendor)
    {
        if (string.IsNullOrWhiteSpace(targetVendor) || string.IsNullOrWhiteSpace(candidateVendor))
            return 0.5; // neutral

        return Normalize(targetVendor) == Normalize(candidateVendor) ? 1.0 : 0.0;
    }

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace("-", " ").Replace("_", " ");

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[n, m];
    }
}
