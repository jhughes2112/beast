using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;


// The /role editor: sets which models a role reaches for, and in what order. A role's model list is
// a preference chain — the first entry that is available and fits gets the turn — so the ORDER is
// the whole configuration, and this screen exists to arrange it.
//
// ←/→ switch roles, ↑/↓ pick a model, +/- move it up or down one place, Esc saves and closes. Each
// role's order is sent the moment you leave it, so arrowing away never loses an edit.
internal class RoleOverlay
{
	private class RoleEntry
	{
		public string Name = string.Empty;
		public string Kind = string.Empty;
		public List<(string Id, string Label, bool Available)> Models = new();
		// Set once the order is changed, so an untouched visit writes nothing.
		public bool Dirty;
	}

	private readonly List<RoleEntry> _roles = new();
	private          int             _roleIndex;
	private          int             _modelIndex;
	private          int             _scroll;
	private          bool            _open;
	private          string          _status = string.Empty;

	private readonly Action<string> _sendCommand;

	public RoleOverlay(Action<string> sendCommand)
	{
		_sendCommand = sendCommand;
	}

	public bool IsOpen => _open;

	public void Open()
	{
		_open = true;
		_roles.Clear();
		_roleIndex  = 0;
		_modelIndex = 0;
		_scroll     = 0;
		_status     = "Loading roles…";
		_sendCommand("/config-roles");
	}

	// Saves whatever is pending before the screen goes away.
	public void Close()
	{
		SaveCurrent();
		_open = false;
	}

	// Consumes a roles payload; returns false for any other Config frame so the caller can route it
	// to the /config picker instead.
	public bool OnConfigFrame(string json)
	{
		if (!_open)
			return false;

		try
		{
			JsonNode? root = JsonNode.Parse(json);
			if ((root?["kind"]?.GetValue<string>() ?? string.Empty) != "roles")
				return false;

			_roles.Clear();
			string active = root?["active"]?.GetValue<string>() ?? string.Empty;

			JsonArray? list = root?["roles"] as JsonArray;
			if (list != null)
			{
				foreach (JsonNode? entry in list)
				{
					if (entry == null)
						continue;

					RoleEntry role = new RoleEntry
					{
						Name = entry["name"]?.GetValue<string>() ?? string.Empty,
						Kind = entry["kind"]?.GetValue<string>() ?? string.Empty
					};
					JsonArray? models = entry["models"] as JsonArray;
					if (models != null)
					{
						foreach (JsonNode? model in models)
						{
							if (model == null)
								continue;
							role.Models.Add((
								model["id"]?.GetValue<string>() ?? string.Empty,
								model["label"]?.GetValue<string>() ?? string.Empty,
								model["available"]?.GetValue<bool>() ?? true));
						}
					}
					_roles.Add(role);
				}
			}

			// Open on the role the session is actually running: that is the one the user came to fix.
			for (int i = 0; i < _roles.Count; i++)
			{
				if (string.Equals(_roles[i].Name, active, StringComparison.OrdinalIgnoreCase))
					_roleIndex = i;
			}
			_modelIndex = 0;
			_scroll     = 0;
			_status     = string.Empty;
		}
		catch (Exception ex)
		{
			_status = $"Bad roles payload: {ex.Message}";
		}
		return true;
	}

	public bool HandleKey(ConsoleKeyInfo key)
	{
		if (_roles.Count == 0)
		{
			if (key.Key == ConsoleKey.Escape)
				_open = false;
			return true;
		}

		RoleEntry role = _roles[_roleIndex];

		if (key.Key == ConsoleKey.Escape)
		{
			Close();
		}
		else if (key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.RightArrow)
		{
			// Leaving a role commits it, so an edit is never lost by arrowing away.
			SaveCurrent();
			int step    = key.Key == ConsoleKey.RightArrow ? 1 : -1;
			_roleIndex  = (_roleIndex + step + _roles.Count) % _roles.Count;
			_modelIndex = 0;
			_scroll     = 0;
			_status     = string.Empty;
		}
		else if (key.Key == ConsoleKey.UpArrow)
		{
			if (_modelIndex > 0)
				_modelIndex--;
		}
		else if (key.Key == ConsoleKey.DownArrow)
		{
			if (_modelIndex < role.Models.Count - 1)
				_modelIndex++;
		}
		else if (key.KeyChar == '+' || key.KeyChar == '=' || key.Key == ConsoleKey.Add || key.Key == ConsoleKey.OemPlus)
		{
			// '=' too: it is the unshifted key most people actually hit reaching for '+'.
			MoveSelected(-1);
		}
		else if (key.KeyChar == '-' || key.KeyChar == '_' || key.Key == ConsoleKey.Subtract || key.Key == ConsoleKey.OemMinus)
		{
			MoveSelected(1);
		}
		return true;
	}

	// Moves the highlighted model one place, carrying the selection with it so repeated presses
	// keep pushing the same entry.
	private void MoveSelected(int step)
	{
		RoleEntry role   = _roles[_roleIndex];
		int       target = _modelIndex + step;
		if (_modelIndex < 0 || _modelIndex >= role.Models.Count || target < 0 || target >= role.Models.Count)
			return;

		(string Id, string Label, bool Available) moved = role.Models[_modelIndex];
		role.Models[_modelIndex]                        = role.Models[target];
		role.Models[target] = moved;
		_modelIndex         = target;
		role.Dirty          = true;
	}

