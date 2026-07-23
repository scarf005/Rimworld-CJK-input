using System;
using System.Collections.Generic;

namespace FcitxCjkInput {
    internal static class TextEditMath {
        public static int TransformIndex(int index, int selectionStart, int selectionEnd,
            int insertedLength) {
            if (index <= selectionStart)
                return index;
            if (index >= selectionEnd)
                return index + insertedLength - (selectionEnd - selectionStart);
            return selectionStart + insertedLength;
        }
    }

    internal readonly struct ControlToken : IEquatable<ControlToken> {
        public readonly int Id;
        public readonly long Generation;

        public ControlToken(int id, long generation) {
            Id = id;
            Generation = generation;
        }

        public bool Equals(ControlToken other) {
            return Id == other.Id && Generation == other.Generation;
        }

        public override bool Equals(object obj) {
            return obj is ControlToken other && Equals(other);
        }

        public override int GetHashCode() {
            unchecked {
                return (Id * 397) ^ Generation.GetHashCode();
            }
        }
    }

    internal readonly struct CompositionView {
        public readonly string Text;
        public readonly int Cursor;
        public readonly int SelectionStart;
        public readonly int SelectionEnd;

        public CompositionView(string text, int cursor, int selectionStart, int selectionEnd) {
            Text = text;
            Cursor = cursor;
            SelectionStart = selectionStart;
            SelectionEnd = selectionEnd;
        }
    }

    internal readonly struct CommitAction {
        public readonly ControlToken Target;
        public readonly int SelectionStart;
        public readonly int SelectionEnd;
        public readonly string Text;
        public readonly long CreatedAt;

        public CommitAction(ControlToken target, int selectionStart, int selectionEnd, string text,
            long createdAt) {
            Target = target;
            SelectionStart = selectionStart;
            SelectionEnd = selectionEnd;
            Text = text;
            CreatedAt = createdAt;
        }
    }

    internal sealed class CompositionStateMachine {
        private sealed class ContextState {
            public long LastSequence;
            public ControlToken Target;
            public bool HasTarget;
            public int SelectionStart;
            public int SelectionEnd;
            public string Preedit = "";
            public int PreeditCursor;
        }

        private readonly Dictionary<int, ContextState> _contexts = new Dictionary<int, ContextState>();
        private readonly Queue<CommitAction> _actions = new Queue<CommitAction>();
        private readonly long _actionLifetime;
        private ControlToken _focused;
        private bool _hasFocus;
        private int _selectionStart;
        private int _selectionEnd;
        private int _activeContext;
        private bool _activeContextLocked;

        public CompositionStateMachine(long actionLifetime) {
            _actionLifetime = actionLifetime;
        }

        public bool HasPreedit {
            get {
                return _activeContext != 0 && _contexts.TryGetValue(_activeContext, out var context) &&
                    context.Preedit.Length > 0;
            }
        }

        public int PendingCount => _actions.Count;
        public int ActiveContext => _activeContext;

        public void Focus(ControlToken target, int cursorIndex, int selectIndex) {
            _focused = target;
            _hasFocus = target.Id != 0;
            _selectionStart = Math.Min(cursorIndex, selectIndex);
            _selectionEnd = Math.Max(cursorIndex, selectIndex);
        }

        public void Blur() {
            _hasFocus = false;
        }

        public void Reset() {
            _contexts.Clear();
            _activeContext = 0;
            _activeContextLocked = false;
        }

        public void CancelComposition(int contextId) {
            if (_contexts.TryGetValue(contextId, out var context))
                Clear(context);
        }

        public void FocusIn(int contextId, long sequence) {
            var context = GetContext(contextId);
            if (!Accept(context, sequence))
                return;
            if (_activeContext != 0 && _activeContext != contextId &&
                _contexts.TryGetValue(_activeContext, out var previous))
                Clear(previous);
            _activeContext = contextId;
            _activeContextLocked = true;
        }

