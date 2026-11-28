using System.Text.Json.Serialization;


// The one piece of long-running work a session is waiting on: a Developer it assigned work to, or a
// Reviewer it asked for a review. Starting the work returns at once and opens this; the delegate's
// terminator fills it, and the session is told to collect the report with collect_report, which empties
// it. Persisted with the session so a restart knows the delegate still owes this session its report.
public sealed class Mailbox
{
	// The delegate session working on it. Follows the delegate through its compactions.
	[JsonPropertyName("childId")]
	public string ChildId { get; }

	// The delegate's display name, which the notice and the refusals use.
	[JsonPropertyName("name")]
	public string Name { get; }

	// True once the delegate has reported; Success, Report and Tokens are its reply.
	[JsonPropertyName("delivered")]
	public bool Delivered { get; }

	[JsonPropertyName("success")]
	public bool Success { get; }

	[JsonPropertyName("report")]
	public string Report { get; }

	[JsonPropertyName("tokens")]
	public int Tokens { get; }

	[JsonConstructor]
	public Mailbox(string childId, string name, bool delivered, bool success, string report, int tokens)
	{
		ChildId   = childId ?? string.Empty;
		Name      = name ?? string.Empty;
		Delivered = delivered;
		Success   = success;
		Report    = report ?? string.Empty;
		Tokens    = tokens;
	}
}