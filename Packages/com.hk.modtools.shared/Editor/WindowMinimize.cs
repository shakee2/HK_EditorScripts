using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HK.ModTools.Shared
{
    /// <summary>
    /// Fake minimize for floating <see cref="EditorWindow"/>s: shrink to a fixed strip, park in the
    /// main editor's lower-right, and stack additional strips horizontally leftward. Paired windows
    /// (e.g. Compat Patcher + Compare) share one stack slot via sibling Types.
    /// Stack survives domain reload via <see cref="SessionState"/> (geometry alone is not enough —
    /// Unity keeps strip min/max while static state would otherwise be lost).
    /// </summary>
    [InitializeOnLoad]
    public static class WindowMinimize
    {
        const float StripW = 220f;
        const float StripH = 22f;
        const float Margin = 8f;
        const float Gap = 4f;
        /// <summary>
        /// Lift above <see cref="EditorGUIUtility.GetMainWindowPosition"/> bottom — that rect often
        /// extends under the Windows taskbar when Unity is maximized.
        /// </summary>
        const float TaskbarInset = 48f;
        const string SessionKey = "HK.ModTools.Shared.WindowMinimize.Stack";
        static readonly Vector2 DefaultRescueSize = new Vector2(900f, 560f);

        sealed class Group
        {
            public readonly HashSet<Type> Types = new HashSet<Type>();
            public readonly HashSet<int> MemberIds = new HashSet<int>();
            public int HostId;
            public Rect RestoredPosition;
            public Vector2 RestoredMinSize;
            public Vector2 RestoredMaxSize;
            public string Title;
        }

        [Serializable]
        class SavedStack
        {
            public SavedGroup[] groups = Array.Empty<SavedGroup>();
        }

        [Serializable]
        class SavedGroup
        {
            public string[] typeNames = Array.Empty<string>();
            public float rx, ry, rw, rh;
            public float minX, minY, maxX, maxY;
            public string title;
        }

        static readonly List<Group> s_stack = new List<Group>();
        static bool s_sessionLoaded;

        static WindowMinimize()
        {
            EditorApplication.delayCall += OnAfterDomainReload;
        }

        static void OnAfterDomainReload()
        {
            LoadSession();
            RebindLiveWindows();
            ReflowStack();
            RescueOrphanedStrips();
            RepaintStack();
        }

        /// <summary>True when this window (or a paired sibling type) is in the minimize stack.</summary>
        public static bool IsMinimized(EditorWindow w)
        {
            if (w == null) return false;
            EnsureSessionLoaded();
            return FindGroup(w) != null;
        }

        /// <summary>
        /// Compact toolbar control. Hidden when docked or already minimized — caller may also gate
        /// (e.g. Database Browser Window mode only).
        /// </summary>
        public static void DrawToolbarButton(EditorWindow w, params Type[] siblings)
        {
            if (w == null || w.docked) return;
            EnsureSessionLoaded();
            if (IsMinimized(w)) return;
            // Domain-reload orphan: strip-locked but not in stack — unlock so the window is usable.
            if (LooksStripLocked(w))
            {
                RescueWindow(w);
                return;
            }
            if (GUILayout.Button("Min", EditorStyles.toolbarButton, GUILayout.Width(36)))
                Minimize(w, siblings);
        }

        /// <summary>
        /// When minimized: draw the restore strip and return true (caller should return from OnGUI).
        /// Otherwise false. Pass the same sibling Types used for <see cref="DrawToolbarButton"/>.
        /// </summary>
        public static bool DrawMinimizedChrome(EditorWindow w, params Type[] siblings)
        {
            if (w == null) return false;
            EnsureSessionLoaded();
            EnsureJoined(w, siblings);
            var g = FindGroup(w);
            if (g == null)
            {
                if (LooksStripLocked(w)) RescueWindow(w);
                return false;
            }

            var r = new Rect(0, 0, w.position.width, StripH);
            EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin
                ? new Color(0.22f, 0.22f, 0.22f, 1f)
                : new Color(0.76f, 0.76f, 0.76f, 1f));

            string title = string.IsNullOrEmpty(g.Title) ? w.titleContent.text : g.Title;
            var labelRect = new Rect(6, 0, Mathf.Max(40f, r.width - 70f), StripH);
            GUI.Label(labelRect, title, EditorStyles.miniLabel);

            var btnRect = new Rect(r.xMax - 64f, 1f, 58f, StripH - 2f);
            if (GUI.Button(btnRect, "Restore", EditorStyles.miniButton))
            {
                Restore(w);
                return true;
            }

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)
                && !btnRect.Contains(e.mousePosition))
            {
                Restore(w);
                e.Use();
            }

            return true;
        }

        /// <summary>Restore even if the caller is switching modes (e.g. Database Browser → Only List).</summary>
        public static void ForceRestore(EditorWindow w)
        {
            if (w == null) return;
            EnsureSessionLoaded();
            if (!IsMinimized(w))
            {
                if (LooksStripLocked(w)) RescueWindow(w);
                return;
            }
            Restore(w);
        }

        static void Minimize(EditorWindow w, Type[] siblings)
        {
            if (w == null || w.docked || IsMinimized(w)) return;

            var types = CollectTypes(w, siblings);
            if (FindGroupByTypes(types) != null)
            {
                EnsureJoined(w, siblings);
                SaveSession();
                return;
            }

            var members = CollectMembers(types);
            if (members.Count == 0) members.Add(w);

            var host = w;
            var g = new Group
            {
                HostId = host.GetInstanceID(),
                RestoredPosition = host.position,
                RestoredMinSize = host.minSize,
                RestoredMaxSize = host.maxSize,
                Title = BuildTitle(members),
            };
            foreach (var t in types) g.Types.Add(t);
            foreach (var m in members)
            {
                g.MemberIds.Add(m.GetInstanceID());
                m.minSize = new Vector2(StripW, StripH);
                m.maxSize = new Vector2(StripW, StripH);
            }

            s_stack.Add(g);
            ReflowStack();
            SaveSession();
            foreach (var m in members) m.Repaint();
        }

        static void Restore(EditorWindow w)
        {
            var g = FindGroup(w);
            if (g == null) return;

            var host = FindWindowById(g.HostId) ?? w;
            foreach (var m in CollectMembers(g.Types))
            {
                m.minSize = g.RestoredMinSize;
                m.maxSize = g.RestoredMaxSize;
            }

            if (host != null)
                host.position = g.RestoredPosition;

            s_stack.Remove(g);
            ReflowStack();
            SaveSession();

            foreach (var m in CollectMembers(g.Types))
                m.Repaint();
        }

        static void EnsureJoined(EditorWindow w, Type[] siblings)
        {
            var types = CollectTypes(w, siblings);
            var g = FindGroupByTypes(types) ?? FindGroup(w);
            if (g == null) return;

            g.MemberIds.Add(w.GetInstanceID());
            foreach (var t in types) g.Types.Add(t);
            if (g.HostId == 0)
                g.HostId = w.GetInstanceID();
            w.minSize = new Vector2(StripW, StripH);
            w.maxSize = new Vector2(StripW, StripH);
        }

        static void ReflowStack()
        {
            for (int i = s_stack.Count - 1; i >= 0; i--)
            {
                var g = s_stack[i];
                if (FindAnyMember(g) == null) s_stack.RemoveAt(i);
            }

            if (s_stack.Count == 0) return;

            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float y = main.yMax - TaskbarInset - Margin - StripH;
            float xRight = main.xMax - Margin;

            for (int i = 0; i < s_stack.Count; i++)
            {
                float x = xRight - (s_stack.Count - i) * (StripW + Gap) + Gap;
                var host = FindWindowById(s_stack[i].HostId) ?? FindAnyMember(s_stack[i]);
                if (host == null) continue;
                s_stack[i].HostId = host.GetInstanceID();
                host.minSize = new Vector2(StripW, StripH);
                host.maxSize = new Vector2(StripW, StripH);
                host.position = new Rect(x, y, StripW, StripH);
            }
        }

        static void EnsureSessionLoaded()
        {
            if (s_sessionLoaded) return;
            LoadSession();
            RebindLiveWindows();
        }

        static void LoadSession()
        {
            s_stack.Clear();
            s_sessionLoaded = true;
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return;

            SavedStack saved;
            try { saved = JsonUtility.FromJson<SavedStack>(json); }
            catch { return; }
            if (saved?.groups == null) return;

            foreach (var sg in saved.groups)
            {
                if (sg?.typeNames == null || sg.typeNames.Length == 0) continue;
                var g = new Group
                {
                    RestoredPosition = new Rect(sg.rx, sg.ry, sg.rw, sg.rh),
                    RestoredMinSize = new Vector2(sg.minX, sg.minY),
                    RestoredMaxSize = new Vector2(sg.maxX, sg.maxY),
                    Title = sg.title ?? "",
                };
                foreach (var name in sg.typeNames)
                {
                    var t = FindType(name);
                    if (t != null) g.Types.Add(t);
                }
                if (g.Types.Count == 0) continue;
                if (g.RestoredMaxSize.x < 50f || g.RestoredMaxSize.y < 50f)
                    g.RestoredMaxSize = new Vector2(4000f, 4000f);
                if (g.RestoredPosition.width < 100f || g.RestoredPosition.height < 80f)
                {
                    Rect main = EditorGUIUtility.GetMainWindowPosition();
                    g.RestoredPosition = new Rect(
                        main.x + 80f, main.y + 80f, DefaultRescueSize.x, DefaultRescueSize.y);
                }
                s_stack.Add(g);
            }
        }

        static void SaveSession()
        {
            var saved = new SavedStack { groups = new SavedGroup[s_stack.Count] };
            for (int i = 0; i < s_stack.Count; i++)
            {
                var g = s_stack[i];
                var names = new List<string>(g.Types.Count);
                foreach (var t in g.Types)
                    if (t != null) names.Add(t.FullName);
                saved.groups[i] = new SavedGroup
                {
                    typeNames = names.ToArray(),
                    rx = g.RestoredPosition.x,
                    ry = g.RestoredPosition.y,
                    rw = g.RestoredPosition.width,
                    rh = g.RestoredPosition.height,
                    minX = g.RestoredMinSize.x,
                    minY = g.RestoredMinSize.y,
                    maxX = g.RestoredMaxSize.x,
                    maxY = g.RestoredMaxSize.y,
                    title = g.Title ?? "",
                };
            }
            SessionState.SetString(SessionKey, JsonUtility.ToJson(saved));
        }

        static void RebindLiveWindows()
        {
            foreach (var g in s_stack)
            {
                g.MemberIds.Clear();
                g.HostId = 0;
                foreach (var m in CollectMembers(g.Types))
                {
                    g.MemberIds.Add(m.GetInstanceID());
                    if (g.HostId == 0) g.HostId = m.GetInstanceID();
                }
            }
        }

        static void RescueOrphanedStrips()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w == null || !LooksStripLocked(w)) continue;
                if (FindGroup(w) != null) continue;
                RescueWindow(w);
            }
        }

        static bool LooksStripLocked(EditorWindow w)
        {
            if (w == null) return false;
            // min/max clamped to the strip, or still sitting at strip-sized geometry after a reload.
            if (w.maxSize.x <= StripW + 1f && w.maxSize.y <= StripH + 1f) return true;
            if (w.minSize.x >= StripW - 1f && w.minSize.y >= StripH - 1f
                && w.position.width <= StripW + 40f && w.position.height <= StripH + 40f)
                return true;
            return false;
        }

        static void RescueWindow(EditorWindow w)
        {
            if (w == null) return;
            w.minSize = new Vector2(200f, 120f);
            w.maxSize = new Vector2(4000f, 4000f);
            Rect p = w.position;
            if (p.width < 200f || p.height < 120f)
            {
                Rect main = EditorGUIUtility.GetMainWindowPosition();
                w.position = new Rect(
                    Mathf.Clamp(p.x, main.x, main.xMax - DefaultRescueSize.x),
                    Mathf.Clamp(p.y, main.y, main.yMax - DefaultRescueSize.y),
                    DefaultRescueSize.x,
                    DefaultRescueSize.y);
            }
            w.Repaint();
        }

        static void RepaintStack()
        {
            foreach (var g in s_stack)
                foreach (var m in CollectMembers(g.Types))
                    m.Repaint();
        }

        static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(fullName); }
                catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        static HashSet<Type> CollectTypes(EditorWindow w, Type[] siblings)
        {
            var types = new HashSet<Type> { w.GetType() };
            if (siblings == null) return types;
            foreach (var t in siblings)
                if (t != null && typeof(EditorWindow).IsAssignableFrom(t))
                    types.Add(t);
            return types;
        }

        static List<EditorWindow> CollectMembers(IEnumerable<Type> types)
        {
            var list = new List<EditorWindow>();
            var seen = new HashSet<int>();
            foreach (var t in types)
            {
                if (t == null) continue;
                foreach (var obj in Resources.FindObjectsOfTypeAll(t))
                {
                    if (obj is not EditorWindow ew || ew == null) continue;
                    int id = ew.GetInstanceID();
                    if (!seen.Add(id)) continue;
                    list.Add(ew);
                }
            }
            return list;
        }

        static Group FindGroup(EditorWindow w)
        {
            if (w == null) return null;
            int id = w.GetInstanceID();
            Type t = w.GetType();
            for (int i = 0; i < s_stack.Count; i++)
            {
                var g = s_stack[i];
                if (g.MemberIds.Contains(id) || g.Types.Contains(t))
                    return g;
            }
            return null;
        }

        static Group FindGroupByTypes(HashSet<Type> types)
        {
            for (int i = 0; i < s_stack.Count; i++)
            {
                var g = s_stack[i];
                foreach (var t in types)
                    if (g.Types.Contains(t)) return g;
            }
            return null;
        }

        static EditorWindow FindWindowById(int id)
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (w != null && w.GetInstanceID() == id) return w;
            return null;
        }

        static EditorWindow FindAnyMember(Group g)
        {
            var host = FindWindowById(g.HostId);
            if (host != null) return host;
            var members = CollectMembers(g.Types);
            return members.Count > 0 ? members[0] : null;
        }

        static string BuildTitle(List<EditorWindow> members)
        {
            if (members == null || members.Count == 0) return "Window";
            if (members.Count == 1) return members[0].titleContent.text;
            string a = members[0].titleContent.text;
            string b = members[1].titleContent.text;
            if (string.Equals(a, b, StringComparison.Ordinal)) return a;
            return a + " / " + b;
        }
    }
}
