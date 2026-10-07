using O5Kit.Core;
using O5Kit.Input;
using Overlayer.Tag.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Overlayer.Module.KeyViewer.Tag;

public static class KeyViewerInput {
    private static int cacheFrame = -1;
    private static readonly Dictionary<KeyCode, bool> held = new();
    private static int kpsFrame = -1;
    private static double kpsCache;
    private static readonly Dictionary<string, EaseState> eased = new(StringComparer.Ordinal);

    private sealed class EaseState {
        public bool Initialized;
        public bool Pressed;
        public float From;
        public float To;
        public float StartTime;
        public float Duration;
        public string Ease = "Unset";
        public int CacheFrame = -1;
        public double CacheValue;
    }

    /// <summary>
    /// String-safe input tag for KeyViewer-generated Fx. The core IsKeyHeld
    /// tag accepts a KeyCode enum and cannot be called with a JS string via
    /// TagAccessHelper.Get; this tag intentionally accepts the serialized enum
    /// name and caches the result for the current Unity frame.
    /// </summary>
    [Tag(Name = "KV_IsKeyHeld", Desc = "[KeyViewer] Returns true while the specified Unity key is held. Ex) {KV_IsKeyHeld:A}, {KV_IsKeyHeld:Space}")]
    public static bool IsKeyHeld(string keyName) {
        if (string.IsNullOrWhiteSpace(keyName)
            || !Enum.TryParse(keyName.Trim(), true, out KeyCode key)
            || key == KeyCode.None) {
            return false;
        }

        int frame = Time.frameCount;
        if (frame != cacheFrame) {
            held.Clear();
            cacheFrame = frame;
        }

        if (held.TryGetValue(key, out bool isHeld)) {
            return isHeld;
        }

        try {
            if (key is >= KeyCode.Mouse0 and <= KeyCode.Mouse2) {
                int button = (int)key - (int)KeyCode.Mouse0;
                isHeld = O5Input.GetMouseButton(button);
                if (!isHeld) isHeld = UnityEngine.Input.GetKey(key);
            } else if (key is >= KeyCode.Mouse3 and <= KeyCode.Mouse6) {
                isHeld = UnityEngine.Input.GetKey(key);
            } else {
                isHeld = O5Input.GetKey(key);
                // O5Input maps the common KeyCode values to Input System keys.
                // Some legacy KeyCodes (notably Keypad*) do not have an exact
                // Input System enum name, so preserve Unity's legacy path too.
                if (!isHeld) isHeld = UnityEngine.Input.GetKey(key);
            }
        } catch {
            isHeld = false;
        }

        held[key] = isHeld;
        return isHeld;
    }

    /// <summary>
    /// Interpolates one scalar of a KeyViewer PressRelease value. All
    /// arguments stay strings across ClearScript's JS-to-CLR boundary.
    /// <paramref name="keyCodes"/> is one KeyCode name or a pipe-separated
    /// set for KeyViewer's profile-level (any key pressed) animation.
    /// </summary>
    [Tag(Name = "KV_EaseScalar", Desc = "[KeyViewer] Smoothly interpolates a KeyViewer PressRelease scalar")]
    public static double EaseScalar(
        string keyCodes,
        string slot,
        string releasedValue,
        string pressedValue,
        string pressedDuration,
        string pressedEase,
        string releasedDuration,
        string releasedEase
    ) {
        if (string.IsNullOrEmpty(slot)) return ParseNumber(releasedValue);

        int frame = Time.frameCount;
        string stateKey = (keyCodes ?? string.Empty) + "\u001f" + slot;
        if (!eased.TryGetValue(stateKey, out var state)) {
            if (eased.Count > 8192) eased.Clear();
            state = new EaseState();
            eased[stateKey] = state;
        }
        if (state.CacheFrame == frame) return state.CacheValue;

        float released = ParseNumber(releasedValue);
        float pressed = ParseNumber(pressedValue);
        bool isPressed = AnyKeyHeld(keyCodes);
        // Unscaled clock: KeyViewer tweens keep running while the game is
        // paused (timeScale = 0 freezes Time.time/Time.deltaTime).
        float now = Time.unscaledTime;

        if (!state.Initialized) {
            state.Initialized = true;
            state.Pressed = false;
            state.From = released;
            state.To = released;
            state.StartTime = now;
        }

        float current = Evaluate(state, now);
        if (isPressed != state.Pressed) {
            state.From = current;
            state.To = isPressed ? pressed : released;
            state.StartTime = now;
            state.Pressed = isPressed;
            state.Duration = Math.Max(0f, ParseNumber(isPressed ? pressedDuration : releasedDuration));
            state.Ease = isPressed ? pressedEase : releasedEase;
        }

        state.CacheValue = Evaluate(state, now);
        state.CacheFrame = frame;
        return state.CacheValue;
    }

    private static bool AnyKeyHeld(string keyCodes) {
        if (string.IsNullOrWhiteSpace(keyCodes)) return false;
        var codes = keyCodes.Split(new[]{'|'});
        for (int i = 0; i < codes.Length; i++) {
            if (IsKeyHeld(codes[i])) return true;
        }
        return false;
    }

    private static float Evaluate(EaseState state, float now) {
        if (state.Duration <= 0f) return state.To;
        float t = Mathf.Clamp01((now - state.StartTime) / state.Duration);
        float easedT = EaseProgress(state.Ease, t);
        return state.From + (state.To - state.From) * easedT;
    }

    public static float EaseProgress(string name, float t) {
        // Overlayer's Ease system is the O5Ease enum. It exposes no public
        // scalar evaluator, so curves are evaluated here dispatched on the
        // enum value (same LitMotion curve set O5Kit uses internally).
        if (!Enum.TryParse(name, true, out O5Ease ease)) ease = O5Ease.Linear;
        return EvaluateEase(ease, t);
    }

