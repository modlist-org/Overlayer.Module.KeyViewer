using Overlayer.Compat;
using Overlayer.Core;
using Overlayer.Localization;
using Overlayer.Module.KeyViewer.Import;
using Overlayer.UI.Factory;
using O5Kit.Behaviour;
using O5Kit.Control;
using O5Kit.Core;
using O5Kit.Factory;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

#if ML && IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace Overlayer.Module.KeyViewer.UI;

public static class MainUI {
    private static O5InputField pathInput;
    private static O5InputField nameInput;
    private static O5Toggle countToggle;
    private static O5Toggle imageToggle;
    private static TextMeshProUGUI statusLabel;

    private static readonly Dictionary<string, O5Object> objects = new Dictionary<string, O5Object>();

    public static void CreateMenu(RectTransform parent)
        => MenuFactory.CreateItem(parent, "KeyViewer", MainCore.Spr.Get(Overlayer.Resource.UISprite.Text128), 101)
        .label.gameObject.AddComponent<TextLocalization>().Init("KEYVIEWER", "KeyViewer", Core.Tr);

    public static void CreatePage(RectTransform parent) {
        GameObject pad = new GameObject("Pad");
        pad.transform.SetParent(parent, false);

        RectTransform padRect = pad.AddComponent<RectTransform>();
        padRect.anchorMin = Vector2.zero;
        padRect.anchorMax = Vector2.one;
        padRect.pivot = new Vector2(0.5f, 0.5f);
        padRect.offsetMin = new Vector2(18f, 18f);
        padRect.offsetMax = new Vector2(-18f, -18f);

        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(pad.transform, false);

        RectTransform viewportRect = viewport.AddComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewportRect.pivot = new Vector2(0.5f, 0.5f);

        viewport.AddComponent<EmptyGraphic>().raycastTarget = true;
        viewport.AddComponent<RectMask2D>();

        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);

        RectTransform contentRect = content.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 12f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        pad.AddComponent<UIScrollController>().SetContent(contentRect, viewportRect);

        _ = O5Factory.ControlTextH1(O5KitAdapters.Ctx, O5Factory.Row(O5KitAdapters.Ctx, content.transform))
           .gameObject.AddComponent<TextLocalization>()
           .Init("KEYVIEWER", "KeyViewer", Core.Tr);

        var desc = O5Factory.ControlText(O5KitAdapters.Ctx,
            O5Factory.Row(O5KitAdapters.Ctx, content.transform), 20f);
        desc.gameObject.AddComponent<TextLocalization>().Init(
            "KEYVIEWER_DESC",
            "Imports KeyViewer v4 profiles (*.json) into an Overlayer Canvas, including held-state transitions, easing, and Rain trails. Positions remain editable in the Overlayer inspector.",
            Core.Tr);

        pathInput = O5Factory.Input(O5KitAdapters.Ctx,
            O5Factory.Row(O5KitAdapters.Ctx, content.transform),
            string.Empty,
            string.Empty,
            _ => { },
            "KeyViewer profile JSON path",
            null,
            "keyviewer_import_path");
        pathInput.Placeholder.gameObject.AddComponent<TextLocalization>()
            .Init("KEYVIEWER_PATH_PH", "KeyViewer profile JSON path", Core.Tr);
        objects[pathInput.Id] = pathInput;

        var browseRow = O5Factory.Row(O5KitAdapters.Ctx, content.transform);
        var browse = O5Factory.Button(O5KitAdapters.Ctx, browseRow, BeginBrowse, "Browse", "keyviewer_browse");
        browse.Label.gameObject.AddComponent<TextLocalization>().Init("BROWSE", "Browse", Core.Tr);
        objects[browse.Id] = browse;

        nameInput = O5Factory.Input(O5KitAdapters.Ctx,
            O5Factory.Row(O5KitAdapters.Ctx, content.transform),
            "KeyViewer",
            "KeyViewer",
            _ => { },
            "Canvas profile name",
            null,
            "keyviewer_profile_name");
        nameInput.Placeholder.gameObject.AddComponent<TextLocalization>()
            .Init("KEYVIEWER_NAME_PH", "Canvas profile name", Core.Tr);
        objects[nameInput.Id] = nameInput;

        countToggle = O5Factory.Toggle(O5KitAdapters.Ctx,
            O5Factory.Row(O5KitAdapters.Ctx, content.transform),
            true,
            true,
            _ => { },
            "Include Count Text",
            "keyviewer_include_count");
        countToggle.Label.gameObject.AddComponent<TextLocalization>()
            .Init("KEYVIEWER_INCLUDE_COUNT", "Include Count Text", Core.Tr);
        countToggle.EnabledWhen = () => MainCore.IsModEnabled;
        objects[countToggle.Id] = countToggle;

