using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Hosts the two inspector analysis panels — DescriptorMapperPreview ("Tooltip Breakdown Preview")
/// and InspectorDiagnostics — inside ONE height-capped, scrollable container drawn under the
/// datatable-element header (Editor.finishedDefaultHeaderGUI).
///
/// Previously each panel subscribed to that seam itself and grew unbounded, shoving the inspected
/// asset's own body far down the inspector. Both now expose WillDraw/Draw and this coordinator owns
/// the single hook and the single scroll view, so a Descriptor with many effects + many diagnostics
/// stays a fixed-height, scrollable block instead of pushing everything below it off-screen.
///
/// The scroll-vs-inline choice for a frame comes from the container height MEASURED on the previous
/// Repaint (the BeginVertical group rect is only valid then), keyed per target. So it is fixed for the
/// whole of a given frame's Layout+Repaint pass — no IMGUI control-count divergence, which both panels'
/// caches are carefully built around — and self-corrects the next frame. A short element (a couple of
/// diagnostics, no preview) draws inline; a tall one caps and scrolls. No per-panel pixel estimation to
/// drift. A little hysteresis keeps it from flip-flopping when content sits right at the cap (adding the
/// scrollbar narrows the content and re-wraps text, which would otherwise nudge the height back down).
/// </summary>
[InitializeOnLoad]
public static class InspectorAnalysisPanel
{
    const float MaxHeight = 340f;    // cap for the combined panel; taller content scrolls inside
    const float Hysteresis = 28f;    // once scrolling, keep scrolling until content drops this far under the cap

    static readonly Dictionary<int, float> s_contentHeight = new();   // per-target measured content height
    static readonly Dictionary<int, Vector2> s_scroll = new();        // per-target scroll offset
    static readonly HashSet<int> s_scrolled = new();                  // targets currently in scroll mode (hysteresis)

    static InspectorAnalysisPanel()
    {
        Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
    }

    static void OnHeaderGUI(Editor editor)
    {
        if (editor == null || editor.targets == null || editor.targets.Length != 1) return;
        var target = editor.target;
        if (target == null) return;

        bool willPreview = DescriptorMapperPreview.WillDraw(editor);
        bool willDiag = InspectorDiagnostics.WillDraw(editor);
        if (!willPreview && !willDiag) return;

        int id = target.GetInstanceID();
        float last = s_contentHeight.TryGetValue(id, out var h) ? h : 0f;
        float threshold = s_scrolled.Contains(id) ? MaxHeight - Hysteresis : MaxHeight;
        bool scroll = last > threshold;

        if (scroll)
        {
            var pos = s_scroll.TryGetValue(id, out var v) ? v : Vector2.zero;
            s_scroll[id] = EditorGUILayout.BeginScrollView(pos, GUILayout.Height(MaxHeight));
        }

        // The group rect measures the true content height (inside the scroll viewport when scrolling),
        // which is what next frame compares against the cap.
        Rect group = EditorGUILayout.BeginVertical();
        if (willPreview) DescriptorMapperPreview.Draw(editor);
        if (willDiag) InspectorDiagnostics.Draw(editor);
        EditorGUILayout.EndVertical();

        if (scroll) EditorGUILayout.EndScrollView();

        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            if (s_contentHeight.Count > 256) { s_contentHeight.Clear(); s_scrolled.Clear(); }  // bound long sessions
            s_contentHeight[id] = group.height;
            if (scroll) s_scrolled.Add(id); else s_scrolled.Remove(id);
        }
    }
}
