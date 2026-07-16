using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// This single-package layout is retired as of v1.1.0 — the repo restructured into separate
/// com.hk.modtools.* packages under Packages/ (shared/core/compatpatcher/unitvisuals), each with
/// its own Update Checker. This stub replaces the old tag-checking logic with a one-time "package
/// moved" notice so existing installs pinned to this git URL get an actionable heads-up instead of
/// the updater silently going stale (there will never be a newer plain-semver tag for it to find).
/// See the README's "Migrating from the pre-1.1.0 single-package layout" section.
/// </summary>
[InitializeOnLoad]
public static class UpdateChecker
{
    const string MenuPath = "Tools/shakee's Tools/Check For Updates";
    const string ShownPrefKey = "HKEditorScripts.UpdateChecker.MovedNoticeShown";
    const string MovedMessage =
        "HK Editor Scripts has moved to a multi-package layout.\n\n" +
        "This git URL (no ?path=) now points at a retired single-package snapshot that will not " +
        "receive further updates. To keep getting updates, switch to the new per-tool packages " +
        "described in the README:\n\n" +
        "https://github.com/shakee2/HK_EditorScripts#readme";

    static bool startupCheckDone;

    static UpdateChecker()
    {
        EditorApplication.update += RunStartupCheckOnce;
    }

    static void RunStartupCheckOnce()
    {
        EditorApplication.update -= RunStartupCheckOnce;
        if (startupCheckDone) return;
        startupCheckDone = true;

        if (EditorPrefs.GetBool(ShownPrefKey, false)) return;
        EditorPrefs.SetBool(ShownPrefKey, true);
        ShowMovedDialog();
    }

    [MenuItem(MenuPath, false, 201)]
    static void CheckForUpdateMenuItem() => ShowMovedDialog();

    static void ShowMovedDialog() =>
        EditorUtility.DisplayDialog("HK Editor Scripts Has Moved", MovedMessage, "OK");
}
