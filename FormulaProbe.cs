using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class FormulaProbe2
{
    const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public   | BindingFlags.NonPublic;

    static FieldInfo Field(Type t, string name) =>
        t?.GetField(name, ALL);

    // Run with a descriptor CONTAINER asset selected (any sub-asset works).
    [MenuItem("Tools/Probe/Resolve Types + Test ToString")]
    static void Run()
    {
        var path = AssetDatabase.GetAssetPath(Selection.activeObject);
        if (string.IsNullOrEmpty(path)) { Debug.LogWarning("Select a descriptor asset."); return; }

        var descType = Type.GetType("Amplitude.Framework.Simulation.Description.Descriptor, " +
                                    "Amplitude.Mercury.Firstpass")
                        ?? FindType("Amplitude.Framework.Simulation.Description.Descriptor");
        if (descType == null) { Debug.LogError("Descriptor type not found."); return; }

        var sb = new StringBuilder();

        // Find a descriptor sub-asset that actually has effects
        object descriptorWithEffect = null;
        object firstEffect = null;
        object firstPropEffect = null;

        var f_effects = Field(descType, "Effects");

        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (obj == null || !descType.IsInstanceOfType(obj)) continue;
            var effects = f_effects.GetValue(obj) as Array;
            if (effects == null || effects.Length == 0) continue;

            var effectType = effects.GetValue(0).GetType();
            var f_propEffects = Field(effectType, "PropertyEffects");
            foreach (var eff in effects)
            {
                var pes = f_propEffects.GetValue(eff) as Array;
                if (pes != null && pes.Length > 0)
                {
                    descriptorWithEffect = obj;
                    firstEffect          = eff;
                    firstPropEffect      = pes.GetValue(0);
                    break;
                }
            }
            if (firstPropEffect != null) break;
        }

        if (firstPropEffect == null)
        {
            Debug.LogWarning("No descriptor with a PropertyEffect found in this asset.");
            return;
        }

        var peType  = firstPropEffect.GetType();
        var effType = firstEffect.GetType();

        sb.AppendLine($"Sample descriptor: {((UnityEngine.Object)descriptorWithEffect).name}\n");

        // ── Exact element types ────────────────────────────────────────────────
        var f_rpn      = Field(peType, "RpnOperationStack");
        var f_const    = Field(peType, "ConstantStack");
        var f_toTarget = Field(peType, "ToTargetOperation");

        Type rpnElem   = f_rpn?.FieldType.GetElementType();
        Type constElem = f_const?.FieldType.GetElementType();
        Type toTargetT = f_toTarget?.FieldType;

        sb.AppendLine("=== EXACT TYPES (derive from these, never hardcode) ===");
        sb.AppendLine($"RpnOperationStack element : {rpnElem?.FullName}  (enum={rpnElem?.IsEnum})");
        sb.AppendLine($"ConstantStack element     : {constElem?.FullName}");
        sb.AppendLine($"ToTargetOperation         : {toTargetT?.FullName}  (enum={toTargetT?.IsEnum})");

        // ── RPN enum values ────────────────────────────────────────────────────
        if (rpnElem != null && rpnElem.IsEnum)
        {
            sb.AppendLine($"\n=== {rpnElem.Name} values (THE RPN ENUM) ===");
            foreach (var n in Enum.GetNames(rpnElem))
                sb.AppendLine($"  {Convert.ToInt64(Enum.Parse(rpnElem, n)),4} = {n}");
        }

        // ── ToTargetOperation enum values ──────────────────────────────────────
        if (toTargetT != null && toTargetT.IsEnum)
        {
            sb.AppendLine($"\n=== {toTargetT.Name} values (ToTargetOperation) ===");
            foreach (var n in Enum.GetNames(toTargetT))
                sb.AppendLine($"  {Convert.ToInt64(Enum.Parse(toTargetT, n)),4} = {n}");
        }

        // ── FixedPoint fields (find the real RawValue) ─────────────────────────
        if (constElem != null)
        {
            sb.AppendLine($"\n=== {constElem.Name} fields ===");
            foreach (var fi in constElem.GetFields(ALL))
                sb.AppendLine($"  {fi.FieldType.Name} {fi.Name}");
        }

        // ── THE GOLD TEST: does ToString() render the formula? ─────────────────
        sb.AppendLine("\n=== ToString() output ===");
        try { sb.AppendLine($"PropertyEffect.ToString(): \"{firstPropEffect}\""); }
        catch (Exception e) { sb.AppendLine($"PropertyEffect.ToString() threw: {e.Message}"); }
        try { sb.AppendLine($"Effect.ToString():         \"{firstEffect}\""); }
        catch (Exception e) { sb.AppendLine($"Effect.ToString() threw: {e.Message}"); }

        Debug.Log(sb.ToString());
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }
}