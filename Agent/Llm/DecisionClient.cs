using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// One question for a decision model, sent as a System One "choice" question.
public class DecisionQuestion
{
	public string Instructions { get; }

	// Option name → what it means. The model returns a probability for each.
	public Dictionary<string, DecisionOption> Options { get; }

	public DecisionQuestion(string instructions, Dictionary<string, DecisionOption> options)
	{
		Instructions = instructions;
		Options      = options;
	}
}

public class DecisionOption
{
	public string Description { get; }

	// True when choosing this option flags the text (for HallMonitor: a reason to block the command). A
	// question's chance of flagging is the summed probability of its flag options.
	public bool Flag { get; }

	public DecisionOption(string description, bool flag)
	{
		Description = description;
		Flag        = flag;
	}
}

// One TypeSafe System One call (POST /v1/systemone) to a decision model: a state plus choice
// questions in, a probability per option out. No conversation, tools or streaming, so it bypasses
// the chat protocols entirely.
public static class DecisionClient
{
	// The request body: every question asked as a "choice" over its options, in one forward pass.
	public static JsonObject BuildRequest(string modelId, string state, Dictionary<string, DecisionQuestion> questions)
	{
		JsonObject asked = new JsonObject();
		foreach ((string key, DecisionQuestion question) in questions)
		{
			JsonObject criteria = new JsonObject();
			foreach ((string option, DecisionOption meaning) in question.Options)
				criteria[option] = meaning.Description;

			asked[key] = new JsonObject
			{
				["type"]         = "choice",
				["instructions"] = question.Instructions,
				["criteria"]     = criteria
			};
		}

		return new JsonObject
		{
			["model"]     = modelId,
			["state"]     = state,
			["questions"] = asked
		};
	}

	// Asks the model and returns the response's "answers" object, or null and what went wrong. The
	// model's request timeout bounds the call; the caller's token cancels it.
	public static async Task<(JsonObject? Answers, string Error)> AskAsync(LlmModel model, string state, Dictionary<string, DecisionQuestion> questions, CancellationToken ct)
	{
		JsonObject? answers = null;
		string      error   = string.Empty;

		using CancellationTokenSource timeout = ProtocolHelpers.CreateRequestTimeout(model, ct);
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
			req.Content                  = new StringContent(BuildRequest(model.Config.Id, state, questions).ToJsonString(), Encoding.UTF8, "application/json");
			if (!string.IsNullOrEmpty(model.ApiKey))
				req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {model.ApiKey}");
			foreach (JsonObject headers in model.Headers)
			{
				foreach ((string name, JsonNode? value) in headers)
				{
					string? text = value?.ToString();
					if (!string.IsNullOrEmpty(text))
						req.Headers.TryAddWithoutValidation(name, text);
				}
			}

			using HttpResponseMessage response = await ProtocolHelpers.GetClient().SendAsync(req, timeout.Token);
			string body                        = await response.Content.ReadAsStringAsync(timeout.Token);
			if (!response.IsSuccessStatusCode)
			{
				error = $"HTTP {(int)response.StatusCode}: {Clip(body)}";
			}
			else
			{
				answers = JsonNode.Parse(body)?["answers"] as JsonObject;
				if (answers == null)
					error = $"no answers in the response: {Clip(body)}";
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			error = $"no response within {ProtocolHelpers.RequestTimeoutSeconds(model)}s";
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}
		return (answers, error);
	}

	private static string Clip(string text)
	{
		return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
	}
}