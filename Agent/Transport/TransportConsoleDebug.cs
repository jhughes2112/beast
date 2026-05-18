using System;
using System.Threading;
using System.Threading.Tasks;


// Debug transport: reads plain text lines from stdin, writes typed output to stdout.
// Use with --debug to run the Agent directly in an IDE or terminal without Beast.
public class TransportConsoleDebug : ITransportServer
{
    public void Send(FrameType type, string text)
    {
        string prefix = type switch
        {
            FrameType.Output      => "",
            FrameType.Error       => "[error] ",
            FrameType.Status      => "[status] ",
            FrameType.Tool        => "[tool] ",
            FrameType.Thinking    => "[thinking] ",
            FrameType.Completions => "[completions] ",
            FrameType.Debug       => "[debug] ",
            _                     => $"[{type}] "
        };
        Console.WriteLine($"{prefix}{text}");
    }

    public void StreamStart(string tag)
    {
        Console.Write($"[stream:{tag}] ");
    }

    public void StreamChunk(string chunk)
    {
        Console.Write(chunk);
    }

    public void StreamEnd(string tag)
    {
        Console.WriteLine();
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        Console.Write("> ");
        // Console.ReadLine() has no async API; Task.Run offloads the synchronous block
        // to a pool thread so the caller's async context is not frozen.
        string? line = await Task.Run(() =>
        {
            try
            {
                return Console.ReadLine();
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

        return line;
    }
}
