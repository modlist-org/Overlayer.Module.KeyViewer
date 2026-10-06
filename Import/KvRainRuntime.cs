using Newtonsoft.Json.Linq;
using Overlayer.Core;
using Overlayer.IO.User;
using Overlayer.Overlay;
using Overlayer.Module.KeyViewer.Tag;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Overlayer.Module.KeyViewer.Import;

/// <summary>Persists the original KeyViewer rain configuration and installs the runtime renderer.</summary>
public static class KvRainStore {
    private static string Folder => Path.Combine(MainCore.Paths.ModulePath, "KeyViewer", "Rain");

    // New imports use Overlayer's Rain component; drop any legacy sidecar left under this canvas name.
    public static void DeleteSidecar(string canvasName) {
        try {
            string path = SidecarPath(canvasName);
            if (File.Exists(path)) File.Delete(path);
        } catch { }
    }

    public static void AttachExisting(IEnumerable<OvCanvas> canvases) {
        if (canvases == null) return;
        foreach (var canvas in canvases) {
            if (canvas == null) continue;
            string path = SidecarPath(canvas.Config.Name.Value);
            Attach(canvas, path);
        }
    }

    private static void Attach(OvCanvas canvas, string path) {
        if (!File.Exists(path) || canvas.OvObjects.Count == 0) return;
        try {
            var root = JObject.Parse(File.ReadAllText(path));
            if (root["Entries"] is not JArray entries) return;
            float spacing = ReadFloat(root["KeySpacing"], 10f);
            var viewer = canvas.OvObjects[0];
            foreach (var entry in entries.OfType<JObject>()) {
                int index = ReadInt(entry["Index"], -1);
                if (index < 0 || index >= viewer.Children.Count) continue;
                var key = viewer.Children[index];
                var controller = key.GameObject.GetComponent<KvRainController>();
                if (controller == null) controller = key.GameObject.AddComponent<KvRainController>();
                controller.Initialize(entry, spacing);
            }
        } catch (Exception e) {
            Core.Logger.Wrn($"[KeyViewer] Could not restore rain for '{canvas.Config.Name.Value}': {e.Message}");
        }
    }

    internal static JArray ResolveRainImages(JObject rain, string keyName, string sourcePath,
        bool importImages, Dictionary<string, string> refMap, string prefix) {
        var images = new JArray();
        if (rain?["RainImages"] is not JArray source) return images;
        foreach (var item in source.OfType<JObject>()) {
            int count = Math.Max(0, ReadInt(item["Count"], 0));
            string raw = item["Image"]?.Value<string>();
            string spriteKey = ResolveRainSprite(raw, keyName, sourcePath, importImages, refMap, prefix);
            for (int i = 0; i < count; i++) {
                images.Add(new JObject {
                    ["SpriteKey"] = spriteKey ?? string.Empty,
                    ["Roundness"] = ReadFloat(item["Roundness"], 0f)
                });
            }
        }
        return images;
    }

    private static string ResolveRainSprite(string raw, string keyName, string sourcePath,
        bool importImages, Dictionary<string, string> refMap, string prefix) {
        if (!importImages || string.IsNullOrWhiteSpace(raw)) return null;
        try {
            string file = KvPath.FileName(raw);
            string normalized = KvPath.Normalize(raw);
            if (refMap != null
                && (refMap.TryGetValue(normalized, out var refKey)
                    || refMap.TryGetValue(file, out refKey))) return refKey;
            string path = KvPath.Expand(raw, sourcePath);
            if (!File.Exists(path)) return null;
            return KvResources.ImportImageBytes(prefix,
                keyName + "_Rain_" + KvPath.FileNameWithoutExtension(file), File.ReadAllBytes(path), null);
        } catch {
            return null;
        }
    }

