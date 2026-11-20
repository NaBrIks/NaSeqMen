// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Threading.Channels;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Runtime;

/// <summary>Live subscribers only. Nothing is retained when no editor is listening.</summary>
public sealed class DiagnosticHub
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();

    public Subscription Subscribe()
    {
        var subscription = new Subscription(Guid.NewGuid(), this);
        _subscribers[subscription.Id] = subscription;
        return subscription;
    }

    public void Publish(Diagnostic diagnostic)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Publish(diagnostic);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly DiagnosticHub _owner;
        private readonly Channel<Diagnostic> _channel = Channel.CreateBounded<Diagnostic>(
            new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private long _lost;
        private readonly object _gate = new();
        private readonly ChannelReader<Diagnostic> _reader;

        internal Subscription(Guid id, DiagnosticHub owner) { Id = id; _owner = owner; _reader = new LiveReader(this); }
        internal Guid Id { get; }
        public ChannelReader<Diagnostic> Reader => _reader;

        internal void Publish(Diagnostic diagnostic)
        {
            lock (_gate)
            {
                FlushLoss();
                if (!_channel.Writer.TryWrite(diagnostic)) _lost++;
            }
        }

        private void FlushLoss()
        {
            if (_lost <= 0) return;
            var gap = new Diagnostic(DateTimeOffset.UtcNow, "", "", "error",
                M("runtime.diagnosticsDropped", ("count", _lost)));
            if (_channel.Writer.TryWrite(gap)) _lost = 0;
        }

        private sealed class LiveReader(Subscription subscription) : ChannelReader<Diagnostic>
        {
            public override Task Completion => subscription._channel.Reader.Completion;
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
                => subscription._channel.Reader.WaitToReadAsync(cancellationToken);
            public override bool TryRead(out Diagnostic item)
            {
                if (!subscription._channel.Reader.TryRead(out item!)) return false;
                // The burst may already have ended. Report losses when delivery resumes,
                // without requiring a new physical input just to flush the notice.
                lock (subscription._gate) subscription.FlushLoss();
                return true;
            }
        }

        public void Dispose()
        {
            _owner._subscribers.TryRemove(Id, out _);
            _channel.Writer.TryComplete();
        }
    }
}
