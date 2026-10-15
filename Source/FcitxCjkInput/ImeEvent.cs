using System;
using System.Collections.Generic;
using System.Text;

namespace FcitxCjkInput;

/// <summary>A message queued by the native fcitx5 bridge.</summary>
internal abstract record ImeEvent {
    internal const char Separator = '\u001f';

    internal static ImeEvent? Parse(string line) =>
        line.Split(Separator) switch {
            ["commit", var text] => new Commit(text),
            ["preedit", var cursor, var text] => new Preedit(text, CharIndex(text, int.Parse(cursor))),
            ["forward", var codepoint, var keysym, var release] =>
                new Forward(int.Parse(codepoint), uint.Parse(keysym), release == "1"),
            ["panel", var index, var header, .. var candidates] => new Panel(header, candidates, int.Parse(index)),
            ["focusout"] => new FocusOut(),
            _ => null,
        };

    /// <summary>Converts fcitx5's UTF-8 byte cursor to a UTF-16 index; a negative cursor means the end.</summary>
    internal static int CharIndex(string text, int bytes) {
        if (bytes < 0) {
            return text.Length;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        return Encoding.UTF8.GetCharCount(utf8, 0, Math.Min(bytes, utf8.Length));
    }
}

internal sealed record Commit(string Text) : ImeEvent;

internal sealed record Preedit(string Text, int Cursor) : ImeEvent;

internal sealed record Forward(int Codepoint, uint Keysym, bool Release) : ImeEvent;

internal sealed record Panel(string Header, IReadOnlyList<string> Candidates, int Index) : ImeEvent {
    internal bool IsEmpty => Header.Length == 0 && Candidates.Count == 0;
}

internal sealed record FocusOut : ImeEvent;