    public static void MoveSidecar(string oldCanvasName, string newCanvasName) {
        try {
            string oldPath = SidecarPath(oldCanvasName);
            string newPath = SidecarPath(newCanvasName);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(oldPath) || File.Exists(newPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            File.Move(oldPath, newPath);
        } catch { }
    }

    private static string SidecarPath(string canvasName) {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = canvasName ?? "KeyViewer";
        foreach (char c in invalid) safe = safe.Replace(c, '_');
        return Path.Combine(Folder, safe + ".json");
    }

    internal static float ReadFloat(JToken token, float fallback) {
        try { return token?.Value<float>() ?? fallback; } catch { return fallback; }
    }

    internal static int ReadInt(JToken token, int fallback) {
        try { return token?.Value<int>() ?? fallback; } catch { return fallback; }
    }
}

#if ML && IL2CPP
[MelonLoader.RegisterTypeInIl2Cpp]
public sealed class KvRainController : MonoBehaviour {
    public KvRainController(IntPtr ptr) : base(ptr) { }
#else
public sealed class KvRainController : MonoBehaviour {
#endif
    private sealed class Particle {
        public GameObject GameObject;
        public RectTransform Rect;
        public Image Image;
        public KvRainGradient Gradient;
        public bool Active;
        public bool Stretching;
        public Vector2 Position;
        public Vector2 Size;
        public Vector2 StartOffset, TargetOffset;
        public Vector3 StartScale, TargetScale, StartRotation, TargetRotation;
        public Color StartColor, TargetColor;
        public float StyleStart;
        public float OffsetDuration, ScaleDuration, RotationDuration, ColorDuration;
        public string OffsetEase, ScaleEase, RotationEase, ColorEase;
        public KvGColor StartGradient, TargetGradient;
    }

    private RectTransform keyRect;
    private RectTransform maskRect;
    private RectMask2D mask;
    private Image maskImage;
    private readonly List<Particle> pool = new();
    private readonly List<string> spriteKeys = new();
    private string keyCode;
    private Vector2 keySize;
    private float spacing;
    private int poolSize;
    private float speedReleased, speedPressed;
    private float lengthReleased, lengthPressed;
    private int softnessReleased, softnessPressed;
    private float roundness;
    private string direction = "Up";
    private string displayMode = "Sequential";
    private string maskPivot = "MiddleCenter";
    private string maskAnchor = "MiddleCenter";
    private KvMotion2 rainOffset = new KvMotion2(Vector2.zero);
    private KvMotion2 rainScale = new KvMotion2(Vector2.one);
    private KvMotion3 rainRotation = new KvMotion3(Vector3.zero);
    private KvMotionColor rainColor = new KvMotionColor();
    private int nextSprite;
    private bool previousPressed;
    private bool initialized;

    public void Initialize(JObject entry, float keySpacing) {
        if (initialized || entry == null) return;
        keyRect = transform as RectTransform;
        if (keyRect == null || entry["Rain"] is not JObject rainData) return;
        spacing = keySpacing;
        keyCode = entry["Code"]?.Value<string>() ?? string.Empty;
        // KeyViewer derives rain geometry from the live key size (100x100/150
        // scaled by the key's own scale). Read it live so stale sidecars or
        // manual inspector edits can never desync the rain start; the sidecar
        // Size stays as a fallback for degenerate (zero-scale) key rects.
        Vector2 ks = keyRect.localScale;
        bool tall = transform.Find("CountText") != null;
        Vector2 live = new Vector2(100f * ks.x, (tall ? 150f : 100f) * ks.y);
        if (live.x != 0f && live.y != 0f) {
            keySize = live;
        } else if (entry["Size"] is JArray size && size.Count >= 2) {
            keySize = new Vector2(size[0].Value<float>(), size[1].Value<float>());
        } else if (transform.Find("Background") is RectTransform bgRect) {
            // Sidecars predating the persisted Size: fall back to the
            // background artwork instead of the (zero-size) key rect.
            keySize = bgRect.rect.size;
        } else {
            keySize = new Vector2(100f, 100f);
        }

        direction = CanonicalDirection(rainData["Direction"]?.Value<string>());
        poolSize = Math.Max(0, KvRainStore.ReadInt(rainData["PoolSize"], 32));
        roundness = KvRainStore.ReadFloat(rainData["Roundness"], 0f);
        displayMode = rainData["ImageDisplayMode"]?.Value<string>() ?? "Sequential";
        (speedReleased, speedPressed) = ReadPair(rainData["Speed"], 400f);
        (lengthReleased, lengthPressed) = ReadPair(rainData["Length"], 400f);
        (softnessReleased, softnessPressed) = ReadIntPair(rainData["Softness"], 100);

        var objConfig = rainData["ObjectConfig"];
        var vec = objConfig?["VectorConfig"];
        if (vec != null) {
            rainOffset = KvMotion2.FromToken(vec["Offset"], Vector2.zero);
            rainScale = KvMotion2.FromToken(vec["Scale"], Vector2.one);
            rainRotation = KvMotion3.FromToken(vec["Rotation"], Vector3.zero);
            maskPivot = vec["Pivot"]?.Value<string>() ?? maskPivot;
            maskAnchor = vec["Anchor"]?.Value<string>() ?? maskAnchor;
        }
        rainColor = ReadColorMotion(objConfig?["Color"]);

        if (entry["Images"] is JArray images) {
            foreach (var item in images.OfType<JObject>()) spriteKeys.Add(item["SpriteKey"]?.Value<string>() ?? string.Empty);
        }
        if (string.Equals(displayMode, "Random", StringComparison.OrdinalIgnoreCase)) {
            for (int i = spriteKeys.Count - 1; i > 0; i--) {
                int j = UnityEngine.Random.Range(0, i + 1);
                (spriteKeys[i], spriteKeys[j]) = (spriteKeys[j], spriteKeys[i]);
            }
        }

        CreateMask();
        for (int i = 0; i < poolSize; i++) pool.Add(CreateParticle(i));
        previousPressed = false;
        initialized = true;
        UpdateMask(false);
    }

