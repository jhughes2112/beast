using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

// Decides which media an outgoing request carries.
//
// Media is sent until the model has ANSWERED it — produced a turn with text or thinking after it —
// and is then replaced by a note (Messages and ChatCompletions; the Responses protocol keeps its
// history server-side and leaves it alone). Keeping answered media costs twice over: it is re-sent
// on every later request, so a long visual session grows until the provider refuses the whole thing,
// and it only gets dearer to remove, because dropping an item invalidates the prompt cache for
// everything after it — one short reply right after the model has used it, a whole run of work
// later on. A turn that is nothing but tool calls is not an answer: a model inspecting several
// images in a row before saying anything still sees all of them together. Every item is a file on
// disk, and the model is told it can inspect it again.
//
// The rule is stateless — "an answer comes after it" — so a live turn, a rehydrated session, and a
// retry of a refused request all come out the same.
//
// What remains is sent as-is until the provider refuses it. Limits vary by provider and none are
// published in a catalog (OpenRouter caps image content at 30MB; some models refuse more than 4
// images), so they are learned: a 4xx that talks about images or media means the request carried too
// much, and the OLDEST unanswered item is dropped and the request re-sent — or, when the provider
// states its cap, straight down to that, which is also persisted as a fact about the model. The
// learned cap holds for the rest of this instance, so later requests keep the newest items within it
// instead of paying a refusal for each new one.
//
// Everything here runs on the per-request clone, never on native state.
public class MediaBudget
{
	private const string OmittedNote =
		"[This media was left out of the request to stay within the provider's limits. "
		+ "If you need it, look at the file again with inspect_media.]";

	private const string AnsweredNote =
		"[Media removed from context after you responded to it. If you need it again, call inspect_media on its file.]";

	// Most media items one request may carry, learned by this instance from a refusal; 0 = none.
	// Kept alongside the config value because the sink that persists a stated cap is absent in tests,
	// and a cap derived by dropping items one at a time is not persisted at all — it may reflect a
	// byte limit on this route rather than a count limit of the model.
	private int _learnedMaxItems;

	// What the last trimmed request actually carried, so a refusal can be judged against it.
	private int _itemsSent;
	private int _omitted;
	private int _omittedReported;

	// Removes answered media from an outgoing message array — a per-request clone — when elideAnswered
	// is set, then keeps only the newest remaining items within the cap, replacing each removed part
	// with a text part of the protocol's text type ("text" or "input_text"). configMaxImages is the
	// model's declared cap; 0 means none is known.
	public void Apply(JsonArray messages, int configMaxImages, string textPartType, bool elideAnswered)
	{
		// Everything before the last turn with real output has been answered.
		int answered = 0;
		if (elideAnswered)
		{
			for (int i = messages.Count - 1; i >= 0; i--)
			{
				if (messages[i] is JsonObject item && IsAnswer(item))
				{
					answered = i;
					break;
				}
			}
		}

		List<(JsonArray Parent, JsonObject Part)> found = new List<(JsonArray Parent, JsonObject Part)>();
		for (int i = 0; i < messages.Count; i++)
		{
			if (i < answered)
			{
				List<(JsonArray Parent, JsonObject Part)> old = new List<(JsonArray Parent, JsonObject Part)>();
				Collect(messages[i], old);
				foreach ((JsonArray parent, JsonObject part) in old)
					parent[parent.IndexOf(part)] = new JsonObject { ["type"] = textPartType, ["text"] = AnsweredNote };
			}
			else
			{
				Collect(messages[i], found);
			}
		}

		// Newest first; once one is dropped every older one goes too, so the model sees an unbroken
		// recent window rather than a scattering of old items it cannot place.
		int maxItems = EffectiveMaxItems(configMaxImages);
		int kept     = 0;
		int omitted  = 0;
		for (int i = found.Count - 1; i >= 0; i--)
		{
			if (maxItems == 0 || kept < maxItems)
			{
				kept++;
			}
			else
			{
				(JsonArray parent, JsonObject part) = found[i];
				parent[parent.IndexOf(part)]        = new JsonObject { ["type"] = textPartType, ["text"] = OmittedNote };
				omitted++;
			}
		}

		_itemsSent = kept;
		_omitted   = omitted;
	}

	// A log line when the trim changed since it was last reported, else null — one note each time the
	// window moves rather than one per request.
	public string? TakeNote()
	{
		string? note = null;
		if (_omitted != _omittedReported)
		{
			note             = $"[media] {_omitted} older media item(s) left out of this request to stay within the provider's limits (sending {_itemsSent})";
			_omittedReported = _omitted;
		}
		return note;
	}

	// Classifies a rejected request. When the provider refused the MEDIA, tightens the cap and says the
	// request should be re-sent. Every retry carries strictly fewer items than the refused one and never
	// fewer than one, so a server that keeps refusing ends on the ordinary failure path instead of
	// looping.
	public bool LearnFromRefusal(LlmModel model, int statusCode, string errorBody, ModelCapabilitySink? sink, SessionLogger logger)
	{
		bool retry = false;
		if (_itemsSent > 0 && IsMediaRefusal(statusCode, errorBody))
		{
			int stated  = ImageCountLimit(errorBody);
			int allowed = stated > 0 ? Math.Min(stated, _itemsSent - 1) : _itemsSent - 1;
			if (allowed > 0)
			{
				_learnedMaxItems = allowed;
				retry            = true;
				logger.Write(model.Config.Name, model.Endpoint, $"[media] provider refused a request carrying {_itemsSent} media item(s); re-sending with the newest {allowed}");

				// A number the provider stated is a fact about the model worth keeping; a cap reached by
				// dropping items one at a time may only reflect this request's bytes.
				if (stated > 0)
					sink?.Invoke(model.ConfigId, null, null, stated);
			}
		}
		return retry;
	}