        imageToggle = O5Factory.Toggle(O5KitAdapters.Ctx,
            O5Factory.Row(O5KitAdapters.Ctx, content.transform),
            true,
            true,
            _ => { },
            "Import Custom Images",
            "keyviewer_import_images");
        imageToggle.Label.gameObject.AddComponent<TextLocalization>()
            .Init("KEYVIEWER_IMPORT_IMAGES", "Import Custom Images", Core.Tr);
        imageToggle.EnabledWhen = () => MainCore.IsModEnabled;
        objects[imageToggle.Id] = imageToggle;
        imageToggle.Rect.AddToolTip(O5KitAdapters.Ctx, () => TooltipText(
            "DESC_KEYVIEWER_IMPORT_IMAGES",
            "Copies profile References and image files into UserResources"));

        var importRow = O5Factory.Row(O5KitAdapters.Ctx, content.transform);
        var import = O5Factory.Button(O5KitAdapters.Ctx, importRow, BeginImport, "Import To Canvas", "keyviewer_import");
        import.Label.gameObject.AddComponent<TextLocalization>().Init("KEYVIEWER_IMPORT", "Import To Canvas", Core.Tr);
        objects[import.Id] = import;

        var statusRow = O5Factory.Row(O5KitAdapters.Ctx, content.transform);
        statusLabel = O5Factory.ControlText(O5KitAdapters.Ctx, statusRow, 20f);
        statusLabel.text = Core.Tr.Get("KEYVIEWER_READY", "Ready.");
    }

    private static void BeginBrowse() {
        string currentPath = string.Empty;
        try { currentPath = pathInput?.Value ?? string.Empty; } catch { }
        string initialDirectory = !string.IsNullOrWhiteSpace(currentPath)
            ? Path.GetDirectoryName(currentPath)
            : null;
        _ = Task.Run(() => {
            try {
                string dir = initialDirectory;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) {
                    dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
                }
                return NativeFileDialog.Extended.NFD.OpenDialog(
                    dir,
                    new Dictionary<string, string> { ["JSON"] = "json" });
            } catch (System.Exception e) {
                Core.Logger.Err($"[KeyViewer] File dialog failed: {e.Message}");
                return null;
            }
        }).ContinueWith(task => {
            Overlayer.Async.MainThread.Enqueue(() => {
                if (!MainCore.IsModEnabled) return;
                string path = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (string.IsNullOrWhiteSpace(path)) return;
                try {
                    pathInput.Set(path);
                    nameInput.Set(Path.GetFileNameWithoutExtension(path));
                } catch { }
                SetStatus(Core.Tr.Get("KEYVIEWER_FILE_SELECTED", "File selected. Press Import To Canvas."));
            });
        });
    }

    private static void BeginImport() {
        string path = null;
        string name = "KeyViewer";
        try {
            path = pathInput != null ? pathInput.Value : string.Empty;
            name = nameInput != null ? nameInput.Value : "KeyViewer";
        } catch {
            path = string.Empty;
            name = "KeyViewer";
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
            SetStatus(Core.Tr.Get("KEYVIEWER_NO_FILE", "Profile JSON file not found."));
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) {
            name = Path.GetFileNameWithoutExtension(path);
        }
        SetStatus(Core.Tr.Get("KEYVIEWER_IMPORTING", "Importing..."));
        KvImportResult result;
        try {
            result = KvImporter.ImportFile(path, name,
                countToggle?.Value ?? true,
                imageToggle?.Value ?? true);
        } catch (System.Exception e) {
            SetStatus($"Import failed: {e.Message}");
            Core.Logger.Err($"[KeyViewer] Import exception: {e}");
            return;
        }
        if (!result.Success) {
            SetStatus($"Import failed: {result.Error}");
            return;
        }
        string msg = $"Imported '{result.CanvasName}'.";
        if (result.Warnings != null && result.Warnings.Count > 0) {
            msg += " " + string.Join(" ", result.Warnings);
        }
        SetStatus(msg);
    }

    private static void SetStatus(string text) {
        try {
            if (statusLabel != null) statusLabel.text = text;
        } catch { }
        try {
            Core.Logger.Msg($"[KeyViewer] {text}");
        } catch { }
    }

    private static string TooltipText(string key, string def)
        => MainCore.Conf.AdvancedTooltip
            ? $"{Core.Tr.Get(key, def)}\n--\n{Core.Tr.Get("ADV_" + key, def)}"
            : Core.Tr.Get(key, def);
}
