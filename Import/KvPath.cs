using System.IO;

namespace Overlayer.Module.KeyViewer.Import;

internal static class KvPath {
    public static string Normalize(string path) => (path ?? string.Empty).Replace('\\', '/');

    public static string FileName(string path) => Path.GetFileName(Normalize(path));

    public static string FileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(Normalize(path));

    public static string Expand(string path, string sourceFile) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try {
            string sourceDirectory = Path.GetDirectoryName(sourceFile) ?? string.Empty;
            string expanded = Normalize(path).Replace("{ModDir}", Normalize(sourceDirectory));
            if (Path.IsPathRooted(expanded)) {
                return expanded.Replace('/', Path.DirectorySeparatorChar);
            }
            string relative = expanded.Replace('/', Path.DirectorySeparatorChar);
            string besideProfile = Path.Combine(sourceDirectory, relative);
            if (File.Exists(besideProfile)) return besideProfile;
            if (File.Exists(relative)) return relative;
            return besideProfile;
        } catch {
            return null;
        }
    }
}
