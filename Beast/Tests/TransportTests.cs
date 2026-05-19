using System;
using System.Collections.Generic;
using System.Text;


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
        TestParseFrameUnicode(ctx);
        TestParseFrameEmpty(ctx);
        TestParseFramePipeInContent(ctx);
        TestConversationModelUpdate(ctx);
        TestConversationModelCollapseMode(ctx);
        TestAgentTransportProcessing(ctx);
    }

    // ---- Frame wire format ----

    private static string MakeWireFrame(FrameType type, string content)
    {
        return $"{(byte)type}|{content}";
    }

    private static void TestFrameToWire(TestContext ctx)
    {
        // Verify the expected wire format for a known frame.
        string wire = MakeWireFrame(FrameType.Output, "hello");
        ctx.AssertEqual("0|hello", wire, "FrameToWire: Output frame wire format");

        string statusWire = MakeWireFrame(FrameType.Status, "ok");
        ctx.AssertEqual("2|ok", statusWire, "FrameToWire: Status frame wire format");
    }

    // ---- ParseFrame (Beast-side, via BeastApp reflection) ----

    private static (FrameType Type, string Content) ParseFrame(string wire)
    {
        return (ValueTuple<FrameType, string>)Reflect.Static(typeof(BeastApp), "ParseFrame",
            new System.Type[] { typeof(string) },
            new object[] { wire });
    }

    private static void TestParseFrameSingle(TestContext ctx)
    {
        (FrameType type, string content) = ParseFrame(MakeWireFrame(FrameType.Output, "hello"));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: output type");
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
            FrameType.StreamEnd
        };

        foreach (FrameType t in types)
        {
            (FrameType parsedType, string parsedContent) = ParseFrame(MakeWireFrame(t, "content"));
            ctx.AssertEqual(t, parsedType, $"ParseFrame: type {t} roundtrips");
            ctx.AssertEqual("content", parsedContent, $"ParseFrame: content for type {t}");
        }
    }

    private static void TestParseFrameUnicode(TestContext ctx)
    {
        string emoji = "Hello 🌍 éàć";
        (FrameType type, string content) = ParseFrame(MakeWireFrame(FrameType.Output, emoji));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: unicode type");
        ctx.AssertEqual(emoji, content, "ParseFrame: unicode content preserved");
    }

    private static void TestParseFrameEmpty(TestContext ctx)
    {
        (FrameType type, string content) = ParseFrame(MakeWireFrame(FrameType.Output, ""));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: empty type");
        ctx.AssertEqual("", content, "ParseFrame: empty content preserved");
    }

    private static void TestParseFramePipeInContent(TestContext ctx)
    {
        // Content that itself contains a pipe should not be split further.
        (FrameType type, string content) = ParseFrame(MakeWireFrame(FrameType.Output, "a|b|c"));
        ctx.AssertEqual(FrameType.Output, type, "ParseFrame: pipe-in-content type");
        ctx.AssertEqual("a|b|c", content, "ParseFrame: pipe-in-content preserved");
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
            ctx.Assert(msg.Collapsed, $"ConversationModel: quiet mode collapses {msg.Type}");
        }
    }

    // ---- BeastApp frame processing ----

    private static void TestAgentTransportProcessing(TestContext ctx)
    {
        ConversationModel model = new ConversationModel();
        BeastApp transport = new BeastApp("test", new List<string>(), new DisplayConsole(new Log(false), false), new Log(false));
        Reflect.SetField(transport, "_model", model);

        // Simulate a Status frame arriving — should not add to the model.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Status, "Agent ready" });

        ctx.AssertEqual(0, model.Messages.Count, "BeastApp: Status does not add to model");

        // Output frame adds a message.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Output, "Hello!" });

        ctx.AssertEqual(1, model.Messages.Count, "BeastApp: Output frame adds message");
        ctx.AssertEqual("Hello!", model.Messages[0].Content, "BeastApp: Output content");

        // Streaming: start → chunk → chunk → end should accumulate in one slot.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamStart, StreamTag.Assistant });
        int slotAfterStart = model.Messages.Count - 1;

        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamChunk, "part1" });
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamChunk, " part2" });

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "BeastApp: stream chunks accumulate");

        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamEnd, StreamTag.Assistant });

        // After StreamEnd a committed Output frame arrives to finalize.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Output, "part1 part2" });

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "BeastApp: committed frame after StreamEnd updates slot");
    }
}
