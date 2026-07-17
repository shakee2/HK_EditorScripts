Orphan Resource Finder
======================
Finds unreferenced ("orphaned") image assets in a Humankind / Amplitude Unity mod
so you can delete them and shrink your published package.

OPEN:  Tools > shakee's Tools > Orphan Resource Finder

HOW TO USE
  1. Set "Images folder" to the folder to scan (default: Assets/Resources).
  2. Press "Scan". The list shows images that nothing in the mod references.
  3. Click a row to ping/select it in the Project window and preview it.
  4. Tick the ones to remove (all are ticked by default) and press
     "Delete selected -> Trash" (recoverable from your Recycle Bin),
     or "Export list..." to save a .txt for review.
  5. Rebuild your mod to capture the size saving.

SCOPE
  * IMAGES ONLY (.png/.jpg/.tga/.psd/...). It does NOT scan 3D resources
    (meshes, skeletons, atlases, prefabs). Those are often referenced from
    JSON registries or int[] GUID arrays this tool doesn't read, so it would
    flag referenced assets as orphans — don't point it at a mesh folder.

NOTES
  * Editor-only: never included in your built mod.
  * Detection auto-calibrates Amplitude's {a,b,c,d} GUID encoding each scan,
    and excludes the built AssetBundles folder so it can't be fooled.
  * "Name cross-check" keeps any image whose file name appears in source text
    (covers Resources.Load-by-name). Leave it on unless you know you don't need it.
  * Always use version control / keep a backup before deleting.
