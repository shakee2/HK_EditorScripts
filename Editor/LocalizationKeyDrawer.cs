using System;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector.Editor;
#endif

/// <summary>
/// Draws an inline translation editor directly below %key string fields on UIMapper and
/// DescriptorMapper assets — same Import-for-editing + TextArea flow as TechTreeWindow,
/// without opening the Mod Editor's Localization Window.
/// </summary>
#if ODIN_INSPECTOR
[DrawerPriority(DrawerPriorityLevel.WrapperPriority)]
public class LocalizationKeyStringDrawer : OdinValueDrawer<string>
{
    protected override bool CanDrawValueProperty(InspectorProperty property)
    {
        if (property == null) return false;
        var root = property.Tree?.WeakTargets != null && property.Tree.WeakTargets.Count > 0
            ? property.Tree.WeakTargets[0] as UnityEngine.Object
            : null;
        return InlineLocalizationEditor.AppliesTo(root);
    }

    protected override void DrawPropertyLayout(GUIContent label)
    {
        CallNextDrawer(label);
        string key = ValueEntry.SmartValue;
        if (string.IsNullOrEmpty(key) || key[0] != '%') return;
        bool multi = Property.Name.IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0
                     || Property.Name.IndexOf("EffectLocalization", StringComparison.OrdinalIgnoreCase) >= 0;
        InlineLocalizationEditor.DrawBelowField(key, multi);
    }
}
#else
// Fallback for projects without Odin: a PropertyDrawer on string fields can't be scoped to
// mapper types without a custom attribute, so inline loc editing is Odin-only here.
#endif
