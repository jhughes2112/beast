using System;
using System.Collections.Generic;
using System.Text;


// Describes a session entry for display in the session tree overlay.
public class SessionDisplayInfo
{
	public string Id     { get; }
	public string Name   { get; }
	public bool   IsBusy { get; }
	// Depth in the session hierarchy (0 = root, 1 = first-level child, etc.).
	public int Depth { get; }
	// Termination status for non-busy sessions; Ongoing while the session is busy.
	public SessionStatus Status { get; }
	// Paused by the user. It may still be busy for a moment, finishing the turn in flight.
	public bool Paused { get; }
	// Stopped with a turn still to send, so Play would do something. The agent decides; the client cannot tell.
	public bool CanContinue { get; }

	public SessionDisplayInfo(string id, string name, bool isBusy, int depth, SessionStatus status, bool paused, bool canContinue)
	{
		Id          = id;
		Name        = name;
		IsBusy      = isBusy;
		Depth       = depth;
		Status      = status;
		Paused      = paused;
		CanContinue = canContinue;
	}
}

// Controls the initial collapse state applied to messages as they arrive.
// Individual messages can always be toggled regardless of mode.
public enum CollapseMode
{
	Verbose,   // nothing collapsed on arrival
	Minimized, // Tool, Thinking, and System collapsed; Output and Error expanded
	Quiet      // everything collapsed on arrival
}

// A single slot in the conversation display. Index is stable; content is overwritten in place, or appended to
// while it streams. Appending never copies what is already there, so a long stream costs each chunk only its
// own length; the whole text is materialized only when something reads Content.
public class DisplayMessage
{
	public int       Index     { get; }
	public FrameType Type      { get; set; }
	public bool      Collapsed { get; set; }

	private readonly StringBuilder _text = new StringBuilder();
	private          string?       _content;
	private          int           _generation;

	// The whole text. Setting it replaces the text (a no-op when it is unchanged); reading it after appends
	// builds the string once and keeps it until the next append.
	public string Content
	{
		get
		{
			_content ??= _text.ToString();
			return _content;
		}
		set
		{
			if (!string.Equals(value, Content, StringComparison.Ordinal))
			{
				_text.Clear();
				_text.Append(value);
				_content = value;
				_generation++;
			}
		}
	}

	// Moves on whenever the text is replaced rather than appended to, so a renderer that has consumed the
	// text so far can tell growth from a rewrite without comparing it.
	public int Generation => _generation;

	public int Length => _text.Length;

	public void Append(string chunk)
	{
		_text.Append(chunk);
		_content = null;
	}

	// The text from start to the end: what arrived since a reader last consumed start characters.
	public string Since(int start) => _text.ToString(start, _text.Length - start);

	// Up to count characters from the start of the text.
	public string Head(int count) => _text.ToString(0, Math.Min(count, _text.Length));

	// When this is a ToolCall slot, holds the paired ToolResponse stdout (or null if not yet received).
	public string? PairedResponseContent { get; set; }
	// When this is a ToolCall slot, holds the paired ToolResponse stderr (or null if not yet received).
	public string? PairedResponseError { get; set; }
	// True when the paired response exit code indicates an error (non-zero).
	public bool PairedResponseIsError { get; set; }
	// When this is a ToolCall slot, wall-clock time the call was first observed.
	public DateTime ToolCallStartedUtc { get; set; }
	// When this is a ToolCall slot, measured turnaround between call observation and paired response arrival.
	public TimeSpan? ToolDuration { get; set; }

	public DisplayMessage(int index, FrameType type, string content)
	{
		Index     = index;
		Type      = type;
		Collapsed = false;
		_text.Append(content);
		_content = content;
	}
}

// Holds all display messages and notifies the UI of changes.
// Update() is the only mutation path — callers always supply (index, type, content).
public class ConversationModel
{
	private readonly List<DisplayMessage> _messages = new List<DisplayMessage>();
	private          CollapseMode         _mode     = CollapseMode.Minimized;
	// Maps tool call ID → message slot index; populated on ToolCall arrival, consumed on ToolResponse.
	private readonly Dictionary<string, int> _pendingToolCallById = new Dictionary<string, int>(StringComparer.Ordinal);

	// Fired on every Update() or ToggleCollapsed() call. Argument is the affected message.
	public event System.Action<DisplayMessage>? MessageUpdated;

	public IReadOnlyList<DisplayMessage> Messages => _messages;

	public CollapseMode Mode
	{
		get => _mode;
		set
		{
			_mode = value;
			// Re-apply collapse state to every existing message and notify.
			foreach (DisplayMessage msg in _messages)
			{
				msg.Collapsed = ShouldCollapse(msg.Type, _mode);
				MessageUpdated?.Invoke(msg);
			}
		}
	}

