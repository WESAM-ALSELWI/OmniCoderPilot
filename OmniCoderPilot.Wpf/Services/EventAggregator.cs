using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Threading;

namespace OmniCoderPilot.Wpf.Services;

public interface IEventAggregator
{
    void Publish<T>(T eventData);
    IDisposable Subscribe<T>(Action<T> handler);
}

public sealed class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, ConcurrentBag<Delegate>> _subscribers = new();
    private readonly Dispatcher _dispatcher;

    public EventAggregator(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Publish<T>(T eventData)
    {
        if (!_subscribers.TryGetValue(typeof(T), out var handlers)) return;
        foreach (var handler in handlers)
        {
            if (handler is Action<T> typed)
            {
                _dispatcher.BeginInvoke(() =>
                {
                    try { typed(eventData); }
                    catch { /* swallow handler errors */ }
                });
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        var bag = _subscribers.GetOrAdd(typeof(T), _ => new ConcurrentBag<Delegate>());
        bag.Add(handler);
        return new Unsubscriber(() =>
        {
            var newBag = new ConcurrentBag<Delegate>();
            foreach (var h in bag)
                if (!Equals(h, handler)) newBag.Add(h);
            _subscribers[typeof(T)] = newBag;
        });
    }

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
