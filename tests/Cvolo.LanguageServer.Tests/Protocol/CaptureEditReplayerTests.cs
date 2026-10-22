using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class CaptureEditReplayerTests
{
    [Fact]
    public void Multi_edit_surrogate_and_crlf_replay_matches_expected_text()
    {
        DocumentChange[] edits =
        [
            new(new TextRange(new TextPosition(1, 2),
                new TextPosition(1, 2)), "!"),
            new(new TextRange(new TextPosition(0, 0),
                new TextPosition(0, 1)), "z"),
        ];
        Assert.True(CaptureEditReplayer.TryReplay("a\r\n😀", edits, out string text));
        Assert.Equal("z\r\n😀!", text);
    }

    [Fact]
    public void Full_text_replace_followed_by_incremental_change()
    {
        DocumentChange[] edits =
        [
            new(null, "line1\nline2"),
            new(new TextRange(new TextPosition(1, 5),
                new TextPosition(1, 5)), "!")
        ];
        Assert.True(CaptureEditReplayer.TryReplay("unused", edits, out string text));
        Assert.Equal("line1\nline2!", text);
    }

    [Fact]
    public void Invalid_range_is_transactionally_rejected()
    {
        DocumentChange[] edits =
        [
            new(new TextRange(new TextPosition(0, 0),
                new TextPosition(0, 0)), "prefix"),
            new(new TextRange(new TextPosition(3, 0),
                new TextPosition(3, 1)), "invalid")
        ];
        Assert.False(CaptureEditReplayer.TryReplay("abc", edits, out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void CrLf_terminator_cannot_be_addressed_by_range()
    {
        DocumentChange[] edits =
        [
            new(new TextRange(new TextPosition(0, 2),
                new TextPosition(0, 2)), "!")
        ];
        Assert.False(CaptureEditReplayer.TryReplay("a\r\nb", edits, out _));
    }
}
