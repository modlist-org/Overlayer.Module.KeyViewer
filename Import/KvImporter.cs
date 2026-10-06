using Newtonsoft.Json.Linq;
using Overlayer.Core;
using Overlayer.IO.Fx;
using Overlayer.IO.Overlay;
using Overlayer.IO.UnityComponent.Impl;
using Overlayer.IO.User;
using Overlayer.Overlay;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

#if ML && IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace Overlayer.Module.KeyViewer.Import;

public sealed class KvImportResult {
    public bool Success;
    public string Error;
    public string CanvasName;
    public List<string> Warnings = new List<string>();
}

public static class KvImporter {
    private static readonly Regex HashCountPattern = new Regex(
        "Tag\\.KV_[0-9a-fA-F]{7}_Count\\(",
        RegexOptions.Compiled);
    private static readonly Regex HashResetPattern = new Regex(
        "Tag\\.KV_[0-9a-fA-F]{7}_ResetCounts\\(\\)",
        RegexOptions.Compiled);
    private static readonly Regex BareCountPattern = new Regex(
        "Tag\\.KV_Count\\((?!\"KeyViewer_)",
        RegexOptions.Compiled);
    private static readonly Regex BareResetPattern = new Regex(
        "Tag\\.KV_ResetCounts\\(\\s*\\)",
        RegexOptions.Compiled);
    private static readonly Regex CollectCountPattern = new Regex(
        "Tag\\.KV_(?:[0-9a-fA-F]{7}_)?Count\\(\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]*)\"\\s*\\)",
        RegexOptions.Compiled);
    private static readonly Regex CollectEasePattern = new Regex(
        "Tag\\.KV_EaseScalar\\(\\s*\"([^\"]*)\"",
        RegexOptions.Compiled);

