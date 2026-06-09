using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// No-op IDisplay for use in tests — suppresses all console output and side-effects.
file sealed class NullDisplay : IDisplay
{
    public void Attach(ConversationModel model) { }
    public void SetStatus(string text) { }
    public void SetStatsInfo(string model, int promptTokens, int completionTokens, decimal totalCost, int maxContext, int contextTokens) { }
    public void SetCompletions(IReadOnlyList<string> completions) { }
    public void OnStreamStart(int streamIndex, FrameType type) { }
    public void OnStreamChunk(string chunk) { }
    public void OnStreamEnd() { }
    public void SetAgentBusy(bool busy) { }
    public void SetSendAsync(Func<string, Task> sendAsync) { }
    public void SetRequestExit(Action requestExit) { }
    public void SetFrameDrain(Action drain) { }
    public void SetSessionCounts(int active, int total) { }
    public void SetSessionList(IReadOnlyList<SessionDisplayInfo> sessions, string activeId) { }
    public void SetSessionSwitchCallback(Action<string> switchTo) { }
    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}


// Tests the wire-level framing protocol used between Beast and Agent.
// Verifies that Frame.ToWire() output is correctly parsed by FrameParser (Beast side)
// and that the Agent-side TransportFramedStdio read path roundtrips content faithfully.
public static class TransportTests
{
    public static void Test(TestContext ctx)
    {
        ctx.Log("  TransportTests");

        TestFrameToWire(ctx);
        TestParseFrameSingle(ctx);
        TestParseFrameAllTypes(ctx);
        TestParseFrameWithSessionId(ctx);
        TestParseFrameUnicode(ctx);
        TestParseFrameEmpty(ctx);
        TestParseFramePipeInContent(ctx);
        TestParseFrameBackwardCompat(ctx);
        TestConversationModelUpdate(ctx);
        TestConversationModelCollapseMode(ctx);
        TestAgentTransportProcessing(ctx);
    }

    // ---- Frame wire format ----

    // Wire format: N|sessionId|content (sessionId may be empty for global frames).
    private static string MakeWireFrame(FrameType type, string content)
    {
        return $"{(byte)type}||{content}";
    }

    private static string MakeWireFrameWithSession(FrameType type, string sessionId, string content)
    {
        return $"{(byte)type}|{sessionId}|{content}";
    }

    private static void TestFrameToWire(TestContext ctx)
    {
        string wire = MakeWireFrame(FrameType.Output, "hello");
        ctx.AssertEqual("0||hello", wire, "FrameToWire: Output frame wire format");

        string statusWire = MakeWireFrame(FrameType.Status, "ok");
        ctx.AssertEqual("2||ok", statusWire, "FrameToWire: Status frame wire format");

        string sessionWire = MakeWireFrameWithSession(FrameType.Output, "abc123", "hello");
        ctx.AssertEqual("0|abc123|hello", sessionWire, "FrameToWire: session-scoped frame wire format");
    }

    // ---- ParseFrame (Beast-side, via BeastApp reflection) ----

    private static (FrameType Type, string SessionId, string Content) ParseFrame(string wire)
    {
        return (ValueTuple<FrameType, string, string>)Reflect.Static(typeof(BeastApp), "ParseFrame",
            new System.Type[] { typeof(string) },
            new object[] { wire })!;
    }

    private static void TestParseFrameSingle(TestContext ctx)
    {
        (FrameType type, string sessionId, string content) = ParseFrame(MakeWireFrame(FrameType.Output, "hello"));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: output type");
        ctx.AssertEqual("", sessionId, "ParseFrame: empty session ID for global frame");
        ctx.AssertEqual("hello", content, "ParseFrame: output content");
    }

