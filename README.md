# Overlayer.Module.KeyViewer

Import-only KeyViewer v4 profile converter for Overlayer Canvas. Key states, press/release easing, the KeyViewer-style generated background/outline, and Rain trails are handled by this module.

The module requires Overlayer and O5Kit; it cannot run standalone.

## Build

Set `GamePath` and `GameData` in `Directory.Build.props` (copy `Directory.Build.example.props` first), then build `Overlayer.Module.sln` with `Release`.

The KeyViewer background and outline are generated at runtime and registered in Overlayer UserResources; no KeyViewer image files are embedded in the module.
