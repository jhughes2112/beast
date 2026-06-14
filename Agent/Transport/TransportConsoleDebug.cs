using System;
using System.Threading;
using System.Threading.Tasks;


// Debug transport: reads plain text lines from stdin, writes typed output to stdout.
// Use with --debug to run the Agent directly in an IDE or terminal without Beast.
public class TransportConsoleDebug : ITransportServer
{
    public void Output(string sessionId, string text)      => Console.WriteLine($"{sessionId} {text}");
    public void Error(string sessionId, string text)       => Console.WriteLine($"{sessionId} [error] {text}");
    public void Status(string sessionId, string text)      => Console.WriteLine($"{sessionId} [status] {text}");
    public void Thinking(string sessionId, string text)    => Console.WriteLine($"{sessionId} [thinking] {text}");
    public void System(string sessionId, string text)      => Console.WriteLine($"{sessionId} [system] {text}");
    public void User(string sessionId, string text)        => Console.WriteLine($"{sessionId} [user] {text}");
    public void Debug(string sessionId, string text)       => Console.WriteLine($"{sessionId} [debug] {text}");
    public void Stats(string sessionId, string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens)
        => Console.WriteLine($"{sessionId} [stats] model={model}, role={role}, prompt={promptTokens}, completion={completionTokens}, cost={totalCost}, maxCtx={maxContext}, ctx={contextTokens}");
    public void Completions(string sessionId, string json) => Console.WriteLine($"{sessionId} [completions] {json}");
    public void Idle(string sessionId, bool subagent)      => Console.WriteLine($"{sessionId} [idle]{(subagent ? " (subagent)" : "")}");
    public void Busy(string sessionId)                     => Console.WriteLine($"{sessionId} [busy]");
    public void ToolCallWithId(string sessionId, string callId, string text)           => Console.WriteLine($"{sessionId} [tool-call] {text}");
    public void ToolResponseWithId(string sessionId, ToolResult result) => Console.WriteLine($"{sessionId} [tool-response] {result.StdOut}");
    public void SessionAnnounce(string sessionId, string json) { }

    // Streaming renders inline so the user sees tokens as they arrive.
    public void StreamStart(string sessionId, string tag)   => Console.Write($"{sessionId} [stream:{tag}] ");
    public void StreamChunk(string sessionId, string chunk) => Console.Write(chunk);
    public void StreamEnd(string sessionId, string tag)     => Console.WriteLine();

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        Console.Write("> ");
        // Console.ReadLine() has no async API; Task.Run offloads the synchronous block
        // to a pool thread so the caller's async context is not frozen.
        string? line = await Task.Run(() =>
        {
            try { return Console.ReadLine(); }
            catch { return null; }
        }, cancellationToken);
        return line;
    }
}
