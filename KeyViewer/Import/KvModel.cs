using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace Overlayer.Module.KeyViewer.Import;

// Lightweight reader for KeyViewer v4 profile JSON.
// Only the fields needed for Canvas import are parsed.
// Blur and fonts are intentionally ignored; Rain is restored by the module runtime.
public sealed class KvColor {
    public float R = 1f;
    public float G = 1f;
    public float B = 1f;
    public float A = 1f;

    public Color ToUnity() => new Color(R, G, B, A);

    public static KvColor FromToken(JToken token) {
        var c = new KvColor();
        if (token is JArray arr) {
            if (arr.Count > 0) c.R = ToFloat(arr[0], c.R);
            if (arr.Count > 1) c.G = ToFloat(arr[1], c.G);
            if (arr.Count > 2) c.B = ToFloat(arr[2], c.B);
            if (arr.Count > 3) c.A = ToFloat(arr[3], c.A);
        }
        return c;
    }

    private static float ToFloat(JToken token, float fallback) {
        try {
            return token == null ? fallback : Convert.ToSingle(token.ToObject<object>());
        } catch {
            return fallback;
        }
    }
}

public sealed class KvGColor {
    public KvColor TopLeft = new KvColor();
    public KvColor TopRight = new KvColor();
    public KvColor BottomLeft = new KvColor();
    public KvColor BottomRight = new KvColor();
    public bool GradientEnabled;

    public static KvGColor FromToken(JToken node) {
        var g = new KvGColor();
        if (node == null || node.Type != JTokenType.Object) {
            return g;
        }
        var tl = node["topLeft"];
        var tr = node["topRight"];
        var bl = node["bottomLeft"];
        var br = node["bottomRight"];
        if (tl != null) g.TopLeft = KvColor.FromToken(tl);
        if (tr != null) g.TopRight = KvColor.FromToken(tr);
        else g.TopRight = g.TopLeft;
        if (bl != null) g.BottomLeft = KvColor.FromToken(bl);
        else g.BottomLeft = g.TopLeft;
        if (br != null) g.BottomRight = KvColor.FromToken(br);
        else g.BottomRight = g.TopLeft;
        g.GradientEnabled = tr != null || bl != null || br != null;
        if (!g.GradientEnabled) {
            g.TopRight = g.TopLeft;
            g.BottomLeft = g.TopLeft;
            g.BottomRight = g.TopLeft;
        }
        return g;
    }
}

public sealed class KvEase {
    public string Type = "Unset";
    public float Duration;

    public bool Enabled => Duration > 0f && !string.Equals(Type, "Unset", StringComparison.OrdinalIgnoreCase);

    public static KvEase FromToken(JToken token, string side) {
        var ease = new KvEase();
        var easeToken = token?[side + "Ease"];
        if (easeToken == null) return ease;
        ease.Type = easeToken["Ease"]?.Value<string>() ?? "Unset";
        try { ease.Duration = Math.Max(0f, easeToken["Duration"]?.Value<float>() ?? 0f); } catch { }
        return ease;
    }
}

public sealed class KvMotion2 {
    public Vector2 Released;
    public Vector2 Pressed;
    public KvEase PressedEase = new KvEase();
    public KvEase ReleasedEase = new KvEase();

    public KvMotion2(Vector2 value) { Released = value; Pressed = value; }

    public static KvMotion2 FromToken(JToken token, Vector2 fallback) {
        var motion = new KvMotion2(fallback);
        if (token == null) return motion;
        motion.Released = ReadVector2(token["Released"], token.Type == JTokenType.Array ? token : null, fallback);
        motion.Pressed = ReadVector2(token["Pressed"], null, motion.Released);
        motion.PressedEase = KvEase.FromToken(token, "Pressed");
        motion.ReleasedEase = KvEase.FromToken(token, "Released");
        return motion;
    }

    private static Vector2 ReadVector2(JToken token, JToken directArray, Vector2 fallback) {
        token ??= directArray;
        if (token is not JArray arr || arr.Count < 2) return fallback;
        try { return new Vector2(arr[0].Value<float>(), arr[1].Value<float>()); } catch { return fallback; }
    }
}

