namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The one-line text prompt (a new name): a blank answer is no answer, and an answer is trimmed.
/// Closing the dialog answers nothing too, so a caller only ever sees a non-empty name.
/// </summary>
public static class TextPrompt
{
    /// <summary>Whether the text is an answer: something other than whitespace.</summary>
    public static bool HasAnswer(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>The answer the prompt returns, or null when it was blank (or the dialog was closed).</summary>
    public static string? Answer(string? text) => HasAnswer(text) ? text!.Trim() : null;
}
