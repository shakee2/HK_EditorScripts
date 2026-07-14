using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Live "how would this row actually render in a tooltip breakdown" preview for Descriptor /
/// DescriptorMapper assets. Reverse engineered from the decompiled Amplitude.Mercury.Firstpass
/// sources (EffectTranslator / SimulationEvaluatorHelper.{GenerateEffectPathEvaluation,
/// FillPropertyEffectEvaluation, TryGenerateSynergyDescriptorEvaluation} / EffectParameters):
///
///   - Flags are AUTO-computed by the runtime, not taken from a mapper: EffectParameters.ComputeFlags
///     over the PropertyEffect (Value|Property, +ValueFactorProperty/+OtherValueFactorProperty from
///     the RPN shape, +BonusFimsResource for the Bonus*IfProducing* properties) OR'd with the path
///     flags (Target/Condition/Intermediate|Plural/SynergySource/SynergyTarget).
///   - A DescriptorMapper.PropertyEffectPolicy (per EffectIndex,PropertyEffectIndex) only overrides:
///     HidePropertyEffect drops the row; ParameterFlags==None + EffectLocalization = "exotic" literal
///     (no substitution); an EffectLocalization/subset ParameterFlags selects a custom string / reduced
///     param set (must be a subset of the auto flags, else the runtime errors and falls back to auto).
///   - Path classification honours Effect.ApplyEffectOnSource (a navigation is only a Target when the
///     effect is applied on the target, not the source).
///   - Category "District_Synergy" descriptors take the separate synergy evaluation: the single
///     non-inverted "Must Have" validation is the SynergySource (via ValidationMapper.Localization),
///     navigation/other validations ignored; key from EffectMapperConfiguration.AdditionalEffectKeys.
///   - The chosen template (EffectMapperConfiguration field for the flag combo, incl. AdditionalEffectKeys)
///     is resolved and the ordered params substituted into its {N_Name} placeholders → the "Rendered" line.
///
/// Known limitations (documented rather than faked):
///   - World-constant multipliers (SimulationController.TryGetWorldConstantMultiplier) aren't applied to
///     the Value; assumed 1.
///   - Target/Condition "collection" plurality (SimulationController.IsPathReferenceCollection) isn't
///     reproduced; Intermediate is assumed singular (IntermediateCondition, never PluralCondition).
///   - SolveRPNFormula policies evaluate live against game state; shown as the static formula.
///
/// Hooks into Editor.finishedDefaultHeaderGUI (fires for every inspector, right after the
/// name/icon header) instead of registering a [CustomEditor] for Descriptor/DescriptorMapper,
/// so this layers on top of whatever inspector (Odin's own, or default) Unity already draws for
/// those types without competing for ownership of the editor. Output is built once and cached per
/// target (see Render cache); rebuilt only when the underlying data changes.
/// </summary>
[InitializeOnLoad]
public static class DescriptorMapperPreview
{
    static DescriptorMapperPreview()
    {
        s_active = EditorPrefs.GetBool(ActiveKey, true);
        // The header seam is owned by InspectorAnalysisPanel, which draws this panel (via Draw) and the
        // diagnostics panel inside one shared, height-capped scroll container.
        Undo.undoRedoPerformed += () => { s_generation++; s_previewCache.Clear(); };
        EditorApplication.projectChanged += () => { s_generation++; s_previewCache.Clear(); s_byNameCache.Clear(); s_effectMapperConfig = null; s_effectMapperConfigSearched = false; };
    }

    static bool s_expanded = true;
    static bool s_active = true;
    const string ActiveKey = "DescriptorMapperPreview.Active";

    /// <summary>Generation counter — bumps when caches are invalidated. Used by PropertyEffectDrawer.</summary>
    public static int Generation => s_generation;

    /// <summary>When false, the header panel and the PropertyEffectDrawer inline content are suppressed.</summary>
    public static bool IsActive => s_active;

    // ── Render cache ─────────────────────────────────────────────────────────
    // finishedDefaultHeaderGUI fires for every inspector event (every Layout + every
    // Repaint — mouse-move, hover, etc.), many times per second. The preview is a pure
    // function of the inspected asset's serialized data, so we build a flat list of cheap
    // draw commands once, cache it per target, and replay it on every event. We only rebuild
    // when a lightweight structural hash of the underlying data changes (an actual edit) or
    // when the generation counter is bumped (name/translation cache cleared). This turns the
    // per-event cost from "copy a ~48k-entry translation dict + project scan, per Resolve, per
    // row" into "replay N cached LabelFields".
    enum OpKind { LabelTwo, LabelOne, HelpBox, BeginBox, EndBox }
    struct DrawOp { public OpKind kind; public string a, b; public GUIStyle style; public MessageType msg; }
    sealed class Cached { public int hash; public int generation; public List<DrawOp> ops; }

    static readonly Dictionary<int, Cached> s_previewCache = new();
    static int s_generation;
    // Translation key->text dict for the build currently in progress; built at most once per
    // rebuild (see Resolve) instead of once per Resolve call.
    static Dictionary<string, string> s_buildDict;

    // ── Reflection cache ────────────────────────────────────────────────────
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static bool s_resolved, s_resolveFailed;

    static Type t_Descriptor, t_Effect, t_Path, t_Validation, t_PropertyEffect, t_OperationEnum, t_FixedPoint;
    static Type t_DescriptorMapper, t_PropertyEffectPolicy, t_FlagsEnum;
    static Type t_PropertyMapper, t_ValidationMapper, t_NavigationMapper, t_EffectMapperConfiguration;

    static FieldInfo f_startingType, f_effects;
    static FieldInfo f_applyOnSource, f_path, f_propertyEffects;
    static FieldInfo f_propertyToFollow, f_validations;
    static FieldInfo f_val_pathIndex, f_val_elementName, f_val_inverted;
    static FieldInfo f_targetProperty, f_toTargetOp, f_rpnStack, f_constantStack, f_propertyLocalNames;
    static FieldInfo f_rawValue, f_note;
    static long s_oneRaw = 1000;

    static FieldInfo f_dm_localizedName, f_dm_hideDescriptor, f_dm_policies;
    static FieldInfo f_pol_effectIndex, f_pol_peIndex, f_pol_hide, f_pol_localization, f_pol_flags, f_pol_solveRpn;

    static FieldInfo f_desc_category;               // Descriptor.serializableCategory (string; "District_Synergy" => synergy path)
    static FieldInfo f_pm_localization, f_pm_displaySigned, f_pm_displayPercent;
    static FieldInfo f_vm_localization, f_vm_localizationTarget, f_vm_localizationTargetInverted;
    static FieldInfo f_nm_localization;

    // EffectMapperConfiguration.AdditionalEffectKeys[] (struct AdditionalEffectKey { Flags; Key; })
    static Type t_AdditionalEffectKey;
    static FieldInfo f_emc_additionalKeys, f_aek_flags, f_aek_key;

    static int OP_Add, OP_Sub, OP_Mult, OP_Div, OP_Percent, OP_Pow, OP_Max, OP_Min;
    static int OP_GetTarget, OP_GetSource, OP_GetConst, OP_GetVariable, OP_GetWorld;

    static int FLAG_None, FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_Condition, FLAG_Target,
        FLAG_SynergySource, FLAG_SynergyTarget, FLAG_IntermediateCondition, FLAG_PluralCondition,
        FLAG_BonusFimsResource, FLAG_OtherValueFactorProperty;

    // Mirrors EffectTranslator.Load(): exact flags combo -> EffectMapperConfiguration field name.
    static readonly List<(int flags, string field)> s_genericTemplates = new();

