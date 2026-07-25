using System;
using System.Collections.Generic;
using FcitxCjkInput;

internal static class Program {
    private const long Lifetime = 1000;

    private static int Main() {
        var tests = new Action[] {
            ImeActivationTracksFreshTextFieldFocus,
            GameplayKeysRemainAvailableOutsideTextFields,
            TextFieldRawHangulKeysAreSuppressed,
            BackspaceSuppressionRequiresFocusedPreedit,
            CommittedCharacterIsSuppressedOnce,
            MultipleCommittedCharactersAreSuppressedInOrder,
            DifferentCharacterIsNotSuppressedLater,
            CommittedCharacterSuppressionExpires,
            RecoveredSearchShortcutDiscardsItsHangulCommit,
            HangulDirectionalKeysRecoverCameraInput,
            HangulShortcutPressesAreConsumedWithoutUnityKeyDownEvent,
            ShortcutPressesExpireInsteadOfFiringLater,
            ShortcutPressQueueIsBounded,
            UnrelatedKeysAreNotRecovered,
            DirectionalKeysStayUnavailableInTextFields,
            ReleasedDirectionalKeysAreNotRecovered,
            StaleDirectionalPressIsNotRecovered,
            ClearingDirectionalKeysPreventsStuckMovement,
            PreeditDisplayDoesNotChangeSavedText,
            EndNavigationIsPreservedAfterAnchoredCommit,
            RepeatedSyllablesRemainDistinct,
            CommitClearsPreeditImmediately,
            CommitRemainsBoundAcrossFocusChange,
            FocusInSelectsContext,
            OtherContextCannotJoinActiveComposition,
            ReplayedSequenceIsIgnored,
            ResetAcceptsRestartedSequence,
            HardResetDiscardsPendingCommit,
            FocusOutClearsPreedit,
            EngineChangeCancelsPreedit,
            ExpiredCommitIsDiscarded
        };
        foreach (var test in tests) {
            test();
            Console.WriteLine("PASS " + test.Method.Name);
        }
        return 0;
    }

    private static void ImeActivationTracksFreshTextFieldFocus() {
        Equal(true, ImeRouting.TextFieldIsActive(7, 7, 99, 100));
        Equal(false, ImeRouting.TextFieldIsActive(0, 7, 100, 100));
        Equal(false, ImeRouting.TextFieldIsActive(8, 7, 100, 100));
        Equal(false, ImeRouting.TextFieldIsActive(7, 7, 98, 100));
    }

    private static void GameplayKeysRemainAvailableOutsideTextFields() {
        Equal(false, InputSuppression.ShouldSuppress(false, true, true, false, false, false));
    }

    private static void TextFieldRawHangulKeysAreSuppressed() {
        Equal(true, InputSuppression.ShouldSuppress(true, true, true, false, false, false));
        Equal(false, InputSuppression.ShouldSuppress(true, false, true, false, false, false));
        Equal(false, InputSuppression.ShouldSuppress(true, true, true, false, false, true));
    }

    private static void BackspaceSuppressionRequiresFocusedPreedit() {
        Equal(true, InputSuppression.ShouldSuppress(true, true, false, true, true, false));
        Equal(false, InputSuppression.ShouldSuppress(true, true, false, true, false, false));
        Equal(false, InputSuppression.ShouldSuppress(false, true, false, true, true, false));
    }

    private static void CommittedCharacterIsSuppressedOnce() {
        var target = new ControlToken(7, 1);
        var tracker = new CommittedCharacterTracker();
        tracker.Expect(target, "메", 1, 10);

        Equal(true, tracker.ShouldSuppress(target, '메', 10));
        Equal(false, tracker.ShouldSuppress(target, '메', 10));
    }

    private static void MultipleCommittedCharactersAreSuppressedInOrder() {
        var target = new ControlToken(7, 1);
        var tracker = new CommittedCharacterTracker();
        tracker.Expect(target, "검색", 2, 10);

        Equal(true, tracker.ShouldSuppress(target, '검', 10));
        Equal(true, tracker.ShouldSuppress(target, '색', 11));
        Equal(false, tracker.ShouldSuppress(target, '색', 11));
    }

    private static void DifferentCharacterIsNotSuppressedLater() {
        var target = new ControlToken(7, 1);
        var tracker = new CommittedCharacterTracker();
        tracker.Expect(target, "메", 1, 10);

        Equal(false, tracker.ShouldSuppress(target, '가', 10));
        Equal(false, tracker.ShouldSuppress(target, '메', 10));
    }