    private void CreateMask() {
        var go = new GameObject("KeyViewer Rain Mask");
        go.transform.SetParent(keyRect, false);
        go.transform.SetAsFirstSibling();
        maskRect = go.AddComponent<RectTransform>();
        SetMaskAnchor();
        maskImage = go.AddComponent<Image>();
        maskImage.color = new Color(1f, 1f, 1f, 0f);
        maskImage.raycastTarget = false;
        mask = go.AddComponent<RectMask2D>();
    }

    private Particle CreateParticle(int index) {
        var go = new GameObject("KeyViewer Rain " + index);
        go.transform.SetParent(maskRect, false);
        var rect = go.AddComponent<RectTransform>();
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.color = rainColor.Released.TopLeft.ToUnity();
        image.type = Image.Type.Simple;
        var gradient = go.AddComponent<KvRainGradient>();
        gradient.Set(rainColor.Released);
        go.SetActive(false);
        return new Particle {
            GameObject = go,
            Rect = rect,
            Image = image,
            Gradient = gradient,
            StartOffset = rainOffset.Released,
            TargetOffset = rainOffset.Released,
            StartScale = new Vector3(rainScale.Released.x, rainScale.Released.y, 1f),
            TargetScale = new Vector3(rainScale.Released.x, rainScale.Released.y, 1f),
            StartRotation = rainRotation.Released,
            TargetRotation = rainRotation.Released,
            StartColor = rainColor.Released.TopLeft.ToUnity(),
            TargetColor = rainColor.Released.TopLeft.ToUnity(),
            StartGradient = rainColor.Released,
            TargetGradient = rainColor.Released
        };
    }

