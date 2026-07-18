using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Framework;
using Amplitude.Framework.Utility;
using Amplitude.Mercury.Data;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Builds the patch the way the Mod Editor's "Override from Archives" does
    /// (VanillaDatabaseMount.OverrideVanillaElement): resolve the element's collection type,
    /// get-or-create that collection under Assets/Databases/Patch/, then duplicate the element into
    /// it by name (ensureUniqueName:false → overrides by name at load).
    ///
    /// Sources are read as text (never imported), so to get a live element we first *stage* its whole
    /// source .asset into a scratch folder we control. Staging is also used by the side-by-side compare
    /// view. Callers must CleanupStage() when done with a staged object.
    /// </summary>
    public static class PatchBuilder
    {
        public const string PatchDir = "Assets/Databases/Patch";
        const string StageRoot = "Assets/_PatcherStage";

        /// <summary>Stage an element's source file and return the live element (+ its collection type + scratch path).</summary>
        public static (UnityEngine.Object element, Type collectionType, string stagePath) StageElement(HkMod mod, HkElement el)
        {
            if (mod == null || el == null) return (null, null, null);

            // Assetbundle path: use the mounted live object (no _PatcherStage).
            if (el.LiveObject != null)
            {
                Type colType = null;
                DatatableElementCollectionUtility.TryGetCollectionTypeFromElementType(el.LiveObject.GetType(), ref colType);
                return (el.LiveObject, colType, null);
            }

            if (!mod.RawFiles.TryGetValue(el.SourcePath, out var text))
                return (null, null, null);
            string stagePath = StageFile(mod.Name, el.SourcePath, text);
            return LoadStagedElement(stagePath, el.Name);
        }

        /// <summary>Duplicate an already-live object into Patch/ (no staging / no CleanupStage).</summary>
        public static UnityEngine.Object ImportLive(UnityEngine.Object live, string typeHint)
            => Duplicate(live, null, typeHint);

        static (UnityEngine.Object element, Type collectionType, string stagePath) LoadStagedElement(string stagePath, string elementName)
        {
            UnityEngine.Object live = null;
            Type fileColType = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(stagePath))
            {
                if (o == null) continue;
                if (fileColType == null && o is IDatatableElementCollection) fileColType = o.GetType();
                if (live == null && o is IDatatableElement && o.name == elementName) live = o;
            }
            return (live, fileColType, stagePath);
        }

        static readonly HashSet<string> PinnedStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Keep a staged path alive across other callers' <see cref="CleanupStage"/> (e.g. Compare's
        /// session repository). Unpin when the owner is done.
        /// </summary>
        public static void PinStage(string stagePath)
        {
            if (!string.IsNullOrEmpty(stagePath)) PinnedStages.Add(stagePath);
        }

        public static void UnpinStage(string stagePath)
        {
            if (!string.IsNullOrEmpty(stagePath)) PinnedStages.Remove(stagePath);
        }

        public static void CleanupStage(string stagePath)
        {
            if (string.IsNullOrEmpty(stagePath)) return;
            if (PinnedStages.Contains(stagePath)) return;
            AssetDatabase.DeleteAsset(stagePath);
            // Prune the now-empty per-source subfolder we created for this file (best-effort).
            try
            {
                string dir = System.IO.Path.GetDirectoryName(stagePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir) && dir.StartsWith(StageRoot + "/", StringComparison.Ordinal)
                    && dir.Length > StageRoot.Length + 1 && Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                    AssetDatabase.DeleteAsset(dir);
            }
            catch { /* leftover empty scratch folder is harmless */ }
        }

        /// <summary>
        /// Stage a whole source .asset file and hand back *every* live object in it (elements + collection),
        /// so a caller that needs several elements out of one file imports it only once. Caller must
        /// CleanupStage(stagePath) when done. Used by the load-order validator to overlay Odin-correct mod
        /// versions on top of the vanilla live objects. For assetbundle mods, returns LiveObjects with a
        /// null stagePath (nothing to clean up).
        /// </summary>
        public static (UnityEngine.Object[] objects, string stagePath) StageSourceFile(HkMod mod, string sourcePath)
        {
            if (mod == null || sourcePath == null) return (Array.Empty<UnityEngine.Object>(), null);

            if (mod.FromAssetBundle)
            {
                var lives = mod.Elements.Values
                    .Where(e => e.SourcePath == sourcePath && e.LiveObject != null)
                    .Select(e => e.LiveObject)
                    .Distinct()
                    .ToArray();
                return (lives, null);
            }

            if (!mod.RawFiles.TryGetValue(sourcePath, out var text))
                return (Array.Empty<UnityEngine.Object>(), null);
            string stagePath = StageFile(mod.Name, sourcePath, text);
            return (AssetDatabase.LoadAllAssetsAtPath(stagePath), stagePath);
        }

        /// <summary>Duplicate one element into the Patch collection; returns the new element.</summary>
        public static UnityEngine.Object ImportElement(HkMod mod, HkElement el)
        {
            var (live, colType, stagePath) = StageElement(mod, el);
            try { return Duplicate(live, colType, el.TypeHint); }
            finally { CleanupStage(stagePath); AssetDatabase.SaveAssets(); }
        }

        /// <summary>
        /// Batch-import many elements, staging each distinct source file only once. Returns the count
        /// duplicated. Used by mass "import all conflicts".
        /// </summary>
        public static int ImportElements(IEnumerable<(HkMod mod, HkElement el)> targets)
        {
            var seen = new HashSet<string>();
            var list = targets.Where(t => t.mod != null && t.el != null
                                          && seen.Add(t.mod.Name + ":" + t.el.Type + ":" + t.el.Name))
                              .ToList();

            // Bundle-sourced elements: import directly from LiveObject (no text staging).
            var liveTargets = list.Where(t => t.el.LiveObject != null).ToList();
            var textTargets = list.Where(t => t.el.LiveObject == null).ToList();

            int done = 0;
            try
            {
                for (int i = 0; i < liveTargets.Count; i++)
                {
                    var t = liveTargets[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Importing to patch",
                        t.el.Name, (float)i / System.Math.Max(1, list.Count))) break;
                    if (Duplicate(t.el.LiveObject, null, t.el.TypeHint) != null) done++;
                }

                var groups = textTargets.GroupBy(t => t.mod.Name + "" + t.el.SourcePath).ToList();
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    var first = g.First();
                    if (!first.mod.RawFiles.TryGetValue(first.el.SourcePath, out var text)) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("Importing to patch",
                        first.el.SourcePath, (float)(liveTargets.Count + gi) / System.Math.Max(1, list.Count))) break;

                    string stage = StageFile(first.mod.Name, first.el.SourcePath, text);
                    try
                    {
                        var all = AssetDatabase.LoadAllAssetsAtPath(stage);
                        Type fileColType = all.FirstOrDefault(o => o is IDatatableElementCollection)?.GetType();
                        foreach (var t in g)
                        {
                            var live = all.FirstOrDefault(o => o != null && o.name == t.el.Name && o is IDatatableElement);
                            if (Duplicate(live, fileColType, t.el.TypeHint) != null) done++;
                        }
                    }
                    finally { CleanupStage(stage); }
                }
            }
            finally { EditorUtility.ClearProgressBar(); AssetDatabase.SaveAssets(); }
            return done;
        }

        static UnityEngine.Object Duplicate(UnityEngine.Object live, Type colType, string collectionName)
        {
            if (live == null) { Debug.LogWarning("[CompatPatcher] no live element to import (staging miss)."); return null; }
            try
            {
                if (colType == null)
                    DatatableElementCollectionUtility.TryGetCollectionTypeFromElementType(live.GetType(), ref colType);
                if (colType == null) { Debug.LogError($"[CompatPatcher] no collection type for {live.GetType().Name}."); return null; }

                Directory.CreateDirectory(PatchDir);
                var collection = DatatableElementCollectionUtility.GetOrCreateDatatableElementCollection(
                    colType, PatchDir, collectionName, startNameEditing: false);
                if (collection == null) { Debug.LogError($"[CompatPatcher] get-or-create '{PatchDir}/{collectionName}' failed."); return null; }

                IDatatableElement[] dups = null;
                bool ok = DatatableElementCollectionUtility.TryDuplicateDatatableElements(
                    new[] { (IDatatableElement)live }, ref collection, ref dups,
                    showWarningDialogThresholdCount: false, ensureUniqueName: false, reimport: true);
                if (!ok || dups == null || dups.Length == 0) { Debug.LogError($"[CompatPatcher] duplicate failed for '{live.name}'."); return null; }
                dups[0].SetEditable(true);
                var dupObj = dups[0] as UnityEngine.Object;
                PreserveDefinitionKey(dupObj, live);
                return dupObj;
            }
            catch (Exception e) { Debug.LogError("[CompatPatcher] Duplicate failed: " + e); return null; }
        }

        /// <summary>
        /// Definition <c>Key</c> is Mod Tools–locked identity (byte/ushort). Many source mods omit it
        /// (serialize as 0); Duplicate then leaves 0. If the source live object had a non-zero Key,
        /// copy it so the patch override keeps the same identity. Do not invent a new key — that can
        /// diverge from vanilla's key for the same name.
        /// </summary>
        static void PreserveDefinitionKey(UnityEngine.Object dup, UnityEngine.Object source)
        {
            if (dup == null || source == null) return;
            if (dup is IDatatableElementWithByteKey db)
            {
                if (db.Key != 0) return;
                if (source is IDatatableElementWithByteKey sb && sb.Key != 0)
                {
                    db.Key = sb.Key;
                    EditorUtility.SetDirty(dup);
                }
                return;
            }
            if (dup is IDatatableElementWithShortKey ds)
            {
                if (ds.Key != 0) return;
                if (source is IDatatableElementWithShortKey ss && ss.Key != 0)
                {
                    ds.Key = ss.Key;
                    EditorUtility.SetDirty(dup);
                }
            }
        }

        /// <summary>
        /// Remove a named non-root element from a patch <c>.asset</c> (sub-asset destroy). Returns true if removed.
        /// Does not delete the collection file even if it becomes empty.
        /// </summary>
        public static bool RemoveNamedElement(string assetPath, string elementName)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(elementName)) return false;
            UnityEngine.Object target = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (o == null || o.name != elementName) continue;
                if (o is IDatatableElementCollection) continue;
                if (o is IDatatableElement) { target = o; break; }
            }
            if (target == null) return false;
            Undo.DestroyObjectImmediate(target);
            EditorUtility.SetDirty(AssetDatabase.LoadMainAssetAtPath(assetPath));
            AssetDatabase.SaveAssets();
            return true;
        }

        static string StageFile(string modName, string sourcePath, string fileText)
        {
            // Keep the ORIGINAL file name so the imported main object's name matches the filename — otherwise
            // Unity's NativeFormatImporter logs "Main Object Name '…' does not match filename '…'". Uniqueness
            // across source paths (and mods) comes from the per-source hash subfolder, so a given source file
            // still maps to one deterministic stage path (element + its mapper reuse it).
            string dir = StageRoot + "/" + Sanitize(modName) + "/" + Hash(sourcePath);
            Directory.CreateDirectory(dir);
            string baseName = Sanitize(System.IO.Path.GetFileNameWithoutExtension(sourcePath));
            if (string.IsNullOrEmpty(baseName)) baseName = "staged";
            string stagePath = dir + "/" + baseName + ".asset";

            // Reuse an existing stage without ImportAsset — each Import/Delete bumps Amplitude
            // DatatableElementCache.CacheRevisionIndex and forces BuildCacheBuilderEntry (~0.4s).
            if (File.Exists(stagePath))
            {
                try
                {
                    if (File.ReadAllText(stagePath) == fileText)
                        return stagePath;
                }
                catch { /* fall through to rewrite */ }
                File.WriteAllText(stagePath, fileText);
                AssetDatabase.ImportAsset(stagePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                return stagePath;
            }

            File.WriteAllText(stagePath, fileText);
            File.WriteAllText(stagePath + ".meta",
                "fileFormatVersion: 2\nguid: " + System.Guid.NewGuid().ToString("N") + "\n" +
                "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 0\n  userData: \n" +
                "  assetBundleName: \n  assetBundleVariant: \n");
            AssetDatabase.ImportAsset(stagePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return stagePath;
        }

        static string Sanitize(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
        static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in s) { h ^= c; h *= 16777619; }
                return h.ToString("x8");
            }
        }
    }
}
