using System.Threading;
using System.Threading.Tasks;


// Discards everything sent to it. Used for the throwaway sub-sessions behind a tool â€” the media
// inspection stage session in particular â€” whose work belongs in the tool's result block, not
// streamed into the caller's transcript as unattributed assistant text. Errors from those runs
// still reach the caller: they come back inside the ToolResult.
public class TransportSilent : ITransportServer
{
	public void Output            (string sessionId, string text) { }
	public void Error             (string sessionId, string text) { }
	public void Alert             (string sessionId, string text) { }
	public void Status            (string sessionId, string text) { }
	public void Thinking          (string sessionId, string text) { }
	public void System            (string sessionId, string text) { }
	public void User              (string sessionId, string text) { }
	public void Debug             (string sessionId, string text) { }
	public void Stats             (string sessionId, string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens, int cachedTokens) { }
	public void Completions       (string sessionId,   string json)              { }
	public void Config            (string sessionId,   string json)              { }
	public void Idle              (string sessionId, bool subagent)              { }
	public void Busy              (string sessionId)                             { }
	public void ToolCallWithId    (string sessionId, string callId, string text) { }
	public void ToolResponseWithId(string sessionId, ToolResult result)          { }
	public void SessionAnnounce   (string sessionId,       string json)          { }
	public void SessionActivate   (string sessionId)                             { }
	public void SessionStatus     (string sessionId,  string status)             { }
	public void PendingQueue      (string sessionId, string[] lines)             { }
	public void StreamStart       (string sessionId,     string tag)             { }
	public void StreamChunk       (string sessionId,   string chunk)             { }
	public void StreamEnd         (string sessionId,     string tag)             { }

	public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

	public Task<string?> TryReadAsync(int timeoutMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}