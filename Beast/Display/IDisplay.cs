using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


// Abstraction for all output rendering. Attach subscribes to the model; RunAsync blocks until the session ends.
// SetSendAsync wires up the user input path (no-op for non-interactive displays).
public interface IDisplay
{
    void Attach(ConversationModel model);
    void SetStatus(string text);
    void SetCompletions(IReadOnlyList<string> completions);
    void OnStreamStart(int streamIndex, FrameType type);
    void OnStreamChunk(string chunk);
    void OnStreamEnd();
    void SetSendAsync(Func<string, Task> sendAsync);
    Task RunAsync(CancellationToken cancellationToken);
}
