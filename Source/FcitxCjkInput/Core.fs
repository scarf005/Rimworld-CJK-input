namespace FcitxCjkInput

open System

module TextEditMath =
    let replaceRange (text: string) (selectionStart: int) (selectionEnd: int) (replacement: string) =
        text.Remove(selectionStart, selectionEnd - selectionStart).Insert(selectionStart, replacement)

    let transformIndex (index: int) (selectionStart: int) (selectionEnd: int) (insertedLength: int) =
        if index <= selectionStart then
            index
        elif index >= selectionEnd then
            index + insertedLength - (selectionEnd - selectionStart)
        else
            selectionStart + insertedLength

module ImeRouting =
    let textFieldIsActive (keyboardControl: int) (focusedControl: int) (lastSeenFrame: int) (currentFrame: int) =
        keyboardControl <> 0
        && keyboardControl = focusedControl
        && currentFrame - lastSeenFrame <= 1

module InputSuppression =
    let shouldSuppress
        (focusedTextField: bool)
        (hangul: bool)
        (letter: bool)
        (backspace: bool)
        (hasPreedit: bool)
        (shortcut: bool)
        =
        focusedTextField
        && hangul
        && ((letter && not shortcut) || (backspace && hasPreedit))

[<Struct; CustomEquality; NoComparison>]
type ControlToken =
    val Id: int
    val Generation: int64

    new(id: int, generation: int64) = { Id = id; Generation = generation }

    override this.Equals(obj: obj) =
        match obj with
        | :? ControlToken as other -> this.Id = other.Id && this.Generation = other.Generation
        | _ -> false

    override this.GetHashCode() = (this.Id * 397) ^^^ this.Generation.GetHashCode()

    member this.Equals(other: ControlToken) = this.Id = other.Id && this.Generation = other.Generation

    interface IEquatable<ControlToken> with
        member this.Equals(other) = this.Equals(other)

[<Struct>]
type CompositionView =
    { Text: string
      Cursor: int
      SelectionStart: int
      SelectionEnd: int }

[<Struct>]
type CommitAction =
    { Target: ControlToken
      SelectionStart: int
      SelectionEnd: int
      Text: string
      CreatedAt: int64 }
