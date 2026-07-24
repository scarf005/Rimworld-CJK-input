module FcitxCjkInput.Tests.Program

open System
open System.Collections.Generic
open FcitxCjkInput

[<Literal>]
let Lifetime = 1000L

let equal<'T when 'T: equality> (expected: 'T) (actual: 'T) =
    if expected <> actual then
        raise (InvalidOperationException(sprintf "Expected %A, got %A" expected actual))

let single (values: List<CommitAction>) =
    equal 1 values.Count
    values.[0]

let createFocused cursor =
    let machine = CompositionStateMachine(Lifetime)
    let target = ControlToken(7, 1L)
    machine.Focus(target, cursor, cursor)
    machine, target

let imeActivationTracksFreshTextFieldFocus () =
    equal true (ImeRouting.textFieldIsActive 7 7 99 100)
    equal false (ImeRouting.textFieldIsActive 0 7 100 100)
    equal false (ImeRouting.textFieldIsActive 8 7 100 100)
    equal false (ImeRouting.textFieldIsActive 7 7 98 100)

let gameplayKeysRemainAvailableOutsideTextFields () =
    equal false (InputSuppression.shouldSuppress false true true false false false)

let textFieldRawHangulKeysAreSuppressed () =
    equal true (InputSuppression.shouldSuppress true true true false false false)
    equal false (InputSuppression.shouldSuppress true false true false false false)
    equal false (InputSuppression.shouldSuppress true true true false false true)

let backspaceSuppressionRequiresFocusedPreedit () =
    equal true (InputSuppression.shouldSuppress true true false true true false)
    equal false (InputSuppression.shouldSuppress true true false true false false)
    equal false (InputSuppression.shouldSuppress false true false true true false)

let committedCharacterIsSuppressedOnce () =
    let target = ControlToken(7, 1L)
    let tracker = CommittedCharacterTracker()
    tracker.Expect(target, "메", 1, 10)
    equal true (tracker.ShouldSuppress(target, '메', 10))
    equal false (tracker.ShouldSuppress(target, '메', 10))

let multipleCommittedCharactersAreSuppressedInOrder () =
    let target = ControlToken(7, 1L)
    let tracker = CommittedCharacterTracker()
    tracker.Expect(target, "검색", 2, 10)
    equal true (tracker.ShouldSuppress(target, '검', 10))
    equal true (tracker.ShouldSuppress(target, '색', 11))
    equal false (tracker.ShouldSuppress(target, '색', 11))

let differentCharacterIsNotSuppressedLater () =
    let target = ControlToken(7, 1L)
    let tracker = CommittedCharacterTracker()
    tracker.Expect(target, "메", 1, 10)
    equal false (tracker.ShouldSuppress(target, '가', 10))
    equal false (tracker.ShouldSuppress(target, '메', 10))

let committedCharacterSuppressionExpires () =
    let target = ControlToken(7, 1L)
    let tracker = CommittedCharacterTracker()
    tracker.Expect(target, "메", 1, 10)
    equal false (tracker.ShouldSuppress(target, '메', 12))

let hangulDirectionalKeysRecoverCameraInput () =
    let keys = DirectionalKeyState()
    keys.Update(int 'w', false)
    equal true (GameplayKeyRecovery.shouldRecover false false true (int 'w') 0 keys)
    equal false (GameplayKeyRecovery.shouldRecover false false false (int 'w') 0 keys)

let unrelatedKeysAreNotRecovered () =
    let keys = DirectionalKeyState()
    keys.Update(int 'q', false)
    equal false (GameplayKeyRecovery.shouldRecover false false true (int 'q') 0 keys)

let directionalKeysStayUnavailableInTextFields () =
    let keys = DirectionalKeyState()
    keys.Update(int 'a', false)
    equal false (GameplayKeyRecovery.shouldRecover false true true (int 'a') 0 keys)

