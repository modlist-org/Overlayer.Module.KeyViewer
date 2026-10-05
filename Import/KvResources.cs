using Overlayer.Core;
using Overlayer.IO.User;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Overlayer.Module.KeyViewer.Import;

public static class KvResources {
    // v3 is generated at runtime from the original KeyViewer sprite geometry.
    public const string BackgroundTextureKey = "KeyViewer_Background_v3";
    public const string OutlineTextureKey = "KeyViewer_Outline_v3";
    public const string BackgroundSpriteKey = "KeyViewer_Background_v3";
    public const string OutlineSpriteKey = "KeyViewer_Outline_v3";

    public static void EnsureDefaults() {
        try {
            EnsureImage(
                BackgroundTextureKey, BackgroundSpriteKey,
                "KeyViewer_Background_v3.png", false,
                new Vector4(15, 15, 15, 15));
            EnsureImage(
                OutlineTextureKey, OutlineSpriteKey,
                "KeyViewer_Outline_v3.png", true,
                new Vector4(15, 15, 15, 15));
        } catch (Exception e) {
            Core.Logger.Err($"[KeyViewer] EnsureDefaults failed: {e.Message}");
        }
    }

    private static void EnsureImage(string textureKey, string spriteKey, string fileName, bool outline, Vector4 border) {
        string dir = MainCore.Paths.UserImagePath;
        try { Directory.CreateDirectory(dir); } catch { }
        string path = Path.Combine(dir, fileName);

        if (!UserResourceManager.T2D.TryGet(textureKey, out _)) {
            bool loaded = false;
            try {
                if (File.Exists(path)) {
                    var first = UserResourceManager.T2D.Load(textureKey, path, false, false);
                    loaded = first
                        == Overlayer.IO.User.Impl.UserTexture2D.Result.Success
                        || first == Overlayer.IO.User.Impl.UserTexture2D.Result.KeyAlreadyExists;
                }
            } catch { }
            if (!loaded) {
                try {
                    byte[] png = GeneratePng(outline);
                    if (png != null && png.Length > 0) {
                        File.WriteAllBytes(path, png);
                    }
                } catch (Exception e) {
                    Core.Logger.Wrn($"[KeyViewer] Failed to write {fileName}: {e.Message}");
                }
                try {
                    var result = UserResourceManager.T2D.Load(textureKey, path, false, false);
                    loaded = result
                        == Overlayer.IO.User.Impl.UserTexture2D.Result.Success
                        || result == Overlayer.IO.User.Impl.UserTexture2D.Result.KeyAlreadyExists;
                } catch (Exception e) {
                    Core.Logger.Wrn($"[KeyViewer] Texture load failed ({textureKey}): {e.Message}");
                }
            }
        }

        if (!UserResourceManager.Spr.TryGet(spriteKey, out _)) {
            try {
                if (UserResourceManager.T2D.TryGet(textureKey, out var tex)) {
                    var texture = tex.texture;
                    if (texture != null) {
                        var rect = new Rect(0, 0, texture.width, texture.height);
                        UserResourceManager.Spr.Load(
                            spriteKey, textureKey, rect,
                            new Vector2(0.5f, 0.5f), 100f, border, out _);
                    }
                }
            } catch (Exception e) {
                Core.Logger.Wrn($"[KeyViewer] Sprite load failed ({spriteKey}): {e.Message}");
            }
        }

    }

    private static byte[] GeneratePng(bool outline) {
        const int size = 100;
        const int samples = 4;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true) {
            name = outline ? "KeyViewerGeneratedOutline" : "KeyViewerGeneratedBackground",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                int coverage = 0;
                for (int sy = 0; sy < samples; sy++) {
                    for (int sx = 0; sx < samples; sx++) {
                        float px = x + (sx + 0.5f) / samples;
                        float py = y + (sy + 0.5f) / samples;
                        bool inside;
                        if (outline) {
                            inside = InsideRoundedRect(px, py, 49.5f, 49.5f, 49.5f, 49.5f, 13f)
                                && !InsideRoundedRect(px, py, 49.5f, 49.5f, 45.5f, 45.5f, 9f);
                        } else {
                            inside = InsideRoundedRect(px, py, 49.5f, 49.5f, 47.5f, 47.5f, 12f);
                        }
                        if (inside) coverage++;
                    }
                }
                byte alpha = (byte)((coverage * 255 + samples * samples / 2) / (samples * samples));
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        try {
            return texture.EncodeToPNG();
        } finally {
            UnityEngine.Object.Destroy(texture);
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

    // Imports raw image bytes (profile References or existing files) into
    // UserResources and returns the sprite key to use. Falls back to
    // fallbackSpriteKey when anything fails. Keys share the import prefix:
    // KeyViewer_(hash7)_Name_Base_contenthash.
    public static string ImportImageBytes(string prefix, string baseName, byte[] png, string fallbackSpriteKey) {
        if (png == null || png.Length == 0) return fallbackSpriteKey;
        try {
            string safeBase = Sanitize(string.IsNullOrWhiteSpace(baseName) ? "image" : baseName);
            string contentHash = KvIdentityBuilder.Hash7(png);
            string textureKey = $"{prefix}_{safeBase}_{contentHash}";
            string spriteKey = textureKey;
            if (UserResourceManager.Spr.TryGet(spriteKey, out _)) return spriteKey;

            string dir = MainCore.Paths.UserImagePath;
            Directory.CreateDirectory(dir);
            string fileName = $"{textureKey}.png";
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path) && !UserResourceManager.T2D.TryGet(textureKey, out _)) {
                // File from a previous run (or a user-provided file): adopt it.
                var adopted = UserResourceManager.T2D.Load(textureKey, path, false, false);
                if (adopted == Overlayer.IO.User.Impl.UserTexture2D.Result.Success
                    || adopted == Overlayer.IO.User.Impl.UserTexture2D.Result.KeyAlreadyExists) {
                    return EnsureSprite(spriteKey, textureKey) ? spriteKey : fallbackSpriteKey;
                }
            }
            int suffix = 1;
            while (File.Exists(path) && !UserResourceManager.T2D.TryGet(textureKey, out _)) {
                textureKey = $"{prefix}_{safeBase}_{suffix}";
                spriteKey = textureKey;
                if (UserResourceManager.Spr.TryGet(spriteKey, out _)) return spriteKey;
                fileName = $"{textureKey}.png";
                path = Path.Combine(dir, fileName);
                suffix++;
            }
            if (!File.Exists(path)) {
                File.WriteAllBytes(path, png);
            }
            if (!UserResourceManager.T2D.TryGet(textureKey, out _)) {
                var result = UserResourceManager.T2D.Load(textureKey, path, false, false);
                if (result != Overlayer.IO.User.Impl.UserTexture2D.Result.Success
                    && result != Overlayer.IO.User.Impl.UserTexture2D.Result.KeyAlreadyExists) {
                    return fallbackSpriteKey;
                }
            }
            if (!UserResourceManager.Spr.TryGet(spriteKey, out _)) {
                if (!EnsureSprite(spriteKey, textureKey)) {
                    return fallbackSpriteKey;
                }
            }
            try { UserResourceManager.Config.Save(); } catch { }
            return spriteKey;
        } catch {
            return fallbackSpriteKey;
        }
    }