public sealed class KvMotion3 {
    public Vector3 Released;
    public Vector3 Pressed;
    public KvEase PressedEase = new KvEase();
    public KvEase ReleasedEase = new KvEase();

    public KvMotion3(Vector3 value) { Released = value; Pressed = value; }

    public static KvMotion3 FromToken(JToken token, Vector3 fallback) {
        var motion = new KvMotion3(fallback);
        if (token == null) return motion;
        motion.Released = ReadVector3(token["Released"], token.Type == JTokenType.Array ? token : null, fallback);
        motion.Pressed = ReadVector3(token["Pressed"], null, motion.Released);
        motion.PressedEase = KvEase.FromToken(token, "Pressed");
        motion.ReleasedEase = KvEase.FromToken(token, "Released");
        return motion;
    }

    private static Vector3 ReadVector3(JToken token, JToken directArray, Vector3 fallback) {
        token ??= directArray;
        if (token is not JArray arr || arr.Count < 3) return fallback;
        try { return new Vector3(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>()); } catch { return fallback; }
    }
}

public sealed class KvMotionColor {
    public KvGColor Released = new KvGColor();
    public KvGColor Pressed = new KvGColor();
    public KvEase PressedEase = new KvEase();
    public KvEase ReleasedEase = new KvEase();
}

public sealed class KvKey {
    public string Code = "None";
    public string DummyName;
    public string Font = "Default";
    public bool EnableCountText = true;
    public bool EnableOutlineImage = true;
    public bool DisableSorting;
    public bool DoNotScaleText = true;
    public bool RainEnabled;
    public JObject RainToken = new JObject();
    public float TextFontSize = 75f;
    public float CountTextFontSize = 50f;
    public string DisplayName => string.IsNullOrEmpty(DummyName) ? Code : DummyName;
    public bool IsDummy => !string.IsNullOrEmpty(DummyName);

    public string TextPressed;
    public string TextReleased;
    public string CountTextPressed;
    public string CountTextReleased;
    public string BgPressedPath;
    public string BgReleasedPath;
    public string OlPressedPath;
    public string OlReleasedPath;

    public KvMotionColor TextColor = new KvMotionColor();
    public KvMotionColor CountColor = new KvMotionColor();
    public KvMotionColor BackgroundColor = new KvMotionColor();
    public KvMotionColor OutlineColor = new KvMotionColor();

    public KvMotion2 KeyOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 KeyScale = new KvMotion2(Vector2.one);
    public KvMotion3 KeyRotation = new KvMotion3(Vector3.zero);

    public KvMotion2 BgOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 BgScale = new KvMotion2(Vector2.one);
    public KvMotion3 BgRotation = new KvMotion3(Vector3.zero);
    public KvMotion2 OlOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 OlScale = new KvMotion2(Vector2.one);
    public KvMotion3 OlRotation = new KvMotion3(Vector3.zero);
    public KvMotion2 TextOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 TextScale = new KvMotion2(Vector2.one);
    public KvMotion3 TextRotation = new KvMotion3(Vector3.zero);
    public KvMotion2 CountOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 CountScale = new KvMotion2(Vector2.one);
    public KvMotion3 CountRotation = new KvMotion3(Vector3.zero);

    // Raw Pivot/Anchor flag values, mirroring KeyViewer's enums.
    // MiddleCenter defaults (Pivot 18, Anchor 34).
    public int KeyPivot = 18;
    public int KeyAnchor = 34;
    public int BgPivot = 18;
    public int BgAnchor = 34;
    public int OlPivot = 18;
    public int OlAnchor = 34;
    public int TextPivot = 18;
    public int TextAnchor = 34;
    public int CountPivot = 18;
    public int CountAnchor = 34;
}

public sealed class KvReference {
    public string ReferenceType = string.Empty;
    public string From = string.Empty;
    public string Name = string.Empty;
    public byte[] Raw = Array.Empty<byte>();
}