let releasedDirectionalKeysAreNotRecovered () =
    let keys = DirectionalKeyState()
    keys.Update(int 'd', false)
    keys.Update(int 'd', true)
    equal false (GameplayKeyRecovery.shouldRecover false false true (int 'd') 0 keys)

let clearingDirectionalKeysPreventsStuckMovement () =
    let keys = DirectionalKeyState()
    keys.Update(int 's', false)
    keys.Clear()
    equal false (GameplayKeyRecovery.shouldRecover false false true (int 's') 0 keys)

let preeditDisplayDoesNotChangeSavedText () =
    let saved = ""
    let display = TextEditMath.replaceRange saved 0 0 "메"
    equal "" saved
    equal "메" display

let endNavigationIsPreservedAfterAnchoredCommit () =
    let machine, target = createFocused 6
    equal true (machine.Preedit(10, 1L, "견", 1))
    equal true (machine.Commit(10, 2L, "견", 100L))
    let action = single (machine.TakeActions(target, 100L))
    equal 6 action.SelectionStart
    equal 6 action.SelectionEnd
    equal "견" action.Text
    let original = "예쁜 꽃 발☆"
    let result =
        original.Remove(action.SelectionStart, action.SelectionEnd - action.SelectionStart)
            .Insert(action.SelectionStart, action.Text)
    equal "예쁜 꽃 발견☆" result
    equal result.Length (TextEditMath.transformIndex original.Length action.SelectionStart action.SelectionEnd action.Text.Length)

let repeatedSyllablesRemainDistinct () =
    let machine, target = createFocused 0
    machine.Preedit(10, 1L, "하", 1) |> ignore
    machine.Commit(10, 2L, "하", 100L) |> ignore
    machine.Preedit(10, 3L, "하", 1) |> ignore
    machine.Commit(10, 4L, "하", 101L) |> ignore
    let actions = machine.TakeActions(target, 101L)
    equal 2 actions.Count
    equal 0 actions.[0].SelectionStart
    equal 1 actions.[1].SelectionStart

let commitClearsPreeditImmediately () =
    let machine, target = createFocused 0
    machine.Preedit(10, 1L, "견", 1) |> ignore
    equal true (machine.TryGetView(target).IsSome)
    machine.Commit(10, 2L, "견", 100L) |> ignore
    equal false (machine.TryGetView(target).IsSome)

let commitRemainsBoundAcrossFocusChange () =
    let machine, first = createFocused 2
    machine.Preedit(10, 1L, "견", 1) |> ignore
    let second = ControlToken(8, 2L)
    machine.Focus(second, 9, 9)
    machine.Commit(10, 2L, "견", 100L) |> ignore
    equal 0 (machine.TakeActions(second, 100L).Count)
    equal 1 (machine.TakeActions(first, 100L).Count)

let focusInSelectsContext () =
    let machine, target = createFocused 0
    machine.FocusIn(10, 1L)
    equal false (machine.Preedit(11, 2L, "나", 1))
    equal true (machine.Preedit(10, 3L, "가", 1))
    equal true (machine.Commit(10, 4L, "가", 100L))
    equal 1 (machine.TakeActions(target, 100L).Count)

let otherContextCannotJoinActiveComposition () =
    let machine, target = createFocused 0
    equal true (machine.Preedit(10, 1L, "가", 1))
    equal false (machine.Preedit(11, 2L, "나", 1))
    equal true (machine.Commit(10, 3L, "가", 100L))
    equal false (machine.Commit(11, 4L, "가", 101L))
    let action = single (machine.TakeActions(target, 101L))
    equal "가" action.Text

let replayedSequenceIsIgnored () =
    let machine, target = createFocused 0
    machine.Preedit(10, 5L, "가", 1) |> ignore
    equal true (machine.Commit(10, 6L, "가", 100L))
    equal false (machine.Commit(10, 6L, "가", 101L))
    equal 1 (machine.TakeActions(target, 101L).Count)

