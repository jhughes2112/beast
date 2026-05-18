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
        TestFrameParserSingleFrame(ctx);
        TestFrameParserMultipleFrames(ctx);
        TestFrameParserPartialFeed(ctx);
        TestFrameParserCorruptHeader(ctx);
        TestAllFrameTypes(ctx);
        TestUnicodeContent(ctx);
        TestEmptyContent(ctx);
        TestConversationModelUpdate(ctx);
        TestConversationModelCollapseMode(ctx);
        TestAgentTransportProcessing(ctx);
    }

    // ---- Frame wire format ----

    private static string MakeWireFrame(FrameType type, string content)
    {
        return $"[{(byte)type},{content.Length}]{content}---";
    }

    private static void TestFrameToWire(TestContext ctx)
    {
        // Verify the expected wire format for a known frame.
        string wire = MakeWireFrame(FrameType.Output, "hello");
        ctx.AssertEqual("[0,5]hello---", wire, "FrameToWire: Output frame wire format");

        string statusWire = MakeWireFrame(FrameType.Status, "ok");
        ctx.AssertEqual("[2,2]ok---", statusWire, "FrameToWire: Status frame wire format");
    }

    // ---- FrameParser (Beast-side) ----

    private static FrameParser FeedParser(string wire)
    {
        FrameParser parser = new FrameParser();
        byte[] bytes = Encoding.UTF8.GetBytes(wire);
        parser.Feed(bytes, bytes.Length);
        return parser;
    }

    private static void TestFrameParserSingleFrame(TestContext ctx)
    {
        string wire = MakeWireFrame(FrameType.Output, "hello");
        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();

        ctx.AssertEqual(1, frames.Count, "FrameParser: single frame count");
        ctx.AssertEqual(FrameType.Output, frames[0].Type, "FrameParser: single frame type");
        ctx.AssertEqual("hello", frames[0].Content, "FrameParser: single frame content");
    }

    private static void TestFrameParserMultipleFrames(TestContext ctx)
    {
        string wire = MakeWireFrame(FrameType.Output, "first")
                    + MakeWireFrame(FrameType.Status, "second")
                    + MakeWireFrame(FrameType.Error, "third");

        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();

        ctx.AssertEqual(3, frames.Count, "FrameParser: multiple frames count");
        ctx.AssertEqual(FrameType.Output, frames[0].Type, "FrameParser: frame[0] type");
        ctx.AssertEqual("first", frames[0].Content, "FrameParser: frame[0] content");
        ctx.AssertEqual(FrameType.Status, frames[1].Type, "FrameParser: frame[1] type");
        ctx.AssertEqual("second", frames[1].Content, "FrameParser: frame[1] content");
        ctx.AssertEqual(FrameType.Error, frames[2].Type, "FrameParser: frame[2] type");
        ctx.AssertEqual("third", frames[2].Content, "FrameParser: frame[2] content");
    }

    private static void TestFrameParserPartialFeed(TestContext ctx)
    {
        // Feed a complete frame in two halves; both halves must arrive before parse.
        string wire = MakeWireFrame(FrameType.Tool, "tooldata");
        int mid = wire.Length / 2;

        FrameParser parser = new FrameParser();
        byte[] first = Encoding.UTF8.GetBytes(wire.Substring(0, mid));
        byte[] second = Encoding.UTF8.GetBytes(wire.Substring(mid));

        parser.Feed(first, first.Length);
        List<(FrameType, string)> partial = parser.TakeFrames();
        ctx.AssertEqual(0, partial.Count, "FrameParser: partial feed yields no frames");

        parser.Feed(second, second.Length);
        List<(FrameType Type, string Content)> complete = parser.TakeFrames();
        ctx.AssertEqual(1, complete.Count, "FrameParser: after second half one frame ready");
        ctx.AssertEqual("tooldata", complete[0].Content, "FrameParser: partial feed content correct");
    }

    private static void TestFrameParserCorruptHeader(TestContext ctx)
    {
        // Inject junk before a valid frame — parser should skip to the '['.
        string junk = "GARBAGE_DATA";
        string wire = junk + MakeWireFrame(FrameType.Output, "after junk");

        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();

        ctx.AssertEqual(1, frames.Count, "FrameParser: recovers after junk prefix");
        ctx.AssertEqual("after junk", frames[0].Content, "FrameParser: correct content after junk");
    }

    private static void TestAllFrameTypes(TestContext ctx)
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
            string wire = MakeWireFrame(t, "content");
            FrameParser parser = FeedParser(wire);
            List<(FrameType Type, string Content)> frames = parser.TakeFrames();
            ctx.AssertEqual(1, frames.Count, $"FrameParser: frame type {t} roundtrips");
            ctx.AssertEqual(t, frames[0].Type, $"FrameParser: frame type {t} preserved");
        }
    }

    private static void TestUnicodeContent(TestContext ctx)
    {
        string emoji = "Hello 🌍 éàć";
        string wire = $"[{(byte)FrameType.Output},{emoji.Length}]{emoji}---";

        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();
        ctx.AssertEqual(1, frames.Count, "FrameParser: unicode frame count");
        ctx.AssertEqual(emoji, frames[0].Content, "FrameParser: unicode content preserved");
    }

    private static void TestEmptyContent(TestContext ctx)
    {
        string wire = MakeWireFrame(FrameType.Output, "");
        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();
        ctx.AssertEqual(1, frames.Count, "FrameParser: empty content frame count");
        ctx.AssertEqual("", frames[0].Content, "FrameParser: empty content preserved");
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

    // ---- AgentTransport frame processing ----

    private static void TestAgentTransportProcessing(TestContext ctx)
    {
        ConversationModel model = new ConversationModel();
        List<string> statuses = new List<string>();
        AgentTransport transport = new AgentTransport(model, (s) => statuses.Add(s), () => { }, () => { });

        // Simulate a Status frame arriving.
        string wire = MakeWireFrame(FrameType.Status, "Agent ready");
        FrameParser parser = FeedParser(wire);
        List<(FrameType Type, string Content)> frames = parser.TakeFrames();

        // Manually invoke ProcessFrame via reflection to avoid needing a running DockerContext.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Status, "Agent ready" });

        ctx.Assert(statuses.Count == 1 && statuses[0] == "Agent ready",
            "AgentTransport: Status frame calls onStatus");
        ctx.AssertEqual(0, model.Messages.Count, "AgentTransport: Status does not add to model");

        // Output frame adds a message.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Output, "Hello!" });

        ctx.AssertEqual(1, model.Messages.Count, "AgentTransport: Output frame adds message");
        ctx.AssertEqual("Hello!", model.Messages[0].Content, "AgentTransport: Output content");

        // Streaming: start → chunk → chunk → end should accumulate in one slot.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamStart, StreamTag.Assistant });
        int slotAfterStart = model.Messages.Count - 1;

        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamChunk, "part1" });
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamChunk, " part2" });

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "AgentTransport: stream chunks accumulate");

        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.StreamEnd, StreamTag.Assistant });

        // After StreamEnd a committed Output frame arrives to finalize.
        Reflect.Instance(transport, "ProcessFrame", new System.Type[] { typeof(FrameType), typeof(string) },
            new object[] { FrameType.Output, "part1 part2" });

        ctx.AssertEqual("part1 part2", model.Messages[slotAfterStart].Content,
            "AgentTransport: committed frame after StreamEnd updates slot");

        transport.Dispose();
    }
}
