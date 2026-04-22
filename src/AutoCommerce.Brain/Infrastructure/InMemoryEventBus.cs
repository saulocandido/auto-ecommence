using System.Collections.Concurrent;
using System.Threading.Channels;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.Brain.Infrastructure;

public sealed class InMemoryEventBus : IEventBus, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, List<Subscription>> _typed = new();
    private readonly List<Subscription> _wildcards = new();
    private readonly object _wildcardLock = new();
    private readonly Channel<DomainEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(DispatchLoopAsync);
    }

    public Task PublishAsync(DomainEvent evt, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(evt, ct).AsTask();

    public IDisposable Subscribe(string eventType, Func<DomainEvent, CancellationToken, Task> handler)
    {
        var sub = new Subscription(handler);
        var list = _typed.GetOrAdd(eventType, _ => new List<Subscription>());
        lock (list) list.Add(sub);
        return new Unsubscriber(() =>
        {
            lock (list) list.Remove(sub);
        });
    }

    public IDisposable SubscribeAll(Func<DomainEvent, CancellationToken, Task> handler)
    {
        var sub = new Subscription(handler);
        lock (_wildcardLock) _wildcards.Add(sub);
        return new Unsubscriber(() =>
        {
            lock (_wildcardLock) _wildcards.Remove(sub);
        });
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                await DispatchAsync(evt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event dispatch loop crashed");
        }
    }

    private async Task DispatchAsync(DomainEvent evt)
    {
        var tasks = new List<Task>();
        if (_typed.TryGetValue(evt.Type, out var typed))
        {
            Subscription[] snapshot;
            lock (typed) snapshot = typed.ToArray();
            foreach (var sub in snapshot) tasks.Add(InvokeSafe(sub, evt));
        }

        Subscription[] wildcardSnapshot;
        lock (_wildcardLock) wildcardSnapshot = _wildcards.ToArray();
        foreach (var sub in wildcardSnapshot) tasks.Add(InvokeSafe(sub, evt));

        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }

    private async Task InvokeSafe(Subscription sub, DomainEvent evt)
    {
        try
        {
            await sub.Handler(evt, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event handler failed for {Type}", evt.Type);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker; } catch { }
        _cts.Dispose();
    }

    private sealed record Subscription(Func<DomainEvent, CancellationToken, Task> Handler);

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dispose();
        }
    }
}
