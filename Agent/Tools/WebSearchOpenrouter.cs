using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


// Web search via the OpenRouter plugin API. The search model (configured under .beast settings) retrieves
// live results through the OpenRouter web plugin, then the WebSearch role digests them down to what the
// caller's goal asks for. Rather than a single bare protocol call, the model is driven through HelperSession
// — the same spine behind fetch_url and read_file — so it gets the multi-turn tool_choice cycling, the
// return_to_caller terminator, the salvage fallback, session-tree visibility, and cost roll-up into the
// caller. The LlmService is built once from the search model at construction time.
public class WebSearchOpenrouter
{
    private readonly LlmService _service;

    // A few turns is plenty: the model normally answers on turn one. The extras drop covers HelperSession's
    // tool_choice cycle (force return_to_caller, force any tool, auto) so a flaky server still lands one form.
    private const int MaxTurns = 4;

    public WebSearchOpenrouter(LlmModel model)
    {
        // The search model is configured separately from the registry, so build its service directly with a
        // fresh availability tracker. The protocol is left Unknown and resolved from the endpoint URL on the
        // first turn, exactly as the previous direct ProtocolProxy use relied on.
        _service = new LlmService(model, DetectedProtocol.Unknown, new ModelAvailability());
    }

    public async Task<ToolResult> SearchWebAsync(
		string toolCallId,
        string query,
        string goal,
        RoleService roleService,
        ITransportServer transport,
        Session parent,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(toolCallId, string.Empty, "Error: Search query cannot be empty.", 1, 0);
        if (string.IsNullOrWhiteSpace(goal))
            return new ToolResult(toolCallId, string.Empty, "Error: Search goal cannot be empty.", 1, 0);

        Role? searchRole = roleService.GetRole("WebSearch");
        if (searchRole == null)
            return new ToolResult(toolCallId, string.Empty, "Error: WebSearch role is not defined.", 1, 0);

        try
        {
            // The OpenRouter web plugin searches off the conversation, so the seed both triggers the search
            // (the query) and steers the digest (the goal). The role has no extra tools — only return_to_caller.
            string seed = $"Search Query: {query}\nGoal: {goal}";

            (bool ok, string answer, int tokens) = await HelperSession.RunAsync(parent, searchRole, _service, $"search_web {query}", seed, MaxTurns, maxOutputTokens, ToolFactory.BuildHelperTools(searchRole.Tools), transport, cancellationToken);
            if (!ok)
                return new ToolResult(toolCallId, string.Empty, "Error: OpenRouter search failed for query: " + query, 1, 0);

            return new ToolResult(toolCallId, string.IsNullOrWhiteSpace(answer) ? "No search results found." : answer, string.Empty, 0, Math.Max(1, tokens));
        }
        catch (OperationCanceledException)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Search request timed out or cancelled for query: " + query, 1, 0);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Network error during search: " + ex.Message, 1, 0);
        }
        catch (Exception ex)
        {
            return new ToolResult(toolCallId, string.Empty, "Error: Search failed: " + ex.Message, 1, 0);
        }
    }
}
