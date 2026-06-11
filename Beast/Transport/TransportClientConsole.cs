using System;
using System.Threading;
using System.Threading.Tasks;


// ITransportServer implementation that writes directly to the console.
// Used for running Beast-side tests locally before the agent container is involved.
public class TransportClientConsole : ITransportServer
{
    public void Output(string sessionId, string text)      => Console.WriteLine(text);
    public void Error(string sessionId, string text)       => Console.WriteLine($"[error] {text}");
    public void Status(string sessionId, string text)      { }
    public void Thinking(string sessionId, string text)    { }
    public void System(string sessionId, string text)      { }
    public void User(string sessionId, string text)        { }
    public void Debug(string sessionId, string text)       { }
    public void Stats(string sessionId, string json)       { }
    public void Completions(string sessionId, string json) { }
    public void Idle(string sessionId, bool subagent)      { }
    public void Busy(string sessionId)                     { }
    public void Clear(string sessionId)                    { }
    public void ToolCallWithId(string sessionId, string callId, string text)           { }
    public void ToolResponseWithId(string sessionId, string callId, ToolResult result) { }
    public void SessionAnnounce(string sessionId, string json) { }
    public void StreamStart(string sessionId, string tag)   { }
    public void StreamChunk(string sessionId, string chunk) { }
    public void StreamEnd(string sessionId, string tag)     { }

    public Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }
}