public sealed class KvProfile {
    public List<KvKey> Keys = new List<KvKey>();
    public float KeySpacing = 10f;
    public KvMotion2 VecOffset = new KvMotion2(Vector2.zero);
    public KvMotion2 VecScale = new KvMotion2(Vector2.one);
    public KvMotion3 VecRotation = new KvMotion3(Vector3.zero);
    public int VecPivot = 18;
    public int VecAnchor = 34;
    public List<KvReference> References = new List<KvReference>();

    public static bool TryParse(JToken root, out KvProfile profile, out string error) {
        profile = new KvProfile();
        error = null;
        try {
            if (root == null || root.Type != JTokenType.Object) {
                error = "Root is not an object.";
                return false;
            }
            profile.KeySpacing = GetFloat(root["KeySpacing"], 10f);
            var vec = root["VectorConfig"];
            if (vec != null) {
                profile.VecOffset = KvMotion2.FromToken(vec["Offset"], Vector2.zero);
                profile.VecScale = KvMotion2.FromToken(vec["Scale"], Vector2.one);
                profile.VecRotation = KvMotion3.FromToken(vec["Rotation"], Vector3.zero);
                profile.VecPivot = GetPivotValue(vec["Pivot"], 18);
                profile.VecAnchor = GetAnchorValue(vec["Anchor"], 34);
            }
            var keys = root["Keys"];
            if (keys is JArray arr) {
                foreach (var k in arr) {
                    profile.Keys.Add(ParseKey(k));
                }
            }
            var refs = root["References"];
            if (refs is JArray rarr) {
                foreach (var r in rarr) {
                    var reference = ParseReference(r);
                    if (reference != null) profile.References.Add(reference);
                }
            }
            return true;
        } catch (Exception e) {
            error = e.Message;
            return false;
        }
    }

