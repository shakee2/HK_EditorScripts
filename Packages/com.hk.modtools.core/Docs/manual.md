# Mod Editor Scripts — User Manual

Short guide to the scripts shipped in `ModEditorScripts.unitypackage` (menu: **Tools → shakee's Tools → Export Mod Editor Scripts Package**).

## Requirements & install

- Unity **2021.3+** with **Humankind Mod Tools** (Amplitude/Mercury editor assemblies).
- **Odin Inspector** recommended — required for **formula autocomplete** on descriptor effects and inline `%key` translation drawers. Without Odin, descriptor effects still get a basic fallback editor.
- Import the `.unitypackage` into a mod project (or use the full `com.shakee.hk-editorscripts` UPM package). Scripts expect the Mod Tools translations bundle at `Assets/Editor/Resources/Translations/` (ships with Mod Tools, **not** in this package).
- Set the **Humankind game folder** in the Mod Editor so vanilla `.assetbundle` paths resolve.

**Not included in this export:** Compatibility Patcher, unit-visual workflow, pawn probes, mod build/deploy, and other tools listed in the full package README without a **📦** marker.

---

## Shared infrastructure

### VanillaDatabaseMount

**Automatic — adopts Mod Tools' mount.** You do not mount anything at first install. When a browser or inspector tool needs vanilla data, `TryMount`:

1. Reuses its own cached provider if already set.
2. Otherwise checks `AssetDatabase.IsMounted("mercurydatabases.assetbundle")` — if the **Mod Editor already mounted** MercuryDatabases (normal when Mod Tools is open), it **adopts that provider** from `AssetDatabase.AllProviders` without loading the file again.
3. Only if nothing is mounted yet does it cold-mount from the Humankind install path.

So in a typical Mod Tools session, our scripts piggyback on the database the editor already loaded.

**Prerequisite:** Humankind game folder set in the Mod Editor. If that path is wrong, lists stay empty and the console logs an error — fix the path first.

**Debug menus only** (`Tools → shakee's Tools → Debug → Tech Tree → …`):

| Item | When to use |
|------|-------------|
| **Vanilla Mount** | Log how many descriptors are visible — troubleshooting, not setup. |
| **Force Re-mount Vanilla Database** | Rare recovery when bookkeeping is out of sync after a **domain reload / mod build** (`IsMounted` says yes but the provider is missing or broken). **Not** for first install and **not** when Mod Tools already shows vanilla data fine. |

**Limits:** Read-only reference from the game install. Intentionally does **not** force-remount on every call — double-mounting the same file can break the Mod Editor's provider. **Override from Archives** runs inside Tech Tree save — not a separate menu here.

### ArchiveTranslations

**No menu** — used by Tech Tree, inline loc editors, and tooltip preview.

Mounts the Mod Editor **archive translations** bundle and builds a `%key → text` lookup. **Import for editing** creates or updates rows in `Assets/Localization/Translations/Translations.asset`.

**Limits:** If the translations provider is stale after a domain reload, labels may fall back to raw keys until the Mod Editor Localization window remounts the bundle. Project override lookup is skipped when the archive bundle is not mounted (avoids poisoning Amplitude's global cache).

### WindowMinimize

**No menu** — toolbar **Min** on Tech Tree, Database Browser (**Window** mode), and Descriptor Property Browser (Compat Patcher uses the same helper outside this export).

Floating windows shrink to a restore strip parked in the main editor's lower-right; additional minimized tools stack horizontally leftward. Docked windows hide the control. Domain reload clears minimize state.

---

## Windows

### Database Browser

**Menu:** `Tools → shakee's Tools → Database Browser`

Searchable list of database elements (project + mounted vanilla). Click a row to inspect it in Unity's inspector. Filter by type, scope (My Mod / Vanilla / **Dupes !**), and optional **issue** filter (warnings / hard stops on **your mod's** project assets only).

**Handling:** **Content only** hides build/plugin ScriptableObjects, leaving just database content. A ⚠ / 🛑 dot on a row means it has issues — select it and check the inspector header's Diagnostics panel for details. My Mod content rows that share the same `(type, name)` with another project object are prefixed with **`!`** (load-order collision); the **Dupes !** scope filter shows only those.

**Import:** Right-click a **Vanilla** row → **Import (Override from Archives)**. Copies that element into your project under the matching `Assets/Databases/…` collection (get-or-create), then selects the new asset. At mod load, your copy wins by element **name** — same override pattern as Tech Tree copy-on-write and the Mod Editor's archives override. With a multi-selection of vanilla rows, the menu becomes **Import N Selected** and imports each (cancelable progress bar).

**Delete:** Right-click a **My Mod** row → **Delete**. Confirms, then removes that project asset (sub-asset only when it lives inside a collection file; whole `.asset` when it is the main object). Assets outside `Assets/` are refused. Multi-selection uses **Delete N Selected** the same way.

**Inputs:**

| Input | Action |
|-------|--------|
| **Left-click** row | Select that element (clears multi-select). **List Only** mode drives the docked Inspector; **Window** mode shows an embedded inspector on the right. |
| **Ctrl/Cmd+click** row | Toggle that row in the multi-selection. |
| **Shift+click** row | Select the range from the anchor row to this one. |
| **Right-click** Vanilla row(s) | **Import** (or **Import N Selected**) from archives. |
| **Right-click** My Mod row(s) | **Delete** (or **Delete N Selected**) under `Assets/`. |
| **Click** type group header | Expand/collapse when **Group** is Type or Folder. |
| **Group** popup | **None** (flat list), **Type**, or **Folder** (directory of the project asset / vanilla collection path). |
| **Drag** vertical splitter | Resize list vs inspector (**Window** mode only). |
| **Scroll wheel** | Scroll the list. |
| **Search** field | Live name filter; **x** clears. |

No window-specific keyboard shortcuts.

**Limits:** Vanilla rows are not analyzed for issues. Import is only on vanilla rows; Delete is only on My Mod rows under `Assets/`. Right-clicking a row outside the current selection replaces the selection with that row first. Very large projects may take a moment on first scan.

### Descriptor Property Browser

**Menu:** `Tools → shakee's Tools → Descriptor Property Browser`

Table of Descriptor **PropertyEffect** rows (vanilla cache + live mod scan): formula, scope, duplicates. Useful for finding who touches a property.

**Handling:** Vanilla rows come from a one-time cache (**Rebuild Vanilla Cache**); mod rows are scanned live. Rescan after large mod changes so results stay current.

**Import:** Right-click a **Vanilla** row → **Import (Override from Archives)**. Pulls the matching Descriptor into your project (same get-or-create collection + name override as Database Browser). Handy when you found a property row in vanilla and want an editable Descriptor to tweak or attach a mapper to.

**Inputs:**

| Input | Action |
|-------|--------|
| **Click** Descriptor cell | Ping/select that Descriptor (vanilla bundle or project). |
| **Right-click** Vanilla row | **Import (Override from Archives)**. |
| **Click** column header | Sort by that column (click again toggles ascending/descending). |
| **Drag** column divider | Resize columns (Unity multi-column header). |
| **Scroll wheel** | Scroll the table vertically; horizontal scroll when columns are wider than the window. |

Filter text fields update live. No window-specific keyboard shortcuts.

**Limits:** Cache can be stale until you rescan. Import applies to vanilla Descriptor rows only; other columns are browse-only.

### Tech Tree Viewer

**Menu:** `Tools → shakee's Tools → Tech Tree Viewer`

Pan/zoom tech tree; select a tech to edit **position**, **prerequisites**, and **Title/Description** localization in the sidebar.

**Handling:** Turn **Edit mode** on to drag nodes and edit prerequisites — changes are staged, not written immediately. **Save** commits them: in place for a tech you already own, or as a copy into your mod for a vanilla-only tech. Loc fields need **Import for editing** once per key before you can retype them (same as inline loc below).

**Inputs:**

| Input | Action |
|-------|--------|
| **Scroll wheel** (canvas) | Zoom in/out, anchored to the cursor. |
| **Middle-drag** or **Alt + left-drag** (canvas) | Pan the tree. |
| **Left-click** node (canvas) | Select tech; ping and set Project selection to its asset. |
| **Left-drag** node (canvas, **Edit** on) | Move node; position snaps to the grid. Staged until **Save**. |
| **Drag** canvas/sidebar splitter | Resize the sidebar. |
| **Click** prerequisite name (sidebar) | Jump to that tech on the canvas. |
| **−** / **+ add prerequisite** (sidebar, **Edit** on) | Remove prereq, or open the add-prerequisite picker. |
| **Import for editing** (sidebar) | Create a project translation row for that `%key`. |
| Toolbar **View** / **● Edit**, **Reload**, **Frame All**, **Save**, **Revert** | Mode toggle, reload data, fit all nodes, commit or discard staged edits. |

No canvas keyboard shortcuts (no arrow-key pan, etc.).

**Limits:** Adding prerequisites via UI picker and **creating new techs** are not implemented yet. Staged edits are lost if you close the window without saving.

**Debug menus (TechTreeData):** `Diagnose Mod Split`, `Dump Data` — report where tech defs/mappers live under `Assets/Databases/`.

### Asset Explorer

**Menu:** `Tools → shakee's Tools → Asset Explorer`

Pick a `.assetbundle`, browse descriptors, preview (texture/mesh/inspector), **import** into a project folder.

**Sources (dropdown groups):**
- **Mounted** — already in Amplitude’s provider registry (Mod Tools’ preloaded vanilla bundles, plus Compat Patcher mod mounts after Compare).
- **Vanilla/&lt;folder&gt;** — on-disk under the Humankind `AssetBundles/` install.
- **Custom** — any file opened via **Open…** (e.g. a mod `.assetbundle` not yet mounted).

**Handling:** MercuryDatabases reuses the shared `VanillaDatabaseMount`. Bundles already mounted elsewhere are *adopted* (closing Asset Explorer does not unload Compat Patcher / Mod Tools mounts). Bundles this window mounts itself unmount on close.

**Inputs:**

| Input | Action |
|-------|--------|
| **Left-click** row | Select asset for preview/inspector on the right. |
| **Right-click** row | **Import to project…**; **Copy name** / **descriptor path** / **GUID**; for **MeshCollection**, also **Copy SourcePrefab GUID**. |
| **Click** group header | Expand/collapse when grouped by Type or Folder. |
| **Drag** vertical splitter | Resize list vs preview pane. |
| **Scroll wheel** | Scroll the asset list. |
| **Import** button (right pane) | Same as right-click import when an item is selected. |

No window-specific keyboard shortcuts.

**Limits:** Import clones assets — know what you pull in; not every asset type is game-safe as a loose project file. Textures export as PNG.

---

## Inspector header (datatable elements)

These layer under the asset name header via `Editor.finishedDefaultHeaderGUI`. They do not replace Odin's main inspector body.

### Analysis strip (`InspectorAnalysisPanel`)

For Descriptors, DescriptorMappers, and other elements with preview/diagnostics content.

**Handling:** **Tooltip Preview** and **Diagnostics** toggle each panel independently — turn off what you don't need to keep the inspector compact.

**Inputs:** Click **Tooltip Preview** / **Diagnostics** to show or hide each panel. Scroll inside the analysis block when content exceeds the height cap.

**Limits:** Preview cost grows with effect count — turn **Tooltip Preview** off on huge Descriptors if the editor feels sluggish.

### DescriptorMapper toolbar (`DescriptorMapperGenerator`)

Shown on **Descriptor** inspectors only.

**Handling:** **Generate DescriptorMapper** creates the paired mapper for this Descriptor if it doesn't have one yet (same name, so the game links them automatically). **Select mapper** jumps straight to it once it exists.

**Inputs:** Toolbar buttons only — no drag or keyboard shortcuts.

**Limits:** Lookup refreshes on **selection change** or after **Generate** — not every repaint. **UIMapper** auto-generation is **not** supported (many definition-specific mapper types).

### Tooltip Breakdown Preview (`DescriptorMapperPreview`)

**Menu (cache):** `Tools → shakee's Tools → Debug → Descriptor Mapper Preview → Clear Name Cache`

Shows how each PropertyEffect row would render in a tooltip (flags, template, substituted text). Pairs Descriptor + DescriptorMapper by name. Bracket icon tags (`[ScienceColored]`, `[Pollution1]`, …) resolve to the matching UIMapper `Symbol` → Picto when present in the project or mounted vanilla databases. Amplitude rich-text (`<c=RRGGBB>`, `<b>`, `<i>`, …) is applied in the preview.

**Inputs:** Read-only. Scroll inside the analysis panel when the preview is taller than the height cap. Enable via the **Tooltip Preview** toggle on the inspector header.

**Limits:** Does not evaluate `SolveRPNFormula` live or world-constant multipliers. Assumes singular intermediate conditions. Synergy descriptors use the District_Synergy rules only. Wrong `ParameterFlags` on a policy is flagged — matching runtime can **crash load** if shipped.

### Inspector Diagnostics (`InspectorDiagnostics`)

Crash-risk and quality warnings (unfilled list rows, unlock-event patterns, malformed Descriptor RPN, bad mapper flags). **Localization** foldout lists `%key` fields on the element (aggregate view).

**Handling:** 🛑 means the issue can **reset a player's game** on load — fix before shipping. ⚠ is a lower-severity quality/runtime issue. **Select referenced element**, where offered, jumps to the asset actually causing the problem.

**Inputs:** Scroll the diagnostics list when it overflows the analysis panel. Click **Select referenced element** to ping/jump to a linked asset.

**Limits:** Only ~15 validations mirror retail reset gate; hundreds more exist in DEBUG-only game builds. Loc foldout shows at most 16 keys. Vanilla-only assets are skipped for issue scanning in Database Browser.

### Inline localization (`LocalizationKeyDrawer` + `InlineLocalizationEditor`)

On **UIMapper** (Title, Description, facet titles) and **DescriptorMapper** (`LocalizedName`, `EffectLocalization`, …) when the string starts with `%`.

**Handling:** The `%key` field still shows the raw key. Below it, the resolved text is shown read-only until you click **Import for editing**, which unlocks a **Translation** box you can edit and save without leaving the inspector.

**Inputs:** Type in the **Translation** box after import; standard Unity text-field shortcuts apply. **Import for editing** is a button click only.

**Limits:** Odin-only. Does not replace the Mod Editor Localization Window for bulk/shard editing. Editing does not change the `%key` on the mapper — only the override row text.

### Editing descriptor effects (formula field)

Intellisense for the **formula** box on each Descriptor **Effect** row: type `Source.`, `Target.`, or `World.` and get an autocomplete list of valid property names, instead of guessing/checking against the docs. Also adds a **Rendered** preview below the row (the same substituted tooltip line as the header Tooltip Breakdown Preview) when **Tooltip Preview** is on.

**Inputs (while the suggestion list is open):**

| Key | Action |
|-----|--------|
| **↑** / **↓** (Arrow Keys)| Move selection. |
| **→** (Arrow Key), or **Ctrl+E** / **Ctrl+D** | Accept the highlighted suggestion. |

Each accept binding can be turned off individually in **Tools/shakee's Tools/Options** → Formula Autocomplete (e.g. if **→** fights with normal cursor movement for your workflow, disable it and keep **Ctrl+E**/**Ctrl+D**).

**Limits:** Autocomplete requires Odin. The Rendered preview is suppressed when **Tooltip Preview** is off.

---

## Element identity reminder

The game keys elements by **`(type, name)`** — collection file path does not matter. Descriptor and DescriptorMapper pair by **matching name**.

---

## Export package contents

Fourteen `.cs` files plus this manual. See the exporting project's README **Export package** table for the exact file list. When adding features, update `ExportModEditorScriptsPackage.cs` and that table together.
