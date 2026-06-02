using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Result of a protocol probe against a single endpoint.
public enum ProbeOutcome
{
    Supported,    // The endpoint speaks this protocol.
    NotSupported, // The endpoint returned a definitive 404 or wrong-shaped body — not this protocol.
    Unreachable   // The probe could not connect at all (network error, timeout).
}

// Reports running token counts and the protocol-computed running cost for the current
// (still in-flight) assistant turn while a response streams. These numbers are provisional:
// LlmService uses them only to push live stats frames, and discards them at commit in favor
// of the authoritative payload usage and cost. The protocol owns the cost basis so the live
// display and the committed total are computed identically.
public delegate void LiveUsageProgress(int inputTokens, int outputTokens, decimal turnCost);

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
