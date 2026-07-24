namespace FcitxCjkInput

open System
open System.Collections.Generic

type private ContextState() =
    member val LastSequence = 0L with get, set
    member val Target = Unchecked.defaultof<ControlToken> with get, set
    member val HasTarget = false with get, set
    member val SelectionStart = 0 with get, set
    member val SelectionEnd = 0 with get, set
    member val Preedit = "" with get, set
    member val PreeditCursor = 0 with get, set

type CompositionStateMachine(actionLifetime: int64) =
    let contexts = Dictionary<int, ContextState>()
    let actions = Queue<CommitAction>()
    let mutable focused = Unchecked.defaultof<ControlToken>
    let mutable hasFocus = false
    let mutable selectionStart = 0
    let mutable selectionEnd = 0
    let mutable activeContext = 0
    let mutable activeContextLocked = false

    member _.HasPreedit =
        activeContext <> 0
        && (match contexts.TryGetValue(activeContext) with
            | true, ctx -> ctx.Preedit.Length > 0
            | _ -> false)

    member _.PendingCount = actions.Count

    member _.ActiveContext = activeContext

    member this.Focus(target: ControlToken, cursorIndex: int, selectIndex: int) =
        focused <- target
        hasFocus <- target.Id <> 0
        selectionStart <- min cursorIndex selectIndex
        selectionEnd <- max cursorIndex selectIndex

    member _.Blur() = hasFocus <- false

    member this.Reset() =
        contexts.Clear()
        activeContext <- 0
        activeContextLocked <- false

    member _.CancelComposition(contextId: int) =
        match contexts.TryGetValue(contextId) with
        | true, ctx ->
            ctx.Preedit <- ""
            ctx.PreeditCursor <- 0
            ctx.HasTarget <- false
        | _ -> ()

    member this.FocusIn(contextId: int, sequence: int64) =
        match contexts.TryGetValue(contextId) with
        | true, ctx ->
            if sequence <= ctx.LastSequence then
                ()
            else
                ctx.LastSequence <- sequence

                if activeContext <> 0 && activeContext <> contextId then
                    match contexts.TryGetValue(activeContext) with
                    | true, prev ->
                        prev.Preedit <- ""
                        prev.PreeditCursor <- 0
                        prev.HasTarget <- false
                    | _ -> ()

                activeContext <- contextId
                activeContextLocked <- true
        | _ ->
            let ctx = ContextState(LastSequence = sequence)
            contexts.Add(contextId, ctx)

            if activeContext <> 0 && activeContext <> contextId then
                match contexts.TryGetValue(activeContext) with
                | true, prev ->
                    prev.Preedit <- ""
                    prev.PreeditCursor <- 0
                    prev.HasTarget <- false
                | _ -> ()

            activeContext <- contextId
            activeContextLocked <- true

    member this.FocusOut(contextId: int, sequence: int64) =
        match contexts.TryGetValue(contextId) with
        | true, ctx ->
            if sequence <= ctx.LastSequence then
                false
            else
                ctx.LastSequence <- sequence
                ctx.Preedit <- ""
                ctx.PreeditCursor <- 0
                ctx.HasTarget <- false

                if activeContext <> contextId then
                    false
                else
                    activeContext <- 0
                    activeContextLocked <- false
                    true
        | _ -> false

    member this.Preedit(contextId: int, sequence: int64, text: string, cursor: int) =
        match contexts.TryGetValue(contextId) with
        | true, ctx ->
            if sequence <= ctx.LastSequence then
                false
            else
                ctx.LastSequence <- sequence

                if text.Length > 0 then
                    if not (this.Activate(contextId)) then
                        false
                    elif not ctx.HasTarget && not (this.Bind(ctx)) then
                        false
                    else
                        ctx.Preedit <- text
                        ctx.PreeditCursor <- max 0 (min cursor text.Length)
                        true
                else
                    let wasActive = activeContext = contextId
                    ctx.Preedit <- ""
                    ctx.PreeditCursor <- 0
                    ctx.HasTarget <- false
                    wasActive
        | _ ->
            let ctx = ContextState(LastSequence = sequence)
            contexts.Add(contextId, ctx)

            if text.Length > 0 then
                if not (this.Activate(contextId)) then
                    false
                elif not (this.Bind(ctx)) then
                    false
                else
                    ctx.Preedit <- text
                    ctx.PreeditCursor <- max 0 (min cursor text.Length)
                    true
            else
                false

    member this.Commit(contextId: int, sequence: int64, text: string, now: int64) =
        match contexts.TryGetValue(contextId) with
        | true, ctx ->
            if sequence <= ctx.LastSequence then
                false
            else
                ctx.LastSequence <- sequence

                if not (this.Activate(contextId)) then
                    false
                elif not ctx.HasTarget && not (this.Bind(ctx)) then
                    false
                else
                    actions.Enqueue(
                        { Target = ctx.Target
                          SelectionStart = ctx.SelectionStart
                          SelectionEnd = ctx.SelectionEnd
                          Text = text
                          CreatedAt = now }
                    )

                    let next = ctx.SelectionStart + text.Length
                    ctx.SelectionStart <- next
                    ctx.SelectionEnd <- next
                    ctx.Preedit <- ""
                    ctx.PreeditCursor <- 0
                    true
        | _ ->
            let ctx = ContextState(LastSequence = sequence)
            contexts.Add(contextId, ctx)

            if not (this.Activate(contextId)) then
                false
            elif not (this.Bind(ctx)) then
                false
            else
                actions.Enqueue(
                    { Target = ctx.Target
                      SelectionStart = ctx.SelectionStart
                      SelectionEnd = ctx.SelectionEnd
                      Text = text
                      CreatedAt = now }
                )

                let next = ctx.SelectionStart + text.Length
                ctx.SelectionStart <- next
                ctx.SelectionEnd <- next
                true

    member this.TryGetView(target: ControlToken) =
        if not this.HasPreedit then
            None
        else
            let ctx = contexts.[activeContext]

            if ctx.HasTarget && ctx.Target.Equals(target) then
                Some
                    { Text = ctx.Preedit
                      Cursor = ctx.PreeditCursor
                      SelectionStart = ctx.SelectionStart
                      SelectionEnd = ctx.SelectionEnd }
            else
                None

    member _.TakeActions(target: ControlToken, now: int64) =
        let matches = List<CommitAction>()
        let remaining = actions.Count

        for _ in 1..remaining do
            let action = actions.Dequeue()

            if now - action.CreatedAt > actionLifetime then
                ()
            elif action.Target.Equals(target) then
                matches.Add(action)
            else
                actions.Enqueue(action)

        matches

    member _.DiscardExpired(now: int64) =
        let remaining = actions.Count

        for _ in 1..remaining do
            let action = actions.Dequeue()

            if not (now - action.CreatedAt > actionLifetime) then
                actions.Enqueue(action)

    member private this.Activate(contextId: int) =
        if activeContext = contextId then
            activeContextLocked <- true
            true
        elif activeContextLocked then
            false
        else
            activeContext <- contextId
            activeContextLocked <- true
            true

    member private this.Bind(ctx: ContextState) =
        if not hasFocus then
            false
        else
            ctx.Target <- focused
            ctx.HasTarget <- true
            ctx.SelectionStart <- selectionStart
            ctx.SelectionEnd <- selectionEnd
            true
