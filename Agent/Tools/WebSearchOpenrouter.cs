using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


// Web search via the OpenRouter plugin API. The search model (configured under .beast settings) retrieves
// live results through the OpenRouter web plugin and answers in a single step — the retrieval and the digest
// both happen inside that model's own turn. We make one bare call and return exactly what it produced, rather
// than feeding the result through a second LlmService loop: that model has already processed the page content,
// so passing it through another LLM only dilutes the signal. A child session is created so the turn is visible
// in the session tree and its cost rolls up into the caller; it is persisted on reload unless ephemeral.
public class WebSearchOpenrouter
{
    private readonly LlmModel _model;

    // Shared across every search so rate-limit / down state set by one call is seen by the next, exactly as
    // LlmRegistry shares one ModelAvailability across its per-session services.
    private readonly ModelAvailability _availability;

    public WebSearchOpenrouter(LlmModel model)
    {
        _model = model;
        _availability = new ModelAvailability();
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

        // A fresh LlmService per call, so each search gets its own ProtocolProxy and starts from a clean
        // protocol message list. The proxy caches its protocol instance for the life of the service and only
        // rehydrates from canonical when that instance is null; reusing one service across calls would leak the
        // previous search's messages into the next. This mirrors LlmRegistry, which builds a fresh service per
        // session for the same reason. Availability is shared so rate-limit state still carries across calls.
        // A single-model list: there is only the configured search model, so the service never falls back.
        LlmService service = new LlmService(_model, DetectedProtocol.Unknown, _availability, new List<string> { _model.ConfigId });

        // Allocate the child id and immediately persist the parent so its bumped ChildCounter reaches disk
        // before this (non-ephemeral) search writes its own file. Without it a reload restores the old counter
        // and the next child reissues this id, overwriting the file. Root parent updates lastSession; a
        // subagent parent does not. Skipped for an ephemeral parent, whose children are never saved anyway.
        string childId = parent.AllocateChildId();
        if (!parent.Ephemeral)
            SessionService.Save(parent.Data, !parent.IsSubagent);

        BeastSession data = new BeastSession(childId, $"search_web {query}", _model.ConfigId, searchRole.Name, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, parent.Ephemeral, 0);
        Session session = new Session(data, searchRole.SystemPrompt, transport, true);

        // The constructor no longer displays the system prompt; a helper session has no other replay path, so
        // emit its (system-only) history now. The seed user message displays when flushed during the run.
        session.ReplayToTransport();

        // The OpenRouter web plugin searches off the conversation, so the seed both triggers the search (the
        // query) and steers the answer (the goal). It is the only message the model sees.
        session.AddUserMessage($"Search Query: {query}\nGoal: {goal}");
        session.AnnounceToClient();
        parent.AddChild(session);

        // This search's own cancellation scope, linked to the caller's token: a /cancel on the caller cascades
        // down and stops the search, while a /cancel on the search alone leaves the caller running.
        using CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.SetDispatchScope(scope);

        session.SendBusy();
        try
        {
            // One bare call: no tools, no forced tool choice. The web plugin runs inside the model's turn and
            // the assistant text it returns is the answer we hand straight back to the caller.
            ProtocolResult result = await service.RunToCompletionAsync(session, Array.Empty<Tool>(), null, 0, maxOutputTokens, transport, scope.Token);
            if (result.Outcome != ProtocolCallOutcome.Success)
            {
                // A /cancel returns "cancelled by the user" so the caller is unblocked; anything else is a failure.
                if (scope.IsCancellationRequested)
                    return new ToolResult(toolCallId, "Cancelled by the user.", string.Empty, 0, 1);
                return new ToolResult(toolCallId, string.Empty, $"Error: OpenRouter search failed for query \"{query}\": {result.ErrorMessage}", 1, 0);
            }

            session.CommitAssistantTurn(result.Payload!);

            string answer = result.Payload!.AssistantText;
            int tokens = session.LastTokenUsage?.CompletionTokens ?? 0;
            return new ToolResult(toolCallId, string.IsNullOrWhiteSpace(answer) ? "No search results found." : answer, string.Empty, 0, Math.Max(1, tokens));
        }
        catch (OperationCanceledException)
        {
            // A /cancel returns "cancelled by the user" so the caller is unblocked; a client-side timeout (the
            // caller's token is untouched) is a real failure.
            if (scope.IsCancellationRequested)
                return new ToolResult(toolCallId, "Cancelled by the user.", string.Empty, 0, 1);
            return new ToolResult(toolCallId, string.Empty, "Error: Search request timed out for query: " + query, 1, 0);
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
            session.SetDispatchScope(null);

            // Cost is spent regardless of how the call ended; roll it up into the calling session.
            parent.RecordCost(session.TotalCost);

            // Persist the search session unless it inherited an ephemeral parent (a no-worktree root), so a
            // non-ephemeral search survives a reload and stays in the session tree like any other child session.
            if (!session.Ephemeral)
                SessionService.Save(session.Data, false);

            session.SendIdle();
        }
    }
}
