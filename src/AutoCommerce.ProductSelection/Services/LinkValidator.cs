using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.ProductSelection.Services;

public sealed record LinkValidationResult(
    string ExternalId,
    string SupplierKey,
    string OriginalUrl,
    string? CorrectedUrl,
    LinkStatus Status,
    string? Detail);

public enum LinkStatus
{
    Verified,
    Corrected,
    Invalid,
    Skipped
}

public sealed record LinkValidationReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Total,
    int Verified,
    int Corrected,
    int Invalid,
    int Skipped,
    IReadOnlyList<LinkValidationResult> Results);

public interface ILinkValidator
{
    Task<LinkValidationReport> ValidateRecommendationsAsync(CancellationToken ct);
}

public sealed class LinkValidator : ILinkValidator
{
    private readonly IRecommendationCache _cache;
    private readonly IBrainClient _brain;
    private readonly HttpClient _http;
    private readonly ILogger<LinkValidator> _logger;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaxRetries = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxConcurrency = 4;

    public LinkValidator(
        IRecommendationCache cache,
        IBrainClient brain,
        HttpClient http,
        ILogger<LinkValidator> logger)
    {
        _cache = cache;
        _brain = brain;
        _http = http;
        _logger = logger;
    }

    public async Task<LinkValidationReport> ValidateRecommendationsAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var cached = await _cache.GetLatestAsync(ct);

        if (cached is null || cached.Recommendations.Count == 0)
        {
            _logger.LogInformation("No cached recommendations to validate");
            return new LinkValidationReport(started, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, Array.Empty<LinkValidationResult>());
        }

        // Collect all (product, supplier) pairs that have URLs
        var tasks = new List<(ScoredCandidate Candidate, SupplierListing Supplier)>();
        foreach (var rec in cached.Recommendations)
        {
            foreach (var supplier in rec.Candidate.SupplierCandidates)
            {
                tasks.Add((rec, supplier));
            }
        }

        _logger.LogInformation("Validating {Count} supplier links across {Products} products",
            tasks.Count, cached.Recommendations.Count);

        // Throttle concurrency
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var results = await Task.WhenAll(tasks.Select(async t =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ValidateSingleLinkAsync(t.Candidate, t.Supplier, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        var resultList = results.ToList();

        // Publish events for each result
        foreach (var result in resultList)
        {
            var eventType = result.Status switch
            {
                LinkStatus.Verified => EventTypes.ProductLinkVerified,
                LinkStatus.Corrected => EventTypes.ProductLinkCorrected,
                LinkStatus.Invalid => EventTypes.ProductLinkInvalid,
                _ => null
            };

            if (eventType is not null)
            {
                try
                {
                    await _brain.PublishEventAsync(DomainEvent.Create(eventType, "product-selection", new
                    {
                        result.ExternalId,
                        result.SupplierKey,
                        result.OriginalUrl,
                        result.CorrectedUrl,
                        Status = result.Status.ToString(),
                        result.Detail
                    }), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish {Event} for {Id}", eventType, result.ExternalId);
                }
            }
        }

        var report = new LinkValidationReport(
            StartedAt: started,
            CompletedAt: DateTimeOffset.UtcNow,
            Total: resultList.Count,
            Verified: resultList.Count(r => r.Status == LinkStatus.Verified),
            Corrected: resultList.Count(r => r.Status == LinkStatus.Corrected),
            Invalid: resultList.Count(r => r.Status == LinkStatus.Invalid),
            Skipped: resultList.Count(r => r.Status == LinkStatus.Skipped),
            Results: resultList);

        _logger.LogInformation(
            "Link validation complete: {Verified} verified, {Corrected} corrected, {Invalid} invalid, {Skipped} skipped",
            report.Verified, report.Corrected, report.Invalid, report.Skipped);

        // Remove supplier entries whose links are broken
        if (report.Invalid > 0)
        {
            await RemoveInvalidLinksFromCache(cached, resultList, ct);
        }

        return report;
    }

    private async Task RemoveInvalidLinksFromCache(
        RecommendationResponse cached,
        List<LinkValidationResult> results,
        CancellationToken ct)
    {
        var invalidKeys = results
            .Where(r => r.Status == LinkStatus.Invalid)
            .Select(r => (r.ExternalId, r.SupplierKey))
            .ToHashSet();

        if (invalidKeys.Count == 0) return;

        var updatedRecs = cached.Recommendations.Select(rec =>
        {
            var filtered = rec.Candidate.SupplierCandidates
                .Where(s => !invalidKeys.Contains((rec.Candidate.ExternalId, s.SupplierKey)))
                .ToList();

            if (filtered.Count == rec.Candidate.SupplierCandidates.Count) return rec;

            var updatedCandidate = rec.Candidate with { SupplierCandidates = filtered };
            return rec with { Candidate = updatedCandidate };
        }).ToList();

        var updatedResponse = new RecommendationResponse(cached.GeneratedAt, cached.Config, updatedRecs);
        await _cache.SaveAsync(updatedResponse, ct);
        _logger.LogInformation("Removed {Count} broken supplier links from recommendation cache", invalidKeys.Count);
    }

    private async Task<LinkValidationResult> ValidateSingleLinkAsync(
        ScoredCandidate candidate, SupplierListing supplier, CancellationToken ct)
    {
        var externalId = candidate.Candidate.ExternalId;
        var url = supplier.Url;

        if (string.IsNullOrWhiteSpace(url))
        {
            return new LinkValidationResult(externalId, supplier.SupplierKey, "", null, LinkStatus.Skipped, "No URL provided");
        }

        // Validate URL format
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid, "Malformed URL");
        }

        // Try the link with retries
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "AutoCommerce-LinkValidator/1.0");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    // Read a portion of the body to check it's a real page (not an empty/error page)
                    var content = await ReadPartialContentAsync(response, 4096, cts.Token);

                    // If the page is very short or clearly an error page, mark as invalid
                    if (string.IsNullOrWhiteSpace(content) || content.Length < 200)
                    {
                        _logger.LogWarning("Link returned empty/tiny page for {Id} at {Url}", externalId, url);
                        return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid,
                            "Page returned but content is empty or too small");
                    }

