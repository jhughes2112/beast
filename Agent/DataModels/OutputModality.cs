using System;
using System.Collections.Generic;


// What a model produces, as catalogs declare it in architecture.output_modalities. A role states the
// one modality it needs, and only models producing it may serve the role: a chat role cannot run on a
// decision model, and a decision role cannot run on a chat model.
public static class OutputModality
{
	// Generated text: every chat, completion and tool-calling model.
	public const string Text = "text";

	// Generated images or audio, alongside or instead of text.
	public const string Image = "image";
	public const string Audio = "audio";

	// Vectors from an embeddings endpoint; never conversational.
	public const string Embeddings = "embeddings";

	// Probabilities over caller-supplied options, answered on POST /v1/systemone (TypeSafe System One).
	// llama.cpp's convention: a native decision model lists only this.
	public const string Decisions = "decisions";

	// True when a model with these declared outputs produces the modality. An empty list means the
	// catalog said nothing, which has always meant an ordinary text model.
	public static bool Produces(List<string> outputs, string modality)
	{
		bool produces = outputs.Count == 0 && string.Equals(modality, Text, StringComparison.OrdinalIgnoreCase);
		foreach (string output in outputs)
		{
			if (string.Equals(output, modality, StringComparison.OrdinalIgnoreCase))
				produces = true;
		}
		return produces;
	}

	// True for a model that answers only decision requests, so it has no chat endpoint to call or probe.
	public static bool DecisionsOnly(List<string> outputs)
	{
		return Produces(outputs, Decisions) && !Produces(outputs, Text);
	}
}