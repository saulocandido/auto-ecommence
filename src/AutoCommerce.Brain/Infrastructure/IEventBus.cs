using AutoCommerce.Shared.Events;

namespace AutoCommerce.Brain.Infrastructure;

public interface IEventBus
{
    Task PublishAsync(DomainEvent evt, CancellationToken ct = default);
    IDisposable Subscribe(string eventType, Func<DomainEvent, CancellationToken, Task> handler);
    IDisposable SubscribeAll(Func<DomainEvent, CancellationToken, Task> handler);
}