    private static KvKey ParseKey(JToken node) {
        var k = new KvKey();
        if (node == null) return k;
        k.Code = node["Code"]?.Value<string>() ?? "None";
        k.DummyName = node["DummyName"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(k.DummyName)) k.DummyName = null;
        k.Font = node["Font"]?.Value<string>() ?? "Default";
        k.EnableCountText = GetBool(node["EnableCountText"], true);
        k.EnableOutlineImage = GetBool(node["EnableOutlineImage"], true);
        k.DisableSorting = GetBool(node["DisableSorting"], false);
        k.DoNotScaleText = GetBool(node["DoNotScaleText"], true);
        k.RainEnabled = GetBool(node["RainEnabled"], false);
        k.RainToken = node["Rain"] is JObject rainNode
            ? (JObject)rainNode.DeepClone()
            : new JObject();
        k.TextFontSize = GetFloat(node["TextFontSize"], 75f);
        k.CountTextFontSize = GetFloat(node["CountTextFontSize"], 50f);

        var text = node["Text"];
        k.TextPressed = GetPRString(text, "Pressed", null);
        k.TextReleased = GetPRString(text, "Released", null);
        var countText = node["CountText"];
        k.CountTextPressed = GetPRString(countText, "Pressed", null);
        k.CountTextReleased = GetPRString(countText, "Released", null);
        var bg = node["Background"];
        k.BgPressedPath = GetPRString(bg, "Pressed", null);
        k.BgReleasedPath = GetPRString(bg, "Released", null);
        var ol = node["Outline"];
        k.OlPressedPath = GetPRString(ol, "Pressed", null);
        k.OlReleasedPath = GetPRString(ol, "Released", null);

        k.TextColor = GetColorMotion(node["TextConfig"], new Color(0f, 0f, 0f, 1f), Color.white);
        k.CountColor = GetColorMotion(node["CountTextConfig"], new Color(0f, 0f, 0f, 1f), Color.white);
        k.BackgroundColor = GetColorMotion(node["BackgroundConfig"], Color.white, new Color(0f, 0f, 0f, 0.4f));
        k.OutlineColor = GetColorMotion(node["OutlineConfig"], Color.white, Color.white);

        var kvec = node["VectorConfig"];
        if (kvec != null) {
            k.KeyOffset = KvMotion2.FromToken(kvec["Offset"], Vector2.zero);
            k.KeyScale = KvMotion2.FromToken(kvec["Scale"], Vector2.one);
            k.KeyRotation = KvMotion3.FromToken(kvec["Rotation"], Vector3.zero);
            k.KeyPivot = GetPivotValue(kvec["Pivot"], 18);
            k.KeyAnchor = GetAnchorValue(kvec["Anchor"], 34);
        }
        var bgVec = node["BackgroundConfig"]?["VectorConfig"];
        if (bgVec != null) {
            k.BgOffset = KvMotion2.FromToken(bgVec["Offset"], Vector2.zero);
            k.BgScale = KvMotion2.FromToken(bgVec["Scale"], Vector2.one);
            k.BgRotation = KvMotion3.FromToken(bgVec["Rotation"], Vector3.zero);
            k.BgPivot = GetPivotValue(bgVec["Pivot"], 18);
            k.BgAnchor = GetAnchorValue(bgVec["Anchor"], 34);
        }
        var olVec = node["OutlineConfig"]?["VectorConfig"];
        if (olVec != null) {
            k.OlOffset = KvMotion2.FromToken(olVec["Offset"], Vector2.zero);
            k.OlScale = KvMotion2.FromToken(olVec["Scale"], Vector2.one);
            k.OlRotation = KvMotion3.FromToken(olVec["Rotation"], Vector3.zero);
            k.OlPivot = GetPivotValue(olVec["Pivot"], 18);
            k.OlAnchor = GetAnchorValue(olVec["Anchor"], 34);
        }
        var textVec = node["TextConfig"]?["VectorConfig"];
        if (textVec != null) {
            k.TextOffset = KvMotion2.FromToken(textVec["Offset"], Vector2.zero);
            k.TextScale = KvMotion2.FromToken(textVec["Scale"], Vector2.one);
            k.TextRotation = KvMotion3.FromToken(textVec["Rotation"], Vector3.zero);
            k.TextPivot = GetPivotValue(textVec["Pivot"], 18);
            k.TextAnchor = GetAnchorValue(textVec["Anchor"], 34);
        }
        var countVec = node["CountTextConfig"]?["VectorConfig"];
        if (countVec != null) {
            k.CountOffset = KvMotion2.FromToken(countVec["Offset"], Vector2.zero);
            k.CountScale = KvMotion2.FromToken(countVec["Scale"], Vector2.one);
            k.CountRotation = KvMotion3.FromToken(countVec["Rotation"], Vector3.zero);
            k.CountPivot = GetPivotValue(countVec["Pivot"], 18);
            k.CountAnchor = GetAnchorValue(countVec["Anchor"], 34);
        }
        return k;
    }

    private static KvReference ParseReference(JToken node) {
        if (node == null || node.Type != JTokenType.Object) return null;
        var r = new KvReference {
            ReferenceType = node["ReferenceType"]?.Value<string>() ?? string.Empty,
            From = node["From"]?.Value<string>() ?? string.Empty,
            Name = node["Name"]?.Value<string>() ?? string.Empty,
        };
        var raw = node["Raw"];
        if (raw == null) return r;
        try {
            byte[] compressed;
            if (raw.Type == JTokenType.Array) {
                compressed = raw.Values<byte>().ToArray();
            } else {
                compressed = Convert.FromBase64String(raw.Value<string>());
            }
            r.Raw = Decompress(compressed);
        } catch {
            r.Raw = Array.Empty<byte>();
        }
        return r;
    }

    private static byte[] Decompress(byte[] data) {
        if (data == null || data.Length == 0) return Array.Empty<byte>();
        using (var input = new MemoryStream(data))
        using (var dstream = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream()) {
            dstream.CopyTo(output);
            return output.ToArray();
        }
    }

    private static bool GetBool(JToken token, bool fallback) {
        if (token == null) return fallback;
        try { return token.Value<bool>(); } catch { return fallback; }
    }

