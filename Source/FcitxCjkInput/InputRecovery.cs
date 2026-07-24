using System;

namespace FcitxCjkInput {
    internal sealed class CommittedCharacterTracker {
        private ControlToken _target;
        private string _characters = "";
        private int _index;
        private int _length;
        private int _expiresAfterFrame = -1;

        public void Expect(ControlToken target, string text, int insertedLength, int frame) {
            var length = Math.Max(0, Math.Min(insertedLength, text.Length));
            if (target.Id == 0 || length == 0)
                return;

            var expected = text.Substring(0, length);
            if (!_target.Equals(target) || frame > _expiresAfterFrame || _index >= _length) {
                _target = target;
                _characters = expected;
                _index = 0;
                _length = expected.Length;
            } else {
                _characters = _characters.Substring(_index, _length - _index) + expected;
                _index = 0;
                _length = _characters.Length;
            }
            _expiresAfterFrame = frame + 1;
        }

        public bool ShouldSuppress(ControlToken target, char character, int frame) {
            if (character == '\0')
                return false;
            if (!_target.Equals(target) || frame > _expiresAfterFrame || _index >= _length) {
                Clear();
                return false;
            }
            if (_characters[_index] != character) {
                Clear();
                return false;
            }

            _index++;
            if (_index >= _length)
                Clear();
            return true;
        }

        public void Clear() {
            _target = default;
            _characters = "";
            _index = 0;
            _length = 0;
            _expiresAfterFrame = -1;
        }
    }

    internal sealed class DirectionalKeyState {
        private const int A = 1 << 0;
        private const int D = 1 << 1;
        private const int S = 1 << 2;
        private const int W = 1 << 3;

        private int _pressed;

        public void Update(int keyValue, bool release) {
            var mask = MaskFor(keyValue);
            if (release)
                _pressed &= ~mask;
            else
                _pressed |= mask;
        }

        public bool IsDown(int keyCode) {
            var mask = MaskFor(keyCode);
            return mask != 0 && (_pressed & mask) != 0;
        }

        public void Clear() {
            _pressed = 0;
        }

        private static int MaskFor(int keyValue) {
            switch (keyValue) {
                case 'a':
                case 'A':
                    return A;
                case 'd':
                case 'D':
                    return D;
                case 's':
                case 'S':
                    return S;
                case 'w':
                case 'W':
                    return W;
                default:
                    return 0;
            }
        }
    }

    internal static class GameplayKeyRecovery {
        public static bool ShouldRecover(bool original, bool textFieldActive, bool cameraDolly,
            int primaryKey, int secondaryKey, DirectionalKeyState keys) {
            return original || (!textFieldActive && cameraDolly &&
                (keys.IsDown(primaryKey) || keys.IsDown(secondaryKey)));
        }
    }
}