	private int EffectiveMaxItems(int configMaxImages)
	{
		int limit = configMaxImages;
		if (_learnedMaxItems > 0 && (limit == 0 || _learnedMaxItems < limit))
			limit = _learnedMaxItems;
		return limit;
	}

	// A client error about the request's images or media: a 413 (payload too large), or any other 4xx
	// that names them — "Too many images in request: > 4", "Downloaded image content cannot exceed
	// 30MB". A model that cannot take the input at all ("No endpoints found that support image input")
	// is excluded: dropping images one at a time would only strip the conversation to fit a model that
	// was the wrong choice, which is the human's call.
	private static bool IsMediaRefusal(int statusCode, string errorBody)
	{
		string lower     = errorBody.ToLowerInvariant();
		bool   clientErr = statusCode >= 400 && statusCode < 500 && statusCode != 429;
		bool   namesIt   = statusCode == 413 || lower.Contains("image") || lower.Contains("media");
		bool   support   = lower.Contains("support");
		return clientErr && namesIt && !support;
	}

	// The cap an image-count refusal states, or 0 when it states none. Wordings seen: "Too many images
	// in request: > 4" and vLLM's "At most 4 image(s) may be provided in one request".
	private static int ImageCountLimit(string errorBody)
	{
		string lower  = errorBody.ToLowerInvariant();
		int    result = 0;

		int tooMany = lower.IndexOf("too many image", StringComparison.Ordinal);
		if (tooMany >= 0)
		{
			result = FirstIntegerWithin(lower, tooMany, 60);
		}
		else
		{
			int atMost = lower.IndexOf("at most ", StringComparison.Ordinal);
			if (atMost >= 0 && lower.IndexOf("image", atMost, Math.Min(40, lower.Length - atMost), StringComparison.Ordinal) >= 0)
				result = FirstIntegerWithin(lower, atMost, 20);
		}
		return result;
	}

	// The first run of digits in text[start, start+window), or 0 when there is none. Stops at a quote
	// so the scan never leaves the message string and reads a neighboring field ("code":400) instead.
	private static int FirstIntegerWithin(string text, int start, int window)
	{
		int end   = Math.Min(text.Length, start + window);
		int quote = text.IndexOf('"', start, end - start);
		if (quote >= 0)
			end = quote;

		int digits = start;
		while (digits < end && !char.IsDigit(text[digits]))
			digits++;

		int stop = digits;
		while (stop < end && char.IsDigit(text[stop]))
			stop++;

		int value = 0;
		if (stop > digits)
			int.TryParse(text.AsSpan(digits, stop - digits), out value);
		return value;
	}

	// A model turn with real output — text or thinking — as opposed to one that was only tool calls.
	// Messages: text / thinking / redacted_thinking blocks. ChatCompletions: non-empty content, or a
	// replayed reasoning field (present only for models configured to retain it — otherwise the
	// thinking is gone from the wire, and the media is the model's only record of what it saw).
	private static bool IsAnswer(JsonObject item)
	{
		bool assistant = item["role"] is JsonValue role && role.TryGetValue<string>(out string? r) && r == "assistant";
		bool output    = false;
		if (assistant)
		{
			JsonNode? content = item["content"];
			if (content is JsonValue text && text.TryGetValue<string>(out string? s))
				output = s.Length > 0;
			else if (content is JsonArray blocks)
				output = HasOutputBlock(blocks);

			output = output || item["reasoning_content"] != null || item["reasoning"] != null || item["reasoning_details"] != null;
		}
		return output;
	}

	private static bool HasOutputBlock(JsonArray blocks)
	{
		bool output = false;
		foreach (JsonNode? block in blocks)
		{
			if (block is not JsonObject obj)
				continue;

			string type = PartType(obj);
			if (type == "thinking" || type == "redacted_thinking")
				output = true;
			else if (type == "text" && obj["text"] is JsonValue t && t.TryGetValue<string>(out string? s) && s.Length > 0)
				output = true;
		}
		return output;
	}

	// Media parts across the three wire formats: ChatCompletions image_url / input_audio / video_url,
	// Anthropic image (including inside a tool_result's content), Responses input_image.
	private static bool IsMediaPart(JsonObject part)
	{
		string type = PartType(part);
		return type == "image_url" || type == "image" || type == "input_image" || type == "input_audio" || type == "video_url";
	}

	// The part's "type", or empty when it has none. Tolerant of a non-string value, since the walk
	// visits every object in the message tree, tool inputs included.
	private static string PartType(JsonObject part)
	{
		string type = string.Empty;
		if (part["type"] is JsonValue value && value.TryGetValue<string>(out string? s))
			type = s;
		return type;
	}

	// Every media part in document order, with the array holding it so it can be replaced in place.
	private static void Collect(JsonNode? node, List<(JsonArray Parent, JsonObject Part)> found)
	{
		if (node is JsonArray array)
		{
			foreach (JsonNode? child in array)
			{
				if (child is JsonObject obj && IsMediaPart(obj))
					found.Add((array, obj));
				else
					Collect(child, found);
			}
		}
		else if (node is JsonObject obj)
		{
			// A tool call's arguments are the model's own JSON, not message content: an argument that
			// happens to say "type":"image" must never be mistaken for media and rewritten.
			string type = PartType(obj);
			if (type != "tool_use" && type != "function_call")
			{
				foreach ((string _, JsonNode? child) in obj)
					Collect(child, found);
			}
		}
	}
}