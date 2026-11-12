namespace AliceVoice.AppU;

/// <summary>
/// Conversation text. A plain MAUI Label is a WinUI TextBlock with selection
/// disabled, and an Android TextView that does not accept a selection.
/// <see cref="SelectableTranscript"/> turns selection on for this type only,
/// so status lamps and chrome stay as they are.
/// </summary>
public sealed class SelectableLabel : Label
{
}