    // Raw Pivot/Anchor flag values, mirroring KeyViewer's enums
    // (Pivot: Top 1, MiddleV 2, Bottom 4, Left 8, CenterH 16, Right 32;
    // Anchor: Left 1, Center 2, Right 4, HStretch 8, Top 16, Middle 32,
    // Bottom 64, VStretch 128). Unknown names fall back like a missing field.
    private static readonly Dictionary<string, int> PivotNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        ["None"] = 0, ["Top"] = 1, ["MiddleV"] = 2, ["Bottom"] = 4,
        ["Left"] = 8, ["CenterH"] = 16, ["Right"] = 32,
        ["TopLeft"] = 9, ["TopCenter"] = 17, ["TopRight"] = 33,
        ["MiddleLeft"] = 10, ["MiddleCenter"] = 18, ["MiddleRight"] = 34,
        ["BottomLeft"] = 12, ["BottomCenter"] = 20, ["BottomRight"] = 36,
    };

    private static readonly Dictionary<string, int> AnchorNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        ["None"] = 0, ["Left"] = 1, ["Center"] = 2, ["Right"] = 4,
        ["HStretch"] = 8, ["Top"] = 16, ["Middle"] = 32, ["Bottom"] = 64,
        ["VStretch"] = 128,
        ["TopLeft"] = 17, ["TopCenter"] = 18, ["TopRight"] = 20,
        ["MiddleLeft"] = 33, ["MiddleCenter"] = 34, ["MiddleRight"] = 36,
        ["BottomLeft"] = 65, ["BottomCenter"] = 66, ["BottomRight"] = 68,
        ["HorizontalStretchTop"] = 24, ["HorizontalStretchMiddle"] = 40, ["HorizontalStretchBottom"] = 72,
        ["VerticalStretchLeft"] = 129, ["VerticalStretchCenter"] = 130, ["VerticalStretchRight"] = 132,
        ["FullStretch"] = 136,
    };

    private static int GetFlagValue(JToken token, Dictionary<string, int> names, int fallback) {
        if (token == null) return fallback;
        try {
            string s = token.Value<string>();
            if (string.IsNullOrEmpty(s)) return fallback;
            if (int.TryParse(s, out int numeric)) return numeric;
            if (names.TryGetValue(s.Trim(), out int value)) return value;
        } catch { }
        return fallback;
    }

    private static int GetPivotValue(JToken token, int fallback)
        => GetFlagValue(token, PivotNames, fallback);

    private static int GetAnchorValue(JToken token, int fallback)
        => GetFlagValue(token, AnchorNames, fallback);

    private static float GetFloat(JToken token, float fallback) {
        if (token == null) return fallback;
        try { return token.Value<float>(); } catch { return fallback; }
    }

    private static string GetPRString(JToken pr, string side, string fallback) {
        if (pr == null) return fallback;
        var v = pr[side];
        if (v == null || v.Type == JTokenType.Null) return fallback;
        try { return v.Value<string>(); } catch { return fallback; }
    }

    private static KvMotionColor GetColorMotion(JToken objectConfig, Color pressedFallback, Color releasedFallback) {
        var motion = new KvMotionColor {
            Pressed = new KvGColor { TopLeft = new KvColor { R = pressedFallback.r, G = pressedFallback.g, B = pressedFallback.b, A = pressedFallback.a } },
            Released = new KvGColor { TopLeft = new KvColor { R = releasedFallback.r, G = releasedFallback.g, B = releasedFallback.b, A = releasedFallback.a } }
        };
        try {
            var color = objectConfig?["Color"];
            if (color == null) return motion;
            motion.Released = color["Released"] != null ? KvGColor.FromToken(color["Released"]) : motion.Released;
            motion.Pressed = color["Pressed"] != null ? KvGColor.FromToken(color["Pressed"]) : motion.Released;
            motion.PressedEase = KvEase.FromToken(color, "Pressed");
            motion.ReleasedEase = KvEase.FromToken(color, "Released");
        } catch { }
        return motion;
    }
}