    public static void MigrateExistingCanvases() {
        bool changed = false;
        foreach (var canvas in OverlayCore.Canvases) {
            string canvasName = canvas?.Config?.Name?.Value;
            if (string.IsNullOrEmpty(canvasName)
                || !canvasName.StartsWith("KeyViewer", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            bool canvasSettingsChanged = false;
            if (canvas.Config.CanvasConfig.SortingOrder.Value != 200) {
                canvas.Config.CanvasConfig.SortingOrder.Value = 200;
                canvasSettingsChanged = true;
            }
            if (canvas.Config.CanvasScalerConfig.ReferenceResolution.Value != new Vector2(1280f, 720f)) {
                canvas.Config.CanvasScalerConfig.ReferenceResolution.Value = new Vector2(1280f, 720f);
                canvasSettingsChanged = true;
            }
            if (!Mathf.Approximately(canvas.Config.CanvasScalerConfig.MatchWidthOrHeight.Value, 0.5f)) {
                canvas.Config.CanvasScalerConfig.MatchWidthOrHeight.Value = 0.5f;
                canvasSettingsChanged = true;
            }
            if (canvas.Config.CanvasScalerConfig.ScreenMatchMode.Value != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight) {
                canvas.Config.CanvasScalerConfig.ScreenMatchMode.Value = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                canvasSettingsChanged = true;
            }
            if (canvas.Config.GraphicRaycasterConfig.Enabled.Value) {
                canvas.Config.GraphicRaycasterConfig.Enabled.Value = false;
                canvasSettingsChanged = true;
            }
            if (canvas.Config.CanvasGroupConfig.BlocksRaycasts.Value) {
                canvas.Config.CanvasGroupConfig.BlocksRaycasts.Value = false;
                canvasSettingsChanged = true;
            }
            if (canvasSettingsChanged) canvas.ApplyConfig();
            changed |= canvasSettingsChanged;

            // Resolve this canvas to the unified KeyViewer_(hash7)_Name scheme.
            string hash;
            string newName;
            if (KvIdentityBuilder.IsPrefixed(canvasName)) {
                hash = KvIdentityBuilder.HashFromPrefixed(canvasName);
                newName = canvasName;
            } else {
                hash = KvIdentityBuilder.Hash7(canvas.Serialize().ToString());
                string suffix = canvasName.Substring("KeyViewer".Length).TrimStart(' ', '_');
                var identity = new KvIdentity {
                    Hash7 = hash,
                    Name = KvIdentityBuilder.Sanitize(string.IsNullOrEmpty(suffix) ? "Profile" : suffix)
                };
                identity.Prefix = "KeyViewer_" + identity.Hash7 + "_" + identity.Name;
                newName = identity.Prefix;
            }
            var target = new KvIdentity { Hash7 = hash, Name = string.Empty, Prefix = newName };
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in canvas.OvObjects) {
                MigrateObject(root, target.Prefix, ref changed);
                CollectCodes(root, codes);
            }
            // Total() must know every code even on canvases that display no
            // per-key text, so harvest them from the migrated expressions.
            // (Fresh imports register from the profile instead.)
            if (codes.Count > 0) {
                try { KvCountStore.Register(newName, codes); } catch { }
            }
            if (newName != canvasName) {
                KvRainStore.MoveSidecar(canvasName, newName);
                canvas.Config.Name.Value = newName;
                canvas.ApplyConfig();
                changed = true;
            }
        }
        if (changed) {
            OverlayCore.SaveAllCanvases();
            Core.Logger.Msg("[KeyViewer] Migrated existing canvases to native count tags.");
        }
    }

    // Deletes the legacy shared helper (KeyViewer.js) if it is one of ours.
    // Its global KV_Count tag would otherwise shadow the native C# tag after
    // a script reload, sending counts back to session-only memory.
    public static void RemoveLegacySharedHelper() {
        try {
            string path = Path.Combine(MainCore.Paths.JSPath, "Script", "KeyViewer.js");
            if (!File.Exists(path)) return;
            string content = File.ReadAllText(path);
            if (!content.Contains("Auto-generated by Overlayer.Module.KeyViewer")) return;
            File.Delete(path);
        } catch (System.Exception e) {
            Core.Logger.Wrn($"[KeyViewer] Legacy JS helper removal failed: {e.Message}");
        }
    }

    private static void MigrateObject(OvObject obj, string prefix, ref bool changed) {
        if (obj == null) return;
        bool changedBefore = changed;
        var config = obj.Config;
        MigrateDefaultSprite(config?.ImageConfig?.SpriteKey, config?.ImageConfig, ref changed);
        ReplaceExpression(config?.ImageConfig?.SpriteKey, prefix, ref changed);
        ReplaceExpression(config?.ImageConfig?.Color, prefix, ref changed);
        ReplaceExpression(config?.TextConfig?.Color, prefix, ref changed);
        ReplaceExpression(config?.TextEngineConfig?.PlayingText, prefix, ref changed);
        ReplaceExpression(config?.TextEngineConfig?.NotPlayingText, prefix, ref changed);
        foreach (var child in obj.Children) {
            MigrateObject(child, prefix, ref changed);
        }
        if (changed != changedBefore) {
            obj.ApplyComponent();
            obj.ApplyConfig();
        }
    }

    // Harvests every key code referenced by a canvas so Total() can feed
    // them even when the canvas shows no per-key count text. Runs over the
    // migrated (native-tag) expressions.
    private static void CollectCodes(OvObject obj, HashSet<string> codes) {
        if (obj == null || codes == null) return;
        var config = obj.Config;
        if (config != null) {
            CollectFromExpression(config.ImageConfig?.SpriteKey, codes);
            CollectFromExpression(config.ImageConfig?.Color, codes);
            CollectFromExpression(config.TextConfig?.Color, codes);
            CollectFromExpression(config.TextEngineConfig?.PlayingText, codes);
            CollectFromExpression(config.TextEngineConfig?.NotPlayingText, codes);
        }
        foreach (var child in obj.Children) {
            CollectCodes(child, codes);
        }
    }

    private static void CollectFromExpression<T>(FxValue<T> fx, HashSet<string> codes) {
        if (fx == null || !fx.UseFx || string.IsNullOrEmpty(fx.Expression)) return;
        string expression = fx.Expression;
        foreach (Match match in CollectCountPattern.Matches(expression)) {
            if (match.Groups.Count > 1) AddCode(codes, match.Groups[1].Value);
        }
        foreach (Match match in CollectEasePattern.Matches(expression)) {
            if (match.Groups.Count > 1) {
                foreach (var part in match.Groups[1].Value.Split(new[]{'|'})) {
                    AddCode(codes, part);
                }
            }
        }
    }

    private static void AddCode(HashSet<string> codes, string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string code = raw.Trim();
        if (code.Length == 0 || string.Equals(code, "None", StringComparison.OrdinalIgnoreCase)) return;
        codes.Add(code);
    }

    private static void MigrateDefaultSprite(FxValue<string> spriteKey, ImageSettings image, ref bool changed) {
        if (spriteKey == null || image == null) return;
        string replacement = spriteKey.UseFx
            ? spriteKey.Expression
                .Replace(KvText.Quote("KeyViewer_Background"), KvText.Quote(KvResources.BackgroundSpriteKey))
                .Replace(KvText.Quote("KeyViewer_Background_v2"), KvText.Quote(KvResources.BackgroundSpriteKey))
                .Replace(KvText.Quote("KeyViewer_Outline"), KvText.Quote(KvResources.OutlineSpriteKey))
                .Replace(KvText.Quote("KeyViewer_Outline_v2"), KvText.Quote(KvResources.OutlineSpriteKey))
            : spriteKey.StaticValue switch {
                "KeyViewer_Background" => KvResources.BackgroundSpriteKey,
                "KeyViewer_Background_v2" => KvResources.BackgroundSpriteKey,
                "KeyViewer_Outline" => KvResources.OutlineSpriteKey,
                "KeyViewer_Outline_v2" => KvResources.OutlineSpriteKey,
                _ => spriteKey.StaticValue
            };
        bool replaced = replacement != (spriteKey.UseFx ? spriteKey.Expression : spriteKey.StaticValue);
        if (!replaced) return;
        if (spriteKey.UseFx) spriteKey.SetExpression(replacement);
        else spriteKey.SetStatic(replacement);
        image.Type.SetStatic(Image.Type.Sliced);
        changed = true;
    }

    private static void ReplaceExpression<T>(FxValue<T> fx, string prefix, ref bool changed) {
        if (fx == null || !fx.UseFx || string.IsNullOrEmpty(fx.Expression)) return;
        string oldExpression = fx.Expression;
        // Per-profile JS tags (Tag.KV_<hash>_Count / ResetCounts) predate the
        // native C# tags; re-point them at the global native tags bound to
        // this canvas, whose name is the import prefix.
        // NOTE: bare legacy forms first — "Tag.KV_Count(" is not a substring
        // of the hashed form, but the rewritten output IS, so the hashed
        // regex must run last to avoid double-prefixing.
        string expression = oldExpression
            .Replace("Tag.IsKeyHeld(", "Tag.KV_IsKeyHeld(")
            .Replace("Tag.Kps(0)", KvText.Quote("{Kps:0,F0}"))
            .Replace("Tag.KV_Kps()", KvText.Quote("{Kps:0,F0}"));
        expression = BareCountPattern.Replace(expression,
            _ => "Tag.KV_Count(" + KvText.Quote(prefix) + ", ");
        expression = BareResetPattern.Replace(expression,
            _ => "Tag.KV_ResetCounts(" + KvText.Quote(prefix) + ")");
        expression = HashCountPattern.Replace(expression,
            _ => "Tag.KV_Count(" + KvText.Quote(prefix) + ", ");
        expression = HashResetPattern.Replace(expression,
            _ => "Tag.KV_ResetCounts(" + KvText.Quote(prefix) + ")");
        if (expression == oldExpression) return;
        fx.SetExpression(expression);
        changed = true;
    }

    public static KvImportResult ImportFile(string jsonPath, string profileName, bool includeCountText, bool importImages) {
        var result = new KvImportResult();
        try {
            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath)) {
                result.Error = "File not found.";
                return result;
            }
            byte[] contentBytes;
            JToken root;
            try {
                contentBytes = File.ReadAllBytes(jsonPath);
                root = JToken.Parse(System.Text.Encoding.UTF8.GetString(contentBytes));
            } catch (Exception e) {
                result.Error = $"Bad JSON: {e.Message}";
                return result;
            }
            if (string.IsNullOrWhiteSpace(profileName)) {
                profileName = Path.GetFileNameWithoutExtension(jsonPath);
            }
            return ImportToken(root, jsonPath, profileName, includeCountText, importImages,
                KvIdentityBuilder.Hash7(contentBytes));
        } catch (Exception e) {
            result.Error = e.Message;
            return result;
        }
    }

    public static KvImportResult ImportToken(JToken root, string sourcePath, string profileName, bool includeCountText, bool importImages, string contentHash7 = null) {
        var result = new KvImportResult();
        if (!KvProfile.TryParse(root, out var profile, out string parseError)) {
            result.Error = $"Parse failed: {parseError}";
            return result;
        }
        if (profile.Keys.Count == 0) {
            result.Error = "Profile has no keys.";
            return result;
        }

        var identity = new KvIdentity {
            Hash7 = string.IsNullOrEmpty(contentHash7)
                ? KvIdentityBuilder.Hash7(root.ToString())
                : contentHash7.ToLowerInvariant(),
            Name = KvIdentityBuilder.Sanitize(profileName)
        };
        identity.Prefix = "KeyViewer_" + identity.Hash7 + "_" + identity.Name;

        // Register the profile's key codes up front so Total() feeds them
        // even when the canvas shows no per-key count text at all.
        try {
            KvCountStore.Register(identity.Prefix, profile.Keys
                .Where(k => k != null && !k.IsDummy && !string.IsNullOrWhiteSpace(k.Code))
                .Select(k => k.Code.Trim())
                .Where(c => c.Length > 0 && !string.Equals(c, "None", StringComparison.OrdinalIgnoreCase)));
        } catch { }

        KvResources.EnsureDefaults();
        Dictionary<string, string> refMap = importImages
            ? KvResources.ImportReferences(identity.Prefix, profile.References)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> fontMap = importImages
            ? KvResources.ImportFontReferences(identity.Prefix, profile.References)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string canvasName = identity.Prefix;
        OvCanvas canvas = null;
        try {
            canvas = OverlayCore.CreateOvCanvas();
        } catch (Exception e) {
            result.Error = $"Canvas create failed: {e.Message}";
            return result;
        }

        try {
            canvas.Config.Name.Value = canvasName;
            canvas.Config.CanvasConfig.SortingOrder.Value = 200;
            canvas.Config.CanvasScalerConfig.ReferenceResolution.Value = new Vector2(1280f, 720f);
            canvas.Config.CanvasScalerConfig.MatchWidthOrHeight.Value = 0.5f;
            canvas.Config.CanvasScalerConfig.ScreenMatchMode.Value = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            canvas.Config.GraphicRaycasterConfig.Enabled.Value = false;
            canvas.Config.CanvasGroupConfig.BlocksRaycasts.Value = false;
            string profileCodes = GetInputCodes(profile.Keys);
            var root2 = BuildViewerRoot(profile, identity, includeCountText, profileCodes);
            canvas.Attach(root2);
            root2.ApplyComponent();
            root2.ApplyConfig();
            BuildKeys(root2, profile, identity, sourcePath, includeCountText, importImages, refMap, fontMap, profileCodes, result.Warnings);
            canvas.ApplyConfig();
            KvRainStore.DeleteSidecar(canvasName);
            OverlayCore.RequestLayoutRefresh();
            OverlayCore.SaveAllCanvases();
        } catch (Exception e) {
            try { OverlayCore.DeleteOvCanvas(canvas); } catch { }
            result.Error = $"Build failed: {e.Message}";
            return result;
        }

        result.Warnings.Add("Positions/scales are approximate. Adjust them manually in the Overlayer inspector.");
        result.Warnings.Add("{CurKPS}/{MaxKPS}/{AvgKPS} are approximated with global {Kps}.");

        result.Success = true;
        result.CanvasName = canvasName;
        try {
            Core.Logger.Msg($"[KeyViewer] Imported '{canvasName}' ({profile.Keys.Count} keys) from {sourcePath}");
        } catch { }
        return result;
    }

    private static OvObject BuildViewerRoot(KvProfile profile, KvIdentity identity, bool includeCountText, string profileCodes) {
        float totalW = 0f;
        float maxH = 100f;
        int sortable = 0;
        // NOTE: totalW/maxH only size our own root frame (center-anchored, so
        // they do not move children). Kept for inspector usability.
        foreach (var k in profile.Keys) {
            float sx = Mathf.Max(0.05f, k.KeyScale.Released.x);
            float sy = Mathf.Max(0.05f, k.KeyScale.Released.y);
            float w = 100f * sx;
            float h = ((includeCountText && k.EnableCountText) ? 150f : 100f) * sy;
            maxH = Mathf.Max(maxH, h);
            if (!k.DisableSorting) {
                totalW += w;
                sortable++;
            } else {
                totalW = Mathf.Max(totalW, w);
            }
        }
        if (sortable > 1) totalW += profile.KeySpacing * (sortable - 1);
        if (totalW <= 0f) totalW = 100f;

        var root = new OvObject();
        root.Config.Name.Value = identity.Prefix;
        var rt = root.Config.RectTransformConfig;
        KvAnchorToMinMax(profile.VecAnchor, out Vector2 rootMin, out Vector2 rootMax);
        rt.AnchorMin.Value = rootMin;
        rt.AnchorMax.Value = rootMax;
        rt.Pivot.Value = new Vector2(0.5f, 0.5f);
        rt.AnchoredPosition = Motion2(profile.VecOffset, profileCodes, "profile.offset");
        rt.SizeDelta.Value = new Vector2(totalW, maxH);
        rt.RotationXY = Motion3XY(profile.VecRotation, profileCodes, "profile.rotation");
        rt.Rotation = MotionScalar(profile.VecRotation.Released.z, profile.VecRotation.Pressed.z,
            profile.VecRotation.PressedEase, profile.VecRotation.ReleasedEase, profileCodes, "profile.rotation.z");
        // NOTE: KeyViewer animates the profile scale through the container's
        // sizeDelta, never its localScale. Children positions must not scale
        // with it, so the root scale stays at the released value.
        rt.Scale.Value = new Vector3(
            profile.VecScale.Released.x,
            profile.VecScale.Released.y, 1f);
        return root;
    }

    private static void BuildKeys(OvObject root, KvProfile profile, KvIdentity identity, string sourcePath,
        bool includeCountText, bool importImages, Dictionary<string, string> refMap,
        Dictionary<string, string> fontMap, string profileCodes, List<string> warnings) {
        // Exact replica of KeyManager.UpdateLayout + Key.UpdateLayout (released
        // state), quirks included:
        // - the first sorted key's width counts twice into totalX,
        // - unsorted (e.g. dummy/bar) keys sit at _x = 0,
        // - every key is shifted by -centerOffset + Size * keyPivot.
        float totalX = 0f;
        bool firstSorted = true;
        foreach (var key in profile.Keys) {
            if (key.DisableSorting) continue;
            float w = key.KeyScale.Released.x * 100f;
            if (firstSorted) {
                totalX += w;
                firstSorted = false;
            }
            totalX += w + profile.KeySpacing;
        }
        float globalH = profile.Keys.Any(k => includeCountText && k.EnableCountText) ? 150f : 100f;
        Vector2 profPiv = MapPivot(profile.VecPivot);
        Vector2 layoutSize = new Vector2(totalX - profile.KeySpacing, globalH);
        Vector2 centerOffset = new Vector2(profPiv.x * layoutSize.x, profPiv.y * layoutSize.y);

        float x = 0f;
        for (int index = 0; index < profile.Keys.Count; index++) {
            var k = profile.Keys[index];
            string codes = k.IsDummy ? string.Empty : k.Code;
            string stateId = "key." + index.ToString(CultureInfo.InvariantCulture);
            float keyW = k.KeyScale.Released.x * 100f;
            bool showCount = includeCountText && k.EnableCountText;
            float keyH = (showCount ? 150f : 100f) * k.KeyScale.Released.y;
            Vector2 size = new Vector2(keyW, keyH);
            float xpos = k.DisableSorting ? 0f : x + keyW / 2f;
            Vector2 keyPiv = MapPivot(k.KeyPivot);
            // Static released base (KeyViewer Position, before the key's own offset).
            Vector2 basePos = new Vector2(
                xpos - centerOffset.x + size.x * keyPiv.x,
                0f - centerOffset.y + size.y * keyPiv.y);
            if (!k.DisableSorting) {
                x += keyW + profile.KeySpacing;
            }

            var keyObj = new OvObject();
            keyObj.Config.Name.Value = $"Key {k.DisplayName}";
            var krt = keyObj.Config.RectTransformConfig;
            krt.AnchorMin.Value = new Vector2(0.5f, 0.5f);
            krt.AnchorMax.Value = new Vector2(0.5f, 0.5f);
            krt.Pivot.Value = new Vector2(0.5f, 0.5f);
            // The key rect is a zero-size origin point like KeyViewer's plain
            // Transform, so children anchored to it sit exactly where KeyViewer
            // puts them. Rotation/scale still pivot around that origin.
            krt.SizeDelta.Value = Vector2.zero;
            if (k.IsDummy) {
                krt.AnchoredPosition.Value = k.KeyOffset.Released + basePos;
                krt.RotationXY.Value = new Vector2(k.KeyRotation.Released.x, k.KeyRotation.Released.y);
                krt.Rotation.Value = k.KeyRotation.Released.z;
                krt.Scale.Value = new Vector3(k.KeyScale.Released.x, k.KeyScale.Released.y, 1f);
            } else {
                krt.AnchoredPosition = Motion2Add(k.KeyOffset, basePos, codes, stateId + ".offset");
                krt.RotationXY = Motion3XY(k.KeyRotation, codes, stateId + ".rotation");
                krt.Rotation = MotionScalar(k.KeyRotation.Released.z, k.KeyRotation.Pressed.z,
                    k.KeyRotation.PressedEase, k.KeyRotation.ReleasedEase, codes, stateId + ".rotation.z");
                krt.Scale = Motion2As3(k.KeyScale, codes, stateId + ".scale");
            }
            if (k.RainEnabled && !k.IsDummy) {
                keyObj.Config.RainConfig = BuildRain(k, showCount, codes, stateId, sourcePath, importImages, refMap, identity.Prefix, warnings);
            }
            root.Attach(keyObj);
            keyObj.ApplyComponent();
            keyObj.ApplyConfig();

            var defaultSize = new Vector2(100f, showCount ? 150f : 100f);
            float halfH = defaultSize.y / 4f;

            string bgSprite = ResolveSprite(k.BgReleasedPath, sourcePath, identity.Prefix,
                k.DisplayName + "_BG", refMap, importImages, KvResources.BackgroundSpriteKey);
            string bgSpriteP = ResolveSprite(k.BgPressedPath, sourcePath, identity.Prefix,
                k.DisplayName + "_BG_P", refMap, importImages, bgSprite);
            var bg = NewImage("Background", bgSprite, bgSpriteP, k.Code, k.IsDummy,
                k.BackgroundColor, k.BgScale, k.BgOffset, k.BgRotation, k.BgAnchor, k.BgPivot,
                defaultSize, codes, stateId + ".background");
            keyObj.Attach(bg);

            if (k.EnableOutlineImage) {
                string olSprite = ResolveSprite(k.OlReleasedPath, sourcePath, identity.Prefix,
                    k.DisplayName + "_OL", refMap, importImages, KvResources.OutlineSpriteKey);
                string olSpriteP = ResolveSprite(k.OlPressedPath, sourcePath, identity.Prefix,
                    k.DisplayName + "_OL_P", refMap, importImages, olSprite);
                var ol = NewImage("Outline", olSprite, olSpriteP, k.Code, k.IsDummy,
                    k.OutlineColor, k.OlScale, k.OlOffset, k.OlRotation, k.OlAnchor, k.OlPivot,
                    defaultSize, codes, stateId + ".outline");
                keyObj.Attach(ol);
            }

            string label = KvText.DefaultKeyLabel(k.Code, k.DummyName);
            string tp = KvText.ResolveDefault(k.TextPressed, KvText.ResolveDefault(k.TextReleased, label));
            string tr = KvText.ResolveDefault(k.TextReleased, KvText.ResolveDefault(k.TextPressed, label));
            string fontKey = ResolveFontKey(k, identity.Prefix, sourcePath, importImages, fontMap, warnings);
            KvMotion2 textScale = k.DoNotScaleText ? FixedTextScale(k.TextScale, k.KeyScale) : k.TextScale;
            KvMotion2 textOffset = AddMotion(k.TextOffset, new Vector2(0f, showCount ? halfH : 0f));
            var text = NewText("Text", tp, tr, k.Code, k.IsDummy,
                k.TextColor, k.TextRotation, textScale, textOffset, k.TextAnchor, k.TextPivot,
                k.TextFontSize, fontKey, defaultSize, codes, identity.Prefix, stateId + ".text");
            keyObj.Attach(text);

            if (showCount) {
                string cp = KvText.ResolveDefault(k.CountTextPressed,
                    KvText.ResolveDefault(k.CountTextReleased, "{Count:" + k.DisplayName + "}"));
                string cr = KvText.ResolveDefault(k.CountTextReleased,
                    KvText.ResolveDefault(k.CountTextPressed, "{Count:" + k.DisplayName + "}"));
                KvMotion2 countScale = k.DoNotScaleText ? FixedTextScale(k.CountScale, k.KeyScale) : k.CountScale;
                KvMotion2 countOffset = AddMotion(k.CountOffset, new Vector2(0f, -halfH));
                var count = NewText("CountText", cp, cr, k.Code, k.IsDummy,
                    k.CountColor, k.CountRotation, countScale, countOffset, k.CountAnchor, k.CountPivot,
                    k.CountTextFontSize, fontKey, defaultSize, codes, identity.Prefix, stateId + ".count");
                keyObj.Attach(count);
            }
        }
    }

    // KeyViewer v4 rain -> Overlayer Rain component. Pressed/released pairs become the same
    // KV_EaseScalar Fx the key colors use; the key object is a zero-size point, so width and
    // the start edge come from the key's 100 x (100|150) body.
    private static RainSettings BuildRain(KvKey k, bool showCount, string codes, string stateId, string sourcePath,
        bool importImages, Dictionary<string, string> refMap, string prefix, List<string> warnings) {
        var rain = k.RainToken ?? new JObject();
        string slot = stateId + ".rain";
        var speed = KvRainController.ReadPair(rain["Speed"], 400f);
        var length = KvRainController.ReadPair(rain["Length"], 400f);
        var softness = KvRainController.ReadIntPair(rain["Softness"], 100);
        var vector = rain["ObjectConfig"]?["VectorConfig"];
        var offset = KvMotion2.FromToken(vector?["Offset"], Vector2.zero).Released;
        var scale = KvMotion2.FromToken(vector?["Scale"], Vector2.one).Released;
        var color = KvRainController.ReadColorMotion(rain["ObjectConfig"]?["Color"]);
        var images = KvRainStore.ResolveRainImages(rain, k.DisplayName, sourcePath, importImages, refMap, prefix);
        string sprite = images.OfType<JObject>().Select(i => i["SpriteKey"]?.Value<string>()).FirstOrDefault(s => !string.IsNullOrEmpty(s));

        string direction = KvRainController.CanonicalDirection(rain["Direction"]?.Value<string>());
        if (direction != "Up") warnings.Add($"Rain direction '{direction}' on key '{k.DisplayName}' isn't supported by the Rain component; it rains up.");
        if (images.OfType<JObject>().Any(i => KvRainStore.ReadFloat(i["Roundness"], 0f) > 0f)) warnings.Add($"Rain roundness on key '{k.DisplayName}' isn't supported; use a rounded sprite.");
        if (images.Count > 1) warnings.Add($"Key '{k.DisplayName}' has several rain images; only the first is used.");

        return new RainSettings {
            Active = FxValue<bool>.FromExpression("Tag.KV_IsKeyHeld(" + KvText.Quote(codes) + ")"),
            Speed = MotionScalar(speed.released, speed.pressed, null, null, codes, slot + ".speed"),
            Length = MotionScalar(length.released, length.pressed, null, null, codes, slot + ".length"),
            FadeOut = MotionScalar(softness.released, softness.pressed, null, null, codes, slot + ".softness"),
            Width = FxValue<float>.FromValue(100f * (scale.x > 0f ? scale.x : 1f)),
            Offset = FxValue<Vector2>.FromValue(offset + new Vector2(0f, (showCount ? 150f : 100f) / 2f)),
            Color = MotionTextColor(color, codes, slot + ".color"),
            SpriteKey = FxValue<string>.FromValue(sprite),
            MaxTrails = FxValue<int>.FromValue(Math.Max(32, KvRainStore.ReadInt(rain["PoolSize"], 32)))
        };
    }

    private static OvObject NewImage(string name,
        string spriteReleased, string spritePressed, string code, bool isDummy,
        KvMotionColor color, KvMotion2 scale, KvMotion2 offset, KvMotion3 rotation, int anchor, int pivot,
        Vector2 defaultSize, string codes, string stateId) {
        var obj = new OvObject();
        obj.Config.Name.Value = name;
        var rt = obj.Config.RectTransformConfig;
        KvAnchorToMinMax(anchor, out Vector2 anchorMin, out Vector2 anchorMax);
        rt.AnchorMin.Value = anchorMin;
        rt.AnchorMax.Value = anchorMax;
        rt.Pivot.Value = MapPivot(pivot);
        rt.AnchoredPosition = isDummy ? FxValue<Vector2>.FromValue(offset.Released)
            : Motion2(offset, codes, stateId + ".offset");
        var sizeMotion = MultiplyMotion(scale, defaultSize);
        rt.SizeDelta = isDummy ? FxValue<Vector2>.FromValue(sizeMotion.Released)
            : Motion2(sizeMotion, codes, stateId + ".size");
        rt.RotationXY = isDummy ? FxValue<Vector2>.FromValue(new Vector2(rotation.Released.x, rotation.Released.y))
            : Motion3XY(rotation, codes, stateId + ".rotation");
        rt.Rotation = isDummy ? FxValue<float>.FromValue(rotation.Released.z)
            : MotionScalar(rotation.Released.z, rotation.Pressed.z, rotation.PressedEase, rotation.ReleasedEase, codes, stateId + ".rotation.z");

        var img = new ImageSettings();
        bool sameSprite = string.Equals(spriteReleased, spritePressed, StringComparison.Ordinal);
        if (!isDummy && !sameSprite
            && !string.IsNullOrEmpty(spriteReleased) && !string.IsNullOrEmpty(spritePressed)) {
            img.SpriteKey = FxValue<string>.FromExpression(
                "(Tag.KV_IsKeyHeld(" + KvText.Quote(code) + ") ? "
                + KvText.Quote(spritePressed) + " : " + KvText.Quote(spriteReleased) + ")");
        } else {
            img.SpriteKey = FxValue<string>.FromValue(spriteReleased);
        }

        img.Color = isDummy ? FxValue<Color>.FromValue(color.Released.TopLeft.ToUnity())
            : MotionImageColor(color, codes, stateId + ".color");
        img.Type.Value = Image.Type.Sliced;
        obj.Config.ImageConfig = img;
        obj.ApplyComponent();
        obj.ApplyConfig();
        return obj;
    }

    private static OvObject NewText(string name,
        string pressedTemplate, string releasedTemplate, string code, bool isDummy,
        KvMotionColor color, KvMotion3 rotation, KvMotion2 scale, KvMotion2 offset, int anchor, int pivot,
        float fontSize, string fontKey, Vector2 defaultSize, string codes, string prefix, string stateId) {
        var obj = new OvObject();
        obj.Config.Name.Value = name;
        var rt = obj.Config.RectTransformConfig;
        KvAnchorToMinMax(anchor, out Vector2 anchorMin, out Vector2 anchorMax);
        rt.AnchorMin.Value = anchorMin;
        rt.AnchorMax.Value = anchorMax;
        rt.Pivot.Value = MapPivot(pivot);
        rt.AnchoredPosition = isDummy ? FxValue<Vector2>.FromValue(offset.Released)
            : Motion2(offset, codes, stateId + ".offset");
        rt.SizeDelta.Value = defaultSize;
        rt.Scale = isDummy ? FxValue<Vector3>.FromValue(new Vector3(scale.Released.x, scale.Released.y, 1f))
            : Motion2As3(scale, codes, stateId + ".scale");
        rt.RotationXY = isDummy ? FxValue<Vector2>.FromValue(new Vector2(rotation.Released.x, rotation.Released.y))
            : Motion3XY(rotation, codes, stateId + ".rotation");
        rt.Rotation = isDummy ? FxValue<float>.FromValue(rotation.Released.z)
            : MotionScalar(rotation.Released.z, rotation.Pressed.z, rotation.PressedEase, rotation.ReleasedEase, codes, stateId + ".rotation.z");

        string expr = KvText.BuildHeldExpression(code, pressedTemplate, releasedTemplate, isDummy, prefix, out bool usesJs);

        var tmp = new TextMeshProUGUISettings();
        tmp.FontSize.Value = Mathf.Max(1f, fontSize);
        if (!string.IsNullOrEmpty(fontKey)) {
            tmp.FontKey.Value = fontKey;
        }
        // KeyViewer uses Midline alignment with auto-sizing capped at the
        // configured size (fontSizeMin stays at TMP's default 0, verified
        // from the game's Unity.TextMeshPro.dll), plus a PreferredSize
        // fitter on both axes.
        tmp.Alignment.Value = TextAlignmentOptions.Midline;
        tmp.AutoSize.Value = true;
        tmp.FontSizeRange.Value = new Vector2(0f, Mathf.Max(1f, fontSize));
        obj.Config.ContentSizeFitterConfig = new ContentSizeFitterSettings();
        tmp.RichText.Value = true;
        tmp.EnableShadow.Value = false;
        tmp.TextWrappingMode.Value = 0; // NoWrap
        tmp.OverFlowMode.Value = TextOverflowModes.Overflow;
        bool gradEither = color.Released.GradientEnabled || color.Pressed.GradientEnabled;
        if (isDummy || GColorSame(color.Released, color.Pressed)) {
            tmp.Color.Value = ToGradient(color.Released, gradEither);
        } else {
            tmp.Color = MotionTextColor(color, codes, stateId + ".color");
        }

        if (usesJs) {
            tmp.Text.Value = "Text";
            var engine = new OvTextSettings();
            engine.PlayingText = FxValue<string>.FromExpression(expr);
            engine.NotPlayingText = FxValue<string>.FromExpression(expr);
            obj.Config.TextConfig = tmp;
            obj.Config.TextEngineConfig = engine;
        } else {
            string plain = StripToPlain(releasedTemplate);
            tmp.Text.Value = plain;
            var engine = new OvTextSettings();
            engine.PlayingText = FxValue<string>.FromValue(plain);
            engine.NotPlayingText = FxValue<string>.FromValue(plain);
            obj.Config.TextConfig = tmp;
            obj.Config.TextEngineConfig = engine;
        }
        obj.ApplyComponent();
        obj.ApplyConfig();
        return obj;
    }

    private static string GetInputCodes(IEnumerable<KvKey> keys) {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys) {
            if (key != null && !key.IsDummy && !string.IsNullOrWhiteSpace(key.Code) && key.Code != "None") {
                codes.Add(key.Code.Trim());
            }
        }
        return string.Join("|", codes);
    }

    // Exact replica of KeyViewerUtils.GetPivot (unknown combos fall to zero).
    internal static Vector2 MapPivot(int pivot) => pivot switch {
        9 => new Vector2(0f, 1f),
        17 => new Vector2(0.5f, 1f),
        33 => new Vector2(1f, 1f),
        10 => new Vector2(0f, 0.5f),
        18 => new Vector2(0.5f, 0.5f),
        34 => new Vector2(1f, 0.5f),
        12 => new Vector2(0f, 0f),
        20 => new Vector2(0.5f, 0f),
        36 => new Vector2(1f, 0f),
        _ => Vector2.zero,
    };

    // Exact replica of KeyViewer's RectTransform.SetAnchor extension.
    // Values with no case keep a fresh RectTransform's anchors (full stretch).
    internal static void KvAnchorToMinMax(int anchor, out Vector2 min, out Vector2 max) {
        switch (anchor) {
            case 17: min = new Vector2(0f, 1f); max = new Vector2(0f, 1f); break;
            case 18: min = new Vector2(0.5f, 1f); max = new Vector2(0.5f, 1f); break;
            case 20: min = new Vector2(1f, 1f); max = new Vector2(1f, 1f); break;
            case 33: min = new Vector2(0f, 0.5f); max = new Vector2(0f, 0.5f); break;
            case 34: min = new Vector2(0.5f, 0.5f); max = new Vector2(0.5f, 0.5f); break;
            case 36: min = new Vector2(1f, 0.5f); max = new Vector2(1f, 0.5f); break;
            case 65: min = new Vector2(0f, 0f); max = new Vector2(0f, 0f); break;
            case 66: min = new Vector2(0.5f, 0f); max = new Vector2(0.5f, 0f); break;
            case 68: min = new Vector2(1f, 0f); max = new Vector2(1f, 0f); break;
            case 24: min = new Vector2(0f, 1f); max = new Vector2(1f, 1f); break;
            case 40: min = new Vector2(0f, 0.5f); max = new Vector2(1f, 0.5f); break;
            case 72: min = new Vector2(0f, 0f); max = new Vector2(1f, 0f); break;
            case 129: min = new Vector2(0f, 0f); max = new Vector2(0f, 1f); break;
            case 130: min = new Vector2(0.5f, 0f); max = new Vector2(0.5f, 1f); break;
            case 132: min = new Vector2(1f, 0f); max = new Vector2(1f, 1f); break;
            case 136: min = new Vector2(0f, 0f); max = new Vector2(1f, 1f); break;
            default: min = new Vector2(0f, 0f); max = new Vector2(1f, 1f); break;
        }
    }

    private static KvMotion2 AddMotion(KvMotion2 motion, Vector2 value) => new KvMotion2(motion.Released + value) {
        Pressed = motion.Pressed + value,
        PressedEase = motion.PressedEase,
        ReleasedEase = motion.ReleasedEase
    };

    private static KvMotion2 MultiplyMotion(KvMotion2 motion, Vector2 value) => new KvMotion2(Vector2.Scale(motion.Released, value)) {
        Pressed = Vector2.Scale(motion.Pressed, value),
        PressedEase = motion.PressedEase,
        ReleasedEase = motion.ReleasedEase
    };

    private static KvMotion2 FixedTextScale(KvMotion2 textScale, KvMotion2 keyScale) {
        static float Divide(float value, float denominator) => Mathf.Abs(denominator) < 0.0001f ? value : value / denominator;
        return new KvMotion2(new Vector2(
            Divide(textScale.Released.x, keyScale.Released.x),
            Divide(textScale.Released.y, keyScale.Released.y))) {
            Pressed = new Vector2(
                Divide(textScale.Pressed.x, keyScale.Pressed.x),
                Divide(textScale.Pressed.y, keyScale.Pressed.y)),
            PressedEase = textScale.PressedEase,
            ReleasedEase = textScale.ReleasedEase
        };
    }

    private static FxValue<Vector2> Motion2(KvMotion2 motion, string codes, string slot) {
        if (motion.Released == motion.Pressed) return FxValue<Vector2>.FromValue(motion.Released);
        return FxValue<Vector2>.FromExpression("["
            + MotionComponent(codes, slot + ".x", motion.Released.x, motion.Pressed.x, motion.PressedEase, motion.ReleasedEase)
            + ", "
            + MotionComponent(codes, slot + ".y", motion.Released.y, motion.Pressed.y, motion.PressedEase, motion.ReleasedEase)
            + "]");
    }

    private static FxValue<Vector2> Motion2Add(KvMotion2 motion, Vector2 addition, string codes, string slot)
        => Motion2(AddMotion(motion, addition), codes, slot);

    private static FxValue<Vector3> Motion2As3(KvMotion2 motion, string codes, string slot) {
        if (motion.Released == motion.Pressed)
            return FxValue<Vector3>.FromValue(new Vector3(motion.Released.x, motion.Released.y, 1f));
        return FxValue<Vector3>.FromExpression("["
            + MotionComponent(codes, slot + ".x", motion.Released.x, motion.Pressed.x, motion.PressedEase, motion.ReleasedEase)
            + ", "
            + MotionComponent(codes, slot + ".y", motion.Released.y, motion.Pressed.y, motion.PressedEase, motion.ReleasedEase)
            + ", 1]");
    }

    private static FxValue<Vector2> Motion3XY(KvMotion3 motion, string codes, string slot) {
        if (motion.Released.x == motion.Pressed.x && motion.Released.y == motion.Pressed.y)
            return FxValue<Vector2>.FromValue(new Vector2(motion.Released.x, motion.Released.y));
        return FxValue<Vector2>.FromExpression("["
            + MotionComponent(codes, slot + ".x", motion.Released.x, motion.Pressed.x, motion.PressedEase, motion.ReleasedEase)
            + ", "
            + MotionComponent(codes, slot + ".y", motion.Released.y, motion.Pressed.y, motion.PressedEase, motion.ReleasedEase)
            + "]");
    }

    private static FxValue<float> MotionScalar(float released, float pressed, KvEase pressedEase, KvEase releasedEase, string codes, string slot) {
        if (Mathf.Approximately(released, pressed)) return FxValue<float>.FromValue(released);
        return FxValue<float>.FromExpression(MotionComponent(codes, slot, released, pressed, pressedEase, releasedEase));
    }

    private static string MotionComponent(string codes, string slot, float released, float pressed, KvEase pressedEase, KvEase releasedEase) {
        if (Mathf.Approximately(released, pressed)) return F(released);
        return "Tag.KV_EaseScalar("
            + KvText.Quote(codes ?? string.Empty) + ", "
            + KvText.Quote(slot) + ", "
            + KvText.Quote(F(released)) + ", "
            + KvText.Quote(F(pressed)) + ", "
            + KvText.Quote(F(pressedEase?.Duration ?? 0f)) + ", "
            + KvText.Quote(pressedEase?.Type ?? "Unset") + ", "
            + KvText.Quote(F(releasedEase?.Duration ?? 0f)) + ", "
            + KvText.Quote(releasedEase?.Type ?? "Unset") + ")";
    }

    private static FxValue<Color> MotionImageColor(KvMotionColor motion, string codes, string slot) {
        var r = motion.Released.TopLeft;
        var p = motion.Pressed.TopLeft;
        if (ColorSame(r, p)) return FxValue<Color>.FromValue(r.ToUnity());
        return FxValue<Color>.FromExpression("["
            + MotionComponent(codes, slot + ".r", r.R, p.R, motion.PressedEase, motion.ReleasedEase) + ", "
            + MotionComponent(codes, slot + ".g", r.G, p.G, motion.PressedEase, motion.ReleasedEase) + ", "
            + MotionComponent(codes, slot + ".b", r.B, p.B, motion.PressedEase, motion.ReleasedEase) + ", "
            + MotionComponent(codes, slot + ".a", r.A, p.A, motion.PressedEase, motion.ReleasedEase) + "]");
    }

    private static FxValue<GradientColor> MotionTextColor(KvMotionColor motion, string codes, string slot) {
        bool gradient = motion.Released.GradientEnabled || motion.Pressed.GradientEnabled;
        var released = motion.Released;
        var pressed = motion.Pressed;
        var corners = gradient
            ? new[] { (released.TopLeft, pressed.TopLeft), (released.TopRight, pressed.TopRight), (released.BottomLeft, pressed.BottomLeft), (released.BottomRight, pressed.BottomRight) }
            : new[] { (released.TopLeft, pressed.TopLeft) };
        var values = new List<string>(corners.Length * 4);
        string[] channels = { "r", "g", "b", "a" };
        for (int i = 0; i < corners.Length; i++) {
            var (r, p) = corners[i];
            values.Add(MotionComponent(codes, slot + "." + i + "." + channels[0], r.R, p.R, motion.PressedEase, motion.ReleasedEase));
            values.Add(MotionComponent(codes, slot + "." + i + "." + channels[1], r.G, p.G, motion.PressedEase, motion.ReleasedEase));
            values.Add(MotionComponent(codes, slot + "." + i + "." + channels[2], r.B, p.B, motion.PressedEase, motion.ReleasedEase));
            values.Add(MotionComponent(codes, slot + "." + i + "." + channels[3], r.A, p.A, motion.PressedEase, motion.ReleasedEase));
        }
        return FxValue<GradientColor>.FromExpression("[" + string.Join(", ", values) + "]");
    }

    private static string F(float value) => value.ToString("G9", CultureInfo.InvariantCulture);

    private static string StripToPlain(string template) => template ?? string.Empty;

    private static GradientColor ToGradient(KvGColor g, bool gradient) {
        if (!gradient) {
            return new GradientColor(g.TopLeft.ToUnity(), true);
        }
        return new GradientColor(
            g.TopLeft.ToUnity(), g.TopRight.ToUnity(),
            g.BottomLeft.ToUnity(), g.BottomRight.ToUnity());
    }

    private static bool ColorSame(KvColor a, KvColor b) {
        return Mathf.Approximately(a.R, b.R) && Mathf.Approximately(a.G, b.G)
            && Mathf.Approximately(a.B, b.B) && Mathf.Approximately(a.A, b.A);
    }

    private static bool GColorSame(KvGColor a, KvGColor b) {
        if (a.GradientEnabled != b.GradientEnabled) return false;
        return ColorSame(a.TopLeft, b.TopLeft) && ColorSame(a.TopRight, b.TopRight)
            && ColorSame(a.BottomLeft, b.BottomLeft) && ColorSame(a.BottomRight, b.BottomRight);
    }

    private static string ResolveFontKey(KvKey key, string prefix, string sourcePath,
        bool importFonts, Dictionary<string, string> fontMap, List<string> warnings) {
        string font = (key.Font ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(font) || string.Equals(font, "Default", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        try {
            string normalized = KvPath.Normalize(font);
            string fileName = KvPath.FileName(font);
            if (fontMap != null
                && (fontMap.TryGetValue(normalized, out var mapped)
                    || fontMap.TryGetValue(fileName, out mapped))) {
                return mapped;
            }
            if (!importFonts) return null;
            string candidate = KvPath.Expand(font, sourcePath);
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) {
                string imported = KvResources.ImportFontBytes(prefix,
                    KvPath.FileNameWithoutExtension(fileName),
                    Path.GetExtension(KvPath.Normalize(candidate)), File.ReadAllBytes(candidate));
                if (!string.IsNullOrEmpty(imported)) return imported;
            } else {
                string osPath = ResolveOsFontPath(font);
                if (!string.IsNullOrEmpty(osPath)) {
                    string imported = KvResources.ImportFontBytes(prefix,
                        KvPath.FileNameWithoutExtension(osPath),
                        Path.GetExtension(osPath), File.ReadAllBytes(osPath));
                    if (!string.IsNullOrEmpty(imported)) return imported;
                }
            }
        } catch { }
        try { warnings?.Add($"Font '{font}' for key '{key.DisplayName}' was not found. Using the default font."); } catch { }
        return null;
    }

    private static string ResolveOsFontPath(string fontName) {
        try {
            string[] names = Font.GetOSInstalledFontNames();
            string[] paths = Font.GetPathsToOSFonts();
            if (names == null || paths == null) return null;
            for (int i = 0; i < names.Length && i < paths.Length; i++) {
                if (string.Equals(names[i], fontName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i])) {
                    return paths[i];
                }
            }
        } catch { }
        return null;
    }

    private static string ResolveSprite(string rawPath, string sourcePath, string prefix,
        string baseName, Dictionary<string, string> refMap, bool importImages, string fallback) {
        if (string.IsNullOrWhiteSpace(rawPath)) return fallback;
        if (!importImages) return fallback;
        try {
            string normalized = KvPath.Normalize(rawPath);
            string fileName = KvPath.FileName(rawPath);
            if (refMap != null
                && (refMap.TryGetValue(normalized, out var mapped)
                    || refMap.TryGetValue(fileName, out mapped))) {
                return mapped;
            }
            string candidate = KvPath.Expand(rawPath, sourcePath);
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) {
                byte[] bytes = File.ReadAllBytes(candidate);
                return KvResources.ImportImageBytes(prefix, baseName, bytes, fallback);
            }
        } catch { }
        return fallback;
    }

}