    private void Update() {
        if (!initialized || keyRect == null) return;
        bool pressed = !string.IsNullOrEmpty(keyCode) && KeyViewerInput.IsKeyHeld(keyCode);
        UpdateMask(pressed);
        if (pressed != previousPressed) {
            if (pressed) Spawn(pressed);
            else {
                foreach (var particle in pool) {
                    if (particle.Active && particle.Stretching) {
                        particle.Stretching = false;
                        SetStyle(particle, false, false);
                    }
                }
            }
            previousPressed = pressed;
        }

        float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        float speed = pressed ? speedPressed : speedReleased;
        for (int i = pool.Count - 1; i >= 0; i--) {
            var particle = pool[i];
            if (!particle.Active) continue;
            if (!IsVisible(particle)) {
                particle.Active = false;
                particle.GameObject.SetActive(false);
                continue;
            }

            float travel = deltaTime * speed;
            Vector2 delta = DirectionDelta(travel);
            if (particle.Stretching) {
                particle.Size += new Vector2(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
                particle.Position += delta * 0.5f;
            } else {
                particle.Position += delta;
            }
            particle.Rect.sizeDelta = particle.Size;
            UpdateStyle(particle);
        }
    }

    private void Spawn(bool pressed) {
        // Mirrors EnsurePool.Get: grow past PoolSize when everything is alive.
        Particle particle = pool.FirstOrDefault(p => !p.Active);
        if (particle == null) {
            particle = CreateParticle(pool.Count);
            pool.Add(particle);
        }
        particle.Active = true;
        particle.Stretching = true;
        particle.GameObject.SetActive(true);
        particle.Image.sprite = NextSprite();
        particle.Size = InitialSize(pressed);
        particle.Position = InitialPosition(pressed);
        particle.Rect.sizeDelta = particle.Size;
        particle.StyleStart = Time.unscaledTime;
        SetStyle(particle, true, false);
        UpdateStyle(particle);
    }

    private Sprite NextSprite() {
        if (spriteKeys.Count == 0) return null;
        int index = nextSprite++ % spriteKeys.Count;
        Sprite source = UserResourceManager.Spr.TryGet(spriteKeys[index], out var value) ? value.sprite : null;
        return KvRainSpriteCache.Get(source, roundness);
    }

    private void UpdateMask(bool pressed) {
        int soft = pressed ? softnessPressed : softnessReleased;
        float width = keySize.x;
        float height = keySize.y;
        Vector2 maskScale = pressed ? rainScale.Pressed : rainScale.Released;
        Vector2 maskOffset = CurrentMotion(rainOffset, "rain.mask.offset");
        if (direction == "Up" || direction == "Down") {
            maskRect.sizeDelta = new Vector2(
                maskScale.x > 0f ? keySize.x * maskScale.x : keySize.x,
                soft + (pressed ? lengthPressed : lengthReleased));
            mask.softness = new Vector2Int(0, soft);
            float y = direction == "Up" ? height / 2f - soft + spacing : -(height / 2f - soft + spacing);
            // KeyViewer assigns the mask world position (offset + key world
            // position), not a parent-space position, so rotated or scaled
            // keys place the mask identically.
            maskRect.position = keyRect.position + new Vector3(0f + maskOffset.x, y + maskOffset.y, 0f);
        } else {
            maskRect.sizeDelta = new Vector2(
                soft + (pressed ? lengthPressed : lengthReleased),
                maskScale.y > 0f ? keySize.y * maskScale.y : keySize.y);
            mask.softness = new Vector2Int(soft, 0);
            float x = direction == "Right" ? width / 2f - soft + spacing : -(width / 2f - soft + spacing);
            maskRect.position = keyRect.position + new Vector3(x + maskOffset.x, 0f + maskOffset.y, 0f);
        }
    }

    internal static string CanonicalDirection(string raw) {
        // KeyViewer Direction enums serialize canonically, but accept any
        // casing; anything unparseable falls back to Up like a fresh config.
        if (string.Equals(raw, "Down", StringComparison.OrdinalIgnoreCase)) return "Down";
        if (string.Equals(raw, "Left", StringComparison.OrdinalIgnoreCase)) return "Left";
        if (string.Equals(raw, "Right", StringComparison.OrdinalIgnoreCase)) return "Right";
        if (string.Equals(raw, "Up", StringComparison.OrdinalIgnoreCase)) return "Up";
        return "Up";
    }

    private void SetMaskAnchor() {
        switch (direction) {
            case "Down":
                maskRect.anchorMin = maskRect.anchorMax = new Vector2(0.5f, 1f);
                maskRect.pivot = new Vector2(0.5f, 1f);
                break;
            case "Left":
                maskRect.anchorMin = maskRect.anchorMax = new Vector2(1f, 0.5f);
                maskRect.pivot = new Vector2(1f, 0.5f);
                break;
            case "Right":
                maskRect.anchorMin = maskRect.anchorMax = new Vector2(0f, 0.5f);
                maskRect.pivot = new Vector2(0f, 0.5f);
                break;
            default:
                maskRect.anchorMin = maskRect.anchorMax = new Vector2(0.5f, 0f);
                maskRect.pivot = new Vector2(0.5f, 0f);
                break;
        }

        if (maskPivot != "MiddleCenter") maskRect.pivot = PivotValue(maskPivot, maskRect.pivot);
        if (maskAnchor != "MiddleCenter") SetAnchor(maskAnchor);
    }

    private Vector2 InitialSize(bool pressed) {
        Vector2 scale = pressed ? rainScale.Pressed : rainScale.Released;
        float sx = scale.x;
        float sy = scale.y;
        if (direction == "Up" || direction == "Down") {
            return new Vector2(sx > 0f ? keySize.x * sx : keySize.x, 0f);
        }
        return new Vector2(0f, sy > 0f ? keySize.y * sy : keySize.y);
    }

    private Vector2 InitialPosition(bool pressed) {
        int softness = pressed ? softnessPressed : softnessReleased;
        if (direction == "Up") return new Vector2(0f, -maskRect.sizeDelta.y / 2f + softness);
        if (direction == "Down") return new Vector2(0f, maskRect.sizeDelta.y / 2f - softness);
        if (direction == "Left") return new Vector2(maskRect.sizeDelta.x / 2f - softness, 0f);
        return new Vector2(-maskRect.sizeDelta.x / 2f + softness, 0f);
    }

    private bool IsVisible(Particle p) {
        float length = previousPressed ? lengthPressed : lengthReleased;
        if (direction == "Up") return p.Position.y - p.Size.y <= length;
        if (direction == "Down") return -p.Position.y - p.Size.y <= length;
        if (direction == "Left") return -p.Position.x - p.Size.x <= length;
        return p.Position.x - p.Size.x <= length;
    }

    private Vector2 DirectionDelta(float distance) {
        if (direction == "Down") return new Vector2(0f, -distance);
        if (direction == "Left") return new Vector2(-distance, 0f);
        if (direction == "Right") return new Vector2(distance, 0f);
        return new Vector2(0f, distance);
    }

    private Vector2 CurrentMotion(KvMotion2 motion, string slot) {
        double x = KeyViewerInput.EaseScalar(keyCode, slot + ".x",
            motion.Released.x.ToString("R", CultureInfo.InvariantCulture),
            motion.Pressed.x.ToString("R", CultureInfo.InvariantCulture),
            motion.PressedEase.Duration.ToString("R", CultureInfo.InvariantCulture), motion.PressedEase.Type,
            motion.ReleasedEase.Duration.ToString("R", CultureInfo.InvariantCulture), motion.ReleasedEase.Type);
        double y = KeyViewerInput.EaseScalar(keyCode, slot + ".y",
            motion.Released.y.ToString("R", CultureInfo.InvariantCulture),
            motion.Pressed.y.ToString("R", CultureInfo.InvariantCulture),
            motion.PressedEase.Duration.ToString("R", CultureInfo.InvariantCulture), motion.PressedEase.Type,
            motion.ReleasedEase.Duration.ToString("R", CultureInfo.InvariantCulture), motion.ReleasedEase.Type);
        return new Vector2((float)x, (float)y);
    }

    private void SetAnchor(string anchor) {
        switch (anchor) {
            case "TopLeft": SetAnchors(new Vector2(0, 1), new Vector2(0, 1)); break;
            case "TopCenter": SetAnchors(new Vector2(.5f, 1), new Vector2(.5f, 1)); break;
            case "TopRight": SetAnchors(new Vector2(1, 1), new Vector2(1, 1)); break;
            case "MiddleLeft": SetAnchors(new Vector2(0, .5f), new Vector2(0, .5f)); break;
            case "MiddleRight": SetAnchors(new Vector2(1, .5f), new Vector2(1, .5f)); break;
            case "BottomLeft": SetAnchors(Vector2.zero, Vector2.zero); break;
            case "BottomCenter": SetAnchors(new Vector2(.5f, 0), new Vector2(.5f, 0)); break;
            case "BottomRight": SetAnchors(new Vector2(1f, 0), new Vector2(1f, 0)); break;
            case "HorizontalStretchTop": SetAnchors(new Vector2(0, 1), Vector2.one); break;
            case "HorizontalStretchMiddle": SetAnchors(new Vector2(0, .5f), new Vector2(1, .5f)); break;
            case "HorizontalStretchBottom": SetAnchors(Vector2.zero, new Vector2(1, 0)); break;
            case "VerticalStretchLeft": SetAnchors(Vector2.zero, new Vector2(0, 1)); break;
            case "VerticalStretchCenter": SetAnchors(new Vector2(.5f, 0), new Vector2(.5f, 1)); break;
            case "VerticalStretchRight": SetAnchors(new Vector2(1, 0), Vector2.one); break;
            case "FullStretch": SetAnchors(Vector2.zero, Vector2.one); break;
        }
        // Raw flag values resolve exactly like the original switch; anything
        // else (including "None") leaves the direction anchors untouched.
        // NOTE: MiddleCenter intentionally has no case above, mirroring the
        // original extension (no case → anchors untouched).
        if (!string.IsNullOrEmpty(anchor) && int.TryParse(anchor.Trim(), out int flags)
            && (flags == 17 || flags == 18 || flags == 20 || flags == 33 || flags == 34 || flags == 36
                || flags == 65 || flags == 66 || flags == 68 || flags == 24 || flags == 40 || flags == 72
                || flags == 129 || flags == 130 || flags == 132 || flags == 136)) {
            KvImporter.KvAnchorToMinMax(flags, out Vector2 min, out Vector2 max);
            SetAnchors(min, max);
        }
    }

    private void SetAnchors(Vector2 min, Vector2 max) {
        maskRect.anchorMin = min;
        maskRect.anchorMax = max;
    }

    private static Vector2 PivotValue(string pivot, Vector2 fallback) {
        // NOTE: MiddleCenter intentionally absent, mirroring the original
        // (it skips the override entirely, keeping the direction pivot).
        var table = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase) {
            ["TopLeft"] = new Vector2(0, 1), ["TopCenter"] = new Vector2(.5f, 1), ["TopRight"] = Vector2.one,
            ["MiddleLeft"] = new Vector2(0, .5f), ["MiddleRight"] = new Vector2(1, .5f),
            ["BottomLeft"] = Vector2.zero, ["BottomCenter"] = new Vector2(.5f, 0), ["BottomRight"] = new Vector2(1, 0),
        };
        if (!string.IsNullOrEmpty(pivot) && table.TryGetValue(pivot.Trim(), out var named)) return named;
        // Raw flag values (e.g. "None"/"0") follow GetPivot: unlisted combos
        // collapse to zero, exactly like the original default branch.
        if (string.Equals(pivot?.Trim(), "None", StringComparison.OrdinalIgnoreCase)) return Vector2.zero;
        if (!string.IsNullOrEmpty(pivot) && int.TryParse(pivot.Trim(), out int flags)) return KvImporter.MapPivot(flags);
        return fallback;
    }

