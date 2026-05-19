using System.Text.Json.Nodes;

// Deep-cloned snapshot of all protocol states; used for compaction rollback.
public class StateSnapshot
{
    public JsonArray ChatCompletions { get; }
    public JsonArray Responses { get; }
    public JsonObject Anthropic { get; }

    public StateSnapshot(JsonArray chatCompletions, JsonArray responses, JsonObject anthropic)
    {
        ChatCompletions = chatCompletions;
        Responses = responses;
        Anthropic = anthropic;
    }
}