    static bool TryResolve()
    {
        if (s_resolved) return !s_resolveFailed;
        s_resolved = true;

        t_Descriptor = FindType("Amplitude.Framework.Simulation.Description.Descriptor");
        t_Effect = FindType("Amplitude.Framework.Simulation.Description.Effect");
        t_Path = FindType("Amplitude.Framework.Simulation.Description.Path");
        t_Validation = FindType("Amplitude.Framework.Simulation.Description.Validation");
        t_PropertyEffect = FindType("Amplitude.Framework.Simulation.Description.PropertyEffect");
        t_DescriptorMapper = FindType("Amplitude.Mercury.EffectMapper.DescriptorMapper");
        t_PropertyMapper = FindType("Amplitude.Mercury.EffectMapper.PropertyMapper");
        t_ValidationMapper = FindType("Amplitude.Mercury.EffectMapper.ValidationMapper");
        t_NavigationMapper = FindType("Amplitude.Mercury.EffectMapper.NavigationMapper");
        t_EffectMapperConfiguration = FindType("Amplitude.Mercury.EffectMapper.EffectMapperConfiguration");
        t_FlagsEnum = FindType("Amplitude.Mercury.EffectMapper.EffectParameters+Flags") ?? FindType("Amplitude.Mercury.EffectMapper.EffectParameters$Flags");

        if (t_Descriptor == null || t_Effect == null || t_Path == null || t_Validation == null || t_PropertyEffect == null
            || t_DescriptorMapper == null || t_FlagsEnum == null)
        { s_resolveFailed = true; return false; }

        t_PropertyEffectPolicy = t_DescriptorMapper.GetNestedType("PropertyEffectPolicy", ALL);

        f_startingType = GetField(t_Descriptor, "startingType");
        f_effects = GetField(t_Descriptor, "Effects");
        f_desc_category = GetField(t_Descriptor, "serializableCategory");
        f_applyOnSource = GetField(t_Effect, "ApplyEffectOnSource");
        f_path = GetField(t_Effect, "Path");
        f_propertyEffects = GetField(t_Effect, "PropertyEffects");
        f_propertyToFollow = GetField(t_Path, "PropertyToFollow");
        f_validations = GetField(t_Path, "Validations");
        f_val_pathIndex = GetField(t_Validation, "PathIndex");
        f_val_elementName = GetField(t_Validation, "ElementName");
        f_val_inverted = GetField(t_Validation, "Inverted");
        f_targetProperty = GetField(t_PropertyEffect, "TargetProperty");
        f_toTargetOp = GetField(t_PropertyEffect, "ToTargetOperation");
        f_rpnStack = GetField(t_PropertyEffect, "RpnOperationStack");
        f_constantStack = GetField(t_PropertyEffect, "ConstantStack");
        f_propertyLocalNames = GetField(t_PropertyEffect, "PropertyLocalName");
        f_note = GetField(t_PropertyEffect, "Note");

        t_OperationEnum = f_toTargetOp?.FieldType;
        var fixedPointType = f_constantStack?.FieldType.GetElementType();
        t_FixedPoint = fixedPointType;
        if (fixedPointType != null)
        {
            f_rawValue = GetField(fixedPointType, "RawValue");
            var oneRaw = fixedPointType.GetField("OneRaw", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (oneRaw != null) { try { s_oneRaw = Convert.ToInt64(oneRaw.GetValue(null)); } catch { s_oneRaw = 1000; } }
        }
        if (t_OperationEnum != null && t_OperationEnum.IsEnum)
        {
            OP_Add = OpVal("Add"); OP_Sub = OpVal("Sub"); OP_Mult = OpVal("Mult"); OP_Div = OpVal("Div");
            OP_Percent = OpVal("Percent"); OP_Pow = OpVal("Pow"); OP_Max = OpVal("Max"); OP_Min = OpVal("Min");
            OP_GetTarget = OpVal("GetPropertyFromTarget"); OP_GetSource = OpVal("GetPropertyFromSource");
            OP_GetConst = OpVal("GetFromConstantValue"); OP_GetVariable = OpVal("GetFromVariable");
            OP_GetWorld = OpVal("GetPropertyFromWorld");
        }

        f_dm_localizedName = GetField(t_DescriptorMapper, "LocalizedName");
        f_dm_hideDescriptor = GetField(t_DescriptorMapper, "HideDescriptor");
        f_dm_policies = GetField(t_DescriptorMapper, "PropertyEffectPolicies");
        if (t_PropertyEffectPolicy != null)
        {
            f_pol_effectIndex = GetField(t_PropertyEffectPolicy, "EffectIndex");
            f_pol_peIndex = GetField(t_PropertyEffectPolicy, "PropertyEffectIndex");
            f_pol_hide = GetField(t_PropertyEffectPolicy, "HidePropertyEffect");
            f_pol_localization = GetField(t_PropertyEffectPolicy, "EffectLocalization");
            f_pol_flags = GetField(t_PropertyEffectPolicy, "ParameterFlags");
            f_pol_solveRpn = GetField(t_PropertyEffectPolicy, "SolveRPNFormula");
        }

        if (t_PropertyMapper != null)
        {
            f_pm_localization = GetField(t_PropertyMapper, "Localization");
            f_pm_displaySigned = GetField(t_PropertyMapper, "DisplayAsSigned");
            f_pm_displayPercent = GetField(t_PropertyMapper, "DisplayAsPercent");
        }
        if (t_ValidationMapper != null)
        {
            f_vm_localization = GetField(t_ValidationMapper, "Localization");
            f_vm_localizationTarget = GetField(t_ValidationMapper, "LocalizationTarget");
            f_vm_localizationTargetInverted = GetField(t_ValidationMapper, "LocalizationTargetInverted");
        }
        if (t_NavigationMapper != null) f_nm_localization = GetField(t_NavigationMapper, "Localization");
        if (t_EffectMapperConfiguration != null)
        {
            f_emc_additionalKeys = GetField(t_EffectMapperConfiguration, "AdditionalEffectKeys");
            t_AdditionalEffectKey = f_emc_additionalKeys?.FieldType.GetElementType();
            if (t_AdditionalEffectKey != null)
            {
                f_aek_flags = GetField(t_AdditionalEffectKey, "Flags");
                f_aek_key = GetField(t_AdditionalEffectKey, "Key");
            }
        }

        if (t_FlagsEnum.IsEnum)
        {
            FLAG_None = FlagVal("None"); FLAG_Value = FlagVal("Value"); FLAG_Property = FlagVal("Property");
            FLAG_ValueFactorProperty = FlagVal("ValueFactorProperty"); FLAG_Condition = FlagVal("Condition");
            FLAG_Target = FlagVal("Target"); FLAG_SynergySource = FlagVal("SynergySource");
            FLAG_SynergyTarget = FlagVal("SynergyTarget"); FLAG_IntermediateCondition = FlagVal("IntermediateCondition");
            FLAG_PluralCondition = FlagVal("PluralCondition"); FLAG_BonusFimsResource = FlagVal("BonusFimsResource");
            FLAG_OtherValueFactorProperty = FlagVal("OtherValueFactorProperty");
        }

        BuildGenericTemplateMap();

        bool ok = f_startingType != null && f_effects != null && f_applyOnSource != null && f_path != null
            && f_propertyEffects != null && f_targetProperty != null && f_toTargetOp != null && f_rpnStack != null
            && f_constantStack != null && f_propertyLocalNames != null && f_rawValue != null
            && f_dm_localizedName != null && f_dm_policies != null && t_PropertyEffectPolicy != null
            && f_pol_effectIndex != null && f_pol_peIndex != null && f_pol_hide != null
            && f_pol_localization != null && f_pol_flags != null
            && f_val_pathIndex != null && f_val_elementName != null && f_val_inverted != null;
        s_resolveFailed = !ok;
        return ok;
    }

    static void BuildGenericTemplateMap()
    {
        void Add(string field, params int[] flags)
        {
            int combo = 0; foreach (var f in flags) combo |= f;
            s_genericTemplates.Add((combo, field));
        }
        Add("SimpleEffect", FLAG_Value, FLAG_Property);
        Add("FormulaEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty);
        Add("FormulaTwoFactorsEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_OtherValueFactorProperty); // EffectParameters.Flags.PropertyEffect
        Add("ConditionalEffect", FLAG_Value, FLAG_Property, FLAG_Condition);
        Add("ConditionalFormulaEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_Condition);
        Add("ConditionalFormulaTwoFactorsEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_OtherValueFactorProperty, FLAG_Condition);
        Add("TargetEffect", FLAG_Value, FLAG_Property, FLAG_Target);
        Add("TargetFormulaEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_Target);
        Add("TargetFormulaTwoFactorsEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_OtherValueFactorProperty, FLAG_Target);
        Add("TargetConditionalEffect", FLAG_Value, FLAG_Property, FLAG_Condition, FLAG_Target);
        Add("TargetMultipleConditionalEffect", FLAG_Value, FLAG_Property, FLAG_Target, FLAG_PluralCondition);
        Add("TargetFormulaConditionalEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_Condition, FLAG_Target);
        Add("TargetFormulaTwoFactorsConditionalEffect", FLAG_Value, FLAG_Property, FLAG_ValueFactorProperty, FLAG_OtherValueFactorProperty, FLAG_Condition, FLAG_Target);
        Add("SynergyEffect", FLAG_SynergySource, FLAG_SynergyTarget, FLAG_Value, FLAG_Property);
        Add("SimpleIntermediateEffect", FLAG_Value, FLAG_Property, FLAG_IntermediateCondition);
        Add("IntermediateTargetEffect", FLAG_Value, FLAG_Property, FLAG_Target, FLAG_IntermediateCondition);
        Add("BonusFIMSResource", FLAG_Value, FLAG_Property, FLAG_Target, FLAG_BonusFimsResource);
        Add("BonusFIMSResourceIntermediateCondition", FLAG_Value, FLAG_Property, FLAG_Target, FLAG_IntermediateCondition, FLAG_BonusFimsResource);
        Add("TargetPath", FLAG_Target);
        Add("ConditionPath", FLAG_Condition);
        Add("ConditionTargetPath", FLAG_Condition, FLAG_Target);
        Add("SynergyPath", FLAG_SynergySource, FLAG_SynergyTarget);
    }

    static int OpVal(string name) { try { return Convert.ToInt32(Enum.Parse(t_OperationEnum, name)); } catch { return -999; } }
    static int FlagVal(string name) { try { return Convert.ToInt32(Enum.Parse(t_FlagsEnum, name)); } catch { return 0; } }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName); if (t != null) return t; } catch { }
        }
        return null;
    }

    static FieldInfo GetField(Type t, string name) => t?.GetField(name, ALL);

    // ── Drawn by InspectorAnalysisPanel inside the shared, height-capped container ──
    /// <summary>True when this panel applies to the inspected target (a Descriptor or DescriptorMapper).</summary>
    public static bool WillDraw(Editor editor)
    {
        if (editor == null || editor.targets == null || editor.targets.Length != 1) return false;
        var target = editor.target;
        if (target == null || !TryResolve()) return false;
        return t_DescriptorMapper.IsInstanceOfType(target) || t_Descriptor.IsInstanceOfType(target);
    }

    public static void Draw(Editor editor)
    {
        if (editor.targets == null || editor.targets.Length != 1) return;
        var target = editor.target;
        if (target == null) return;
        if (!TryResolve()) return;

        bool isMapper = t_DescriptorMapper.IsInstanceOfType(target);
        bool isDescriptor = t_Descriptor.IsInstanceOfType(target);
        if (!isMapper && !isDescriptor) return;

        UnityEngine.Object descriptorObj = isDescriptor ? target : FindPairedAsset(t_Descriptor, target.name);
        UnityEngine.Object mapperObj = isMapper ? target : FindPairedAsset(t_DescriptorMapper, target.name);

        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        s_expanded = EditorGUILayout.Foldout(s_expanded, "Tooltip Breakdown Preview", true);
        GUILayout.FlexibleSpace();
        EditorGUI.BeginChangeCheck();
        s_active = GUILayout.Toggle(s_active, "Enabled", EditorStyles.miniButton, GUILayout.Width(60));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(ActiveKey, s_active);
            s_generation++;   // invalidate PropertyEffectDrawer cache so it honours the toggle
            s_previewCache.Clear();
        }
        EditorGUILayout.EndHorizontal();
        if (!s_expanded || !s_active) return;

        var ops = GetOrBuildOps(target.GetInstanceID(), descriptorObj, mapperObj, target.name);
        EditorGUI.indentLevel++;
        try { Replay(ops); }
        finally { EditorGUI.indentLevel--; }
    }

    // Returns the cached draw-command list for this target, rebuilding only when the
    // underlying data (structural hash) or the generation counter has changed.
    static List<DrawOp> GetOrBuildOps(int key, UnityEngine.Object descriptorObj, UnityEngine.Object mapperObj, string name)
    {
        int hash = ComputeDataHash(descriptorObj, mapperObj, name);
        if (s_previewCache.TryGetValue(key, out var cached) && cached.generation == s_generation && cached.hash == hash)
            return cached.ops;

        if (s_previewCache.Count > 64) s_previewCache.Clear();   // bound growth across a long session
        var ops = BuildOps(descriptorObj, mapperObj, name);
        s_previewCache[key] = new Cached { hash = hash, generation = s_generation, ops = ops };
        return ops;
    }

    static void Replay(List<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            switch (op.kind)
            {
                case OpKind.LabelTwo: EditorGUILayout.LabelField(op.a, op.b); break;
                case OpKind.LabelOne:
                    if (op.style != null) EditorGUILayout.LabelField(op.a, op.style);
                    else EditorGUILayout.LabelField(op.a);
                    break;
                case OpKind.HelpBox: EditorGUILayout.HelpBox(op.a, op.msg); break;
                case OpKind.BeginBox: EditorGUILayout.BeginVertical(EditorStyles.helpBox); break;
                case OpKind.EndBox: EditorGUILayout.EndVertical(); break;
            }
        }
    }

    // ── Op emit helpers ──────────────────────────────────────────────────────
    static void OpTwo(List<DrawOp> ops, string a, string b) => ops.Add(new DrawOp { kind = OpKind.LabelTwo, a = a, b = b });
    static void OpOne(List<DrawOp> ops, string a, GUIStyle style = null) => ops.Add(new DrawOp { kind = OpKind.LabelOne, a = a, style = style });
    static void OpHelp(List<DrawOp> ops, string a, MessageType m) => ops.Add(new DrawOp { kind = OpKind.HelpBox, a = a, msg = m });
    static void OpBegin(List<DrawOp> ops) => ops.Add(new DrawOp { kind = OpKind.BeginBox });
    static void OpEnd(List<DrawOp> ops) => ops.Add(new DrawOp { kind = OpKind.EndBox });

    static List<DrawOp> BuildOps(UnityEngine.Object descriptorObj, UnityEngine.Object mapperObj, string name)
    {
        var ops = new List<DrawOp>();
        s_buildDict = null;   // built lazily on first Resolve, reused for the whole build
        try
        {
            if (descriptorObj == null)
            {
                OpHelp(ops, $"No Descriptor named '{name}' found (mod project or mounted vanilla bundle) — can't resolve Effects/PropertyEffects.", MessageType.Info);
                if (mapperObj == null) return ops;
            }
            if (mapperObj == null)
            {
                OpHelp(ops, $"No DescriptorMapper named '{name}' found — rows will render with auto-computed (unmapped) parameters only.", MessageType.Info);
            }

            string localizedName = f_dm_localizedName != null && mapperObj != null ? f_dm_localizedName.GetValue(mapperObj) as string : null;
            bool hideDescriptor = f_dm_hideDescriptor != null && mapperObj != null && (bool)f_dm_hideDescriptor.GetValue(mapperObj);

            OpTwo(ops, "Descriptor label", Resolve(localizedName) ?? "(none — falls back to raw descriptor name)");
            if (hideDescriptor)
            {
                OpHelp(ops, "HideDescriptor = true — this descriptor never shows in any tooltip breakdown, regardless of its effects.", MessageType.Warning);
                return ops;
            }
            if (descriptorObj == null) return ops;

            BuildEffects(ops, descriptorObj, mapperObj);
        }
        catch (Exception e)
        {
            OpHelp(ops, "Preview failed: " + e.Message, MessageType.Error);
        }
        finally { s_buildDict = null; }
        return ops;
    }

    static void BuildEffects(List<DrawOp> ops, UnityEngine.Object descriptorObj, UnityEngine.Object mapperObj)
    {
        if (f_effects.GetValue(descriptorObj) is not Array effects) return;
        for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
        {
            var effect = effects.GetValue(effectIndex);
            if (effect == null) continue;
            if (f_propertyEffects.GetValue(effect) is not Array peArr) continue;

            for (int peIndex = 0; peIndex < peArr.Length; peIndex++)
            {
                var pe = peArr.GetValue(peIndex);
                if (pe == null) continue;
                BuildRow(ops, descriptorObj, effect, effectIndex, pe, peIndex, mapperObj);
            }
        }
        if (effects.Length == 0) OpOne(ops, "(Descriptor has no Effects)", EditorStyles.miniLabel);
    }

    static void BuildRow(List<DrawOp> ops, UnityEngine.Object descriptorObj, object effect, int effectIndex, object pe, int peIndex, UnityEngine.Object mapperObj)
    {
        // Find the matching policy, if any.
        object policy = null;
        if (mapperObj != null && f_dm_policies.GetValue(mapperObj) is Array policies)
        {
            foreach (var p in policies)
            {
                if (p == null) continue;
                if (Convert.ToInt32(f_pol_effectIndex.GetValue(p)) == effectIndex && Convert.ToInt32(f_pol_peIndex.GetValue(p)) == peIndex)
                { policy = p; break; }
            }
        }

        string targetProperty = f_targetProperty.GetValue(pe) as string ?? "";
        bool hidden = policy != null && (bool)f_pol_hide.GetValue(policy);

        OpBegin(ops);

        OpOne(ops, $"Effect[{effectIndex}].PropertyEffect[{peIndex}] — {targetProperty}", EditorStyles.boldLabel);

        if (hidden)
        {
            OpOne(ops, "Hidden (HidePropertyEffect = true) — never shown.", EditorStyles.miniLabel);
            OpEnd(ops);
            return;
        }

        string formula = BuildFormula(pe);
        int toTargetOp = Convert.ToInt32(f_toTargetOp.GetValue(pe));
        OpTwo(ops, "Formula", formula);
        OpTwo(ops, "In-game render", BuildResolvedFormula(pe, toTargetOp, targetProperty));
        string note = f_note?.GetValue(pe) as string;
        if (!string.IsNullOrEmpty(note))
            OpTwo(ops, "Note", note);

        // Policy fields (a mapper's per-PropertyEffect override), matching DescriptorMapper.PropertyEffectPolicy.
        int declaredFlags = policy != null ? Convert.ToInt32(f_pol_flags.GetValue(policy)) : FLAG_None;
        string explicitLoc = policy != null ? f_pol_localization.GetValue(policy) as string : null;
        bool solveRpn = policy != null && f_pol_solveRpn != null && (bool)f_pol_solveRpn.GetValue(policy);
        bool isExotic = !string.IsNullOrEmpty(explicitLoc) && declaredFlags == FLAG_None;   // PropertyEffectPolicy.IsExoticEffect

        // Exotic: a literal localized string, no parameter substitution.
        if (isExotic)
        {
            OpTwo(ops, "ParameterFlags", "None (exotic)");
            OpTwo(ops, "Template (EffectLocalization)", Resolve(explicitLoc));
            OpTwo(ops, "Rendered", Resolve(explicitLoc));
            OpEnd(ops);
            return;
        }

        // SolveRPNFormula: the RPN is evaluated live against game state; the runtime shows it as a
        // plain Value|Property line. We can't evaluate it statically, so show the formula as the value.
        PropShape shape = solveRpn ? default(PropShape) : AnalyzePropertyEffectShape(pe, toTargetOp);
        if (!solveRpn && !shape.valid)
        {
            OpTwo(ops, "ParameterFlags", "(unrenderable)");
            OpHelp(ops, "This PropertyEffect's RPN isn't a recognised simple / one-factor / two-factor form, so the runtime (FillPropertyEffectEvaluation) skips it — it renders no tooltip row. Use an Exotic EffectLocalization on the DescriptorMapper if you need custom text.", MessageType.Warning);
            OpEnd(ops);
            return;
        }

        // Path side. Synergy descriptors (Category == "District_Synergy") take a separate evaluation:
        // the single non-inverted "Must Have" validation becomes the SynergySource; navigation/other
        // validations are ignored. Otherwise the path is classified normally, honouring ApplyEffectOnSource
        // (a navigation only yields a Target when the effect is NOT applied on the source).
        bool applyOnSource = f_applyOnSource != null && (bool)f_applyOnSource.GetValue(effect);
        bool isSynergy = string.Equals(f_desc_category?.GetValue(descriptorObj) as string, "District_Synergy", StringComparison.Ordinal);
        PathInfo pathInfo; int pathFlags;
        if (isSynergy) { pathInfo = ClassifySynergyPath(effect, out string synergyWarn); pathFlags = string.IsNullOrEmpty(pathInfo.synergySource) ? 0 : FLAG_SynergySource; if (synergyWarn != null) OpHelp(ops, synergyWarn, MessageType.Warning); }
        else { pathInfo = ClassifyPath(effect, applyOnSource); pathFlags = ComputePathFlags(pathInfo); }

        int flags; string specificKey;
        if (solveRpn)
        {
            // FillPropertyEffectEvaluation's SolveRPNFormula branch hard-sets Value|Property (no path
            // flags) and `continue`s BEFORE reading the policy's EffectLocalization/ParameterFlags — so
            // both are ignored and only the generic SimpleEffect template applies.
            flags = FLAG_Value | FLAG_Property;
            specificKey = null;
            OpTwo(ops, "ParameterFlags (SolveRPNFormula — forced Value|Property)", DecodeFlags(flags));
            if (!string.IsNullOrEmpty(explicitLoc) || declaredFlags != FLAG_None)
                OpHelp(ops, "SolveRPNFormula is on, so the runtime ignores this policy's EffectLocalization and ParameterFlags — it always uses the generic \"{0_Value} {1_Property}\" template. To use custom text like \"total {0_Value} Industry from …\", turn SolveRPNFormula OFF and set EffectLocalization instead (the value-factor collapses into the solved total {0_Value} automatically for one/two-factor formulas).", MessageType.Warning);
        }
        else
        {
            // Auto flags = property-effect flags (Value|Property + factors + bonus) | path flags — what the
            // runtime uses by default (EffectParameters.ComputeFlags). A policy only overrides when it
            // declares a valid subset with an EffectLocalization.
            int autoFlags = shape.propFlags | pathFlags;
            bool hasLoc = policy != null && (declaredFlags != FLAG_None || !string.IsNullOrEmpty(explicitLoc));
            bool subsetValid = !hasLoc || (declaredFlags & ~autoFlags) == 0;
            flags = (hasLoc && subsetValid) ? declaredFlags : autoFlags;
            specificKey = (hasLoc && subsetValid && !string.IsNullOrEmpty(explicitLoc)) ? explicitLoc : null;

            string flagsNote = policy == null ? " (auto, from path + property effect)"
                : !hasLoc ? " (policy present, no override — auto)"
                : !subsetValid ? " (⚠ policy declares params not in the effect — ignored, using auto)"
                : " (from DescriptorMapper policy)";
            OpTwo(ops, "ParameterFlags" + flagsNote, DecodeFlags(flags));
            if (hasLoc && !subsetValid)
                OpHelp(ops, $"DescriptorMapper policy declares ParameterFlags '{DecodeFlags(declaredFlags)}' but the effect only provides '{DecodeFlags(autoFlags)}'. At runtime this logs \"Specified set of Parameters is invalid\" and falls back to the auto set.", MessageType.Warning);
        }

        // Ordered parameter values, in the exact order EffectTranslator substitutes them.
        var prms = BuildOrderedParams(flags, shape, solveRpn, formula, targetProperty, pathInfo);

        string templateKey = specificKey ?? LookupTemplateKey(flags);
        if (templateKey == null)
        {
            OpHelp(ops, $"No EffectLocalization set and no EffectMapperConfiguration template (incl. Additional Effect Keys) matches flags '{DecodeFlags(flags)}' — this errors at runtime (\"No default Localization key for Flags\").", MessageType.Error);
        }
        else
        {
            string template = Resolve(templateKey);
            OpTwo(ops, specificKey != null ? "Template (EffectLocalization)" : "Template (generic fallback)", template);
            OpTwo(ops, "Rendered", Substitute(template, prms));
        }
        foreach (var p in prms) OpOne(ops, $" • {p.name} = {p.value}");

        OpEnd(ops);
    }

    struct ParamValue { public string name, value; public ParamValue(string n, string v) { name = n; value = v; } }

    // Builds the ordered parameter list matching EffectTranslator.PrepareLocalizedStringParameters:
    // Value, Property, BonusFimsResource, ValueFactorProperty, OtherValueFactorProperty (property side),
    // then Condition, Intermediate/Plural, Target, SynergySource, SynergyTarget (path side).
    static List<ParamValue> BuildOrderedParams(int flags, PropShape shape, bool solveRpn, string formula, string targetProperty, PathInfo path)
    {
        var list = new List<ParamValue>();
        if ((flags & FLAG_Value) != 0)
            list.Add(new ParamValue("Value", solveRpn ? $"({formula}) [solved at runtime]" : shape.valueText));
        if ((flags & FLAG_Property) != 0) list.Add(new ParamValue("Property", ResolvePropertyLabel(targetProperty)));
        if ((flags & FLAG_BonusFimsResource) != 0) list.Add(new ParamValue("BonusFimsResource", ResolvePropertyLabel(targetProperty)));
        if ((flags & FLAG_ValueFactorProperty) != 0) list.Add(new ParamValue("ValueFactorProperty", ResolvePropertyLabel(shape.valueFactorProperty)));
        if ((flags & FLAG_OtherValueFactorProperty) != 0) list.Add(new ParamValue("OtherValueFactorProperty", ResolvePropertyLabel(shape.otherValueFactorProperty)));
        if ((flags & FLAG_Condition) != 0) list.Add(new ParamValue("Condition", ResolveConditionLabel(path.condition)));
        if ((flags & (FLAG_IntermediateCondition | FLAG_PluralCondition)) != 0) list.Add(new ParamValue("Intermediate/PluralCondition", ResolveConditionLabel(path.intermediate)));
        if ((flags & FLAG_Target) != 0) list.Add(new ParamValue("Target", ResolveTargetLabel(path)));
        if ((flags & FLAG_SynergySource) != 0) list.Add(new ParamValue("SynergySource", ResolveSynergyLabel(path.synergySource)));
        if ((flags & FLAG_SynergyTarget) != 0) list.Add(new ParamValue("SynergyTarget", ResolveSynergyLabel(path.synergyTarget)));
        return list;
    }

    // Substitutes {N_Label} (or {N}) placeholders in a resolved template with the Nth ordered param.
    static readonly Regex s_placeholder = new(@"\{(\d+)(?:_[^}]*)?\}", RegexOptions.Compiled);
    static string Substitute(string template, List<ParamValue> prms)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return s_placeholder.Replace(template, m =>
        {
            int i = int.Parse(m.Groups[1].Value);
            return i >= 0 && i < prms.Count ? prms[i].value : m.Value;
        });
    }

    // ── PropertyEffect shape (mirrors SimulationEvaluatorHelper.FillPropertyEffectEvaluation) ──
    struct PropShape
    {
        public bool valid;
        public int propFlags;                 // Value|Property (+ValueFactorProperty/+Other/+BonusFims)
        public string valueText;              // formatted leading constant (signed / percent per PropertyMapper)
        public string valueFactorProperty, otherValueFactorProperty;
    }

    static PropShape AnalyzePropertyEffectShape(object pe, int toTargetOp)
    {
        var s = new PropShape();
        var rpn = f_rpnStack?.GetValue(pe) as Array;
        var names = f_propertyLocalNames?.GetValue(pe) as string[];
        var consts = f_constantStack?.GetValue(pe) as Array;
        int rpnLen = rpn?.Length ?? 0, nameLen = names?.Length ?? 0, constLen = consts?.Length ?? 0;

        bool simple = rpnLen == 0 && nameLen == 0 && constLen == 1;
        bool oneFactor = rpnLen == 3 && OpAt(rpn, 2) == OP_Mult && nameLen == 1 && constLen == 1;
        bool twoFactor = rpnLen == 5 && OpAt(rpn, 2) == OP_Mult && OpAt(rpn, 4) == OP_Mult && nameLen == 2 && constLen == 1;
        s.valid = simple || oneFactor || twoFactor;
        if (!s.valid) return s;

        if (oneFactor || twoFactor) s.valueFactorProperty = names[0];
        if (twoFactor) s.otherValueFactorProperty = names[1];

        s.propFlags = FLAG_Value | FLAG_Property;
        if (!string.IsNullOrEmpty(s.valueFactorProperty)) s.propFlags |= FLAG_ValueFactorProperty;
        if (!string.IsNullOrEmpty(s.otherValueFactorProperty)) s.propFlags |= FLAG_OtherValueFactorProperty;
        string targetProperty = f_targetProperty.GetValue(pe) as string ?? "";
        if (IsBonusEffect(targetProperty)) s.propFlags |= FLAG_BonusFimsResource;

        double value = constLen > 0 ? RawToDouble(consts.GetValue(0)) : 0;
        s.valueText = FormatValueString(value, toTargetOp, targetProperty);
        return s;
    }

    static int OpAt(Array rpn, int i) => rpn != null && i < rpn.Length ? Convert.ToInt32(rpn.GetValue(i)) : int.MinValue;

    static bool IsBonusEffect(string propertyName) => propertyName switch
    {
        "BonusFoodIfProducingFood" or "BonusIndustryIfProducingIndustry" or "BonusMoneyIfProducingMoney"
            or "BonusScienceIfProducingScience" or "BonusFaithIfProducingFaith"
            or "BonusInfluenceIfProducingInfluence" => true,
        _ => false,
    };

    // Formats the leading constant like EffectTranslator.FormatValue: signed unless the PropertyMapper
    // opts out; percent if the PropertyMapper displays as percent (Sub negates).
    static string FormatValueString(double value, int op, string targetProperty)
    {
        if (op == OP_Sub) value = -value;
        bool percent = false, signed = true;
        var pm = FindAssetByName(t_PropertyMapper, targetProperty);
        if (pm != null)
        {
            if (f_pm_displayPercent != null) percent = (bool)f_pm_displayPercent.GetValue(pm);
            if (f_pm_displaySigned != null) signed = (bool)f_pm_displaySigned.GetValue(pm);
        }
        double disp = percent ? value * 100.0 : value;
        string mag = disp == Math.Floor(disp) ? ((long)Math.Abs(disp)).ToString() : Math.Abs(disp).ToString("0.####");
        string sign = disp < 0 ? "-" : (signed ? "+" : "");
        return sign + mag + (percent ? "%" : "");
    }

    static double RawToDouble(object fp)
    {
        if (f_rawValue == null || fp == null) return 0;
        try { return (double)Convert.ToInt64(f_rawValue.GetValue(fp)) / s_oneRaw; } catch { return 0; }
    }

    static string DecodeFlags(int flags)
    {
        if (flags == FLAG_None) return "None";
        var names = new List<string>();
        void Chk(int bit, string name) { if ((flags & bit) != 0 && bit != 0) names.Add(name); }
        Chk(FLAG_Value, "Value"); Chk(FLAG_Property, "Property"); Chk(FLAG_ValueFactorProperty, "ValueFactorProperty");
        Chk(FLAG_OtherValueFactorProperty, "OtherValueFactorProperty"); Chk(FLAG_BonusFimsResource, "BonusFimsResource");
        Chk(FLAG_Condition, "Condition"); Chk(FLAG_IntermediateCondition, "IntermediateCondition"); Chk(FLAG_PluralCondition, "PluralCondition");
        Chk(FLAG_Target, "Target"); Chk(FLAG_SynergySource, "SynergySource"); Chk(FLAG_SynergyTarget, "SynergyTarget");
        return names.Count > 0 ? string.Join(" | ", names) : "0x" + flags.ToString("X");
    }

    // Mirrors EffectTranslator.Load(): the fixed flag->config-field map first, then the config's
    // AdditionalEffectKeys list (which is where synergy combos like Value|Property|SynergySource ->
    // %SynergyEffectNoTarget live).
    static string LookupTemplateKey(int flags)
    {
        foreach (var (comboFlags, field) in s_genericTemplates)
            if (comboFlags == flags)
                return GetEffectMapperConfigField(field);
        return GetAdditionalEffectKey(flags);
    }

    static string GetAdditionalEffectKey(int flags)
    {
        var cfg = GetEffectMapperConfig();
        if (cfg == null || f_emc_additionalKeys == null || f_aek_flags == null || f_aek_key == null) return null;
        if (f_emc_additionalKeys.GetValue(cfg) is not Array keys) return null;
        foreach (var k in keys)
        {
            if (k == null) continue;
            if (Convert.ToInt32(f_aek_flags.GetValue(k)) == flags) return f_aek_key.GetValue(k) as string;
        }
        return null;
    }

    static UnityEngine.Object s_effectMapperConfig;
    static bool s_effectMapperConfigSearched;
    static UnityEngine.Object GetEffectMapperConfig()
    {
        if (!s_effectMapperConfigSearched)
        {
            s_effectMapperConfigSearched = true;
            s_effectMapperConfig = FindAnyAssetOfType(t_EffectMapperConfiguration);
        }
        return s_effectMapperConfig;
    }

    static string GetEffectMapperConfigField(string fieldName)
    {
        var cfg = GetEffectMapperConfig();
        if (cfg == null) return null;
        var f = GetField(t_EffectMapperConfiguration, fieldName);
        return f?.GetValue(cfg) as string;
    }

    // ── Path classification (mirrors SimulationEvaluatorHelper.GenerateEffectPathEvaluation) ──
    struct PathInfo
    {
        public string targetValidation, condition, intermediate, targetNavigation, synergySource, synergyTarget;
        public bool hasTargetValidation, hasTargetNavigation;
    }

    static PathInfo ClassifyPath(object effect, bool applyOnSource)
    {
        var info = new PathInfo();
        var path = f_path.GetValue(effect);
        if (path == null) return info;
        var propertyToFollow = f_propertyToFollow.GetValue(path) as string[];
        int lastIdx = (propertyToFollow?.Length ?? 0) - 1;
        // A navigation only contributes a Target when the effect is applied on the *target*, not the
        // source (GenerateEffectPathEvaluation: `path.HasNavigation() && !reference.ApplyEffectOnSource`).
        if (propertyToFollow != null && propertyToFollow.Length > 0 && !applyOnSource)
        {
            info.hasTargetNavigation = true;
            info.targetNavigation = propertyToFollow[lastIdx];
        }

        if (f_validations.GetValue(path) is Array validations)
        {
            int bestIntermediateIdx = -1;
            foreach (var v in validations)
            {
                if (v == null) continue;
                int pathIndex = Convert.ToInt32(f_val_pathIndex.GetValue(v));
                string elementName = f_val_elementName.GetValue(v)?.ToString() ?? "";
                bool inverted = (bool)f_val_inverted.GetValue(v);
                string tagged = inverted ? "!" + elementName : elementName;

                if (pathIndex == -1) { info.condition = tagged; continue; }
                if (pathIndex == lastIdx) { info.targetValidation = tagged; info.hasTargetValidation = true; continue; }
                if (pathIndex > bestIntermediateIdx) { info.intermediate = tagged; bestIntermediateIdx = pathIndex; }
            }
        }
        return info;
    }

    // Synergy descriptors (Category "District_Synergy") — TryGenerateSynergyDescriptorEvaluation: the
    // single non-inverted "Must Have" validation's ElementName is the SynergySource; navigation and any
    // other validations are ignored.
    static PathInfo ClassifySynergyPath(object effect, out string warning)
    {
        warning = null;
        var info = new PathInfo();
        var path = f_path.GetValue(effect);
        if (path == null) { warning = "Synergy descriptor: Effect has no Path."; return info; }
        int mustHaveCount = 0; string only = null;
        if (f_validations.GetValue(path) is Array validations)
            foreach (var v in validations)
            {
                if (v == null || (bool)f_val_inverted.GetValue(v)) continue;
                mustHaveCount++;
                only = f_val_elementName.GetValue(v)?.ToString() ?? "";
            }
        if (mustHaveCount == 1) info.synergySource = only;
        else warning = $"Synergy descriptor (District_Synergy) must have exactly one non-inverted 'Must Have' validation; found {mustHaveCount}. The runtime logs an error and drops the descriptor.";
        return info;
    }

    // EffectParameters.ComputeFlags(ref EffectPathEvaluation) for the non-synergy path.
    static int ComputePathFlags(PathInfo info)
    {
        int flags = 0;
        if (info.hasTargetNavigation || info.hasTargetValidation) flags |= FLAG_Target;
        if (!string.IsNullOrEmpty(info.condition)) flags |= FLAG_Condition;
        if (!string.IsNullOrEmpty(info.synergySource)) flags |= FLAG_SynergySource;
        if (!string.IsNullOrEmpty(info.synergyTarget)) flags |= FLAG_SynergyTarget;
        if (!string.IsNullOrEmpty(info.intermediate)) flags |= FLAG_IntermediateCondition;   // plurality not derivable here
        return flags;
    }

    // SynergySource / SynergyTarget use ValidationMapper.Localization (not LocalizationTarget).
    static string ResolveSynergyLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return "(none)";
        var vm = FindAssetByName(t_ValidationMapper, name);
        if (vm == null) return $"{name} — ⚠ missing ValidationMapper";
        string key = f_vm_localization?.GetValue(vm) as string;
        return string.IsNullOrEmpty(key) ? $"{name} — ⚠ ValidationMapper has no Localization" : Resolve(key);
    }

