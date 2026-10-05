using Newtonsoft.Json.Linq;
using Overlayer.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overlayer.Module.KeyViewer.Import;

// Persistent backing store for per-profile press counts, and the single owner
// of press edge state.
//
// Why edge detection lives here instead of next to the Fx text: a total-only
// canvas evaluates just Tag.KV_Count("PREFIX",""). If counting happened only
// in per-key evaluations, nothing would ever feed the store and the total
// would sit at 0 forever. Total() therefore feeds every registered code
// itself before summing, so totals advance no matter which texts exist.
//
// Counts live under UserData/.../Module/KeyViewer/Counts/{prefix}.json and
// are flushed at most every few seconds plus on mod disable/dispose.
//
// Called from generated Fx via the native KV_Count / KV_ResetCounts tags.
public static class KvCountStore {
    private static readonly object gate = new object();
    private static readonly Dictionary<string, Dictionary<string, int>> counts =
        new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> codes =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> prev =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    private static readonly HashSet<string> dirty =
        new HashSet<string>(StringComparer.Ordinal);
    private static double lastFlush;
    private const double FlushIntervalSeconds = 5.0;
    private const int MaxCodesPerProfile = 4096;
    private const int MaxTrackedPrev = 8192;
    private const char Separator = '\u001f';

    public static string Folder => Path.Combine(MainCore.Paths.ModulePath, "KeyViewer", "Counts");

    // Registers the key codes belonging to an import so Total() can feed
    // them even when no per-key text is displayed. Union semantics: repeated
    // imports accumulate, never shrink.
    public static void Register(string prefix, IEnumerable<string> keys) {
        if (string.IsNullOrEmpty(prefix) || keys == null) return;
        lock (gate) {
            var set = GetCodesLocked(prefix);
            bool added = false;
            foreach (var raw in keys) {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string code = raw.Trim();
                if (code.Length == 0) continue;
                if (set.Count >= MaxCodesPerProfile && !set.Contains(code)) break;
                added |= set.Add(code);
            }
            // Always ensure the profile entry exists so the code list survives
            // restarts even before the first press lands.
            LoadLocked(prefix);
            if (added) {
                dirty.Add(prefix);
                FlushDirtyLocked();
            }
        }
    }

