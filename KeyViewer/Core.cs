using Overlayer.Compat;
using Overlayer.Core;
using Overlayer.Localization;
using Overlayer.Module.KeyViewer.Import;
using Overlayer.Module.KeyViewer.UI;
using Overlayer.ModuleAPI;
using Overlayer.UI;
using Overlayer.UI.Factory;
using System;
using System.IO;
using System.Reflection;

namespace Overlayer.Module.KeyViewer;

public class Core : OverlayerModule {
    public static Assembly Assembly { get; } = Assembly.GetExecutingAssembly();
    public static OverlayerLogger Logger { get; } = new OverlayerLogger(MainCore.Host.OverlayerLogger, "KeyViewer Module");
    public static Translator Tr { get; private set; } = new Translator();

    private static void LoadTr()
        => _ = Tr.Load(Path.Combine(MainCore.Paths.ModulePath, "KeyViewer/Lang"));

    private void OnLanguageChanged(string lang)
        => Tr.Language = lang;

    public override void OnInitialize() {
        Tr.SetLog(Logger.Msg);

        MainCore.Tr.OnLoadStart += LoadTr;
        MainCore.Tr.OnLanguageChanged += OnLanguageChanged;

        LoadTr();
        Tr.Language = MainCore.Tr.Language;

        try {
            KvResources.EnsureDefaults();
        } catch (Exception e) {
            Logger.Wrn($"[KeyViewer Module] EnsureDefaults failed: {e.Message}");
        }

        try {
            KvRainStore.AttachExisting(Overlayer.Overlay.OverlayCore.Canvases);
        } catch (Exception e) {
            Logger.Wrn($"[KeyViewer Module] Rain restore failed: {e.Message}");
        }

        try {
            KvImporter.MigrateExistingCanvases();
            KvImporter.RemoveLegacySharedHelper();
        } catch (Exception e) {
            Logger.Wrn($"[KeyViewer Module] Existing Canvas migration failed: {e.Message}");
        }

        MainUI.CreateMenu(UICore.MenuContent);
        MainUI.CreatePage(PageFactory.CreatePageBase(101));
    }

    public override void OnDispose() {
        try { KvCountStore.FlushAll(); } catch { }
        try { KvRainSpriteCache.Dispose(); } catch { }
        MainCore.Tr.OnLoadStart -= LoadTr;
        MainCore.Tr.OnLanguageChanged -= OnLanguageChanged;

    }

    public override void OnModEnabledChanged(bool enabled, bool isDispose) {
        if (isDispose) return;
        if (!enabled) {
            try { KvCountStore.FlushAll(); } catch { }
            return;
        }
        try {
            KvResources.EnsureDefaults();
            KvRainStore.AttachExisting(Overlayer.Overlay.OverlayCore.Canvases);
        } catch (Exception e) {
            Logger.Wrn($"[KeyViewer Module] Runtime feature restore failed: {e.Message}");
        }
    }

    public override string Name => Info.Name;
    public override string Author => Info.Author;
    public override string Version => Info.Version;
}
