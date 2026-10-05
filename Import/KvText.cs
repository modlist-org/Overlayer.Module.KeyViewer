using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Overlayer.Module.KeyViewer.Import;

// Translates KeyViewer text templates (with {Count}, {CurKPS}, ...) into
// Overlayer JS expressions evaluated by the V8 Fx engine.
//
// KeyViewer tags become Tag.* calls so they keep working inside the held
// ternary: Tag.KV_IsKeyHeld("A") ? (<pressed>) : (<released>).
// Unknown {Braces} are kept as literal text so the user can fix them manually.
public static class KvText {
    private static readonly Regex TagPattern = new Regex(@"\{([A-Za-z0-9_]+)(?::([^{}]*))?\}",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KeyString = new Dictionary<string, string> {
        { "Alpha0", "0" }, { "Alpha1", "1" }, { "Alpha2", "2" }, { "Alpha3", "3" },
        { "Alpha4", "4" }, { "Alpha5", "5" }, { "Alpha6", "6" }, { "Alpha7", "7" },
        { "Alpha8", "8" }, { "Alpha9", "9" },
        { "Keypad0", "0" }, { "Keypad1", "1" }, { "Keypad2", "2" }, { "Keypad3", "3" },
        { "Keypad4", "4" }, { "Keypad5", "5" }, { "Keypad6", "6" }, { "Keypad7", "7" },
        { "Keypad8", "8" }, { "Keypad9", "9" },
        { "KeypadPlus", "+" }, { "KeypadMinus", "-" }, { "KeypadMultiply", "*" },
        { "KeypadDivide", "/" }, { "KeypadEnter", "↵" }, { "KeypadEquals", "=" },
        { "KeypadPeriod", "." }, { "Return", "↵" }, { "Tab", "⇥" },
        { "Backslash", "\\\\" }, { "Slash", "/" }, { "Minus", "-" }, { "Equals", "=" },
        { "LeftBracket", "[" }, { "RightBracket", "]" }, { "Semicolon", ";" },
        { "Comma", "," }, { "Period", "." }, { "Quote", "'" },
        { "UpArrow", "↑" }, { "DownArrow", "↓" }, { "LeftArrow", "←" }, { "RightArrow", "→" },
        { "Space", "␣" }, { "BackQuote", "`" },
        { "LeftShift", "L⇧" }, { "RightShift", "R⇧" },
        { "LeftControl", "LCtrl" }, { "RightControl", "RCtrl" },
        { "LeftAlt", "LAlt" }, { "RightAlt", "RAlt" },
        { "Delete", "Del" }, { "PageDown", "Pg↓" }, { "PageUp", "Pg↑" },
        { "CapsLock", "⇪" }, { "Insert", "Ins" },
        { "Mouse0", "M0" }, { "Mouse1", "M1" }, { "Mouse2", "M2" },
        { "Mouse3", "M3" }, { "Mouse4", "M4" }, { "Mouse5", "M5" }, { "Mouse6", "M6" },
    };

    public static string DefaultKeyLabel(string code, string dummyName) {
        if (!string.IsNullOrEmpty(dummyName)) return dummyName;
        if (string.IsNullOrEmpty(code)) return "?";
        if (KeyString.TryGetValue(code, out var s)) return s;
        return code;
    }

    public static string ResolveDefault(string template, string fallback)
        => string.IsNullOrEmpty(template) ? fallback : template;

    // Compiles a KeyViewer template into a JS expression string.
    // Returns the expression and whether it contains any live (Tag) calls.
    // Counters resolve through the native KV_Count tag, which takes the
    // import prefix plus the key code and therefore needs no script file.
    public static string CompileTemplate(string template, string prefix, out bool hasLiveTags) {
        hasLiveTags = false;
        if (template == null) return "\"\"";
        var matches = TagPattern.Matches(template);
        if (matches.Count == 0) {
            return Quote(template);
        }
        var sb = new StringBuilder();
        int last = 0;
        bool first = true;
        foreach (Match m in matches) {
            if (m.Index > last) {
                AppendPart(sb, ref first, Quote(template.Substring(last, m.Index - last)));
            }
            string name = m.Groups[1].Value;
            string arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (TryTranslateTag(name, arg, prefix, out string js)) {
                hasLiveTags = true;
                AppendPart(sb, ref first, "(" + js + ")");
            } else {
                AppendPart(sb, ref first, Quote(m.Value));
            }
            last = m.Index + m.Length;
        }
        if (last < template.Length) {
            AppendPart(sb, ref first, Quote(template.Substring(last)));
        }
        if (first) return "\"\"";
        return sb.ToString();
    }

    private static void AppendPart(StringBuilder sb, ref bool first, string part) {
        if (!first) sb.Append(" + ");
        sb.Append(part);
        first = false;
    }

    private static bool TryTranslateTag(string name, string arg, string prefix, out string js) {
        js = null;
        string a = (arg ?? string.Empty).Trim();
        switch (name) {
            case "Count":
                js = "Tag.KV_Count(" + Quote(prefix ?? string.Empty) + ", "
                    + (string.IsNullOrEmpty(a) ? Quote(string.Empty) : Quote(a)) + ")";
                return true;
            case "CurKPS":
            case "MaxKPS":
            case "AvgKPS":
                // Let the Overlayer text engine apply its native Kps formatter.
                // F0 keeps the displayed rate at zero decimal places.
                js = Quote("{Kps:0,F0}");
                return true;
            default:
                return false;
        }
    }

    public static bool TemplateUsesCount(string template) {
        if (string.IsNullOrEmpty(template)) return false;
        foreach (Match m in TagPattern.Matches(template)) {
            if (string.Equals(m.Groups[1].Value, "Count", System.StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }

    // Builds the full held-state JS expression for a text slot.
    // pressedTemplate / releasedTemplate are already default-filled (non-null).
    public static string BuildHeldExpression(string code, string pressedTemplate, string releasedTemplate, bool isDummy, string prefix, out bool usesJs) {
        string p = CompileTemplate(pressedTemplate ?? string.Empty, prefix, out bool pLive);
        string r = CompileTemplate(releasedTemplate ?? string.Empty, prefix, out bool rLive);
        bool same = p == r;
        if (isDummy || same) {
            usesJs = pLive || rLive;
            // Even a "live" single-side expression must go through V8, so keep it as Fx.
            return r;
        }
        usesJs = true;
        return "(Tag.KV_IsKeyHeld(" + Quote(code) + ") ? (" + p + ") : (" + r + "))";
    }

    public static string Quote(string s) {
        if (s == null) return "\"\"";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s) {
            switch (c) {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20) {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("X4"));
                    } else {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string ColorArray(KvColor c) {
        return "[" + F(c.R) + ", " + F(c.G) + ", " + F(c.B) + ", " + F(c.A) + "]";
    }

    public static string GradientArray(KvGColor g) {
        if (!g.GradientEnabled) return ColorArray(g.TopLeft);
        return "[" + ColorArrayInner(g.TopLeft) + ", " + ColorArrayInner(g.TopRight)
            + ", " + ColorArrayInner(g.BottomLeft) + ", " + ColorArrayInner(g.BottomRight) + "]";
    }

    private static string ColorArrayInner(KvColor c) {
        return F(c.R) + ", " + F(c.G) + ", " + F(c.B) + ", " + F(c.A);
    }

    private static string F(float v) {
        return v.ToString("G9", CultureInfo.InvariantCulture);
    }

    public static string EscapeCode(string code) => code ?? "None";
}
