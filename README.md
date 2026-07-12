# HK Editor Scripts

Unity Editor tools for Humankind modding — database editing, custom unit visuals, compatibility patching, and diagnostics.

## Requirements

- Unity 2021.3+
- Humankind Mod Tools installation
- Referenced as a local UPM package in a Humankind modding project

## Tools Overview

### Compatibility Patcher
| Tool | Menu | Description |
|------|------|-------------|
| **CompatPatcherWindow** | `Tools/Compatibility Patcher` | Pick source mods, set load order, diff conflicting elements, select per-element winners, mass-import into Patch folder |
| **CompatCompareWindow** | (opened from above) | Side-by-side inspector of one conflicting element across all mods + existing Patch version |
| **DiffGui** | — | Shared diff rendering for both windows |

### Database Browsing & Editing
| Tool | Menu | Description |
|------|------|-------------|
| **DatabaseBrowser** | `Tools/Database Browser` | Searchable list of all database elements with Unity inspector integration |
| **DescriptorPropertyIndex** | `Tools/Descriptor Property Browser` | Searchable index of Descriptor/DescriptorMapper rows with formula, scope, and duplicate detection |
| **TechTreeWindow** | `Tools/Tech Tree Viewer` | Tech tree viewer/editor with drag-to-reposition, prerequisite editing, and copy-on-write save |
| **DescriptorMapperPreview** | `Tools/Debug/Descriptor Mapper Preview/*` | Live tooltip-breakdown preview for DescriptorMapper rows |

### Custom Unit Visuals
| Tool | Menu | Description |
|------|------|-------------|
| **UnitVisualWorkflow** | `Tools/Unit Visual Workflow` | Step-based wizard for auto tier-detection, FBX prep, mesh baking, fragment authoring, and validation |
| **PawnFragmentAuthor** | — | Fragment authoring and GUID-fix helpers |
| **Tier1MeshBaker** | — | Mesh-bake helpers for the wizard |
| **BoneStructureMatcher** | — | Structural bone matcher for auto tier-detection |
| **FbxPrepPipeline** | — | FBX-prep pipeline (multi-submesh combine, texture atlas, scale normalization) |
| **ModelRequirementsChecker** | — | End-to-end validator for baked fragments against runtime requirements |
| **AnimationManagerContent** | — | Populates AnimationManagerContent registry from baked fragments |

### Asset & Bundle Tools
| Tool | Menu | Description |
|------|------|-------------|
| **AssetExplorer** | `Tools/Asset Explorer` | Mount vanilla `.assetbundle` files, list descriptors, preview assets, import into project |
| **GuidLookup** | `Tools/Pawn Fragment/Debug/Find GUID In Vanilla Bundles` | Resolve an Amplitude GUID against all vanilla bundles |
| **BundleContentProbe** | `Tools/Pawn Fragment/Debug/Inspect Built Mod Bundle` | Inspect the mod's built assetbundle to confirm what shipped |
| **VanillaAssetResolver** | — | Load any asset by GUID from vanilla bundles (used by wizard) |
| **VanillaDatabaseMount** | `Tools/Debug/Tech Tree/Vanilla Mount` | Mount vanilla database bundle for browsing without copying into project |
| **ArchiveTranslations** | — | Mount Mod Editor translations bundle for vanilla localization lookup |

### Build & Export
| Tool | Menu | Description |
|------|------|-------------|
| **ModBuildWindow** | `Tools/Build And Deploy Mod` | Lightweight build + deploy to Community folder (alternative to Mod Editor) |
| **ExportModEditorScriptsPackage** | `Tools/Export Mod Editor Scripts Package` | Export the core editor scripts as a `.unitypackage` for sharing |

### Debug & Diagnostics
| Tool | Menu | Description |
|------|------|-------------|
| **Probing** | `Tools/Debug/Pawn Probe/*` | Three-step diagnostic for presentation-pawn assets (find, dump, clone) |
| **FormulaProbe** | `Tools/Debug/Formula Probe/*` | Resolve Descriptor/Effect types and test formula/RPN serialization |
| **NarrativeEventDiagnostic** | `Tools/Debug/Tech Tree/Find Bad NarrativeEventDefinition` | Find the vanilla narrative event that throws NRE from OnValidate |
| **InspectorDiagnostics** | — | Inline diagnostics panel for datatable elements (crash risks, broken refs, malformed RPN) |

## License

MIT License — see [LICENSE](LICENSE)
