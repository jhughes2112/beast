using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// In-memory IFramedTransport for test scenarios. Captures all sent output.
public class TestCaptureTransport : IFramedTransport
{
    private readonly List<(FrameType Type, string Text)> _sent = new List<(FrameType Type, string Text)>();
    private readonly Queue<string> _pendingReads = new Queue<string>();

    public IReadOnlyList<(FrameType Type, string Text)> Sent => _sent;

    public void EnqueueRead(string text)
    {
        _pendingReads.Enqueue(text);
    }

    public void Send(FrameType type, string text)
    {
        _sent.Add((type, text));
    }

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingReads.Count > 0)
        {
            return Task.FromResult<string?>(_pendingReads.Dequeue());
        }
        return Task.FromResult<string?>(null);
    }
}