                    // Check if this looks like a real product/commerce page (has any price or product indicators)
                    var lowerContent = content.ToLowerInvariant();
                    var isCommercePage = lowerContent.Contains("price") || lowerContent.Contains("$")
                        || lowerContent.Contains("€") || lowerContent.Contains("£")
                        || lowerContent.Contains("add to cart") || lowerContent.Contains("buy")
                        || lowerContent.Contains("product") || lowerContent.Contains("shop")
                        || lowerContent.Contains("item") || lowerContent.Contains("order");

                    if (isCommercePage)
                    {
                        _logger.LogDebug("Link verified for {Id} at {Url}", externalId, url);
                        return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Verified, null);
                    }

                    // The page is reachable and has content but doesn't look like a commerce page —
                    // still mark as verified (link works), but note the mismatch
                    _logger.LogDebug("Link reachable but non-commerce page for {Id} at {Url}", externalId, url);
                    return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Verified,
                        "Page loads but may not be exact product page");
                }

                if ((int)response.StatusCode >= 500 && attempt < MaxRetries)
                {
                    _logger.LogDebug("Retrying {Url} after {Status} (attempt {Attempt})", url, response.StatusCode, attempt + 1);
                    await Task.Delay(RetryDelay * (attempt + 1), ct);
                    continue;
                }

                // 404 or permanent error — mark as invalid so it gets removed
                _logger.LogWarning("Link broken for {Id}: {Status} at {Url}", externalId, response.StatusCode, url);
                return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid,
                    $"HTTP {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelay * (attempt + 1), ct);
                    continue;
                }
                return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid, "Request timed out");
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogDebug(ex, "Transient error for {Url}, retrying", url);
                await Task.Delay(RetryDelay * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate {Url} for {Id}", url, externalId);
                return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid, ex.Message);
            }
        }

        return new LinkValidationResult(externalId, supplier.SupplierKey, url, null, LinkStatus.Invalid, "Max retries exhausted");
    }

    private static async Task<string> ReadPartialContentAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var read = await stream.ReadAsync(buffer.AsMemory(0, maxBytes), ct);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
