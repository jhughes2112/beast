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
    void SetStatsInfo(string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens);
    void SetCompletions(IReadOnlyList<string> completions);
    void OnStreamStart(int streamIndex, FrameType type);
    void OnStreamChunk(string chunk);
    void OnStreamEnd();
    void SetAgentBusy(bool busy);
    void SetSendAsync(Func<string, Task> sendAsync);
    void SetRequestExit(Action requestExit);
    void SetFrameDrain(Action drain);
    // Sets the session counts shown in the status bar (active = busy, total = all known).
    void SetSessionCounts(int active, int total);
    // Updates the session list shown in the F10 overlay. activeId is the currently displayed session.
    void SetSessionList(IReadOnlyList<SessionDisplayInfo> sessions, string activeId);
    // Wires up the callback invoked when the user selects a session in the overlay.
    void SetSessionSwitchCallback(Action<string> switchTo);
    // True when auto-tracking of incoming messages should be suppressed: the session overlay
    // is open or the user has scrolled away from the bottom of the conversation.
    bool IsAutoTrackSuppressed();
    Task RunAsync(CancellationToken cancellationToken);
}
