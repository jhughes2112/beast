using System;
using System.Collections.Generic;


// What the console title says, which sessions count as running, and what each F10 row shows and offers.
public static class TitleBarTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  TitleBarTests");
		TestTitle(ctx);
		TestEngaged(ctx);
		TestWaitingOrder(ctx);
		TestPlayAction(ctx);
		TestRows(ctx);
		TestSeparatorAnimation(ctx);
	}

	// The busy label draws the whole animation frame, every cell of it, and the count only adds to the end.
	private static void TestSeparatorAnimation(TestContext ctx)
	{
		Screen sep   = SeparatorLayer.Build(80, true, Environment.TickCount64, 0, 0, "Developer", 1, 1, 3);
		string label = string.Empty;
		for (int x = 0; x < 40; x++)
		{
			label += sep.Get(x, 0).Ch;
		}
		ctx.Assert(label.StartsWith(" ●∙∙∙ ", StringComparison.Ordinal), $"Separator: a four-cell animation draws all four cells ({label})");
		ctx.AssertContains(label, "(3 processes)", "Separator: the process count follows the timer");

		// What reaches the terminal between two frames: each cell of the new frame placed at its own column.
		long                      now    = Environment.TickCount64;
		Screen                    first  = SeparatorLayer.Build(80, true,       now, 0, 0, "Developer", 1, 1, 0);
		Screen                    second = SeparatorLayer.Build(80, true, now - 130, 0, 0, "Developer", 1, 1, 0);
		System.Text.StringBuilder buf    = new System.Text.StringBuilder();
		ScreenAnsiWriter.WriteDiff(buf, second, first, 1, 1);
		ctx.AssertContains(buf.ToString(), "∙\x1b[3G●\x1b[4G∙\x1b[5G∙", "Separator: the next frame reaches the terminal cell by cell, at its own columns");
	}

	private static void TestTitle(TestContext ctx)
	{
		ctx.Assert(ConsoleChrome.BuildTitle("work", false, false, 0).Contains(" ─ work", StringComparison.Ordinal), "Title: idle shows the plain separator");
		ctx.Assert(ConsoleChrome.BuildTitle("work", false, true, 0).Contains(" !! work", StringComparison.Ordinal), "Title: nothing running and an approval waiting shows !!");
		ctx.Assert(ConsoleChrome.BuildTitle("", false, true, 0).EndsWith(" !!", StringComparison.Ordinal), "Title: with no name too");

		string first  = ConsoleChrome.BuildTitle("work", true, false,   0);
		string second = ConsoleChrome.BuildTitle("work", true, false, 120);
		ctx.Assert(first != second, "Title: busy animates over time");

		string mixed = ConsoleChrome.BuildTitle("work", true, true, 0);
		ctx.Assert(mixed != ConsoleChrome.BuildTitle("work", true, true, 120), "Title: work elsewhere keeps animating past a waiting approval");
		ctx.Assert(           !mixed.Contains("!!", StringComparison.Ordinal), "Title: !! only once nothing is running");
	}

	private static void TestEngaged(TestContext ctx)
	{
		HashSet<string> none = new HashSet<string>(StringComparer.Ordinal);

		ctx.Assert(!SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", false, false, false) }, none), "Engaged: not when everything is idle");
		ctx.Assert(SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", false, false, false), Info("b", true, false, false) }, none), "Engaged: any session counts, not just the one on screen");
		ctx.Assert(SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", true, true, false) }, none), "Engaged: a paused session still finishing its turn");

		ctx.Assert(!SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", false, true, true) }, none), "Engaged: not a parent waiting only on a stopped paused child");
		ctx.Assert(SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", false, true, true), Info("a_2", true, false, false) }, none), "Engaged: a working sibling still counts");
		ctx.Assert(SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", true, false, false), Info("ab_1", false, true, true) }, none), "Engaged: another tree's paused child does not hold this one");
		ctx.Assert(SessionTree.AnyEngaged(new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", false, true, false) }, none), "Engaged: a paused child with nothing left to send holds nothing");

		HashSet<string>          childWaits = new HashSet<string>(StringComparer.Ordinal) { "a_1" };
		List<SessionDisplayInfo> stuck      = new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", true, false, false), Info("b", false, false, false) };
		ctx.Assert(!SessionTree.AnyEngaged(stuck, childWaits), "Engaged: not when the asker and the parent waiting on it are all that is busy");

		List<SessionDisplayInfo> otherWorks = new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", true, false, false), Info("b", true, false, false) };
		ctx.Assert(               SessionTree.AnyEngaged(otherWorks, childWaits), "Engaged: a session elsewhere is still working");
		ctx.Assert(!SessionTree.IsRunning(otherWorks[0], otherWorks, childWaits), "Running: not the parent of the session asking");
		ctx.Assert( SessionTree.IsRunning(otherWorks[2], otherWorks, childWaits), "Running: the unrelated session is");

		List<SessionDisplayInfo> siblingWorks = new List<SessionDisplayInfo> { Info("a", true, false, false), Info("a_1", true, false, false), Info("a_2", true, false, false) };
		ctx.Assert(SessionTree.IsRunning(siblingWorks[0], siblingWorks, childWaits), "Running: a parent with another child still at work");
	}

	private static void TestWaitingOrder(TestContext ctx)
	{
		List<ChoicePopup> choices = new List<ChoicePopup>
		{
			Popup(string.Empty),
			Popup("b_1"),
			Popup("a"),
			Popup("b_1")
		};
		List<string> waiting = ChoicePopup.WaitingSessions(choices);
		ctx.AssertEqual(    2, waiting.Count, "Waiting: each session once, and only ones that asked");
		ctx.AssertEqual("b_1",    waiting[0], "Waiting: the first is the one whose question comes up first");
	}

	private static void TestPlayAction(TestContext ctx)
	{
		ctx.AssertEqual(PlayAction.Pause,  SessionTreeLayer.ActionFor(Info("a", true, false, false)), "F9: a working session pauses");
		ctx.AssertEqual( PlayAction.Play,   SessionTreeLayer.ActionFor(Info("a", true, true, false)), "F9: one finishing its turn under a pause plays");
		ctx.AssertEqual( PlayAction.Play,   SessionTreeLayer.ActionFor(Info("a", false, true, true)), "F9: a paused session with a turn to send plays");
		ctx.AssertEqual( PlayAction.Play,  SessionTreeLayer.ActionFor(Info("a", false, false, true)), "F9: so does one parked by Esc or a failure");
		ctx.AssertEqual( PlayAction.None, SessionTreeLayer.ActionFor(Info("a", false, false, false)), "F9: nothing for a finished session");
		ctx.AssertEqual( PlayAction.None,  SessionTreeLayer.ActionFor(Info("a", false, true, false)), "F9: nor a paused one that has nothing left to send");
	}

	private static void TestRows(TestContext ctx)
	{
		SessionDisplayInfo       running  = Info("run",  true, false, false);
		SessionDisplayInfo       waiter   = Info("ask",  true, false, false);
		SessionDisplayInfo       paused   = Info("pau", false,  true,  true);
		SessionDisplayInfo       finished = new SessionDisplayInfo("fin",   "fin", false, 0, SessionStatus.Ongoing, false, false,      SessionActivity.Idle, 0);
		SessionDisplayInfo       success  = new SessionDisplayInfo("suc",   "suc", false, 0, SessionStatus.Success, false, false,      SessionActivity.Idle, 0);
		SessionDisplayInfo       nested   = new SessionDisplayInfo("run_1", "kid",  true, 1, SessionStatus.Ongoing, false, false,   SessionActivity.Waiting, 0);
		SessionDisplayInfo       talking  = new SessionDisplayInfo("rcv",   "rcv",  true, 0, SessionStatus.Ongoing, false, false, SessionActivity.Receiving, 0);
		SessionDisplayInfo       jobs     = new SessionDisplayInfo("job",   "job", false, 0, SessionStatus.Ongoing, false, false,   SessionActivity.Working, 3);
		List<SessionDisplayInfo> rows     = new List<SessionDisplayInfo> { running, waiter, paused, finished, success, nested, talking, jobs };

		Screen panel = Build(rows, 0);
		ctx.AssertEqual('▶', panel.Get(1, 1).Ch, "Row: running shows the play indicator first");
		ctx.AssertEqual('!', panel.Get(1, 2).Ch, "Row: waiting shows !");
		ctx.Assert(panel.Get(1, 2).Fg!.Value.R > 200 && panel.Get(1, 2).Fg!.Value.G > 200 && panel.Get(1, 2).Fg!.Value.B < 100, "Row: and in bright yellow");
		ctx.AssertEqual(' ', panel.Get(1, 3).Ch, "Row: paused and stopped shows no indicator");
		ctx.AssertEqual(' ', panel.Get(1, 4).Ch, "Row: finished shows no indicator");
		ctx.AssertEqual('p', panel.Get(3, 3).Ch, "Row: the name follows the indicator column");
		ctx.AssertEqual(' ', panel.Get(1, 6).Ch, "Row: depth indents first");
		ctx.AssertEqual('▶', panel.Get(3, 6).Ch, "Row: and the indicator comes after it");
		ctx.AssertEqual('↓', panel.Get(1, 7).Ch, "Row: receiving from its model shows a down arrow");
		ctx.AssertEqual('@', panel.Get(1, 8).Ch, "Row: processes running show @, even with the agent loop idle");

		ctx.Assert(!Same(panel.Get(3, 3).Fg, panel.Get(3, 4).Fg), "Row: a session Play could continue and a finished one differ in color");
		ctx.Assert(!Same(panel.Get(3, 5).Fg, panel.Get(3, 3).Fg), "Row: a delivered result differs from one Play could continue");

		// The button acts on the whole tree, whichever row is selected: Pause while anything works, Play while
		// anything is paused or has a turn to send, nothing once all of it is finished.
		ctx.Assert(ButtonText(Build(rows, 0)).Contains("Pause all", StringComparison.Ordinal), "Button: Pause all while anything is working");
		ctx.Assert(ButtonText(Build(rows, 3)).Contains("Pause all", StringComparison.Ordinal), "Button: whichever row is selected");
		List<SessionDisplayInfo> stopped = new List<SessionDisplayInfo> { paused, finished, success };
		ctx.Assert(ButtonText(Build(stopped, 1)).Contains("Play all", StringComparison.Ordinal), "Button: Play all once nothing works and something can carry on");
		ctx.AssertEqual(string.Empty, ButtonText(Build(new List<SessionDisplayInfo> { finished, success }, 0)).Trim(), "Button: hidden once everything is finished");
		ctx.AssertEqual(PlayAction.Pause, SessionTreeLayer.TreeAction(rows), "F9: the tree pauses while anything works");
	}

	private static Screen Build(List<SessionDisplayInfo> rows, int selected)
	{
		HashSet<string> marked  = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string> waiting = new HashSet<string>(StringComparer.Ordinal) { "ask" };
		return SessionTreeLayer.Build(rows, selected, 0, 40, 12, "none", marked, waiting);
	}

	private static string ButtonText(Screen panel)
	{
		string text = string.Empty;
		for (int x = 1; x < panel.W; x++)
		{
			text += panel.Get(x, panel.H - 1).Ch;
		}

		return text;
	}

	private static bool Same(Rgb? a, Rgb? b)
	{
		return a!.Value.R == b!.Value.R && a.Value.G == b.Value.G && a.Value.B == b.Value.B;
	}

	private static ChoicePopup Popup(string sessionId)
	{
		return new ChoicePopup(string.Empty, sessionId, "t", new Rgb(1, 1, 1), new List<ChoiceLine>(), new List<string> { "&Ok" }, 0, 0, 0, _ => { });
	}

	private static SessionDisplayInfo Info(string id, bool busy, bool paused, bool canContinue)
	{
		return new SessionDisplayInfo(id, id, busy, 0, SessionStatus.Ongoing, paused, canContinue, busy ? SessionActivity.Waiting : SessionActivity.Idle, 0);
	}
}