    static string ResolveConditionLabel(string taggedName)
    {
        if (string.IsNullOrEmpty(taggedName)) return "(none)";
        bool inverted = taggedName.StartsWith("!");
        string name = inverted ? taggedName.Substring(1) : taggedName;
        var vm = FindAssetByName(t_ValidationMapper, name);
        if (vm == null) return $"{name} — ⚠ missing ValidationMapper";
        string key = inverted
            ? (f_vm_localizationTargetInverted?.GetValue(vm) as string)
            : (f_vm_localizationTarget?.GetValue(vm) as string);
        return string.IsNullOrEmpty(key) ? $"{name} — ⚠ ValidationMapper has no LocalizationTarget{(inverted ? "Inverted" : "")}" : Resolve(key);
    }

    static string ResolveTargetLabel(PathInfo info)
    {
        if (info.hasTargetValidation) return ResolveConditionLabel(info.targetValidation);
        if (info.hasTargetNavigation && !string.IsNullOrEmpty(info.targetNavigation))
        {
            // Runtime looks up the NavigationMapper by the lower-invariant nav name; try exact then lowered.
            var nm = FindAssetByName(t_NavigationMapper, info.targetNavigation)
                     ?? FindAssetByName(t_NavigationMapper, info.targetNavigation.ToLowerInvariant());
            if (nm == null) return $"{info.targetNavigation} — ⚠ missing NavigationMapper";
            string key = f_nm_localization?.GetValue(nm) as string;
            return string.IsNullOrEmpty(key) ? $"{info.targetNavigation} — ⚠ NavigationMapper has no Localization" : Resolve(key);
        }
        return "(no Target validation/navigation on this Effect's Path — Target flag looks mismatched)";
    }

