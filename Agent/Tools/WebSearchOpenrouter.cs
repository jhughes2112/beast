using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


// Web search via the OpenRouter plugin API. The search model (configured under .beast settings) retrieves
// live results through the OpenRouter web plugin and answers in a single step — the retrieval and the digest
// both happen inside that model's own turn. We make one bare call and return exactly what it produced, rather
// than feeding the result through a second LlmService loop: that model has already processed the page content,
// so passing it through another LLM only dilutes the signal. A throwaway child session is still created so the
// turn is visible in the session tree and its cost rolls up into the caller. The service is built once from the
// search model at construction time.
public class WebSearchOpenrouter
{
    private readonly LlmService _service;

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

        BeastSession data = new BeastSession(parent.AllocateChildId(), $"search_web {query}", _service.Model.ConfigId, searchRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session session = new Session(data, searchRole.SystemPrompt, transport, true);

        // The constructor no longer displays the system prompt; a helper session has no other replay path, so
        // emit its (system-only) history now. The seed user message displays when flushed during the run.
        session.ReplayToTransport();

        // The OpenRouter web plugin searches off the conversation, so the seed both triggers the search (the
        // query) and steers the answer (the goal). It is the only message the model sees.
        session.AddUserMessage($"Search Query: {query}\nGoal: {goal}");
        session.AnnounceToClient();
        parent.AddChild(session);

        session.SendBusy();
        try
        {
            // One bare call: no tools, no forced tool choice. The web plugin runs inside the model's turn and
            // the assistant text it returns is the answer we hand straight back to the caller.
            ProtocolResult result = await _service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, maxOutputTokens, transport, cancellationToken);
            if (result.Outcome != ProtocolCallOutcome.Success)
                return new ToolResult(toolCallId, string.Empty, $"Error: OpenRouter search failed for query \"{query}\": {result.ErrorMessage}", 1, 0);

            session.CommitAssistantTurn(result.Payload!);

            string answer = result.Payload!.AssistantText;
            int tokens = session.LastTokenUsage?.CompletionTokens ?? 0;
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
        finally
        {
            // Cost is spent regardless of how the call ended; roll it up into the calling session.
            parent.RecordCost(session.TotalCost);
            session.SendIdle();
        }
    }
}
