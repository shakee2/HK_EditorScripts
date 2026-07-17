Orphan Resource Finder
======================
Finds unreferenced ("orphaned") assets in a Humankind / Amplitude Unity mod
so you can delete them and shrink your published package.

OPEN:  Tools > shakee's Tools > Orphan Resource Finder

HOW TO USE
  1. Set "Resources folder" to the folder to scan (default: Assets/Resources).
  2. Pick a "Kind": Images (default), 3D / meshes, or All.
  3. Press "Scan". The list shows assets that nothing in the mod references.
  4. Click a row to ping/select it in the Project window and preview it.
  5. Tick the ones to remove (all are ticked by default) and press
     "Delete selected -> Trash" (recoverable from your Recycle Bin),
     or "Export list..." to save a .txt for review.
  6. Rebuild your mod to capture the size saving.

RESOURCE KINDS
  * Images (default) - .png/.jpg/.tga/.psd/...
  * 3D / meshes      - baked assets (.asset/.prefab/.mat) + raw models
                       (.fbx/.obj/.glb/.gltf/.blend/.mesh).
  * All              - both of the above.
  For the 3D / All kinds the reference scan ALSO reads Unity hex-GUID
  references (a prefab -> its mesh/material) AND JSON int[4] GUID arrays
  (e.g. "skel":[a,b,c,d] in a model registry), since baked meshes /
  skeletons / atlases are frequently referenced that way rather than from a
  .asset. Reference detection is deliberately GENEROUS: a false "referenced"
  only costs unreclaimed space, while a false "orphan" could delete a used
  asset. Treat the 3D list as a review candidate list, not a delete-all.

NOTES
  * Editor-only: never included in your built mod.
  * Detection auto-calibrates Amplitude's {a,b,c,d} GUID encoding each scan,
    and excludes the built AssetBundles folder so it can't be fooled.
  * "Name cross-check" keeps any asset whose file name appears in source text
    (covers Resources.Load-by-name). Leave it on unless you know you don't need it.
  * Always use version control / keep a backup before deleting.