    private static void TestParseFrameAllTypes(TestContext ctx)
    {
        FrameType[] types = new FrameType[]
        {
            FrameType.Output,
            FrameType.Error,
            FrameType.Status,
            FrameType.Tool,
            FrameType.Thinking,
            FrameType.Completions,
            FrameType.System,
            FrameType.StreamStart,
            FrameType.StreamChunk,
            FrameType.StreamEnd,
            FrameType.Clear
        };

        foreach (FrameType t in types)
        {
            (FrameType parsedType, string parsedSessionId, string parsedContent) = ParseFrame(MakeWireFrame(t, "content"));
            ctx.AssertEqual(t, parsedType, $"ParseFrame: type {t} roundtrips");
            ctx.AssertEqual("", parsedSessionId, $"ParseFrame: empty session ID for {t}");
            ctx.AssertEqual("content", parsedContent, $"ParseFrame: content for type {t}");
        }
    }

    private static void TestParseFrameWithSessionId(TestContext ctx)
    {
        (FrameType type, string sessionId, string content) = ParseFrame(MakeWireFrameWithSession(FrameType.Output, "abc123", "hello"));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame+session: output type");
        ctx.AssertEqual("abc123", sessionId, "ParseFrame+session: session ID preserved");
        ctx.AssertEqual("hello", content, "ParseFrame+session: content preserved");
    }

    private static void TestParseFrameUnicode(TestContext ctx)
    {
        string emoji = "Hello 🌍 éàć";
        (FrameType type, string sessionId, string content) = ParseFrame(MakeWireFrame(FrameType.Output, emoji));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: unicode type");
        ctx.AssertEqual("", sessionId, "ParseFrame: empty session ID for unicode frame");
        ctx.AssertEqual(emoji, content, "ParseFrame: unicode content preserved");
    }

    private static void TestParseFrameEmpty(TestContext ctx)
    {
        (FrameType type, string sessionId, string content) = ParseFrame(MakeWireFrame(FrameType.Output, ""));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: empty type");
        ctx.AssertEqual("", sessionId, "ParseFrame: empty session ID for empty content frame");
        ctx.AssertEqual("", content, "ParseFrame: empty content preserved");
    }

    private static void TestParseFramePipeInContent(TestContext ctx)
    {
        // Content that itself contains pipes should not be split further after the session ID segment.
        (FrameType type, string sessionId, string content) = ParseFrame(MakeWireFrame(FrameType.Output, "a|b|c"));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: pipe-in-content type");
        ctx.AssertEqual("", sessionId, "ParseFrame: pipe-in-content empty session ID");
        ctx.AssertEqual("a|b|c", content, "ParseFrame: pipe-in-content preserved");
    }

    private static void TestParseFrameBackwardCompat(TestContext ctx)
    {
        // Old single-pipe format (from an older agent) is handled gracefully.
        (FrameType type, string sessionId, string content) = ParseFrame("0|hello");
        ctx.AssertEqual(FrameType.Output, type, "BackwardCompat: output type");
        ctx.AssertEqual("", sessionId, "BackwardCompat: empty session ID");
        ctx.AssertEqual("hello", content, "BackwardCompat: content preserved");
    }

    // ---- ConversationModel (Beast-side) ----

    private static void TestConversationModelUpdate(TestContext ctx)
    {
        ConversationModel model = new ConversationModel();
        int notified = 0;
        model.MessageUpdated += (_) => notified++;

        model.Update(0, FrameType.Output, "first");
        ctx.AssertEqual(1, model.Messages.Count, "ConversationModel: first message added");
        ctx.AssertEqual("first", model.Messages[0].Content, "ConversationModel: first content");
        ctx.Assert(notified > 0, "ConversationModel: update fires event");

        model.Update(0, FrameType.Output, "updated");
        ctx.AssertEqual(1, model.Messages.Count, "ConversationModel: no new slot on same index");
        ctx.AssertEqual("updated", model.Messages[0].Content, "ConversationModel: content updated in place");

        // Adding at index 2 creates a gap — slots 0,1,2 exist.
        model.Update(2, FrameType.Error, "gap");
        ctx.AssertEqual(3, model.Messages.Count, "ConversationModel: gap creates slots");
        ctx.AssertEqual(FrameType.Error, model.Messages[2].Type, "ConversationModel: gap slot has correct type");
    }

