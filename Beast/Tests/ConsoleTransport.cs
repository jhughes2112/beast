using System;
using System.Threading;
using System.Threading.Tasks;


// IFramedTransport implementation that writes directly to the console.
// Used for running Beast-side tests locally before the agent container is involved.
public class ConsoleTransport : IFramedTransport
{
    public void Send(FrameType type, string text)
    {
        Console.WriteLine(text);
    }

    public void StreamStart(string tag) { }
    public void StreamChunk(string chunk) { }
    public void StreamEnd(string tag) { }

    public Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<string?> TryReadAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }
}