	private void SaveCurrent()
	{
		if (_roles.Count == 0)
			return;

		RoleEntry role = _roles[_roleIndex];
		if (!role.Dirty)
			return;

		JsonArray models = new JsonArray();
		foreach ((string id, string label, bool available) in role.Models)
			models.Add((JsonNode)id!);

		JsonObject payload = new JsonObject { ["role"] = role.Name, ["models"] = models };
		_sendCommand("/config-role-apply " + payload.ToJsonString());
		role.Dirty = false;
	}

	public Screen Build(int w, int h)
	{
		int bw = Math.Min(Math.Max(60, w - 8), 100);
		int bh = Math.Min(Math.Max(12, h - 4),  32);

		Rgb bg        = new Rgb( 24,  24,  30);
		Rgb borderFg  = new Rgb( 90,  90, 110);
		Rgb titleFg   = new Rgb(200, 200, 210);
		Rgb textFg    = new Rgb(160, 160, 165);
		Rgb dimFg     = new Rgb(105, 105, 110);
		Rgb selBg     = new Rgb( 52,  52,  66);
		Rgb topFg     = new Rgb(130, 190, 140);
		Rgb missingFg = new Rgb(206, 178, 108);
		Rgb statusFg  = new Rgb(206, 178, 108);

		Screen s = new Screen(bw, bh, new Cell(' ', textFg, bg, CellStyle.None));

		for (int x = 0; x < bw; x++)
		{
			s.Set(x,      0, new Cell('─', borderFg, bg, CellStyle.None));
			s.Set(x, bh - 1, new Cell('─', borderFg, bg, CellStyle.None));
		}
		for (int y = 0; y < bh; y++)
		{
			s.Set(     0, y, new Cell('│', borderFg, bg, CellStyle.None));
			s.Set(bw - 1, y, new Cell('│', borderFg, bg, CellStyle.None));
		}
		s.Set(     0,      0, new Cell('┌', borderFg, bg, CellStyle.None));
		s.Set(bw - 1,      0, new Cell('┐', borderFg, bg, CellStyle.None));
		s.Set(     0, bh - 1, new Cell('└', borderFg, bg, CellStyle.None));
		s.Set(bw - 1, bh - 1, new Cell('┘', borderFg, bg, CellStyle.None));
		AnsiToScreen.WriteLine(s, 2, 0, " Role Models ", titleFg, bg);

		int innerW = bw - 4;
		if (_roles.Count == 0)
		{
			AnsiToScreen.WriteLine(s, 2, bh / 2, Truncate(_status.Length > 0 ? _status : "No roles.", innerW), statusFg, bg);
			return s;
		}

		RoleEntry role = _roles[_roleIndex];

		// The role name is the headline: it is what the whole screen is about, and the arrows that
		// change it are only discoverable if the name looks like a control.
		string header = $"◄  {role.Name}  ►";
		AnsiToScreen.WriteLine(s, 2, 1, Truncate(header, innerW), topFg, bg);
		AnsiToScreen.WriteLine(s, 2 + header.Length + 2, 1, Truncate($"{role.Kind.ToLowerInvariant()} · role {_roleIndex + 1}/{_roles.Count}", Math.Max(0, innerW - header.Length - 2)), dimFg, bg);
		AnsiToScreen.WriteLine(s, 2, 2, Truncate("↑↓ pick · +/- move up or down · ←→ role · Esc save and close", innerW), dimFg, bg);

		if (role.Models.Count == 0)
		{
			AnsiToScreen.WriteLine(s, 2, 4, Truncate("This role has no models. Enable some in /config.", innerW), statusFg, bg);
			return s;
		}

		int visRows = bh - 6;
		if (_modelIndex < _scroll)
			_scroll = _modelIndex;
		if (_modelIndex >= _scroll + visRows)
			_scroll = _modelIndex - visRows + 1;

		// Size the id column to the widest id actually present rather than to the panel: padding
		// every row out to a fixed share of the width left a canyon between the ids and their
		// costs, and squeezed the modality tags into ellipses. Bounded so one absurd id cannot
		// push the costs off the edge.
		int idWidth = 0;
		foreach ((string id, string label, bool available) in role.Models)
		{
			if (id.Length > idWidth)
				idWidth = id.Length;
		}
		int idCeiling = Math.Max(12, innerW - 36);
		if (idWidth > idCeiling)
			idWidth = idCeiling;
		if (idWidth < 12)
			idWidth = 12;

		for (int r = 0; r < visRows; r++)
		{
			int idx = _scroll + r;
			if (idx >= role.Models.Count)
				break;

			(string id, string label, bool available) = role.Models[idx];
			bool sel   = idx == _modelIndex;
			Rgb  rowBg = sel ? selBg : bg;
			// First choice is the one that actually runs unless it is unavailable or too small.
			Rgb rowFg = !available ? missingFg : idx == 0 ? topFg : textFg;
			s.Fill(new Rect(1, r + 4, bw - 2, 1), new Cell(' ', rowFg, rowBg, CellStyle.None));

			string note = available ? label : "not currently available";
			string line = $"{idx + 1,2}. {Truncate(id, idWidth).PadRight(idWidth)}  {note}";
			AnsiToScreen.WriteLine(s, 2, r + 4, Truncate(line, innerW), rowFg, rowBg);
		}

		if (_status.Length > 0)
			AnsiToScreen.WriteLine(s, 2, bh - 2, Truncate(_status, innerW), statusFg, bg);

		return s;
	}

	private static string Truncate(string text, int max)
	{
		if (max <= 0)
			return string.Empty;
		if (text.Length <= max)
			return text;
		return max > 1 ? text.Substring(0, max - 1) + "…" : text.Substring(0, max);
	}
}