    private static bool EnsureSprite(string spriteKey, string textureKey) {
        try {
            if (UserResourceManager.Spr.TryGet(spriteKey, out _)) return true;
            if (UserResourceManager.T2D.TryGet(textureKey, out var tex) && tex.texture != null) {
                var rect = new Rect(0, 0, tex.texture.width, tex.texture.height);
                UserResourceManager.Spr.Load(
                    spriteKey, textureKey, rect,
                    new Vector2(0.5f, 0.5f), 100f, Vector4.zero, out _);
                return UserResourceManager.Spr.TryGet(spriteKey, out _);
            }
        } catch { }
        return false;
    }

    public static Dictionary<string, string> ImportReferences(string prefix, List<KvReference> references) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (references == null) return map;
        foreach (var r in references) {
            if (r == null) continue;
            if (!string.Equals(r.ReferenceType, "Image", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(r.Name) || r.Raw == null || r.Raw.Length == 0) continue;
            string key = ImportImageBytes(prefix, KvPath.FileNameWithoutExtension(r.Name), r.Raw, null);
            if (!string.IsNullOrEmpty(key)) {
                map[r.Name] = key;
                map[KvPath.Normalize(r.Name)] = key;
                if (!string.IsNullOrEmpty(r.From)) map[KvPath.Normalize(r.From)] = key;
                map[KvPath.FileName(r.Name)] = key;
            }
        }
        return map;
    }

    // Imports raw font bytes (profile References or existing files) into
    // UserResources and returns the font key to use. Returns null when the
    // bytes cannot be registered. Keys share the import prefix:
    // KeyViewer_(hash7)_Name_Base.
    public static string ImportFontBytes(string prefix, string baseName, string extension, byte[] data) {
        if (data == null || data.Length == 0) return null;
        try {
            string ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf") return null;
            string safeBase = Sanitize(string.IsNullOrWhiteSpace(baseName) ? "font" : baseName);
            string fontKey = $"{prefix}_{safeBase}";
            if (UserResourceManager.Fnt.TryGet(fontKey, out _)) return fontKey;

            string dir = MainCore.Paths.UserFontPath;
            Directory.CreateDirectory(dir);
            string fileName = fontKey + ext;
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) {
                File.WriteAllBytes(path, data);
            }
            if (!UserResourceManager.Fnt.TryGet(fontKey, out _)) {
                var result = UserResourceManager.Fnt.Load(fontKey, path);
                if (result != Overlayer.IO.User.Impl.UserFont.Result.Success
                    && result != Overlayer.IO.User.Impl.UserFont.Result.KeyAlreadyExists) {
                    return null;
                }
            }
            if (!UserResourceManager.Fnt.TryGet(fontKey, out _)) return null;
            try { UserResourceManager.Config.Save(); } catch { }
            try { Overlayer.Compat.O5KitAdapters.RefreshFonts(); } catch { }
            return fontKey;
        } catch {
            return null;
        }
    }

    public static Dictionary<string, string> ImportFontReferences(string prefix, List<KvReference> references) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (references == null) return map;
        foreach (var r in references) {
            if (r == null) continue;
            if (!string.Equals(r.ReferenceType, "Font", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(r.Name) || r.Raw == null || r.Raw.Length == 0) continue;
            string ext = Path.GetExtension(KvPath.Normalize(r.Name));
            if (string.IsNullOrEmpty(ext)) ext = ".ttf";
            string key = ImportFontBytes(prefix, KvPath.FileNameWithoutExtension(r.Name), ext, r.Raw);
            if (!string.IsNullOrEmpty(key)) {
                map[r.Name] = key;
                map[KvPath.Normalize(r.Name)] = key;
                if (!string.IsNullOrEmpty(r.From)) map[KvPath.Normalize(r.From)] = key;
                map[KvPath.FileName(r.Name)] = key;
            }
        }
        return map;
    }

    private static string Sanitize(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "profile";
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid) name = name.Replace(c, '_');
        name = name.Replace(' ', '_');
        if (name.Length > 48) name = name.Substring(0, 48);
        return name;
    }
}