    static string ResolvePropertyLabel(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return "(none)";
        var pm = FindAssetByName(t_PropertyMapper, propertyName);
        if (pm == null) return $"{propertyName} — ⚠ missing PropertyMapper";
        string key = f_pm_localization?.GetValue(pm) as string;
        return string.IsNullOrEmpty(key) ? $"{propertyName} — ⚠ PropertyMapper has no Localization" : Resolve(key);
    }

    // ── Name-based asset lookup (mod project scope + mounted vanilla bundle) ──
    static readonly Dictionary<Type, Dictionary<string, UnityEngine.Object>> s_byNameCache = new();

    static UnityEngine.Object FindPairedAsset(Type type, string name) => FindAssetByName(type, name);

    static UnityEngine.Object FindAssetByName(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        if (!s_byNameCache.TryGetValue(type, out var map))
        {
            map = new Dictionary<string, UnityEngine.Object>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (obj != null && type.IsInstanceOfType(obj) && !map.ContainsKey(obj.name)) map[obj.name] = obj;
            }
            foreach (var obj in VanillaDatabaseMount.LoadAllOfType(type))
                if (obj != null && !map.ContainsKey(obj.name)) map[obj.name] = obj;
            s_byNameCache[type] = map;
        }
        return map.TryGetValue(name, out var found) ? found : null;
    }

    static UnityEngine.Object FindAnyAssetOfType(Type type)
    {
        if (type == null) return null;
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj != null && type.IsInstanceOfType(obj)) return obj;
        }
        return VanillaDatabaseMount.LoadAllOfType(type).FirstOrDefault();
    }

    [MenuItem("Tools/shakee's Tools/Debug/Descriptor Mapper Preview/Clear Name Cache")]
    static void ClearCache()
    {
        s_byNameCache.Clear();
        s_effectMapperConfig = null;
        s_effectMapperConfigSearched = false;
        // Rendered previews may embed now-stale asset/translation lookups; force a rebuild.
        s_previewCache.Clear();
        s_generation++;
        Debug.Log("[DescriptorMapperPreview] Name lookup cache cleared.");
    }

    // ── Available property names (for intellisense in PropertyEffect fields) ──
    static IList<string> s_propertyNames;
    static int s_propertyNamesGeneration = -1;

    /// <summary>All PropertyMapper names (project + vanilla), cached per generation. For intellisense.</summary>
    public static IList<string> GetAvailablePropertyNames()
    {
        if (s_propertyNames != null && s_propertyNamesGeneration == s_generation)
            return s_propertyNames;
        s_propertyNamesGeneration = s_generation;
        var set = new HashSet<string>();
        try
        {
            if (t_PropertyMapper != null)
            {
                foreach (var obj in VanillaDatabaseMount.LoadAllOfType(t_PropertyMapper))
                    if (obj != null && !string.IsNullOrEmpty(obj.name)) set.Add(obj.name);
            }
        }
        catch { }
        try
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (obj != null && t_PropertyMapper != null && t_PropertyMapper.IsInstanceOfType(obj)
                        && !string.IsNullOrEmpty(obj.name)) set.Add(obj.name);
            }
        }
        catch { }
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        s_propertyNames = list;
        return list;
    }

    // ── %key -> text resolution ──────────────────────────────────────────────
    static string Resolve(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key[0] != '%') return key;
        // Built at most once per rebuild (BuildKeyToTextDict copies a ~48k-entry dict and scans
        // the project) and reused across every Resolve call in that build.
        var dict = s_buildDict ??= ArchiveTranslations.BuildKeyToTextDict();
        return dict.TryGetValue(key, out var text) ? text : key + " (unresolved key)";
    }

    // ── Structural hash (cheap change-detection for the render cache) ─────────
    // Folds together only the serialized fields the preview reads — no asset scans, no
    // translation-dict build, no Resolve. When this is unchanged between events the cached
    // draw-command list is replayed as-is. Any reflection failure returns a stable sentinel so
    // a broken object doesn't rebuild every frame (BuildOps captures the error into the cache).
    static int ComputeDataHash(UnityEngine.Object descriptorObj, UnityEngine.Object mapperObj, string name)
    {
        try
        {
            int h = 17;
            h = MixStr(h, name);
            // Fold in translation availability so a preview built with raw keys (provider was
            // stuck/unmounted) rebuilds with resolved text once translations come back.
            h = Mix(h, ArchiveTranslations.IsMounted ? 1 : 0);

            if (mapperObj != null)
            {
                h = Mix(h, 1);
                h = MixStr(h, f_dm_localizedName?.GetValue(mapperObj) as string);
                h = Mix(h, f_dm_hideDescriptor != null && (bool)f_dm_hideDescriptor.GetValue(mapperObj) ? 1 : 0);
                if (f_dm_policies?.GetValue(mapperObj) is Array policies)
                {
                    h = Mix(h, policies.Length);
                    foreach (var p in policies)
                    {
                        if (p == null) { h = Mix(h, 0); continue; }
                        h = Mix(h, Convert.ToInt32(f_pol_effectIndex.GetValue(p)));
                        h = Mix(h, Convert.ToInt32(f_pol_peIndex.GetValue(p)));
                        h = Mix(h, (bool)f_pol_hide.GetValue(p) ? 1 : 0);
                        h = MixStr(h, f_pol_localization.GetValue(p) as string);
                        h = Mix(h, Convert.ToInt32(f_pol_flags.GetValue(p)));
                        h = Mix(h, f_pol_solveRpn != null && (bool)f_pol_solveRpn.GetValue(p) ? 1 : 0);
                    }
                }
            }

            if (descriptorObj != null && f_effects.GetValue(descriptorObj) is Array effects)
            {
                h = Mix(h, 2);
                h = MixStr(h, f_desc_category?.GetValue(descriptorObj) as string);   // synergy vs regular
                h = Mix(h, effects.Length);
                foreach (var effect in effects)
                {
                    if (effect == null) { h = Mix(h, 0); continue; }
                    h = Mix(h, f_applyOnSource != null && (bool)f_applyOnSource.GetValue(effect) ? 1 : 0);   // gates navigation Target
                    // Path (Condition / Intermediate / Target classification inputs).
                    if (f_path.GetValue(effect) is object path)
                    {
                        if (f_propertyToFollow.GetValue(path) is string[] ptf)
                            foreach (var s in ptf) h = MixStr(h, s);
                        if (f_validations.GetValue(path) is Array vals)
                            foreach (var v in vals)
                            {
                                if (v == null) { h = Mix(h, 0); continue; }
                                h = Mix(h, Convert.ToInt32(f_val_pathIndex.GetValue(v)));
                                h = MixStr(h, f_val_elementName.GetValue(v)?.ToString());
                                h = Mix(h, (bool)f_val_inverted.GetValue(v) ? 1 : 0);
                            }
                    }
                    if (f_propertyEffects.GetValue(effect) is not Array peArr) { h = Mix(h, -1); continue; }
                    h = Mix(h, peArr.Length);
                    foreach (var pe in peArr)
                    {
                        if (pe == null) { h = Mix(h, 0); continue; }
                        h = MixStr(h, f_targetProperty.GetValue(pe) as string);
                        h = MixStr(h, f_note?.GetValue(pe) as string);
                        h = Mix(h, Convert.ToInt32(f_toTargetOp.GetValue(pe)));
                        if (f_rpnStack.GetValue(pe) is Array rpn) foreach (var o in rpn) h = Mix(h, Convert.ToInt32(o));
                        if (f_constantStack.GetValue(pe) is Array cs) foreach (var c in cs) h = Mix(h, c == null ? 0 : Convert.ToInt32(f_rawValue.GetValue(c)));
                        if (f_propertyLocalNames.GetValue(pe) is string[] pln) foreach (var s in pln) h = MixStr(h, s);
                    }
                }
            }
            return h;
        }
        catch { return int.MinValue; }
    }

    static int Mix(int h, int v) { unchecked { return (h * 397) ^ v; } }
    static int MixStr(int h, string s) => Mix(h, s?.GetHashCode() ?? 0);

    // ── RPN -> infix (same approach as DescriptorPropertyIndex.BuildFormula) ──
    static string BuildFormula(object pe)
    {
        var rpn = f_rpnStack?.GetValue(pe) as Array;
        var constants = f_constantStack?.GetValue(pe) as Array;
        var names = f_propertyLocalNames?.GetValue(pe) as string[];
        if (rpn == null || rpn.Length == 0)
            return (constants != null && constants.Length > 0)
                ? string.Join(", ", constants.Cast<object>().Select(FormatFixed)) : "0";

        var stack = new Stack<string>(); int propIdx = 0, constIdx = 0;
        foreach (var opObj in rpn)
        {
            int op = Convert.ToInt32(opObj);
            if (op == OP_GetConst)
                stack.Push(constants != null && constIdx < constants.Length ? FormatFixed(constants.GetValue(constIdx++)) : $"c{constIdx++}");
            else if (op == OP_GetTarget) stack.Push("Target." + NextName(names, ref propIdx));
            else if (op == OP_GetSource) stack.Push("Source." + NextName(names, ref propIdx));
            else if (op == OP_GetWorld) stack.Push("World." + NextName(names, ref propIdx));
            else if (op == OP_GetVariable) stack.Push("var:" + NextName(names, ref propIdx));
            else
            {
                if (stack.Count < 2) { stack.Push("?op" + op); continue; }
                string b = stack.Pop(), a = stack.Pop();
                stack.Push("(" + a + " " + BinSym(op) + " " + b + ")");
            }
        }
        string result = stack.Count > 0 ? stack.Peek() : "?";
        if (result.Length > 1 && result[0] == '(' && result[^1] == ')') result = result.Substring(1, result.Length - 2);
        return result;
    }

    static string NextName(string[] names, ref int idx) => names != null && idx < names.Length ? names[idx++] : $"p{idx++}";

    // Like BuildFormula, but resolves property names through PropertyMapper (display names) and
    // formats the leading constant with sign/percent per the PropertyMapper. Used both in the
    // preview panel and by the PropertyEffectDrawer for inline display.
    static string BuildResolvedFormula(object pe, int toTargetOp, string targetProperty)
    {
        var rpn = f_rpnStack?.GetValue(pe) as Array;
        var constants = f_constantStack?.GetValue(pe) as Array;
        var names = f_propertyLocalNames?.GetValue(pe) as string[];

        if (rpn == null || rpn.Length == 0)
        {
            if (constants != null && constants.Length > 0)
            {
                double value = RawToDouble(constants.GetValue(0));
                return FormatValueString(value, toTargetOp, targetProperty);
            }
            return "0";
        }

        var stack = new Stack<string>(); int propIdx = 0, constIdx = 0;
        foreach (var opObj in rpn)
        {
            int op = Convert.ToInt32(opObj);
            if (op == OP_GetConst)
            {
                double value = constants != null && constIdx < constants.Length ? RawToDouble(constants.GetValue(constIdx++)) : 0;
                stack.Push(FormatValueString(value, toTargetOp, targetProperty));
            }
            else if (op == OP_GetTarget || op == OP_GetSource || op == OP_GetWorld)
                stack.Push(ResolvePropertyLabel(NextName(names, ref propIdx)));
            else if (op == OP_GetVariable)
                stack.Push("var:" + NextName(names, ref propIdx));
            else
            {
                if (stack.Count < 2) { stack.Push("?op" + op); continue; }
                string b = stack.Pop(), a = stack.Pop();
                stack.Push("(" + a + " " + BinSymResolved(op) + " " + b + ")");
            }
        }
        string result = stack.Count > 0 ? stack.Peek() : "?";
        if (result.Length > 1 && result[0] == '(' && result[^1] == ')') result = result.Substring(1, result.Length - 2);
        return result;
    }

    static string BinSymResolved(int op)
    {
        if (op == OP_Add) return "+"; if (op == OP_Sub) return "-";
        if (op == OP_Mult) return "×"; if (op == OP_Div) return "÷";
        if (op == OP_Pow) return "^"; if (op == OP_Percent) return "% of";
        if (op == OP_Max) return "max"; if (op == OP_Min) return "min";
        return "op" + op;
    }

    static string BinSym(int op)
    {
        if (op == OP_Add) return "+"; if (op == OP_Sub) return "-";
        if (op == OP_Mult) return "*"; if (op == OP_Div) return "/";
        if (op == OP_Pow) return "^"; if (op == OP_Percent) return "percent";
        if (op == OP_Max) return "max"; if (op == OP_Min) return "min";
        return "op" + op;
    }

    static string FormatFixed(object fp)
    {
        if (f_rawValue == null || fp == null) return "0";
        long raw = Convert.ToInt64(f_rawValue.GetValue(fp));
        double v = (double)raw / s_oneRaw;
        return v == Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.####");
    }

    // ── External diagnostics entry point (used by InspectorDiagnostics) ────────
    // Reuses this file's already-resolved reflection + analysis to surface the crash-capable
    // Descriptor / DescriptorMapper problems — malformed RPN and wrong policy ParameterFlags —
    // WITHOUT building the (expensive) translation dictionary. It only reads structural fields.
    // severity: 2 = will crash load, 1 = quality warning. Safe no-op if reflection can't resolve
    // or the target isn't a Descriptor/DescriptorMapper.
    public static void CollectFindings(UnityEngine.Object target, Action<int, string, string> emit)
    {
        if (target == null || emit == null || !TryResolve()) return;

        bool isMapper = t_DescriptorMapper.IsInstanceOfType(target);
        bool isDescriptor = t_Descriptor.IsInstanceOfType(target);
        if (!isMapper && !isDescriptor) return;

        var descriptorObj = isDescriptor ? target : FindPairedAsset(t_Descriptor, target.name);
        var mapperObj = isMapper ? target : FindPairedAsset(t_DescriptorMapper, target.name);
        if (descriptorObj == null) return;   // nothing structural to check without Effects
        if (f_effects.GetValue(descriptorObj) is not Array effects) return;

        bool isSynergy = string.Equals(f_desc_category?.GetValue(descriptorObj) as string, "District_Synergy", StringComparison.Ordinal);

        for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
        {
            var effect = effects.GetValue(effectIndex);
            if (effect == null) continue;
            bool applyOnSource = f_applyOnSource != null && (bool)f_applyOnSource.GetValue(effect);
            if (f_propertyEffects.GetValue(effect) is not Array peArr) continue;

            for (int peIndex = 0; peIndex < peArr.Length; peIndex++)
            {
                var pe = peArr.GetValue(peIndex);
                if (pe == null) continue;
                string targetProperty = f_targetProperty.GetValue(pe) as string ?? "";
                string where = $"Effect[{effectIndex}].PropertyEffect[{peIndex}] ({targetProperty})";

                // 1) Malformed RPN (stack underflow from a trailing operator, or leftover operands).
                //    An underflowing RPN throws while SimulationController.Compile() runs — inside the
                //    reset gate — so it clears the mod list.
                string rpnError = ValidateRpn(pe);
                if (rpnError != null) { emit(2, $"{where}: {rpnError}", "DataController.cs:903 (RPN) / SimulationController.Compile"); continue; }

                // 2) Shape must be a recognised simple / one-factor / two-factor form to render at all.
                int toTargetOp = Convert.ToInt32(f_toTargetOp.GetValue(pe));
                var shape = AnalyzePropertyEffectShape(pe, toTargetOp);
                if (!shape.valid)
                {
                    emit(1, $"{where}: RPN isn't a recognised simple/one-factor/two-factor form — the runtime skips it and renders no tooltip row.", "SimulationEvaluatorHelper.FillPropertyEffectEvaluation");
                    continue;
                }

                // 3) Wrong DescriptorMapper policy ParameterFlags. If a policy declares flags the effect
                //    doesn't actually provide, the runtime logs "Specified set of Parameters is invalid"
                //    during effect translation (inside the reset gate).
                if (mapperObj == null) continue;
                object policy = FindPolicy(mapperObj, effectIndex, peIndex);
                if (policy == null) continue;
                int declaredFlags = Convert.ToInt32(f_pol_flags.GetValue(policy));
                if (declaredFlags == FLAG_None) continue;   // exotic literal / no flag override → no subset requirement

                PathInfo pathInfo; int pathFlags;
                if (isSynergy) { pathInfo = ClassifySynergyPath(effect, out _); pathFlags = string.IsNullOrEmpty(pathInfo.synergySource) ? 0 : FLAG_SynergySource; }
                else { pathInfo = ClassifyPath(effect, applyOnSource); pathFlags = ComputePathFlags(pathInfo); }
                int autoFlags = shape.propFlags | pathFlags;
                if ((declaredFlags & ~autoFlags) != 0)
                    emit(2, $"{where}: DescriptorMapper policy declares ParameterFlags '{DecodeFlags(declaredFlags)}' but the effect only provides '{DecodeFlags(autoFlags)}' — runtime logs \"Specified set of Parameters is invalid\".", "EffectTranslator (ParameterFlags subset)");
            }
        }
    }

    static object FindPolicy(UnityEngine.Object mapperObj, int effectIndex, int peIndex)
    {
        if (mapperObj == null || f_dm_policies.GetValue(mapperObj) is not Array policies) return null;
        foreach (var p in policies)
        {
            if (p == null) continue;
            if (Convert.ToInt32(f_pol_effectIndex.GetValue(p)) == effectIndex && Convert.ToInt32(f_pol_peIndex.GetValue(p)) == peIndex) return p;
        }
        return null;
    }

    // Simulates the RPN operand stack the way SimulationController compiles it: Get* ops push a value,
    // every other (binary) op pops two and pushes one. A binary op with <2 on the stack is the classic
    // "trailing operator" crash; a final stack size != 1 means a missing or extra operation. Returns a
    // human-readable reason, or null when the RPN is well-formed (or absent — a bare constant).
    static string ValidateRpn(object pe)
    {
        if (f_rpnStack.GetValue(pe) is not Array rpn || rpn.Length == 0) return null;
        int stack = 0;
        for (int i = 0; i < rpn.Length; i++)
        {
            int op = Convert.ToInt32(rpn.GetValue(i));
            if (op == OP_GetConst || op == OP_GetTarget || op == OP_GetSource || op == OP_GetWorld || op == OP_GetVariable)
                stack++;
            else
            {
                if (stack < 2) return $"malformed RPN — operation '{BinSym(op)}' at index {i} has only {stack} operand(s) on the stack (stack underflow → crash at Compile).";
                stack -= 1;
            }
        }
        if (stack != 1) return $"malformed RPN — {stack} value(s) left on the stack at the end (expected exactly 1; a missing or extra operation).";
        return null;
    }

    // ── Public API for PropertyEffectDrawer (inline in-game render) ────────
    // Resolves one PropertyEffect into a display-ready formula + warnings, fetching the
    // DescriptorMapper (from project or vanilla) for policy context. Accepts either a
    // Descriptor or a DescriptorMapper as the root (the paired asset is found by name).
    // Safe no-op if reflection isn't ready or the target isn't either type.
    public struct PropertyEffectResolution
    {
        public string rawFormula;           // infix with raw names: "3 * Target.Industry"
        public string resolvedFormula;      // infix with resolved names + formatted constant: "+3 × Industry"
        public string note;                 // PropertyEffect.Note (may be empty)
        public bool foundDescriptorMapper;
        public bool solveRPNFormula;
        public bool hidden;
        public List<string> warnings;
    }

    public static PropertyEffectResolution ResolvePropertyEffect(
        UnityEngine.Object rootObj, int effectIndex, int peIndex)
    {
        var r = new PropertyEffectResolution { warnings = new List<string>() };
        if (!TryResolve() || rootObj == null) return r;

        // Accept either a Descriptor or a DescriptorMapper; find the paired Descriptor.
        UnityEngine.Object descriptorObj, mapperObj;
        if (t_Descriptor != null && t_Descriptor.IsInstanceOfType(rootObj))
        {
            descriptorObj = rootObj;
            mapperObj = FindPairedAsset(t_DescriptorMapper, rootObj.name);
        }
        else if (t_DescriptorMapper != null && t_DescriptorMapper.IsInstanceOfType(rootObj))
        {
            mapperObj = rootObj;
            descriptorObj = FindPairedAsset(t_Descriptor, rootObj.name);
        }
        else return r;

        r.foundDescriptorMapper = mapperObj != null;

        if (descriptorObj == null)
        {
            r.warnings.Add("No paired Descriptor found — can't resolve Effects/PropertyEffects.");
            return r;
        }
        if (f_effects.GetValue(descriptorObj) is not Array effects) return r;
        if (effectIndex < 0 || effectIndex >= effects.Length) return r;
        var effect = effects.GetValue(effectIndex);
        if (effect == null) return r;
        if (f_propertyEffects.GetValue(effect) is not Array peArr) return r;
        if (peIndex < 0 || peIndex >= peArr.Length) return r;
        var pe = peArr.GetValue(peIndex);
        if (pe == null) return r;

        string targetProperty = f_targetProperty.GetValue(pe) as string ?? "";
        int toTargetOp = Convert.ToInt32(f_toTargetOp.GetValue(pe));
        r.rawFormula = BuildFormula(pe);
        r.resolvedFormula = BuildResolvedFormula(pe, toTargetOp, targetProperty);
        r.note = f_note?.GetValue(pe) as string ?? "";

        object policy = FindPolicy(mapperObj, effectIndex, peIndex);
        r.hidden = policy != null && (bool)f_pol_hide.GetValue(policy);
        r.solveRPNFormula = policy != null && f_pol_solveRpn != null && (bool)f_pol_solveRpn.GetValue(policy);

        // Warnings
        if (!r.foundDescriptorMapper)
            r.warnings.Add("No DescriptorMapper found (project or vanilla) — rows render with auto-computed parameters only.");
        if (r.hidden)
            r.warnings.Add("HidePropertyEffect = true — this PropertyEffect is never shown in tooltips.");
        if (r.solveRPNFormula)
            r.warnings.Add("SolveRPNFormula is ON — runtime evaluates this live against game state; the formula is shown as static.");

        string rpnError = ValidateRpn(pe);
        if (rpnError != null) r.warnings.Add(rpnError);
        else
        {
            var shape = AnalyzePropertyEffectShape(pe, toTargetOp);
            if (!shape.valid)
                r.warnings.Add("RPN isn't a recognised simple/one-factor/two-factor form — the runtime skips it and renders no tooltip row.");
        }

        if (mapperObj != null && policy != null)
        {
            int declaredFlags = Convert.ToInt32(f_pol_flags.GetValue(policy));
            if (declaredFlags != FLAG_None)
            {
                bool applyOnSource = f_applyOnSource != null && (bool)f_applyOnSource.GetValue(effect);
                bool isSynergy = string.Equals(f_desc_category?.GetValue(descriptorObj) as string, "District_Synergy", StringComparison.Ordinal);
                PathInfo pathInfo; int pathFlags;
                if (isSynergy) { pathInfo = ClassifySynergyPath(effect, out _); pathFlags = string.IsNullOrEmpty(pathInfo.synergySource) ? 0 : FLAG_SynergySource; }
                else { pathInfo = ClassifyPath(effect, applyOnSource); pathFlags = ComputePathFlags(pathInfo); }
                var shape = AnalyzePropertyEffectShape(pe, toTargetOp);
                int autoFlags = shape.propFlags | pathFlags;
                if ((declaredFlags & ~autoFlags) != 0)
                    r.warnings.Add($"DescriptorMapper policy declares ParameterFlags '{DecodeFlags(declaredFlags)}' but the effect only provides '{DecodeFlags(autoFlags)}' — runtime logs an error and falls back to auto.");
            }
        }

        return r;
    }
}
