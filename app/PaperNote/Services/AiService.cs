using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PaperNote.Services;

// OpenAI-backed text features: meeting-note cleanup and paste-and-rewrite.
public sealed class AiService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    // Fast/cheap model cleans each transcript chunk into a lossless script; a stronger model
    // writes the minutes header and rewrites pasted text. All overridable via environment.
    private readonly string _chunkModel   = Environment.GetEnvironmentVariable("OPENAI_MEETING_CHUNK_MODEL") ?? "gpt-4o-mini";
    private readonly string _finalModel   = Environment.GetEnvironmentVariable("OPENAI_MEETING_FINAL_MODEL") ?? "gpt-4o";
    private readonly string _enhanceModel = Environment.GetEnvironmentVariable("OPENAI_ENHANCE_MODEL")       ?? "gpt-4o";

    // Clean one transcript chunk into a lossless who-said-what script. The editor chunks
    // the transcript and calls this per block so the user sees progress live.
    public Task<string> CleanChunkAsync(string chunk, string apiKey) =>
        CreateResponseAsync(apiKey, _chunkModel, ChunkPrompt, chunk);

    // Second pass: extract the minutes header (TL;DR / decisions / actions) from the full
    // clean script. The script itself is kept by the editor and appended below this header.
    public Task<string> SummarizeAsync(string cleanScript, string apiKey) =>
        CreateResponseAsync(apiKey, _finalModel, FinalPrompt, cleanScript);

    // Rewrite pasted text (email, chat message, rough notes) into a clean, send-ready version.
    public Task<string> EnhanceAsync(string text, string apiKey) =>
        CreateResponseAsync(apiKey, _enhanceModel, EnhancePrompt, text);

    private const string ChunkPrompt = """
        Reconstruct this meeting transcript chunk into a clean, readable script. Keep who said what.

        The input is raw: a Microsoft Teams transcript, WebVTT/VTT, or rough notes. Sentences are
        often split across many short lines or cues, and speaker labels may repeat.

        Rules:
        - Remove ONLY: timestamps, cue ids, WEBVTT/NOTE/STYLE/REGION headers, and exact duplicate lines.
        - Join fragmented lines from the same speaker into complete sentences.
        - Keep the speaker label on each turn, as "Name: what they said". Merge consecutive turns from
          the same speaker into one turn. If the chunk has no speaker labels, keep it as flowing paragraphs.
        - Fix obvious transcription garbles and punctuation only when the meaning is unambiguous.
        - Remove only pure filler words (um, uh, "you know", "like") that carry no meaning.

        Critical: this is a CLEANUP, not a summary. Preserve EVERY point, name, number, date, decision,
        question, and detail. Do NOT summarize, do NOT shorten, do NOT drop or merge distinct sentences.

        Return the cleaned script as Markdown. No headings, no summary — just the script.
        """;

    private const string FinalPrompt = """
            You are given a clean meeting-transcript script. Produce a minutes header that sits ABOVE
            the script. The full script is kept separately and appended automatically after your output,
            so do NOT reproduce the transcript or write a discussion-notes section.

            Return Markdown only, exactly these sections in this order. Omit a section only if it
            genuinely has nothing.

            # Meeting Notes
            [date or title if the script shows one]

            ## TL;DR
            - 3 lines max: what the meeting was about and what came out of it.

            ## Decisions
            - Each decision on one line, with who made or owns it. Quote the specifics (numbers, dates,
              names). Do not generalize.

            ## Action Items
            - [ ] Task — Owner: ... — Due: ...
            (Use "Unassigned" or "TBD" when unclear. One item per real commitment.)

            ## Open Questions
            - Anything raised but left unresolved.

            ## Key Facts
            - Specific numbers, dates, names, systems, amounts, and figures mentioned. One bullet each.
              Be generous here — this is where detail is preserved, so do not drop specifics.

            Rules:
            - Do NOT invent anything. Every item must come from the script.
            - Prefer quoting the specific detail over paraphrasing it away.
            - Do NOT reproduce the transcript — it is appended below your output automatically.
            """;

    private const string EnhancePrompt = """
            You rewrite pasted text — an email, a Teams or chat message, or rough notes — into a
            clean version the user can send as-is. Fix grammar, spelling, and punctuation. Improve
            clarity and flow. Keep the author's meaning and every factual and technical detail.

            Write like a competent engineer explaining something to another professional. The goal
            is clarity, not sounding impressive.

            Rules:
            - Explain directly. State facts plainly. Do not perform.
            - Use words people actually use in real emails and messages. Avoid: furthermore, moreover,
              hence, thus, therefore, subsequently, in order to, with regard to, it should be noted that.
            - Keep technical facts intact. Simplify the language, never the facts.
            - Remove corporate padding ("I hope this finds you well", "please do not hesitate to
              contact me", "I would like to inform you that"). Start with the actual message.
            - One idea per sentence. Vary sentence length for natural rhythm.
            - Order events cause then effect: what happened, why, impact, next action.
            - Never use AI-tell phrases: "it is important to note", "in summary", "in conclusion",
              "going forward", leveraging, utilizing, robust, seamless, comprehensive, cutting-edge,
              revolutionary.
            - Confidence without drama. Stay proportional to reality.
            - Match the input: an email stays an email, a short message stays short. Keep a greeting or
              sign-off only if the input had one, and keep it minimal.
            - Do not add information the author did not provide. Do not invent names, dates, or facts.

            Return only the rewritten text, ready to send. No preamble, no explanation, no surrounding quotes.
            """;

    private async Task<string> CreateResponseAsync(string apiKey, string model, string prompt, string input)
    {
        var payload = new
        {
            model,
            input = new object[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = input }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI request failed ({(int)response.StatusCode}): {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("output_text", out var outputText))
            return outputText.GetString() ?? "";

        var builder = new StringBuilder();
        if (doc.RootElement.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                        builder.Append(text.GetString());
                }
            }
        }

        return builder.ToString();
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500];
}