    private void SetStyle(Particle particle, bool pressed, bool immediate) {
        var current = CurrentStyle(particle, Time.unscaledTime);
        particle.StartOffset = immediate ? (pressed ? rainOffset.Pressed : rainOffset.Released) : current.offset;
        particle.StartScale = immediate
            ? new Vector3(pressed ? rainScale.Pressed.x : rainScale.Released.x, pressed ? rainScale.Pressed.y : rainScale.Released.y, 1f)
            : current.scale;
        var targetRotation = pressed ? rainRotation.Pressed : rainRotation.Released;
        particle.StartRotation = immediate ? targetRotation : current.rotation;
        particle.TargetOffset = pressed ? rainOffset.Pressed : rainOffset.Released;
        var scale = pressed ? rainScale.Pressed : rainScale.Released;
        particle.TargetScale = new Vector3(scale.x, scale.y, 1f);
        particle.TargetRotation = targetRotation;
        var color = pressed ? rainColor.Pressed : rainColor.Released;
        particle.TargetColor = color.TopLeft.ToUnity();
        particle.TargetGradient = color;
        if (immediate) {
            particle.StartColor = particle.TargetColor;
            particle.StartGradient = particle.TargetGradient;
        } else {
            particle.StartColor = current.color;
            particle.StartGradient = current.gradient;
        }
        particle.StyleStart = Time.unscaledTime;
        particle.OffsetDuration = pressed ? rainOffset.PressedEase.Duration : rainOffset.ReleasedEase.Duration;
        particle.OffsetEase = pressed ? rainOffset.PressedEase.Type : rainOffset.ReleasedEase.Type;
        particle.ScaleDuration = pressed ? rainScale.PressedEase.Duration : rainScale.ReleasedEase.Duration;
        particle.ScaleEase = pressed ? rainScale.PressedEase.Type : rainScale.ReleasedEase.Type;
        particle.RotationDuration = pressed ? rainRotation.PressedEase.Duration : rainRotation.ReleasedEase.Duration;
        particle.RotationEase = pressed ? rainRotation.PressedEase.Type : rainRotation.ReleasedEase.Type;
        particle.ColorDuration = pressed ? rainColor.PressedEase.Duration : rainColor.ReleasedEase.Duration;
        particle.ColorEase = pressed ? rainColor.PressedEase.Type : rainColor.ReleasedEase.Type;
    }

