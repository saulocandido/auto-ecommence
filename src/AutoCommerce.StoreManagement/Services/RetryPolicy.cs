using System.Net;

namespace AutoCommerce.StoreManagement.Services;

public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> op, string opName, CancellationToken ct);
    Task ExecuteAsync(Func<CancellationToken, Task> op, string opName, CancellationToken ct);
}

public class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly ILogger<ExponentialBackoffRetryPolicy> _logger;
    private readonly int _maxAttempts;
    private readonly int _baseDelayMs;

    public ExponentialBackoffRetryPolicy(ILogger<ExponentialBackoffRetryPolicy> logger,
        int maxAttempts = 5, int baseDelayMs = 500)
    {
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelayMs = Math.Max(50, baseDelayMs);
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> op, string opName, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await op(ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _maxAttempts)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(_baseDelayMs * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "{Op} attempt {Attempt}/{Max} failed; retrying in {Delay}ms",
                    opName, attempt, _maxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
        throw last ?? new InvalidOperationException($"Retry exhausted for {opName}");
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> op, string opName, CancellationToken ct)
    {
        await ExecuteAsync<object?>(async token => { await op(token); return null; }, opName, ct);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is HttpStatusCode code)
            {
                return code == HttpStatusCode.TooManyRequests
                    || code == HttpStatusCode.RequestTimeout
                    || (int)code >= 500;
            }
            return true;
        }
        return ex is TaskCanceledException || ex is TimeoutException;
    }
}
