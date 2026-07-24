namespace FcitxCjkInput

open System

type CommittedCharacterTracker() =
    let mutable target = Unchecked.defaultof<ControlToken>
    let mutable characters = ""
    let mutable index = 0
    let mutable length = 0
    let mutable expiresAfterFrame = -1

    member this.Expect(newTarget: ControlToken, text: string, insertedLength: int, frame: int) =
        let len = max 0 (min insertedLength text.Length)

        if newTarget.Id = 0 || len = 0 then
            ()
        else

            let expected = text.Substring(0, len)

            if not (target.Equals(newTarget)) || frame > expiresAfterFrame || index >= length then
                target <- newTarget
                characters <- expected
                index <- 0
                length <- expected.Length
            else
                characters <- characters.Substring(index, length - index) + expected
                index <- 0
                length <- characters.Length

            expiresAfterFrame <- frame + 1

    member this.ShouldSuppress(checkTarget: ControlToken, character: char, frame: int) =
        if character = '\000' then
            false
        elif not (target.Equals(checkTarget)) || frame > expiresAfterFrame || index >= length then
            target <- Unchecked.defaultof<ControlToken>
            characters <- ""
            index <- 0
            length <- 0
            expiresAfterFrame <- -1
            false
        elif characters.[index] <> character then
            target <- Unchecked.defaultof<ControlToken>
            characters <- ""
            index <- 0
            length <- 0
            expiresAfterFrame <- -1
            false
        else
            index <- index + 1

            if index >= length then
                target <- Unchecked.defaultof<ControlToken>
                characters <- ""
                index <- 0
                length <- 0
                expiresAfterFrame <- -1

            true

    member _.Clear() =
        target <- Unchecked.defaultof<ControlToken>
        characters <- ""
        index <- 0
        length <- 0
        expiresAfterFrame <- -1

type DirectionalKeyState() =
    [<Literal>]
    static let A = 1 <<< 0

    [<Literal>]
    static let D = 1 <<< 1

    [<Literal>]
    static let S = 1 <<< 2

    [<Literal>]
    static let W = 1 <<< 3

    let mutable pressed = 0

    member _.Update(keyValue: int, release: bool) =
        let mask = DirectionalKeyState.MaskFor keyValue

        if release then
            pressed <- pressed &&& ~~~mask
        else
            pressed <- pressed ||| mask

    member _.IsDown(keyCode: int) =
        let mask = DirectionalKeyState.MaskFor keyCode
        mask <> 0 && (pressed &&& mask) <> 0

    member _.Clear() = pressed <- 0

    static member private MaskFor(keyValue: int) =
        match keyValue with
        | 97
        | 65 -> A
        | 100
        | 68 -> D
        | 115
        | 83 -> S
        | 119
        | 87 -> W
        | _ -> 0

module GameplayKeyRecovery =
    let shouldRecover
        (original: bool)
        (textFieldActive: bool)
        (cameraDolly: bool)
        (primaryKey: int)
        (secondaryKey: int)
        (keys: DirectionalKeyState)
        =
        original
        || (not textFieldActive
            && cameraDolly
            && (keys.IsDown(primaryKey) || keys.IsDown(secondaryKey)))