    private (Vector2 offset, Vector3 scale, Vector3 rotation, Color color, KvGColor gradient) CurrentStyle(Particle particle, float now) {
        return (
            Vector2.Lerp(particle.StartOffset, particle.TargetOffset, Progress(now, particle.OffsetDuration, particle.OffsetEase, particle.StyleStart)),
            Vector3.Lerp(particle.StartScale, particle.TargetScale, Progress(now, particle.ScaleDuration, particle.ScaleEase, particle.StyleStart)),
            Vector3.Lerp(particle.StartRotation, particle.TargetRotation, Progress(now, particle.RotationDuration, particle.RotationEase, particle.StyleStart)),
            Color.Lerp(particle.StartColor, particle.TargetColor, Progress(now, particle.ColorDuration, particle.ColorEase, particle.StyleStart)),
            LerpGradient(particle.StartGradient, particle.TargetGradient, Progress(now, particle.ColorDuration, particle.ColorEase, particle.StyleStart))
        );
    }

    private void UpdateStyle(Particle particle) {
        var style = CurrentStyle(particle, Time.unscaledTime);
        // KeyViewer never adds the rain ObjectConfig offset to the particle
        // position (it only drives the mask), so Position goes through raw.
        particle.Rect.anchoredPosition = particle.Position;
        particle.Rect.localScale = style.scale;
        particle.Rect.localRotation = Quaternion.Euler(style.rotation);
        particle.Image.color = style.gradient.GradientEnabled ? Color.white : style.color;
        particle.Gradient.Set(style.gradient);
        particle.Image.SetVerticesDirty();
    }

