using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Offline verification implementation of LSP UTF-16 edit application.
/// Deliberately does NOT call DocumentStore or IncrementalTextEditor: otherwise
/// the capture verifier would simply repeat the production editor's bugs.
/// Input consists exclusively of consent-retained, already-applied edits.
/// </summary>
internal static class CaptureEditReplayer
{
    internal static bool TryReplay(
        string initialText, IReadOnlyList<DocumentChange> edits, out string resultingText)
    {
        string current = initialText;
        foreach (DocumentChange edit in edits)
        {
            if (edit.Range is null)
            {
                current = edit.Text;
                continue;
            }

            TextRange range = edit.Range.Value;
            if (!TryOffset(current, range.Start, out int from)
                || !TryOffset(current, range.End, out int to)
                || to < from)
            {
                resultingText = string.Empty;
                return false;
            }

            // .NET string offsets count UTF-16 code units, as LSP does. We
            // intentionally do not convert through Unicode scalar values.
            current = string.Concat(current.AsSpan(0, from),
                edit.Text.AsSpan(), current.AsSpan(to));
        }

        resultingText = current;
        return true;
    }

    /// <summary>
    /// Independently walks line breaks instead of using the production
    /// editor's line-start cache/offset conversion. A range cannot address a
    /// line terminator; it can address any UTF-16 code unit in a line.
    /// </summary>
    private static bool TryOffset(string text, TextPosition position, out int offset)
    {
        offset = 0;
        if (position.Line < 0 || position.Character < 0)
            return false;

        int lineStart = 0;
        for (int line = 0; line < position.Line; line++)
        {
            int newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                return false;
            lineStart = newline + 1;
        }

        int breakOffset = text.IndexOf('\n', lineStart);
        int contentEnd = breakOffset < 0 ? text.Length : breakOffset;
        if (breakOffset >= 0 && contentEnd > lineStart && text[contentEnd - 1] == '\r')
            contentEnd--;

        if (position.Character > contentEnd - lineStart)
            return false;

        offset = lineStart + position.Character;
        return true;
    }
}
