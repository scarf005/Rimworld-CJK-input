using System;
using System.Collections.Generic;
using FcitxCjkInput;

internal static class Program {
    private const char S = ImeEvent.Separator;

    private static int Main() {
        Action[] tests = [
            CommitKeepsSeparatorFreeText,
            PreeditByteCursorBecomesCharIndex,
            NegativePreeditCursorMeansEnd,
            CursorPastTextIsClamped,
            CursorInsideSurrogatePairCountsWholeCharacters,
            PreeditInsertsAtCaret,
            PreeditCaretIsClampedToField,
            ForwardKeyFieldsAreParsed,
            PanelKeepsHeaderAndCandidates,
            EmptyPanelHidesCandidates,
            FocusOutIsParsed,
            UnknownMessagesAreIgnored,
        ];
        foreach (var test in tests) {
            test();
            Console.WriteLine($"PASS {test.Method.Name}");
        }

        return 0;
    }

    private static void CommitKeepsSeparatorFreeText() =>
        Equal<ImeEvent?>(new Commit("한 글"), ImeEvent.Parse($"commit{S}한 글"));

    private static void PreeditByteCursorBecomesCharIndex() =>
        Equal<ImeEvent?>(new Preedit("한글", 1), ImeEvent.Parse($"preedit{S}3{S}한글"));

    private static void NegativePreeditCursorMeansEnd() =>
        Equal<ImeEvent?>(new Preedit("한글", 2), ImeEvent.Parse($"preedit{S}-1{S}한글"));

    private static void CursorPastTextIsClamped() => Equal(2, ImeEvent.CharIndex("ab", 10));

    private static void CursorInsideSurrogatePairCountsWholeCharacters() {
        Equal(2, ImeEvent.CharIndex("𠀀a", 4));
        Equal(3, ImeEvent.CharIndex("𠀀a", 5));
    }

    private static void PreeditInsertsAtCaret() {
        var (text, caret) = new Preedit("한", 1).Apply("ab", 1);
        Equal("a한b", text);
        Equal(2, caret);
    }

    private static void PreeditCaretIsClampedToField() {
        var (text, caret) = new Preedit("ㄱ", 5).Apply("ab", 9);
        Equal("abㄱ", text);
        Equal(3, caret);
    }

    private static void ForwardKeyFieldsAreParsed() =>
        Equal<ImeEvent?>(new Forward(0, 0xff08, true), ImeEvent.Parse($"forward{S}0{S}65288{S}1"));

    private static void PanelKeepsHeaderAndCandidates() {
        var panel = (Panel)ImeEvent.Parse($"panel{S}1{S}韓{S}1. 韓{S}2. 汗")!;
        Equal("韓", panel.Header);
        Equal(1, panel.Index);
        Equal("1. 韓|2. 汗", string.Join('|', panel.Candidates));
        Equal(false, panel.IsEmpty);
    }

    private static void EmptyPanelHidesCandidates() =>
        Equal(true, ((Panel)ImeEvent.Parse($"panel{S}-1{S}")!).IsEmpty);

    private static void FocusOutIsParsed() => Equal<ImeEvent?>(new FocusOut(), ImeEvent.Parse("focusout"));

    private static void UnknownMessagesAreIgnored() {
        Equal(null, ImeEvent.Parse("bogus"));
        Equal(null, ImeEvent.Parse($"preedit{S}1"));
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
        }
    }
}
