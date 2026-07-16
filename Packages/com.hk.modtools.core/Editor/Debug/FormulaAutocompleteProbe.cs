using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Amplitude.Framework.Simulation;              // Operation
using Amplitude.Framework.Editor.Simulation.Rpn;   // RpnTextCompiler, CompilerStatus
#endif

/// <summary>
/// Diagnostic probe for the PropertyEffect formula autocomplete. Tests the DATA path (entity types,
/// RpnTextCompiler.GetTypeFieldLabels, and a Parse/Validate/Compile round-trip) independent of the
/// inspector UI, so we can tell whether a failure is in the compiler wiring or purely in the field.
/// Select a Descriptor asset (its startingType is used as the source) and run the menu item.
/// </summary>
public static class FormulaAutocompleteProbe
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tools/shakee's Tools/Debug/Probes/Formula Compiler Probe", false, 141)]
    static void Run()
    {
        var sb = new StringBuilder();
#if !ODIN_INSPECTOR
        sb.AppendLine("ODIN_INSPECTOR not defined — autocomplete drawer is disabled.");
#else
        try
        {
            var sel = Selection.activeObject;
            sb.AppendLine("Selection: " + (sel != null ? sel.name + " (" + sel.GetType().Name + ")" : "none"));

            // 1) Source type from the selected Descriptor's startingType (assembly-qualified name).
            Type sourceType = null;
            var descType = FindType("Amplitude.Framework.Simulation.Description.Descriptor");
            if (sel != null && descType != null && descType.IsInstanceOfType(sel))
            {
                var aqn = descType.GetField("startingType", ALL)?.GetValue(sel) as string;
                sb.AppendLine("Descriptor.startingType = " + (aqn ?? "null"));
                if (!string.IsNullOrEmpty(aqn)) sourceType = Type.GetType(aqn);
            }

            // 2) Fallback: first known simulation entity type.
            var entityTypes = RpnTextCompiler.SimulationEntityTypes;
            sb.AppendLine("RpnTextCompiler.SimulationEntityTypes count = " + (entityTypes?.Length ?? -1));
            if (sourceType == null && entityTypes != null && entityTypes.Length > 0)
            {
                sourceType = entityTypes[0];
                sb.AppendLine("(no descriptor source; using SimulationEntityTypes[0])");
            }
            sb.AppendLine("sourceType = " + (sourceType?.FullName ?? "NULL"));

            // 3) The autocomplete data source.
            string[] labels = sourceType != null ? RpnTextCompiler.GetTypeFieldLabels(sourceType) : null;
            sb.AppendLine("GetTypeFieldLabels(sourceType) count = " + (labels?.Length ?? -1));
            if (labels != null && labels.Length > 0)
                sb.AppendLine("  first: " + string.Join(", ", labels.Take(15)));

            // 4) Full round-trip: Parse -> Validate -> Compile.
            if (sourceType != null && labels != null && labels.Length > 0)
            {
                var c = new RpnTextCompiler();
                c.SetSourceType(sourceType);
                c.SetTargetType(sourceType);
                string formula = "1 + Source." + labels[0];
                var ps = c.Parse(formula);
                var vs = c.Validate();
                sb.AppendLine($"Parse(\"{formula}\") = {ps}");
                sb.AppendLine($"Validate() = {vs}   Status = {c.Status}   Msg = {c.StatusMessage}");
                if (ps == CompilerStatus.Ok && vs == CompilerStatus.Ok)
                {
                    Type s = sourceType, t = sourceType;
                    Operation[] rpn = null; Amplitude.FixedPoint[] cst = null; string[] props = null, vars = null;
                    c.Compile(ref s, ref t, ref rpn, ref cst, ref props, ref vars);
                    sb.AppendLine("Compiled RPN     = " + string.Join(", ", (rpn ?? Array.Empty<Operation>()).Select(o => o.ToString())));
                    sb.AppendLine("Compiled props   = " + string.Join(", ", props ?? Array.Empty<string>()));
                    sb.AppendLine("Compiled consts  = " + ((cst?.Length ?? 0) + " item(s)"));
                    sb.AppendLine("Decompile back   = " + c.Decompile(sourceType, sourceType, rpn, cst, props, null, null) + " -> \"" + c.ControlValue + "\"");
                }
            }
        }
        catch (Exception e) { sb.AppendLine("PROBE THREW: " + e); }
#endif
        Debug.Log("[FormulaAutocompleteProbe]\n" + sb);
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        { try { var t = asm.GetType(fullName); if (t != null) return t; } catch { } }
        return null;
    }
}
