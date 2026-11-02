using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;


// The small box in the top-right corner while HallMonitor calibrates a decision model it has not used
// before: which model, and how many of its calibration decisions are left. When the calibration ends it
// shows the outcome for a few seconds, then disappears. It takes no input and never blocks the screen.
internal class CalibrationPopup
{
	// How long the outcome stays up once a calibration ends.
	private const long kOutcomeMs = 15000;

	private string _model = string.Empty;
	private int    _remaining;
	private int    _total;
	private string _state   = string.Empty;
	private string _message = string.Empty;
	private long   _hideAt;

	// Claims a hallmonitor-calibration frame from the agent; declines every other frame.
	public bool OnConfigFrame(string json)
	{
		JsonNode? root    = JsonNode.Parse(json);
		bool      claimed = (root?["kind"]?.GetValue<string>() ?? string.Empty) == "hallmonitor-calibration";
		if (claimed)
		{
			_model     = root?["model"]?.GetValue<string>() ?? string.Empty;
			_remaining = root?["remaining"]?.GetValue<int>() ?? 0;
			_total     = root?["total"]?.GetValue<int>() ?? 0;
			_state     = root?["state"]?.GetValue<string>() ?? string.Empty;
			_message   = root?["message"]?.GetValue<string>() ?? string.Empty;
			_hideAt    = _state == "running" ? long.MaxValue : Environment.TickCount64 + kOutcomeMs;
		}
		return claimed;
	}

	public bool Visible => _state.Length > 0 && Environment.TickCount64 < _hideAt;

	public Screen Build(int screenW)
	{
		int bw     = Math.Min(64, Math.Max(30, screenW - 4));
		int innerW = bw - 4;

		Rgb bg       = new Rgb( 24,  24,  30);
		Rgb borderFg = new Rgb( 90,  90, 110);
		Rgb titleFg  = new Rgb(200, 200, 210);
		Rgb textFg   = new Rgb(160, 160, 165);
		Rgb barFg    = new Rgb(130, 190, 140);
		Rgb warnFg   = new Rgb(206, 178, 108);

		List<(string Text, Rgb Fg)> lines = new List<(string Text, Rgb Fg)>();
		if (_state == "running")
		{
			int done   = Math.Max(0, _total - _remaining);
			int filled = _total > 0 ? innerW * done / _total : 0;
			lines.Add((PopupFrame.Fit($"Calibrating {_model}", innerW), textFg));
			lines.Add((new string('█', filled) + new string('░', innerW - filled), barFg));
			lines.Add((PopupFrame.Fit($"{_remaining} of {_total} decisions left", innerW), textFg));
		}
		else
		{
			foreach (string line in PopupFrame.WrapWords(_message, innerW))
				lines.Add((line, _state == "done" ? textFg : warnFg));
		}

		int    bh = lines.Count + 2;
		Screen s  = new Screen(bw, bh, new Cell(' ', textFg, bg, CellStyle.None));
		PopupFrame.Draw(s, "HallMonitor", borderFg, titleFg, bg);

		for (int i = 0; i < lines.Count; i++)
			AnsiToScreen.WriteLine(s, 2, i + 1, lines[i].Text, lines[i].Fg, bg);
		return s;
	}
}