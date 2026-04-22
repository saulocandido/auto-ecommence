namespace AutoCommerce.Configuration.Services;

/// <summary>
/// Notifies subscriber microservices when configuration changes.
/// </summary>
public interface IConfigurationNotifier
{
    Task NotifyAsync(CancellationToken ct);
}

public sealed class HttpConfigurationNotifier : IConfigurationNotifier
{
    private readonly HttpClient _http;
    private readonly string[] _subscriberUrls;
    private readonly ILogger<HttpConfigurationNotifier> _logger;

    public HttpConfigurationNotifier(
        HttpClient http,
        string[] subscriberUrls,
        ILogger<HttpConfigurationNotifier> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _subscriberUrls = subscriberUrls;
        _logger = logger;
    }

    public async Task NotifyAsync(CancellationToken ct)
    {
        foreach (var url in _subscriberUrls)
        {
            try
            {
                var resp = await _http.PostAsync(url, null, ct);
                _logger.LogInformation("Notified {Url} → {Status}", url, resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify subscriber {Url}", url);
            }
        }
    }
}
