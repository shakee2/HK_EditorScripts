# Compatibility Patcher — User Manual

Humankind is **last-loaded-wins** per whole element (no field merge). This tool diffs mods, lets you pick winners, and builds a **patch mod** loaded last.

**Open:** Tools → Compatibility Patcher

---

## Quick workflow

1. **Add mods** — **`.assetbundle` recommended** (live refs); also `.unitypackage` / `.zip` / folder with `Assets/Databases/`
2. **Order** with ▲▼ — **last in the list wins** (same as the game)
3. Optional: set **Prior sidecar** if re-patching
4. **Compare** (≥2 mods)
5. Filter → expand a type → click an element → **Import** and/or **Resolve**
6. **Publish** `Assets/Databases/Patch/` via Mod Tools’ assetbundle build (or export `.unitypackage`)

---

## Sources

- **Formats:** `.assetbundle` (recommended) · `.unitypackage` · `.zip` · folder
- **Assetbundles:** stay mounted for the session (live refs). Manage under **Loaded mod bundles** (Force unload). Vanilla is never listed/unloaded here.
- **Other formats:** staged under `Assets/_PatcherStage/` when needed, then cleaned up

---

## After Compare

### Filters (left list)

- **Conflicts / All / New / Identical**
- **needs review** · **hide winner-only** (toggle buttons)
- **Name search** — filters by **element name** (e.g. a UnitDefinition’s name), not type; expands matching types while non-empty
- **Sort:** Diff ↓ or A–Z

### Type list

- Click header → select type (patterns on the right)
- Double-click header → expand / collapse elements
- Click element → embedded Compare (replaces patterns)

### Hazard cards

- **Load-order validation** — crash-pattern checks; `[order-sensitive]` = reorder may fix
- **Patch orphans** — `Patch/` overrides that no longer sit on a live multi-mod conflict  
  - *gone* / *sole mod* / *mods agree* → Compare · Ping · Remove

### Resolve vs import

- **Resolve as winner** — accept load-order winner · sidecar only · no `Patch/` file
- **Import into Patch/** — copy into `Assets/Databases/Patch/` · also resolved in sidecar
- **Resolve all (type)** — same as resolve, for that type’s pending rows

**Import** and **Resolve** both clear “needs review.” Sidecar default: `Assets/Databases/Patch/CompatPatch.sidecar.json`.

### Patterns / Mass Change

- Right pane: recurring field diffs for the selected type
- **Mass Change** / **Apply to N in Patch…** — only on elements **already in Patch**
- Complex structured rows → hand-edit in Compare

---

## Sidecar (re-patch)

On re-Compare with the same prior file:

- **Unchanged** → stay ✓resolved
- **Fingerprint changed** → resurface as *changed*
- **New collisions** → *new*

---

## Patch output

- Written as per-type collections under `Assets/Databases/Patch/`
- Sidecar travels with the folder / exported package
- Patcher does **not** build the game assetbundle

**Import note:** primary element only — conflicted mappers (UIMapper, …) are separate rows.

---

## Gotchas

- Identity is `(type, name)`, not file path
- **hide winner-only** — safe to ignore “ONLY in winner” noise
- Odin / hybrid: assetbundle live objects get field diffs; opaque shells may be refs-only → use Compare
- Validation needs vanilla mounted (Mod Editor Humankind folder)
- Clear vanilla validation cache: **Tools → Debug → Compat Patcher → Clear Vanilla Validation Cache**

---

## Troubleshooting

- **Compare greyed out** — add ≥2 mods
- **Validation “vanilla not mounted”** — set Humankind folder in Mod Editor, Compare again
- **Element *changed* unexpectedly** — source mod updated since sidecar — review & re-resolve
- **Patch folder empty** — import at least one element
- **Wrong element in Compare** — click the element again (cross-type open rebuilds the session)