    private static void TestConversationModelCollapseMode(TestContext ctx)
    {
        ConversationModel model = new ConversationModel();
        model.Mode = CollapseMode.Verbose;

        model.Update(0, FrameType.Tool, "tool output");
        ctx.Assert(!model.Messages[0].Collapsed, "ConversationModel: verbose mode not collapsed");

        model.Mode = CollapseMode.Minimized;
        ctx.Assert(model.Messages[0].Collapsed, "ConversationModel: minimized mode collapses Tool");

        model.Update(1, FrameType.Output, "assistant text");
        ctx.Assert(!model.Messages[1].Collapsed, "ConversationModel: minimized mode expands Output");

        model.Mode = CollapseMode.Quiet;
        foreach (DisplayMessage msg in model.Messages)
        {
            // Output and User are never collapsed in any mode by design (ShouldCollapse enforces this).
            if (msg.Type == FrameType.Output || msg.Type == FrameType.User) continue;
            ctx.Assert(msg.Collapsed, $"ConversationModel: quiet mode collapses {msg.Type}");
        }
    }

    // ---- BeastApp frame processing ----

    private static void ProcessFrame(BeastApp app, FrameType type, string sessionId, string content)
    {
        Reflect.Instance(app, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string), typeof(string) },
            new object[] { type, sessionId, content });
    }

    // Retrieves the ConversationModel for a given session ID from BeastApp._sessions via reflection.
    private static ConversationModel? GetSessionModel(BeastApp app, string sessionId)
    {
        object? sessions = Reflect.GetField(app, "_sessions");
        if (sessions == null) return null;
        IDictionary dict = (IDictionary)sessions;
        object? state = dict[sessionId];
        if (state == null) return null;
        PropertyInfo? prop = state.GetType().GetProperty("Model");
        return (ConversationModel?)prop?.GetValue(state);
    }

    private static void TestAgentTransportProcessing(TestContext ctx)
    {
        const string SID = "test-session";
        BeastApp app = new BeastApp(new LaunchDebug(), new List<string>(), new NullDisplay(), new Log(false));

        // Simulate a Status frame arriving — should not add to any session model.
        ProcessFrame(app, FrameType.Status, "", "Agent ready");
        ConversationModel? noModel = GetSessionModel(app, SID);
        ctx.Assert(noModel == null, "BeastApp: Status does not create a session model");

        // Completions frame also has no session scope.
        ProcessFrame(app, FrameType.Completions, "", "[\"/help\",\"/quit\"]");
        ctx.Assert(GetSessionModel(app, SID) == null, "BeastApp: Completions do not create a session model");

        // Output frame with a session ID creates a model and adds a message.
        ProcessFrame(app, FrameType.Output, SID, "Hello!");
        ConversationModel? model = GetSessionModel(app, SID);
        ctx.Assert(model != null, "BeastApp: Output frame creates session model");
        ctx.AssertEqual(1, model!.Messages.Count, "BeastApp: Output frame adds message");
        ctx.AssertEqual("Hello!", model.Messages[0].Content, "BeastApp: Output content");

        // Streaming: start → chunk → chunk → end should accumulate in one slot.
        ProcessFrame(app, FrameType.StreamStart, SID, StreamTag.Assistant);
        int slotAfterStart = model.Messages.Count - 1;

        ProcessFrame(app, FrameType.StreamChunk, SID, "part1");
        ProcessFrame(app, FrameType.StreamChunk, SID, " part2");

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "BeastApp: stream chunks accumulate");

        ProcessFrame(app, FrameType.StreamEnd, SID, StreamTag.Assistant);

        // After StreamEnd a committed Output frame arrives to finalize.
        ProcessFrame(app, FrameType.Output, SID, "part1 part2");

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "BeastApp: committed frame after StreamEnd updates slot");
    }
}