    private static float Progress(float now, float duration, string ease, float start) {
        if (duration <= 0f) return 1f;
        return KeyViewerInput.EaseProgress(ease, Mathf.Clamp01((now - start) / duration));
    }

    private static KvGColor LerpGradient(KvGColor a, KvGColor b, float t) => new KvGColor {
        TopLeft = Lerp(a.TopLeft, b.TopLeft, t),
        TopRight = Lerp(a.TopRight, b.TopRight, t),
        BottomLeft = Lerp(a.BottomLeft, b.BottomLeft, t),
        BottomRight = Lerp(a.BottomRight, b.BottomRight, t),
        GradientEnabled = a.GradientEnabled || b.GradientEnabled
    };

    private static KvColor Lerp(KvColor a, KvColor b, float t) => new KvColor {
        R = Mathf.Lerp(a.R, b.R, t), G = Mathf.Lerp(a.G, b.G, t),
        B = Mathf.Lerp(a.B, b.B, t), A = Mathf.Lerp(a.A, b.A, t)
    };

    internal static (float released, float pressed) ReadPair(JToken node, float fallback) {
        float released = KvRainStore.ReadFloat(node?["Released"], fallback);
        float pressed = KvRainStore.ReadFloat(node?["Pressed"], released);
        return (released, pressed);
    }

    internal static (int released, int pressed) ReadIntPair(JToken node, int fallback) {
        int released = KvRainStore.ReadInt(node?["Released"], fallback);
        int pressed = KvRainStore.ReadInt(node?["Pressed"], released);
        return (released, pressed);
    }

    internal static KvMotionColor ReadColorMotion(JToken token) {
        var motion = new KvMotionColor();
        if (token == null) return motion;
        motion.Released = token["Released"] != null ? KvGColor.FromToken(token["Released"]) : motion.Released;
        motion.Pressed = token["Pressed"] != null ? KvGColor.FromToken(token["Pressed"]) : motion.Released;
        motion.PressedEase = KvEase.FromToken(token, "Pressed");
        motion.ReleasedEase = KvEase.FromToken(token, "Released");
        return motion;
    }
}

#if ML && IL2CPP
[MelonLoader.RegisterTypeInIl2Cpp]
#endif
public sealed class KvRainGradient : BaseMeshEffect {
#if ML && IL2CPP
    public KvRainGradient(IntPtr ptr) : base(ptr) { }
#endif
    private KvGColor color = new KvGColor();

    public void Set(KvGColor value) {
        color = value ?? new KvGColor();
        if (graphic != null) graphic.SetVerticesDirty();
    }

