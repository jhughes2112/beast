using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Encapsulates a single LLM wire format (ChatCompletions, Responses, Anthropic, etc.).
// A protocol knows how to build a request, send it, and parse the response for one API shape.
// IProvider holds a protocol and delegates HTTP work to it; extra_headers and extra_payload
// from LlmModel.Extras are passed through verbatim so no protocol-specific knowledge is needed
// in providers or in configuration.
public interface IProtocol
{
	// Executes one round-trip using this wire format.
	// extraHeaders are added to the HTTP request; extraPayload entries are merged into the
	// top-level JSON body. Values are JsonNode so nested objects (e.g. provider blocks) work.
	// stream, if non-null, receives each text delta as it arrives for streaming display.
	// Passing null disables streaming entirely regardless of server support.
	Task<ProviderCallResult> ExecuteAsync(LlmModel model, List<ConversationMessage> messages, List<ToolDefinition> tools, int maxCompletionTokens,
										Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, IStreamingMessage? stream, CancellationToken cancellationToken);
}

// Result of a protocol probe against a single endpoint.
public enum ProbeOutcome
{
    Supported,    // The endpoint speaks this protocol.
    NotSupported, // The endpoint returned a definitive 404 or wrong-shaped body — not this protocol.
    Unreachable   // The probe could not connect at all (network error, timeout).
}

public class ProbeResult
{
    public ProbeOutcome Outcome { get; }
    public string       Detail  { get; }

    private ProbeResult(ProbeOutcome outcome, string detail)
    {
        Outcome = outcome;
        Detail = detail;
    }

    public static ProbeResult Supported()                    => new ProbeResult(ProbeOutcome.Supported, "");
    public static ProbeResult NotSupported(string detail)    => new ProbeResult(ProbeOutcome.NotSupported, detail);
    public static ProbeResult Unreachable(string detail)     => new ProbeResult(ProbeOutcome.Unreachable, detail);
}