    public static int Get(string prefix, string code) {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(code)) return 0;
        lock (gate) {
            var profile = LoadLocked(prefix);
            return profile.TryGetValue(code, out int value) ? Math.Max(0, value) : 0;
        }
    }

    // Rising-edge feed for one key. Shared by per-key and total evaluations
    // through a single prev table, so a press is never counted twice.
    public static int Feed(string prefix, string code, bool held) {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(code)) return 0;
        lock (gate) {
            if (FeedLocked(prefix, code, held)) return AddLocked(prefix, code, 1);
            var profile = LoadLocked(prefix);
            return profile.TryGetValue(code, out int value) ? Math.Max(0, value) : 0;
        }
    }

    public static int Add(string prefix, string code, int delta) {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(code)) return 0;
        lock (gate) {
            return AddLocked(prefix, code, delta);
        }
    }

    public static int Total(string prefix) {
        if (string.IsNullOrEmpty(prefix)) return 0;
        lock (gate) {
            var profile = LoadLocked(prefix);
            // Union registered codes with anything already stored so older
            // sidecars (which predate registration) still feed correctly.
            var feed = new HashSet<string>(StringComparer.Ordinal);
            if (codes.TryGetValue(prefix, out var registered)) {
                foreach (var c in registered) feed.Add(c);
            }
            foreach (var c in profile.Keys) feed.Add(c);
            foreach (var code in feed) {
                bool held = false;
                try {
                    held = Tag.KeyViewerInput.IsKeyHeld(code);
                } catch {
                    held = false;
                }
                if (FeedLocked(prefix, code, held)) {
                    AddLocked(prefix, code, 1);
                }
            }
            long total = 0;
            foreach (var value in profile.Values) {
                total += Math.Max(0, value);
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }

    public static string Reset(string prefix) {
        if (string.IsNullOrEmpty(prefix)) return string.Empty;
        lock (gate) {
            var profile = LoadLocked(prefix);
            profile.Clear();
            // Re-arm previous states so currently-held keys do not instantly
            // recount on the next frame.
            string marker = prefix + Separator;
            foreach (var key in new List<string>(prev.Keys)) {
                if (!key.StartsWith(marker, StringComparison.Ordinal)) continue;
                bool held = false;
                try {
                    held = Tag.KeyViewerInput.IsKeyHeld(key.Substring(marker.Length));
                } catch {
                }
                prev[key] = held;
            }
            dirty.Add(prefix);
            FlushDirtyLocked();
            return string.Empty;
        }
    }

    internal static void FlushAll() {
        lock (gate) {
            FlushDirtyLocked();
        }
    }

    private static int AddLocked(string prefix, string code, int delta) {
        var profile = LoadLocked(prefix);
        if (!profile.ContainsKey(code) && profile.Count >= MaxCodesPerProfile) {
            return profile.TryGetValue(code, out int existing) ? existing : 0;
        }
        int next = Math.Max(0, (profile.TryGetValue(code, out int current) ? current : 0) + delta);
        profile[code] = next;
        dirty.Add(prefix);
        FlushThrottledLocked();
        return next;
    }

    private static bool FeedLocked(string prefix, string code, bool held) {
        if (prev.Count >= MaxTrackedPrev) prev.Clear();
        string key = prefix + Separator + code;
        bool was = prev.TryGetValue(key, out bool p) && p;
        prev[key] = held;
        return held && !was;
    }

    private static HashSet<string> GetCodesLocked(string prefix) {
        if (!codes.TryGetValue(prefix, out var set)) {
            set = new HashSet<string>(StringComparer.Ordinal);
            codes[prefix] = set;
        }
        return set;
    }

    private static void FlushThrottledLocked() {
        if (dirty.Count == 0) return;
        double now = UnityEngine.Time.realtimeSinceStartup;
        if (now - lastFlush < FlushIntervalSeconds) return;
        lastFlush = now;
        FlushDirtyLocked();
    }

    private static void FlushDirtyLocked() {
        if (dirty.Count == 0) return;
        try {
            Directory.CreateDirectory(Folder);
        } catch {
            return;
        }
        foreach (var prefix in dirty) {
            try {
                if (!counts.TryGetValue(prefix, out var profile)) continue;
                var obj = new JObject {
                    ["counts"] = new JObject(),
                    ["codes"] = new JArray()
                };
                var countsObj = (JObject)obj["counts"];
                foreach (var kv in profile) {
                    countsObj[kv.Key] = Math.Max(0, kv.Value);
                }
                var codesArr = (JArray)obj["codes"];
                if (codes.TryGetValue(prefix, out var set)) {
                    foreach (var c in set.OrderBy(x => x, StringComparer.Ordinal)) {
                        codesArr.Add(c);
                    }
                }
                File.WriteAllText(SidecarPath(prefix), obj.ToString());
            } catch (Exception e) {
                try { Core.Logger.Wrn($"[KeyViewer] Count save failed ({prefix}): {e.Message}"); } catch { }
            }
        }
        dirty.Clear();
        lastFlush = UnityEngine.Time.realtimeSinceStartup;
    }

    private static Dictionary<string, int> LoadLocked(string prefix) {
        if (counts.TryGetValue(prefix, out var profile)) return profile;
        profile = new Dictionary<string, int>(StringComparer.Ordinal);
        counts[prefix] = profile;
        try {
            string path = SidecarPath(prefix);
            if (!File.Exists(path)) return profile;
            var token = JToken.Parse(File.ReadAllText(path));
            if (token is JObject obj) {
                // Current format first, then the legacy flat {code: count}.
                JToken countsToken = obj["counts"] ?? token;
                if (countsToken is JObject countsObj) {
                    foreach (var property in countsObj.Properties()) {
                        if (string.IsNullOrEmpty(property.Name)) continue;
                        try {
                            profile[property.Name] = Math.Max(0, property.Value.Value<int>());
                        } catch {
                            // Skip corrupt entries, keep the rest.
                        }
                        if (profile.Count >= MaxCodesPerProfile) break;
                    }
                }
                if (obj["codes"] is JArray codesArr) {
                    var set = GetCodesLocked(prefix);
                    foreach (var item in codesArr) {
                        try {
                            string code = item.Value<string>()?.Trim();
                            if (string.IsNullOrEmpty(code)) continue;
                            if (set.Count >= MaxCodesPerProfile && !set.Contains(code)) break;
                            set.Add(code);
                        } catch {
                            // Skip corrupt entries, keep the rest.
                        }
                    }
                }
            }
        } catch (Exception e) {
            try { Core.Logger.Wrn($"[KeyViewer] Count load failed ({prefix}): {e.Message}"); } catch { }
        }
        return profile;
    }

    private static string SidecarPath(string prefix) {
        string safe = prefix ?? "KeyViewer";
        foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return Path.Combine(Folder, safe + ".json");
    }
}
