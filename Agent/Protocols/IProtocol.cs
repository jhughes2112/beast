using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Encapsulates a single LLM wire format (ChatCompletions, Responses, Anthropic, etc.).
// A protocol knows how to build a request, send it, parse the response, and write the
// assistant turn into the session's native state for its own protocol while fanning the
// semantic event out to the other protocol listeners. IProvider holds a protocol and
// delegates HTTP work to it; extra_headers and extra_payload from LlmModel.Extras are
// passed through verbatim so no protocol-specific knowledge is needed in providers or
// in configuration.
public interface IProtocol
{
	// Executes one round-trip using this wire format.
  // bundle carries the protocol-native listeners; the protocol reads its own listener state
    // to build the request, then writes the assistant turn (and any tool results from prior
    // turns) into that same native state and fans out to peers.
	// extraHeaders are added to the HTTP request; extraPayload entries are merged into the
	// top-level JSON body. Values are JsonNode so nested objects (e.g. provider blocks) work.
	// transport receives streaming deltas (content and thinking) as they arrive.
  Task<ProtocolResult> ExecuteAsync(LlmModel model, IProtocolListener bundle, List<ToolDefinition> tools, int maxCompletionTokens,
										Dictionary<string, string> extraHeaders, Dictionary<string, JsonNode?> extraPayload, ITransportServer transport, CancellationToken cancellationToken);
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
