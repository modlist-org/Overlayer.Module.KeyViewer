using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Overlayer.Module.KeyViewer.Import;

// Unified naming for everything one KeyViewer import generates:
// canvas, JS helper, JS tags, image/font resource keys.
//
// Prefix shape: KeyViewer_(hash7)_Name
// hash7 is the first 7 hex chars of the SHA-1 digest, the same short-hash
// style git uses. For fresh imports it hashes the profile file content with
// git's own blob framing, so `git hash-object <profile>` prints the same
// prefix hash.
public sealed class KvIdentity {
    public string Hash7;
    public string Name;
    public string Prefix;
}

public static class KvIdentityBuilder {
    private static readonly Regex PrefixedPattern =
        new Regex(@"^KeyViewer_[0-9a-fA-F]{7}_.+", RegexOptions.Compiled);

    public static string Hash7(byte[] data) {
        data ??= Array.Empty<byte>();
        // Same framing git uses for blobs: "blob {len}\0{content}".
        byte[] header = Encoding.UTF8.GetBytes("blob " + data.Length + "\0");
        byte[] framed = new byte[header.Length + data.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(data, 0, framed, header.Length, data.Length);
        using (var sha = SHA1.Create()) {
            string hex = BitConverter.ToString(sha.ComputeHash(framed)).Replace("-", string.Empty).ToLowerInvariant();
            return hex.Length >= 7 ? hex.Substring(0, 7) : hex.PadRight(7, '0');
        }
    }

    public static string Hash7(string text)
        => Hash7(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static string Sanitize(string name, int maxLength = 48) {
        if (string.IsNullOrWhiteSpace(name)) return "Profile";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Commas/colons/braces would break TextEngine {Tag:arg,arg} parsing
        // if the prefix is ever embedded in a template, so strip them too.
        foreach (char c in new[] { ',', ':', ';', '{', '}', '(', ')' }) name = name.Replace(c, '_');
        name = name.Replace(' ', '_').Trim(new[]{'_'});
        if (string.IsNullOrEmpty(name)) return "Profile";
        if (name.Length > maxLength) name = name.Substring(0, maxLength).TrimEnd(new[]{'_'});
        if (string.IsNullOrEmpty(name)) return "Profile";
        return name;
    }

    public static KvIdentity FromProfile(string profileName, byte[] contentBytes) {
        var identity = new KvIdentity {
            Hash7 = Hash7(contentBytes),
            Name = Sanitize(profileName)
        };
        identity.Prefix = "KeyViewer_" + identity.Hash7 + "_" + identity.Name;
        return identity;
    }

    public static bool IsPrefixed(string name)
        => !string.IsNullOrEmpty(name) && PrefixedPattern.IsMatch(name);

    public static string HashFromPrefixed(string prefixedName) {
        if (string.IsNullOrEmpty(prefixedName)) return null;
        var match = Regex.Match(prefixedName, @"^KeyViewer_([0-9a-fA-F]{7})_.+");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
}