    public override void ModifyMesh(VertexHelper vh) {
        if (!IsActive() || vh == null || !color.GradientEnabled) return;
        Rect rect = graphic.rectTransform.rect;
        float width = Mathf.Max(rect.width, 0.0001f);
        float height = Mathf.Max(rect.height, 0.0001f);
        UIVertex vertex = default;
        for (int i = 0; i < vh.currentVertCount; i++) {
            vh.PopulateUIVertex(ref vertex, i);
            float x = Mathf.Clamp01((vertex.position.x - rect.xMin) / width);
            float y = Mathf.Clamp01((vertex.position.y - rect.yMin) / height);
            Color bottom = Color.Lerp(color.BottomLeft.ToUnity(), color.BottomRight.ToUnity(), x);
            Color top = Color.Lerp(color.TopLeft.ToUnity(), color.TopRight.ToUnity(), x);
            Color tint = color.GradientEnabled ? Color.Lerp(bottom, top, y) : color.TopLeft.ToUnity();
            vertex.color = new Color32(
                (byte)(vertex.color.r * tint.r),
                (byte)(vertex.color.g * tint.g),
                (byte)(vertex.color.b * tint.b),
                (byte)(vertex.color.a * tint.a));
            vh.SetUIVertex(vertex, i);
        }
    }
}

internal static class KvRainSpriteCache {
    private static readonly Dictionary<string, Sprite> sprites = new(StringComparer.Ordinal);
    private static readonly List<Texture2D> textures = new();

    public static Sprite Get(Sprite source, float roundness) {
        if (roundness <= 0f) return source;
        string key = (source != null ? source.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "white")
            + ":" + roundness.ToString("0.###", CultureInfo.InvariantCulture);
        if (sprites.TryGetValue(key, out var cached) && cached != null) return cached;
        try {
            int width = source != null ? Mathf.Max(1, Mathf.RoundToInt(source.rect.width)) : 100;
            int height = source != null ? Mathf.Max(1, Mathf.RoundToInt(source.rect.height)) : 100;
            Color[] pixels;
            if (source != null) {
                Rect sr = source.rect;
                pixels = source.texture.GetPixels(Mathf.RoundToInt(sr.x), Mathf.RoundToInt(sr.y), width, height);
            } else {
                pixels = Enumerable.Repeat(Color.white, width * height).ToArray();
            }

            float radius = Mathf.Clamp(roundness * 90f, 0f, Mathf.Min(width, height) * 0.5f);
            const int samples = 4;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int coverage = 0;
                    for (int sy = 0; sy < samples; sy++) {
                        for (int sx = 0; sx < samples; sx++) {
                            if (InsideRoundedRect(x + (sx + .5f) / samples, y + (sy + .5f) / samples,
                                width * .5f, height * .5f, width * .5f, height * .5f, radius)) coverage++;
                        }
                    }
                    int i = y * width + x;
                    pixels[i].a *= (float)coverage / (samples * samples);
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true) {
                name = "KeyViewerRuntimeRainRoundness",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            textures.Add(texture);
            var rect = new Rect(0, 0, width, height);
            var pivot = source != null ? source.pivot / new Vector2(width, height) : new Vector2(.5f, .5f);
            float ppu = source != null ? source.pixelsPerUnit : 100f;
            Vector4 border = source != null ? source.border : new Vector4(15, 15, 15, 15);
            var generated = Sprite.Create(texture, rect, pivot, ppu, 0, SpriteMeshType.FullRect, border, false);
            generated.name = "KeyViewerRuntimeRainRoundnessSprite";
            sprites[key] = generated;
            return generated;
        } catch {
            return source;
        }
    }

    private static bool InsideRoundedRect(float x, float y, float centerX, float centerY, float halfX, float halfY, float radius) {
        float qx = Mathf.Abs(x - centerX) - (halfX - radius);
        float qy = Mathf.Abs(y - centerY) - (halfY - radius);
        float outsideX = Mathf.Max(qx, 0f);
        float outsideY = Mathf.Max(qy, 0f);
        float distance = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY)
            + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        return distance <= 0f;
    }

    public static void Dispose() {
        foreach (var sprite in sprites.Values) if (sprite != null) UnityEngine.Object.Destroy(sprite);
        foreach (var texture in textures) if (texture != null) UnityEngine.Object.Destroy(texture);
        sprites.Clear();
        textures.Clear();
    }
}