        public bool FocusOut(int contextId, long sequence) {
            var context = GetContext(contextId);
            if (!Accept(context, sequence))
                return false;
            Clear(context);
            if (_activeContext != contextId)
                return false;
            _activeContext = 0;
            _activeContextLocked = false;
            return true;
        }

        public bool Preedit(int contextId, long sequence, string text, int cursor) {
            var context = GetContext(contextId);
            if (!Accept(context, sequence))
                return false;
            if (text.Length > 0) {
                if (!Activate(contextId))
                    return false;
                if (!context.HasTarget && !Bind(context))
                    return false;
                context.Preedit = text;
                context.PreeditCursor = Math.Max(0, Math.Min(cursor, text.Length));
                return true;
            }

            var wasActive = _activeContext == contextId;
            context.Preedit = "";
            context.PreeditCursor = 0;
            ClearTarget(context);
            return wasActive;
        }

        public bool Commit(int contextId, long sequence, string text, long now) {
            var context = GetContext(contextId);
            if (!Accept(context, sequence))
                return false;
            if (!Activate(contextId))
                return false;
            if (!context.HasTarget && !Bind(context))
                return false;

            Enqueue(context, text, now);
            var next = context.SelectionStart + text.Length;
            context.SelectionStart = next;
            context.SelectionEnd = next;
            context.Preedit = "";
            context.PreeditCursor = 0;
            return true;
        }

        public bool TryGetView(ControlToken target, out CompositionView view) {
            if (HasPreedit) {
                var context = _contexts[_activeContext];
                if (context.HasTarget && context.Target.Equals(target)) {
                    view = new CompositionView(context.Preedit, context.PreeditCursor,
                        context.SelectionStart, context.SelectionEnd);
                    return true;
                }
            }
            view = default;
            return false;
        }

        public List<CommitAction> TakeActions(ControlToken target, long now) {
            var matches = new List<CommitAction>();
            var remaining = _actions.Count;
            while (remaining-- > 0) {
                var action = _actions.Dequeue();
                if (IsExpired(action, now))
                    continue;
                if (action.Target.Equals(target))
                    matches.Add(action);
                else
                    _actions.Enqueue(action);
            }
            return matches;
        }

        public void DiscardExpired(long now) {
            var remaining = _actions.Count;
            while (remaining-- > 0) {
                var action = _actions.Dequeue();
                if (!IsExpired(action, now))
                    _actions.Enqueue(action);
            }
        }

        private bool Activate(int contextId) {
            if (_activeContext == contextId) {
                _activeContextLocked = true;
                return true;
            }
            if (_activeContextLocked)
                return false;
            _activeContext = contextId;
            _activeContextLocked = true;
            return true;
        }

        private ContextState GetContext(int contextId) {
            if (_contexts.TryGetValue(contextId, out var context))
                return context;
            context = new ContextState();
            _contexts.Add(contextId, context);
            return context;
        }

        private bool Bind(ContextState context) {
            if (!_hasFocus)
                return false;
            context.Target = _focused;
            context.HasTarget = true;
            context.SelectionStart = _selectionStart;
            context.SelectionEnd = _selectionEnd;
            return true;
        }

        private void Enqueue(ContextState context, string text, long now) {
            _actions.Enqueue(new CommitAction(context.Target, context.SelectionStart,
                context.SelectionEnd, text, now));
        }

        private bool IsExpired(CommitAction action, long now) {
            return now - action.CreatedAt > _actionLifetime;
        }

        private static bool Accept(ContextState context, long sequence) {
            if (sequence <= context.LastSequence)
                return false;
            context.LastSequence = sequence;
            return true;
        }

        private static void Clear(ContextState context) {
            context.Preedit = "";
            context.PreeditCursor = 0;
            ClearTarget(context);
        }

        private static void ClearTarget(ContextState context) {
            context.HasTarget = false;
        }
    }
}
