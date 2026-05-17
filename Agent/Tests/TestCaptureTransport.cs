using System;
using System.Collections.Generic;
using System.Text;
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

    // Collect everything sent so far as a formatted test report header.
    public string BuildReport(TestContext ctx)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"=== Test Report ===");
        sb.AppendLine($"Passed: {ctx.Passed}   Failed: {ctx.Failed}");
        sb.AppendLine();

        foreach ((FrameType type, string text) in _sent)
        {
            sb.AppendLine($"[{type}] {text}");
        }

        return sb.ToString();
    }
}
