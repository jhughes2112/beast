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
	void SetStatsInfo(string model, string role, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens, int cachedTokens);
	void SetCompletions(IReadOnlyList<string> completions);
	void OnStreamStart(int streamIndex, FrameType type);
	void OnStreamChunk(string chunk);
	void OnStreamEnd();
	// startTick is the tick when the busy session's current turn began (ignored when busy is false),
	// so the separator shows the session's own working duration rather than time-since-view-switch.
	void SetAgentBusy(bool busy, long startTick);
	void SetSendAsync(Func<string, Task> sendAsync);
	void SetRequestExit(Action requestExit);
	void SetFrameDrain(Action drain);
	// Sets the session counts shown in the status bar (active = busy, total = all known).
	void SetSessionCounts(int active, int total);
	// Updates the session list shown in the F10 overlay. activeId is the currently displayed session.
	void SetSessionList(IReadOnlyList<SessionDisplayInfo> sessions, string activeId);
	// Wires up the callback invoked when the user selects a session in the overlay.
	void SetSessionSwitchCallback(Action<string> switchTo);
	// Wires up the callback invoked when the user deletes a subagent session in the overlay.
	void SetSessionDeleteCallback(Action<string> deleteSession);
	// Clears the given session's pending-input ghost shown above the input separator. Called when that
	// session echoes a user message back, meaning its queued steering text was consumed.
	void ClearPendingGhost(string sessionId);
	// True when auto-tracking of incoming messages should be suppressed: the session overlay
	// is open or the user has scrolled away from the bottom of the conversation.
	bool IsAutoTrackSuppressed();
	Task RunAsync(CancellationToken cancellationToken);
	// Restores the terminal (leaves the alt screen, re-enables wrap, shows the cursor). Used when the
	// session ends before RunAsync ever took over the screen — e.g. the sandbox fails to launch after the
	// worktree chooser left its alt screen up. No-op for displays that do not own the alt screen.
	void RestoreTerminal();
}