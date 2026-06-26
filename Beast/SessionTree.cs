using System;
using System.Collections.Generic;

// Tree machinery extracted from BeastApp: parent-child lookups, DFS traversal, subtree
// enumeration, and session-list notification. All methods are pure helpers that operate on the
// session dictionaries passed in — nothing is held here.
internal static class SessionTree
{
	// Returns the parent session ID for a given session ID, or null if it is a root.
	// Parent-child relationship is encoded as "parentId_N" where N is a positive integer.
	public static string? GetParentId(string id)
	{
		int last = id.LastIndexOf('_');
		if (last < 0)
			return null;
		string suffix = id.Substring(last + 1);
		if (!int.TryParse(suffix, out _))
			return null;
		return id.Substring(0, last);
	}

	// Appends this node and its children to result in DFS pre-order. Children are sorted by numeric
	// suffix descending so the most recently spawned agent lands directly under its parent — newest
	// activity stays at the top of the list instead of scrolling off the bottom.
	public static void DfsAdd(
		string id,
		int depth,
		Dictionary<string, List<string>> childrenMap,
		List<(string Id, int Depth)> result)
	{
		result.Add((id, depth));
		if (!childrenMap.TryGetValue(id, out List<string>? children))
			return;
		children.Sort((a, b) =>
		{
			int lastA = a.LastIndexOf('_');
			int lastB = b.LastIndexOf('_');
			int numA = lastA >= 0 && int.TryParse(a.Substring(lastA + 1), out int nA) ? nA : 0;
			int numB = lastB >= 0 && int.TryParse(b.Substring(lastB + 1), out int nB) ? nB : 0;
			return numB.CompareTo(numA);
		});
		foreach (string child in children)
			DfsAdd(child, depth + 1, childrenMap, result);
	}

	// Returns the ID of the root session: the one whose parent is not itself a known session.
	public static string GetRootSessionId(Dictionary<string, BeastApp.SessionState> sessions)
	{
		foreach (string id in sessions.Keys)
		{
			if (string.IsNullOrEmpty(id))
				continue;
			string? parentId = GetParentId(id);
			if (parentId == null || !sessions.ContainsKey(parentId))
				return id;
		}
		return "";
	}

	// Refuse while anything in the subtree is still running.
	public static bool IsSubtreeIdle(string sessionId, HashSet<string> busySessions)
	{
		string subtreePrefix = sessionId + "_";
		foreach (string busyId in busySessions)
		{
			if (string.Equals(busyId, sessionId, StringComparison.Ordinal) || busyId.StartsWith(subtreePrefix, StringComparison.Ordinal))
				return false;
		}
		return true;
	}

	// Identifies all session IDs in the subtree and checks whether the active session is among them.
	public static (bool WasActiveInSubtree, string SubtreePrefix) CollectSubtreeIds(
		string sessionId,
		IEnumerable<string> sessionKeys,
		string activeSessionId)
	{
		string subtreePrefix = sessionId + "_";
		bool wasActiveInSubtree = false;
		foreach (string id in sessionKeys)
		{
			if (string.Equals(id, sessionId, StringComparison.Ordinal) || id.StartsWith(subtreePrefix, StringComparison.Ordinal))
			{
				if (string.Equals(activeSessionId, id, StringComparison.Ordinal))
					wasActiveInSubtree = true;
			}
		}
		return (wasActiveInSubtree, subtreePrefix);
	}

	// Non-root: drop this session and every descendant from client memory now.
	public static void RemoveSessionFromLists(
		string subtreePrefix,
		string sessionId,
		Dictionary<string, BeastApp.SessionState> sessions,
		HashSet<string> busySessions,
		Dictionary<string, string> sessionDisplayNames)
	{
		List<string> toRemove = new List<string>();
		foreach (string id in sessions.Keys)
		{
			if (string.Equals(id, sessionId, StringComparison.Ordinal) || id.StartsWith(subtreePrefix, StringComparison.Ordinal))
				toRemove.Add(id);
		}
		foreach (string id in toRemove)
		{
			sessions.Remove(id);
			busySessions.Remove(id);
			sessionDisplayNames.Remove(id);
		}
	}

	// Pushes the current session list and counts to the display.
	public static void NotifySessionList(
		Dictionary<string, BeastApp.SessionState> sessions,
		HashSet<string> busySessions,
		Dictionary<string, string> sessionDisplayNames,
		string activeSessionId,
		IDisplay display)
	{
		// Build parent→children map from IDs.
		Dictionary<string, List<string>> childrenMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		HashSet<string> hasParent = new HashSet<string>(StringComparer.Ordinal);
		foreach (string id in sessions.Keys)
		{
			if (string.IsNullOrEmpty(id))
				continue;
			string? parentId = GetParentId(id);
			if (parentId != null && sessions.ContainsKey(parentId))
			{
				if (!childrenMap.TryGetValue(parentId, out List<string>? kids))
				{
					kids = new List<string>();
					childrenMap[parentId] = kids;
				}
				kids.Add(id);
				hasParent.Add(id);
			}
		}

		// DFS from roots (sorted by key for deterministic order).
		List<string> roots = new List<string>();
		foreach (string id in sessions.Keys)
		{
			if (!string.IsNullOrEmpty(id) && !hasParent.Contains(id))
				roots.Add(id);
		}
		roots.Sort(StringComparer.Ordinal);

		List<(string Id, int Depth)> ordered = new List<(string, int)>();
		foreach (string root in roots)
			DfsAdd(root, 0, childrenMap, ordered);

		List<SessionDisplayInfo> list = new List<SessionDisplayInfo>();
		foreach ((string id, int depth) in ordered)
		{
			string name = sessionDisplayNames.TryGetValue(id, out string? announced) ? announced : id;
			list.Add(new SessionDisplayInfo(id, name, busySessions.Contains(id), depth));
		}

		display.SetSessionList(list, activeSessionId);
		display.SetSessionCounts(busySessions.Count, sessions.Count);
	}
}
