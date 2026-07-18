using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// <see cref="AdvancedDropdown.Show"/> only assigns <c>minSize</c>. Unity clamps content height to
    /// <c>[minSize.y, maxSize.y]</c>; default <c>maxSize</c> is huge, so a long type list opens
    /// full-screen (and a naive post-shrink leaves it stuck at the top of the monitor).
    /// Cap height, then re-anchor under the button.
    /// </summary>
    static class AdvancedDropdownHeight
    {
        /// <summary>Search + header + ~22 rows — main-window type lists are long; compare stays smaller naturally.</summary>
        public const float DefaultMaxHeight = 460f;

        static readonly FieldInfo WindowField = typeof(AdvancedDropdown).GetField(
            "m_WindowInstance", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void ShowCapped(AdvancedDropdown dropdown, Rect buttonRect, float maxHeight = DefaultMaxHeight)
        {
            if (dropdown == null) return;

            // Capture screen anchor while GUI matrix is still valid (before ShowAsDropDown).
            Rect btnScreen = GUIUtility.GUIToScreenRect(buttonRect);

            dropdown.Show(buttonRect);
            if (WindowField?.GetValue(dropdown) is not EditorWindow win) return;

            float maxH = Mathf.Max(120f, maxHeight);
            win.maxSize = new Vector2(Mathf.Max(win.maxSize.x, 4000f), maxH);
            if (win.minSize.y > maxH)
                win.minSize = new Vector2(win.minSize.x, maxH);

            float w = Mathf.Max(buttonRect.width, win.minSize.x, 180f);
            float h = Mathf.Min(Mathf.Max(win.position.height, win.minSize.y), maxH);

            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float yBelow = btnScreen.yMax;
            float yAbove = btnScreen.y - h;
            float y = yBelow + h <= main.yMax - 4f ? yBelow
                    : yAbove >= main.yMin + 4f ? yAbove
                    : Mathf.Clamp(yBelow, main.yMin + 4f, Mathf.Max(main.yMin + 4f, main.yMax - h - 4f));

            win.position = new Rect(btnScreen.x, y, w, h);
        }
    }
}