	// Creates or overwrites the message at index with the given type and content.
	// New slots get the mode-derived default collapse state.
	// Existing slots preserve the user's current collapsed state when only content changes;
	// if the type changes the mode default is re-applied.
	public void Update(int index, FrameType type, string content)
	{
		bool isNew = _messages.Count <= index;

		while (_messages.Count <= index)
		{
			_messages.Add(new DisplayMessage(_messages.Count, FrameType.Output, ""));
		}

		DisplayMessage msg         = _messages[index];
		bool           typeChanged = msg.Type != type;

		// Strip the call ID prefix ("callId\x01content") from ToolCall frames.
		// For ToolResponse frames, format is "callId\x01exitCode\x01stdout\x01stderr".
		string callId   = string.Empty;
		int    exitCode = 0;
		string stdOut   = string.Empty;
		string stdErr   = string.Empty;

		if (type == FrameType.ToolCall)
		{
			int sep = content.IndexOf('\x01');
			if (sep >= 0)
			{
				callId  = content.Substring(0, sep);
				content = content.Substring(sep + 1);
			}
		}
		else if (type == FrameType.ToolResponse)
		{
			string[] parts = content.Split('\x01');
			if (parts.Length >= 4)
			{
				callId = parts[0];
				int.TryParse(parts[1], out exitCode);
				stdOut = parts[2];
				stdErr = parts[3];
			}
		}

		msg.Type    = type;
		msg.Content = content;

		if (isNew || typeChanged)
		{
			msg.Collapsed = ShouldCollapse(type, _mode);
		}

		// Pair ToolResponse content into the matching ToolCall slot by call ID.
		if (type == FrameType.ToolCall)
		{
			if (isNew)
				msg.ToolCallStartedUtc = DateTime.UtcNow;
			if (!string.IsNullOrEmpty(callId))
				_pendingToolCallById[callId] = index;
		}
		else if (type == FrameType.ToolResponse)
		{
			int callIndex = -1;
			if (!string.IsNullOrEmpty(callId) && _pendingToolCallById.TryGetValue(callId, out int byIdIndex))
			{
				callIndex = byIdIndex;
				_pendingToolCallById.Remove(callId);
			}

			if (callIndex >= 0)
			{
				DisplayMessage callMsg        = _messages[callIndex];
				callMsg.PairedResponseContent = stdOut;
				callMsg.PairedResponseError   = stdErr;
				callMsg.PairedResponseIsError = exitCode != 0;
				if (callMsg.ToolCallStartedUtc != default)
					callMsg.ToolDuration = DateTime.UtcNow - callMsg.ToolCallStartedUtc;
				MessageUpdated?.Invoke(callMsg);
			}
			// Mark this slot as hidden (reuse Debug type which is hidden in all non-verbose modes).
			msg.Type    = FrameType.Debug;
			msg.Content = string.Empty;
		}

		MessageUpdated?.Invoke(msg);
	}

	// Appends a streamed chunk to an existing slot, keeping its type and collapse state.
	public void Append(int index, string chunk)
	{
		DisplayMessage msg = _messages[index];
		msg.Append(chunk);
		MessageUpdated?.Invoke(msg);
	}

	public void ToggleCollapsed(int index)
	{
		if (index < 0 || index >= _messages.Count)
			return;
		DisplayMessage msg = _messages[index];
		// Output (assistant) and User blocks are never shown collapsed regardless of mode, so a click
		// on them is a no-op rather than a state flip the user can't visually unwind.
		if (msg.Type == FrameType.Output || msg.Type == FrameType.User)
			return;
		msg.Collapsed = !msg.Collapsed;
		MessageUpdated?.Invoke(msg);
	}

	// Returns whether a message of the given type should be hidden entirely in the given mode.
	public static bool ShouldHide(FrameType type, CollapseMode mode)
	{
		if (mode == CollapseMode.Verbose)
			return false;

		// Debug is always hidden in non-verbose modes.
		if (type == FrameType.Debug)
			return true;

		// Quiet: hide everything except Output, User, and the error/alert blocks — a
		// human-actionable Alert must never be hidden by a display mode. A command running on the
		// user's own machine always shows too.
		if (mode == CollapseMode.Quiet)
			return type != FrameType.Output && type != FrameType.User && type != FrameType.Error && type != FrameType.Alert && type != FrameType.Host;

		return false;
	}

	private static bool ShouldCollapse(FrameType type, CollapseMode mode)
	{
		if (mode == CollapseMode.Verbose)
			return false;

		// Output, User, and Alerts are never collapsed in any mode — an Alert exists precisely
		// to stay in the user's face until the underlying problem is fixed. Neither is a live host command.
		if (type == FrameType.Output || type == FrameType.User || type == FrameType.Alert || type == FrameType.Host)
			return false;

		// Minimized: Tool/ToolCall/ToolResponse/Thinking/System shown collapsed.
		if (mode == CollapseMode.Minimized)
			return type == FrameType.Thinking || type == FrameType.Tool || type == FrameType.ToolCall || type == FrameType.ToolResponse || type == FrameType.System;

		// Quiet: collapse everything not already hidden.
		return true;
	}
}