    private static void CommittedCharacterSuppressionExpires() {
        var target = new ControlToken(7, 1);
        var tracker = new CommittedCharacterTracker();
        tracker.Expect(target, "메", 1, 10);

        Equal(false, tracker.ShouldSuppress(target, '메', 12));
    }

    private static void RecoveredSearchShortcutDiscardsItsHangulCommit() {
        var guard = new ShortcutCommitGuard();
        guard.Arm(1, "ㅋ");

        Equal(false, guard.ShouldDiscard(2, "ㅋ"));
        Equal(true, guard.ShouldDiscard(1, "ㅋ"));
        Equal(false, guard.ShouldDiscard(1, "ㅋ"));

        guard.Arm(1, "ㅋ");
        Equal(false, guard.ShouldDiscard(1, "카"));
        Equal(false, guard.ShouldDiscard(1, "ㅋ"));
    }

    private static void HangulDirectionalKeysRecoverCameraInput() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('w', false, 100);

        Equal(true, GameplayKeyRecovery.ShouldRecover(false, false, true, 'w', 0, keys));
        Equal(false, GameplayKeyRecovery.ShouldRecover(false, false, false, 'w', 0, keys));
    }

    private static void HangulShortcutPressesAreConsumedWithoutUnityKeyDownEvent() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('q', false, 100);
        keys.Update('q', true, 101);
        keys.Update('e', false, 102);
        keys.Update('e', true, 103);
        keys.Update('z', false, 104);
        keys.Update('z', true, 105);

        Equal(true, keys.ConsumePress('q', 105));
        Equal(false, keys.ConsumePress('q', 105));
        Equal(true, keys.ConsumePress('e', 105));
        Equal(true, keys.ConsumePress('z', 105));
        Equal(false, keys.ConsumePress('z', 105));
        keys.Update('q', false, 106);
        keys.Update('q', false, 107);
        Equal(true, keys.ConsumePress('q', 107));
        Equal(true, keys.ConsumePress('q', 107));
        Equal(false, keys.ConsumePress('q', 107));
    }

    private static void ShortcutPressesExpireInsteadOfFiringLater() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('z', false, 100);

        Equal(true, keys.ConsumePress('z', 100 + Lifetime));
        keys.Update('z', false, 200);
        Equal(false, keys.ConsumePress('z', 200 + Lifetime + 1));
        keys.Update('q', false, 300);
        keys.DiscardExpired(300 + Lifetime + 1);
        Equal(false, keys.ConsumePress('q', 300 + Lifetime + 1));
    }

    private static void ShortcutPressQueueIsBounded() {
        var keys = new GameplayKeyState(Lifetime);
        for (var i = 0; i < 9; i++)
            keys.Update('z', false, 100 + i);

        for (var i = 0; i < 8; i++)
            Equal(true, keys.ConsumePress('z', 108));
        Equal(false, keys.ConsumePress('z', 108));
    }

    private static void UnrelatedKeysAreNotRecovered() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('r', false, 100);

        Equal(false, keys.IsDown('r'));
        Equal(false, keys.ConsumePress('r', 100));
    }

    private static void DirectionalKeysStayUnavailableInTextFields() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('a', false, 100);

        Equal(false, GameplayKeyRecovery.ShouldRecover(false, true, true, 'a', 0, keys));
    }

    private static void ReleasedDirectionalKeysAreNotRecovered() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('d', false, 100);
        keys.Update('d', true, 101);

        Equal(false, GameplayKeyRecovery.ShouldRecover(false, false, true, 'd', 0, keys));
    }

    private static void StaleDirectionalPressIsNotRecovered() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('w', false, 100, 100 + Lifetime + 1);

        Equal(false, GameplayKeyRecovery.ShouldRecover(false, false, true, 'w', 0, keys));
    }

    private static void ClearingDirectionalKeysPreventsStuckMovement() {
        var keys = new GameplayKeyState(Lifetime);
        keys.Update('s', false, 100);
        keys.Clear();

        Equal(false, GameplayKeyRecovery.ShouldRecover(false, false, true, 's', 0, keys));
    }

    private static void PreeditDisplayDoesNotChangeSavedText() {
        var saved = "";
        var display = TextEditMath.ReplaceRange(saved, 0, 0, "메");
        Equal("", saved);
        Equal("메", display);
    }

    private static void EndNavigationIsPreservedAfterAnchoredCommit() {
        var machine = CreateFocused(out var target, 6);
        Equal(true, machine.Preedit(10, 1, "견", 1));
        Equal(true, machine.Commit(10, 2, "견", 100));

        var action = Single(machine.TakeActions(target, 100));
        Equal(6, action.SelectionStart);
        Equal(6, action.SelectionEnd);
        Equal("견", action.Text);
        var original = "예쁜 꽃 발☆";
        var result = original.Remove(action.SelectionStart,
            action.SelectionEnd - action.SelectionStart).Insert(action.SelectionStart, action.Text);
        Equal("예쁜 꽃 발견☆", result);
        Equal(result.Length, TextEditMath.TransformIndex(original.Length,
            action.SelectionStart, action.SelectionEnd, action.Text.Length));
    }

    private static void RepeatedSyllablesRemainDistinct() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "하", 1);
        machine.Commit(10, 2, "하", 100);
        machine.Preedit(10, 3, "하", 1);
        machine.Commit(10, 4, "하", 101);

        var actions = machine.TakeActions(target, 101);
        Equal(2, actions.Count);
        Equal(0, actions[0].SelectionStart);
        Equal(1, actions[1].SelectionStart);
    }

    private static void CommitClearsPreeditImmediately() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "견", 1);
        Equal(true, machine.TryGetView(target, out _));
        machine.Commit(10, 2, "견", 100);
        Equal(false, machine.TryGetView(target, out _));
    }

    private static void CommitRemainsBoundAcrossFocusChange() {
        var machine = CreateFocused(out var first, 2);
        machine.Preedit(10, 1, "견", 1);
        var second = new ControlToken(8, 2);
        machine.Focus(second, 9, 9);
        machine.Commit(10, 2, "견", 100);

        Equal(0, machine.TakeActions(second, 100).Count);
        Equal(1, machine.TakeActions(first, 100).Count);
    }

    private static void FocusInSelectsContext() {
        var machine = CreateFocused(out var target, 0);
        machine.FocusIn(10, 1);
        Equal(false, machine.Preedit(11, 2, "나", 1));
        Equal(true, machine.Preedit(10, 3, "가", 1));
        Equal(true, machine.Commit(10, 4, "가", 100));
        Equal(1, machine.TakeActions(target, 100).Count);
    }

    private static void OtherContextCannotJoinActiveComposition() {
        var machine = CreateFocused(out var target, 0);
        Equal(true, machine.Preedit(10, 1, "가", 1));
        Equal(false, machine.Preedit(11, 2, "나", 1));
        Equal(true, machine.Commit(10, 3, "가", 100));
        Equal(false, machine.Commit(11, 4, "가", 101));
        var action = Single(machine.TakeActions(target, 101));
        Equal("가", action.Text);
    }

    private static void ReplayedSequenceIsIgnored() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 5, "가", 1);
        Equal(true, machine.Commit(10, 6, "가", 100));
        Equal(false, machine.Commit(10, 6, "가", 101));
        Equal(1, machine.TakeActions(target, 101).Count);
    }

    private static void ResetAcceptsRestartedSequence() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 5, "가", 1);
        machine.Commit(10, 6, "가", 99);
        machine.Reset();
        machine.Preedit(10, 1, "나", 1);
        Equal(true, machine.Commit(10, 2, "나", 100));
        var actions = machine.TakeActions(target, 100);
        Equal(2, actions.Count);
        Equal("가", actions[0].Text);
        Equal("나", actions[1].Text);
    }

    private static void HardResetDiscardsPendingCommit() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "가", 1);
        machine.Commit(10, 2, "가", 100);
        machine.ResetAndDiscardActions();

        Equal(0, machine.TakeActions(target, 100).Count);
        Equal(0, machine.PendingCount);
    }

    private static void FocusOutClearsPreedit() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "가", 1);
        Equal(true, machine.FocusOut(10, 2));
        Equal(false, machine.TryGetView(target, out _));
    }

    private static void EngineChangeCancelsPreedit() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "가", 1);
        machine.CancelComposition(10);
        Equal(false, machine.TryGetView(target, out _));
    }

    private static void ExpiredCommitIsDiscarded() {
        var machine = CreateFocused(out var target, 0);
        machine.Preedit(10, 1, "가", 1);
        machine.Commit(10, 2, "가", 100);
        Equal(0, machine.TakeActions(target, 100 + Lifetime + 1).Count);
        Equal(0, machine.PendingCount);
    }

    private static CompositionStateMachine CreateFocused(out ControlToken target, int cursor) {
        var machine = new CompositionStateMachine(Lifetime);
        target = new ControlToken(7, 1);
        machine.Focus(target, cursor, cursor);
        return machine;
    }

    private static T Single<T>(List<T> values) {
        Equal(1, values.Count);
        return values[0];
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("Expected " + expected + ", got " + actual);
    }
}