let resetAcceptsRestartedSequence () =
    let machine, target = createFocused 0
    machine.Preedit(10, 5L, "가", 1) |> ignore
    machine.Commit(10, 6L, "가", 99L) |> ignore
    machine.Reset()
    machine.Preedit(10, 1L, "나", 1) |> ignore
    equal true (machine.Commit(10, 2L, "나", 100L))
    let actions = machine.TakeActions(target, 100L)
    equal 2 actions.Count
    equal "가" actions.[0].Text
    equal "나" actions.[1].Text

let focusOutClearsPreedit () =
    let machine, target = createFocused 0
    machine.Preedit(10, 1L, "가", 1) |> ignore
    equal true (machine.FocusOut(10, 2L))
    equal false (machine.TryGetView(target).IsSome)

let engineChangeCancelsPreedit () =
    let machine, target = createFocused 0
    machine.Preedit(10, 1L, "가", 1) |> ignore
    machine.CancelComposition(10)
    equal false (machine.TryGetView(target).IsSome)

let expiredCommitIsDiscarded () =
    let machine, target = createFocused 0
    machine.Preedit(10, 1L, "가", 1) |> ignore
    machine.Commit(10, 2L, "가", 100L) |> ignore
    equal 0 (machine.TakeActions(target, 100L + Lifetime + 1L).Count)
    equal 0 machine.PendingCount

[<EntryPoint>]
let main _ =
    let testList: (string * (unit -> unit)) list = [
        ("ImeActivationTracksFreshTextFieldFocus", imeActivationTracksFreshTextFieldFocus)
        ("GameplayKeysRemainAvailableOutsideTextFields", gameplayKeysRemainAvailableOutsideTextFields)
        ("TextFieldRawHangulKeysAreSuppressed", textFieldRawHangulKeysAreSuppressed)
        ("BackspaceSuppressionRequiresFocusedPreedit", backspaceSuppressionRequiresFocusedPreedit)
        ("CommittedCharacterIsSuppressedOnce", committedCharacterIsSuppressedOnce)
        ("MultipleCommittedCharactersAreSuppressedInOrder", multipleCommittedCharactersAreSuppressedInOrder)
        ("DifferentCharacterIsNotSuppressedLater", differentCharacterIsNotSuppressedLater)
        ("CommittedCharacterSuppressionExpires", committedCharacterSuppressionExpires)
        ("HangulDirectionalKeysRecoverCameraInput", hangulDirectionalKeysRecoverCameraInput)
        ("UnrelatedKeysAreNotRecovered", unrelatedKeysAreNotRecovered)
        ("DirectionalKeysStayUnavailableInTextFields", directionalKeysStayUnavailableInTextFields)
        ("ReleasedDirectionalKeysAreNotRecovered", releasedDirectionalKeysAreNotRecovered)
        ("ClearingDirectionalKeysPreventsStuckMovement", clearingDirectionalKeysPreventsStuckMovement)
        ("PreeditDisplayDoesNotChangeSavedText", preeditDisplayDoesNotChangeSavedText)
        ("EndNavigationIsPreservedAfterAnchoredCommit", endNavigationIsPreservedAfterAnchoredCommit)
        ("RepeatedSyllablesRemainDistinct", repeatedSyllablesRemainDistinct)
        ("CommitClearsPreeditImmediately", commitClearsPreeditImmediately)
        ("CommitRemainsBoundAcrossFocusChange", commitRemainsBoundAcrossFocusChange)
        ("FocusInSelectsContext", focusInSelectsContext)
        ("OtherContextCannotJoinActiveComposition", otherContextCannotJoinActiveComposition)
        ("ReplayedSequenceIsIgnored", replayedSequenceIsIgnored)
        ("ResetAcceptsRestartedSequence", resetAcceptsRestartedSequence)
        ("FocusOutClearsPreedit", focusOutClearsPreedit)
        ("EngineChangeCancelsPreedit", engineChangeCancelsPreedit)
        ("ExpiredCommitIsDiscarded", expiredCommitIsDiscarded)
    ]

    for name, test in testList do
        test ()
        printfn "PASS %s" name

    0