    private static float ParseNumber(string value) {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : 0f;
    }

    private static float EvaluateEase(O5Ease ease, float t) {
        float x = Math.Max(0f, Math.Min(1f, t));
        return ease switch {
            O5Ease.Linear => x,
            O5Ease.InSine => 1f - (float)Math.Cos(x * (float)Math.PI * 0.5f),
            O5Ease.OutSine => (float)Math.Sin(x * (float)Math.PI * 0.5f),
            O5Ease.InOutSine => -((float)Math.Cos((float)Math.PI * x) - 1f) * 0.5f,
            O5Ease.InQuad => x * x,
            O5Ease.OutQuad => 1f - (1f - x) * (1f - x),
            O5Ease.InOutQuad => x < 0.5f ? 2f * x * x : 1f - (float)Math.Pow(-2f * x + 2f, 2f) / 2f,
            O5Ease.InCubic => x * x * x,
            O5Ease.OutCubic => 1f - (float)Math.Pow(1f - x, 3f),
            O5Ease.InOutCubic => x < 0.5f ? 4f * x * x * x : 1f - (float)Math.Pow(-2f * x + 2f, 3f) / 2f,
            O5Ease.InQuart => x * x * x * x,
            O5Ease.OutQuart => 1f - (float)Math.Pow(1f - x, 4f),
            O5Ease.InOutQuart => x < 0.5f ? 8f * x * x * x * x : 1f - (float)Math.Pow(-2f * x + 2f, 4f) / 2f,
            O5Ease.InQuint => x * x * x * x * x,
            O5Ease.OutQuint => 1f - (float)Math.Pow(1f - x, 5f),
            O5Ease.InOutQuint => x < 0.5f ? 16f * x * x * x * x * x : 1f - (float)Math.Pow(-2f * x + 2f, 5f) / 2f,
            O5Ease.InExpo => x <= 0f ? 0f : (float)Math.Pow(2f, 10f * x - 10f),
            O5Ease.OutExpo => x >= 1f ? 1f : 1f - (float)Math.Pow(2f, -10f * x),
            O5Ease.InOutExpo => x <= 0f ? 0f : x >= 1f ? 1f : x < 0.5f ? (float)Math.Pow(2f, 20f * x - 10f) / 2f : (2f - (float)Math.Pow(2f, -20f * x + 10f)) / 2f,
            O5Ease.InCirc => 1f - (float)Math.Sqrt(1f - x * x),
            O5Ease.OutCirc => (float)Math.Sqrt(1f - (float)Math.Pow(x - 1f, 2f)),
            O5Ease.InOutCirc => x < 0.5f ? (1f - (float)Math.Sqrt(1f - 4f * x * x)) / 2f : ((float)Math.Sqrt(1f - (float)Math.Pow(-2f * x + 2f, 2f)) + 1f) / 2f,
            O5Ease.InBack => 2.70158f * x * x * x - 1.70158f * x * x,
            O5Ease.OutBack => 1f + 2.70158f * (float)Math.Pow(x - 1f, 3f) + 1.70158f * (float)Math.Pow(x - 1f, 2f),
            O5Ease.InOutBack => x < 0.5f ? (4f * x * x * ((3.59491f * 2f * x) - 2.59491f)) / 2f : ((float)Math.Pow(2f * x - 2f, 2f) * ((3.59491f * (2f * x - 2f)) + 2.59491f) + 2f) / 2f,
            O5Ease.OutBounce => BounceOut(x),
            O5Ease.InBounce => 1f - BounceOut(1f - x),
            O5Ease.InOutBounce => x < 0.5f ? (1f - BounceOut(1f - 2f * x)) / 2f : (1f + BounceOut(2f * x - 1f)) / 2f,
            O5Ease.InElastic => x <= 0f ? 0f : x >= 1f ? 1f : -(float)Math.Pow(2f, 10f * x - 10f) * (float)Math.Sin((10f * x - 10.75f) * 2.0944f),
            O5Ease.OutElastic => x <= 0f ? 0f : x >= 1f ? 1f : (float)Math.Pow(2f, -10f * x) * (float)Math.Sin((10f * x - 0.75f) * 2.0944f) + 1f,
            // Note: O5Kit's private ApplyEase copy drops the trailing +1 here,
            // but the live LitMotion path (EaseUtility.InOutElastic, which is
            // what Overlayer actually renders) keeps it so f(1) == 1.
            O5Ease.InOutElastic => x <= 0f ? 0f : x >= 1f ? 1f : x < 0.5f ? -((float)Math.Pow(2f, 20f * x - 10f) * (float)Math.Sin((20f * x - 11.125f) * 1.39626f)) / 2f : (float)Math.Pow(2f, -20f * x + 10f) * (float)Math.Sin((20f * x - 11.125f) * 1.39626f) / 2f + 1f,
            _ => x,
        };
    }

    private static float BounceOut(float t) {
        const float n = 7.5625f;
        const float d = 2.75f;
        if (t < 1f / d) return n * t * t;
        if (t < 2f / d) { t -= 1.5f / d; return n * t * t + 0.75f; }
        if (t < 2.5f / d) { t -= 2.25f / d; return n * t * t + 0.9375f; }
        t -= 2.625f / d;
        return n * t * t + 0.984375f;
    }

    [Tag(Name = "KV_Kps", Desc = "[KeyViewer] Global key presses per second (approximation)")]
    public static double Kps {
        get {
            int frame = Time.frameCount;
            if (frame != kpsFrame) {
                kpsCache = Overlayer.TagImpl.KpsTracker.Instance?.Rate(0) ?? 0d;
                kpsFrame = frame;
            }
            return kpsCache;
        }
    }
}
