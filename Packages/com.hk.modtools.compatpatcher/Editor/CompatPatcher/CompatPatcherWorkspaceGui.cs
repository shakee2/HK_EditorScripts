using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Post-Compare workspace: hazard cards, expandable type tree, frequency-ranked pattern cards.
    /// Partial of <see cref="CompatPatcherWindow"/>.
    /// </summary>
    public partial class CompatPatcherWindow
    {
        void DrawPostCompareChrome()
        {
            EditorGUILayout.LabelField(
                "Expand a type → click an element to replace patterns with full-element Compare. Patterns are for frequency + mass-apply.",
                EditorStyles.miniLabel);
            DrawHazardCards();
        }

        /// <summary>
        /// Status toolbar, extra filter buttons, stats, and name search above the type list.
        /// Rebuilds when filters change (or when <see cref="_viewDirty"/> was set elsewhere).
        /// Name text also drives expand/collapse.
        /// </summary>
        void DrawListFilters()
        {
            var prevStatus = _status;
            var prevNeeds = _needsReviewOnly;
            var prevHide = _hideWinnerOnly;
            var prevName = _nameFilter;

            _status = (StatusFilter)GUILayout.Toolbar((int)_status, STATUS_LABELS);

            EditorGUILayout.BeginHorizontal();
            _needsReviewOnly = DrawFilterToggleButton(_needsReviewOnly, "needs review",
                "Only elements that still need review (unresolved conflicts).");
            _hideWinnerOnly = DrawFilterToggleButton(_hideWinnerOnly, "hide winner-only",
                "Hide conflicts that only add winner-side rows (not actionable).");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            DrawStatsLine();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                _typeSort == TypeSortMode.NameAsc ? "Types (A–Z)" : "Types (by diff load)",
                EditorStyles.boldLabel);
            string sortTip = _typeSort == TypeSortMode.NameAsc
                ? "Currently A–Z. Click for diff count descending."
                : "Currently diff descending. Click for A–Z.";
            string sortLabel = _typeSort == TypeSortMode.NameAsc ? "A–Z" : "Diff ↓";
            if (GUILayout.Button(new GUIContent(sortLabel, sortTip), EditorStyles.miniButton, GUILayout.Width(52)))
            {
                _typeSort = _typeSort == TypeSortMode.NameAsc
                    ? TypeSortMode.DiffDesc
                    : TypeSortMode.NameAsc;
                EditorPrefs.SetInt(PrefTypeSort, (int)_typeSort);
                SortTypeGroups();
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _nameFilter = EditorGUILayout.TextField(_nameFilter);
            if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(18)) && _nameFilter.Length > 0)
            {
                _nameFilter = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            bool nameChanged = prevName != _nameFilter;
            if (_viewDirty || prevStatus != _status || prevNeeds != _needsReviewOnly
                || prevHide != _hideWinnerOnly || nameChanged)
            {
                ApplyListFilters();
                if (nameChanged)
                {
                    if (string.IsNullOrWhiteSpace(_nameFilter))
                        _expandedTypes.Clear();
                    else
                    {
                        foreach (var g in _typeGroups)
                            _expandedTypes.Add(g.TypeName);
                    }
                }
            }
        }

        /// <summary>
        /// Toggle as a mini button: highlighted when on, muted when off.
        /// Same style + fixed width in both states so enabling does not shift/clip the row.
        /// </summary>
        static bool DrawFilterToggleButton(bool on, string label, string tooltip)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = on
                ? new Color(0.45f, 0.70f, 1f, 1f)
                : new Color(0.55f, 0.55f, 0.55f, 1f);
            bool clicked = GUILayout.Button(
                new GUIContent(label, tooltip),
                EditorStyles.miniButton,
                GUILayout.Width(110f),
                GUILayout.Height(18f));
            GUI.backgroundColor = prev;
            return clicked ? !on : on;
        }

        void ApplyListFilters()
        {
            ApplyFilter();
            RebuildTypeGroups();
            _patternsDirty = true;
            _viewDirty = false;
        }

        void DrawStatsLine()
        {
            if (_result == null) return;
            var s = _result.Stats;
            int need = _elemStatus.Values.Count(v => v == "new" || v == "changed");
            int resolved = _elemStatus.Values.Count(v => v == "resolved");
            int orphans = _patchOrphans.Count;
            int errors = _findings.Count(f => f.Severity == FindingSeverity.Error);
            EditorGUILayout.LabelField(
                $"conflicts {s.Conflicts} (needs review {need}, resolved {resolved}, odin {s.OdinConflicts}) · new {s.New} · identical {s.Identical} · showing {_view.Count}"
                + (orphans > 0 ? $" · patch orphans {orphans}" : "")
                + (errors > 0 || _findings.Count > 0 ? $" · validation {_findings.Count}" : ""),
                EditorStyles.miniLabel);
        }

        void DrawHazardCards()
        {
            DrawValidationCard();
            DrawOrphansCard();
        }

        void DrawValidationCard()
        {
            int errors = _findings.Count(f => f.Severity == FindingSeverity.Error);
            int warns = _findings.Count - errors;
            string title = _findings.Count == 0 && string.IsNullOrEmpty(_validationNote)
                ? "Load-order validation — clean"
                : $"Load-order validation — {errors} error(s), {warns} warning(s)";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool open = EditorGUILayout.Foldout(_showValidation, title, true);
            if (open != _showValidation)
            {
                _showValidation = open;
                EditorPrefs.SetBool(PrefShowValidation, _showValidation);
            }
            if (_showValidation)
            {
                _validationCardScroll = EditorGUILayout.BeginScrollView(_validationCardScroll, GUILayout.MaxHeight(160));
                DrawValidationBody();
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        void DrawValidationBody()
        {
            if (!string.IsNullOrEmpty(_validationNote))
                EditorGUILayout.HelpBox(_validationNote, MessageType.Warning);

            var altByKey = _altFindings.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());
            var goneInReverse = _findings.Where(f => !altByKey.ContainsKey(f.Key)).ToList();
            var detailChangedInReverse = _findings.Where(f => altByKey.TryGetValue(f.Key, out var a) && a.Detail != f.Detail).ToList();
            var introducedInReverse = _altFindings.Where(f => !_findings.Any(x => x.Key == f.Key)).ToList();
            var orderSensitiveKeys = new HashSet<string>(
                goneInReverse.Select(f => f.Key)
                .Concat(detailChangedInReverse.Select(f => f.Key))
                .Concat(introducedInReverse.Select(f => f.Key)));

            if (_findings.Count == 0 && introducedInReverse.Count == 0)
            {
                EditorGUILayout.HelpBox("No known load-time hazards detected in this order.", MessageType.Info);
                return;
            }

            foreach (var f in _findings.OrderBy(f => f.Severity).ThenBy(f => f.Element, StringComparer.OrdinalIgnoreCase))
            {
                bool orderSensitive = orderSensitiveKeys.Contains(f.Key);
                EditorGUILayout.HelpBox(f.Line + (orderSensitive ? "  [order-sensitive]" : ""),
                    f.Severity == FindingSeverity.Error ? MessageType.Error : MessageType.Warning);
            }

            if (orderSensitiveKeys.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("Load order matters here. Under the reverse order (").Append(_altOrderName).Append(") ");
                if (goneInReverse.Count > 0) sb.Append(goneInReverse.Count).Append(" of these would not occur; ");
                if (detailChangedInReverse.Count > 0) sb.Append(detailChangedInReverse.Count).Append(" resolve differently; ");
                if (introducedInReverse.Count > 0) sb.Append(introducedInReverse.Count).Append(" new hazards would appear; ");
                sb.Append("Reorder with ▲▼ above and press Compare again to re-validate.");
                EditorGUILayout.HelpBox(sb.ToString(), MessageType.Warning);
            }
        }

        void DrawOrphansCard()
        {
            int n = _patchOrphans.Count;
            string title = n == 0
                ? "Patch orphans — none"
                : $"Patch orphans — {n} (Patch/ overrides that no longer match a live conflict)";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool open = EditorGUILayout.Foldout(_showPatchOrphans, title, true);
            if (open != _showPatchOrphans)
            {
                _showPatchOrphans = open;
                EditorPrefs.SetBool(PrefShowOrphans, _showPatchOrphans);
            }
            if (_showPatchOrphans)
            {
                _orphanCardScroll = EditorGUILayout.BeginScrollView(_orphanCardScroll, GUILayout.MaxHeight(140));
                if (n == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Every element in Assets/Databases/Patch/ either still conflicts across the compared mods, or Patch/ is empty.",
                        MessageType.None);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "Gone = dropped by all mods. Sole = only one mod left. Identical = mods agree now.",
                        EditorStyles.miniLabel);
                    PatchOrphan toRemove = null;
                    foreach (var o in _patchOrphans.OrderBy(x => x.Kind).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"[{KindLabel(o.Kind)}]  {o.Entry.Name}  ({o.Entry.TypeHint})", GUILayout.MinWidth(240));
                        EditorGUILayout.LabelField(o.Detail, EditorStyles.miniLabel);
                        if (GUILayout.Button("Compare", GUILayout.Width(72)))
                            OpenCompareOrphan(o);
                        if (GUILayout.Button("Ping", GUILayout.Width(44)))
                            PingPatchEntry(o.Entry);
                        if (GUILayout.Button("Remove", GUILayout.Width(64)))
                            toRemove = o;
                        EditorGUILayout.EndHorizontal();
                    }
                    if (toRemove != null)
                        RemovePatchOrphan(toRemove);
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        void DrawWorkspace(Rect rest)
        {
            const float gap = 4f;
            float listW = Mathf.Clamp(_typeListWidth, 180f, Mathf.Max(180f, rest.width - 220f));
            Rect listRect = new Rect(rest.x, rest.y, listW, rest.height);
            Rect splitRect = new Rect(listRect.xMax, rest.y, gap + 2f, rest.height);
            Rect patternRect = new Rect(splitRect.xMax, rest.y, Mathf.Max(80f, rest.width - listW - gap - 2f), rest.height);

            DrawTypeTree(listRect);
            DrawTypeSplitter(splitRect, rest);
            if (_rightPaneCompare && _hostedCompare != null)
                DrawEmbeddedComparePane(patternRect);
            else
                DrawPatternPane(patternRect);
        }

        void DrawEmbeddedComparePane(Rect rect)
        {
            // No chrome strip — type list / SelectType returns to patterns.
            _hostedCompare.DrawEmbedded(rect);
        }

        void ExitEmbeddedCompare()
        {
            if (_hostedCompare != null)
            {
                _hostedCompare.DisposeHosted();
                _hostedCompare = null;
            }
            _rightPaneCompare = false;
            _selectedElementName = "";
            Repaint();
        }

        void DrawTypeSplitter(Rect rect, Rect rest)
        {
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
                _draggingTypeSplit = true;
            if (_draggingTypeSplit)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _typeListWidth = Mathf.Clamp(_typeListWidth + e.delta.x, 180f, rest.width - 220f);
                    EditorPrefs.SetFloat(PrefTypeListWidth, _typeListWidth);
                    Repaint();
                }
                if (e.type == EventType.MouseUp)
                    _draggingTypeSplit = false;
            }
        }

        void RebuildTypeGroups()
        {
            _typeGroups.Clear();
            var map = new Dictionary<string, TypeGroup>(StringComparer.Ordinal);
            foreach (var r in _view)
            {
                string t = FriendlyType(r);
                if (string.IsNullOrEmpty(t)) t = "(unknown type)";
                if (!map.TryGetValue(t, out var g))
                {
                    g = new TypeGroup { TypeName = t };
                    map[t] = g;
                }
                g.Rows.Add(r);
                if (r.Conflict?.Diffs != null)
                    g.DiffCount += r.Conflict.Diffs.Count;
            }
            _typeGroups.AddRange(map.Values);
            SortTypeGroups();

            if (_typeGroups.Count == 0)
            {
                _selectedType = "";
                _patternCandidates = new List<MassChange.Candidate>();
                return;
            }
            if (string.IsNullOrEmpty(_selectedType) || !_typeGroups.Any(g => g.TypeName == _selectedType))
                _selectedType = _typeGroups[0].TypeName;
            _patternsDirty = true;
        }

        void SortTypeGroups()
        {
            if (_typeSort == TypeSortMode.NameAsc)
            {
                _typeGroups.Sort((a, b) =>
                    string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                _typeGroups.Sort((a, b) =>
                {
                    int c = b.DiffCount.CompareTo(a.DiffCount);
                    return c != 0 ? c
                        : string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        void EnsurePatterns()
        {
            if (!_patternsDirty) return;
            _patternsDirty = false;
            var g = _typeGroups.FirstOrDefault(x => x.TypeName == _selectedType);
            _patternCandidates = g == null
                ? new List<MassChange.Candidate>()
                : MassChange.ComputeCandidates(g.Rows);
        }

        static void EnsureSortedRows(TypeGroup g)
        {
            if (g == null) return;
            if (g.SortedRows != null && g.SortedRows.Count == g.Rows.Count) return;
            g.SortedRows = g.Rows
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static GUIStyle _typeHeaderName;
        static GUIStyle _typeHeaderNameSelected;
        static GUIStyle _typeHeaderStats;
        static GUIStyle _typeHeaderStatsSelected;

        static GUIStyle TypeHeaderName => _typeHeaderName ?? (_typeHeaderName = MakeTypeHeaderStyle(EditorStyles.label, TextAnchor.MiddleLeft));
        static GUIStyle TypeHeaderNameSelected => _typeHeaderNameSelected ?? (_typeHeaderNameSelected = MakeTypeHeaderStyle(EditorStyles.boldLabel, TextAnchor.MiddleLeft));
        static GUIStyle TypeHeaderStats => _typeHeaderStats ?? (_typeHeaderStats = MakeTypeHeaderStyle(EditorStyles.miniLabel, TextAnchor.MiddleRight));
        static GUIStyle TypeHeaderStatsSelected => _typeHeaderStatsSelected ?? (_typeHeaderStatsSelected = MakeTypeHeaderStyle(EditorStyles.miniBoldLabel, TextAnchor.MiddleRight));

        static GUIStyle MakeTypeHeaderStyle(GUIStyle basis, TextAnchor align)
        {
            var s = new GUIStyle(basis)
            {
                alignment = align,
                clipping = TextClipping.Clip,
                // EditorStyles.* carry asymmetric padding that sits text low in a fixed row.
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            return s;
        }

        void DrawTypeTree(Rect rect)
        {
            GUILayout.BeginArea(rect);
            DrawListFilters();

            _typeListScroll = EditorGUILayout.BeginScrollView(_typeListScroll);

            if (_typeGroups.Count == 0)
                EditorGUILayout.HelpBox("No elements match the current filters.", MessageType.None);

            foreach (var g in _typeGroups)
            {
                bool selected = g.TypeName == _selectedType;
                bool expanded = _expandedTypes.Contains(g.TypeName);

                Color prevBg = GUI.backgroundColor;
                GUI.backgroundColor = selected
                    ? new Color(0.55f, 0.72f, 1f, 1f)
                    : new Color(0.85f, 0.85f, 0.85f, 1f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.backgroundColor = prevBg;

                bool nowExp = DrawTypeCardHeader(g, selected, expanded);

                if (nowExp && _expandSettleType != g.TypeName)
                {
                    EnsureSortedRows(g);
                    int total = g.SortedRows.Count;
                    const float step = EL_ROW_H + 2f;
                    float viewH = Mathf.Min(TYPE_ELEM_MAX_H, total * step + 2f);

                    // Manual scroll view + windowed draw: only the visible rows are emitted, so a
                    // type with hundreds of elements costs the same as a dozen on-screen ones.
                    Rect inner = GUILayoutUtility.GetRect(10f, viewH, GUILayout.ExpandWidth(true));
                    Rect content = new Rect(0f, 0f, inner.width - 16f, total * step);
                    if (!_typeElemScroll.TryGetValue(g.TypeName, out var scroll))
                        scroll = Vector2.zero;
                    scroll = GUI.BeginScrollView(inner, scroll, content);

                    int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / step));
                    int last = Mathf.Min(total, Mathf.CeilToInt((scroll.y + inner.height) / step) + 1);
                    for (int i = first; i < last; i++)
                    {
                        Rect rr = new Rect(2f, i * step, content.width - 4f, EL_ROW_H);
                        DrawElementCard(g, g.SortedRows[i], i, rr);
                    }

                    GUI.EndScrollView();
                    _typeElemScroll[g.TypeName] = scroll;

                    int approxVisible = Mathf.Max(1, Mathf.FloorToInt(viewH / step));
                    if (total > approxVisible)
                    {
                        EditorGUILayout.LabelField(
                            $"{total - approxVisible} more — scroll within the card",
                            EditorStyles.centeredGreyMiniLabel);
                    }
                }

                EditorGUILayout.EndVertical();

                // helpBox padding / empty chrome: still select the type (child controls Use() first).
                Rect cardRect = GUILayoutUtility.GetLastRect();
                var cardEv = Event.current;
                if (cardEv.type == EventType.MouseDown && cardEv.button == 0
                    && cardRect.Contains(cardEv.mousePosition))
                {
                    SelectType(g.TypeName);
                    cardEv.Use();
                }

                EditorGUILayout.Space(5f);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Absolute-layout header: vertically centered labels; whole row (minus fold / Resolve) selects.
        /// </summary>
        bool DrawTypeCardHeader(TypeGroup g, bool selected, bool expanded)
        {
            const float headerH = 22f;
            const float foldW = 16f;
            Rect row = GUILayoutUtility.GetRect(10f, headerH, GUILayout.ExpandWidth(true));

            int pending = expanded ? CountTypePendingResolve(g) : 0;
            float resolveW = expanded ? (pending > 0 ? 100f : 72f) : 0f;
            const float resolveGap = 4f;

            Rect foldRect = new Rect(row.x, row.y, foldW, row.height);
            Rect resolveRect = resolveW > 0f
                ? new Rect(row.xMax - resolveW, row.y + (row.height - 18f) * 0.5f, resolveW, 18f)
                : default;
            Rect hitRect = new Rect(
                row.x + foldW,
                row.y,
                Mathf.Max(0f, row.width - foldW - (resolveW > 0f ? resolveW + resolveGap : 0f)),
                row.height);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && hitRect.Contains(e.mousePosition))
            {
                SelectType(g.TypeName);
                if (e.clickCount >= 2)
                    SetTypeExpanded(g.TypeName, !expanded);
                e.Use();
            }

            bool nowExp = EditorGUI.Foldout(foldRect, expanded, GUIContent.none, true);
            if (nowExp != expanded)
                SetTypeExpanded(g.TypeName, nowExp);

            if (g.StatsLabel == null)
                g.StatsLabel = $"{g.DiffCount} diffs · {g.Rows.Count} els";
            if (g.StatsWidth < 0f)
                // Measure with the (wider) bold style so the right-aligned rect never clips when selected.
                g.StatsWidth = Mathf.Ceil(TypeHeaderStatsSelected.CalcSize(new GUIContent(g.StatsLabel)).x) + 2f;
            string stats = g.StatsLabel;
            var nameStyle = selected ? TypeHeaderNameSelected : TypeHeaderName;
            var statsStyle = selected ? TypeHeaderStatsSelected : TypeHeaderStats;
            float statsW = g.StatsWidth;
            Rect statsRect = new Rect(hitRect.xMax - statsW, hitRect.y, statsW, hitRect.height);
            Rect nameRect = new Rect(
                hitRect.x + 2f, hitRect.y,
                Mathf.Max(40f, statsRect.x - hitRect.x - 8f), hitRect.height);

            GUI.Label(nameRect, g.TypeName, nameStyle);
            GUI.Label(statsRect, stats, statsStyle);

            if (expanded)
            {
                using (new EditorGUI.DisabledScope(pending == 0))
                {
                    if (GUI.Button(resolveRect,
                            pending > 0 ? $"Resolve all ({pending})" : "Resolve all",
                            EditorStyles.miniButton))
                    {
                        ResolveTypeAsWinner(g);
                        Event.current.Use();
                    }
                }
            }

            return _expandedTypes.Contains(g.TypeName);
        }

        void SetTypeExpanded(string typeName, bool expand)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            if (expand)
            {
                if (!_expandedTypes.Add(typeName)) return;
                _expandSettleType = typeName;
                int gen = ++_expandSettleGen;
                EditorApplication.delayCall += () =>
                {
                    if (this == null || gen != _expandSettleGen) return;
                    if (_expandSettleType == typeName)
                        _expandSettleType = null;
                    Repaint();
                };
            }
            else
            {
                _expandedTypes.Remove(typeName);
                if (_expandSettleType == typeName)
                    _expandSettleType = null;
            }
        }

        void DrawElementCard(TypeGroup g, ElementRow row, int index, Rect rowRect)
        {
            string label = _unlockIndex != null
                ? _unlockIndex.FormatElementLabel(row.Name)
                : row.Name;

            bool elSelected = _rightPaneCompare
                && string.Equals(_selectedElementName, row.Name, StringComparison.Ordinal)
                && string.Equals(_selectedType, g.TypeName, StringComparison.Ordinal);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rowRect.Contains(e.mousePosition))
            {
                OpenCompareForType(g.TypeName, row);
                e.Use();
            }

            EditorGUI.DrawRect(rowRect, elSelected ? ROW_SEL : EL_CARD_BG);
            if (!elSelected && (index % 2) == 1)
                EditorGUI.DrawRect(rowRect, ROW_ALT);

            Color dot = ElementStatusDot(row);
            var dotRect = new Rect(rowRect.x + 6f, rowRect.y + (rowRect.height - 8f) * 0.5f, 8f, 8f);
            DrawStatusDot(dotRect, dot);

            var labelRect = new Rect(rowRect.x + 20f, rowRect.y, rowRect.width - 24f, rowRect.height);
            var style = elSelected ? EditorStyles.boldLabel : EditorStyles.label;
            // Zero padding so the label sits vertically centered in the row rect.
            if (_elRowLabel == null || _elRowLabelBold == null)
            {
                _elRowLabel = MakeTypeHeaderStyle(EditorStyles.label, TextAnchor.MiddleLeft);
                _elRowLabelBold = MakeTypeHeaderStyle(EditorStyles.boldLabel, TextAnchor.MiddleLeft);
            }
            GUI.Label(labelRect, label, elSelected ? _elRowLabelBold : _elRowLabel);
        }

        static GUIStyle _elRowLabel;
        static GUIStyle _elRowLabelBold;

        Color ElementStatusDot(ElementRow row)
        {
            if (row == null) return DOT_OTHER;
            if (_patchNames.Contains(row.Name)) return DOT_PATCH;
            if (IsResolved(row)) return DOT_RESOLVED;
            if (NeedsReview(row) || row.Status == ElemStatus.Conflict) return DOT_REVIEW;
            return DOT_OTHER;
        }

        static void DrawStatusDot(Rect r, Color c)
        {
            // Soft square reads as a “dot” at 8px; cheaper than a mesh circle in IMGUI.
            EditorGUI.DrawRect(r, c);
            var edge = new Color(0f, 0f, 0f, 0.35f);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), edge);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), edge);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), edge);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), edge);
        }

        int CountTypePendingResolve(TypeGroup g)
        {
            if (g?.Rows == null) return 0;
            if (g.PendingResolve >= 0) return g.PendingResolve;
            int n = 0;
            foreach (var r in g.Rows)
            {
                if (r.Status != ElemStatus.Conflict) continue;
                if (_patchNames.Contains(r.Name)) continue;
                if (IsResolved(r)) continue;
                if (!NeedsReview(r)) continue;
                n++;
            }
            g.PendingResolve = n;
            return n;
        }

        void ResolveTypeAsWinner(TypeGroup g)
        {
            if (g?.Rows == null || g.Rows.Count == 0) return;
            var pending = g.Rows.Where(r =>
                r.Status == ElemStatus.Conflict
                && !_patchNames.Contains(r.Name)
                && !IsResolved(r)
                && NeedsReview(r)).ToList();
            if (pending.Count == 0) return;
            if (!EditorUtility.DisplayDialog("Resolve all as winner",
                $"Mark {pending.Count} conflict(s) in {g.TypeName} as resolved, accepting each load-order winner?\n\n"
                + "Does not import into Patch/. Writes the sidecar so this is remembered next session.",
                "Resolve", "Cancel")) return;

            foreach (var row in pending)
                MarkResolved(row, row.Winner, persist: false);
            PersistSidecar();
            _viewDirty = true;
            ApplyFilter();
            RebuildTypeGroups();
            _patternsDirty = true;
            _viewDirty = false;
            Repaint();
        }

        /// <summary>
        /// Updates the selected type immediately (highlight), but defers Compare teardown and
        /// <see cref="MassChange.ComputeCandidates"/> off the MouseUp event so type-header
        /// clicks stay responsive.
        /// </summary>
        void SelectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            bool same = _selectedType == typeName && !_rightPaneCompare;
            _selectedType = typeName;
            _selectedElementName = "";
            if (same) return;

            _patternsDirty = true;
            _typeSwitchPending = true;
            int gen = ++_typeSwitchGen;
            Repaint();
            EditorApplication.delayCall += () =>
            {
                if (this == null || gen != _typeSwitchGen) return;
                if (_rightPaneCompare)
                    ExitEmbeddedCompare();
                EnsurePatterns();
                _typeSwitchPending = false;
                Repaint();
            };
        }

        void DrawPatternPane(Rect rect)
        {
            GUILayout.BeginArea(rect);

            var g = _typeGroups.FirstOrDefault(x => x.TypeName == _selectedType);
            if (g == null)
            {
                EditorGUILayout.HelpBox("Select a type on the left.", MessageType.None);
                GUILayout.EndArea();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{g.TypeName}  ·  {g.Rows.Count} elements · {g.DiffCount} diffs",
                EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Compare side-by-side", GUILayout.Width(180), GUILayout.Height(22)))
                OpenCompareForType(g.TypeName, g.Rows.FirstOrDefault());
            EditorGUILayout.LabelField(
                "Replaces this pattern list with full-element compare for the type.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (_typeSwitchPending)
            {
                EditorGUILayout.HelpBox("Computing patterns…", MessageType.None);
                GUILayout.EndArea();
                return;
            }

            EnsurePatterns();

            _patternScroll = EditorGUILayout.BeginScrollView(_patternScroll);
            if (_patternCandidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No field-level conflict patterns for this type (or only winner-only / Odin refs). Use Compare side-by-side for hand merges.",
                    MessageType.None);
            }
            else
            {
                foreach (var c in _patternCandidates)
                    DrawPatternCard(c);
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawPatternCard(MassChange.Candidate c)
        {
            if (c == null) return;
            int inPatch = c.Elements.Count(e => _patchNames.Contains(e.Name));
            string kindTag = c.Kind == DiffKind.MissingInWinner ? "MISSING"
                           : c.Kind == DiffKind.Changed ? "CHANGED" : "?";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{kindTag}  {c.Path}", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                $"{c.Elements.Count} els · {inPatch} in Patch"
                + (c.Sources.Count > 0 ? " · from: " + string.Join(" / ", c.Sources) : ""),
                EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(c.Preview))
                EditorGUILayout.SelectableLabel("preview: " + DiffGui.Short(c.Preview),
                    EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (c.ApplyKind == FieldApplier.ApplyKind.Unsupported)
            {
                EditorGUILayout.LabelField(
                    "Unsupported for mass apply: " + (c.UnsupportedReason ?? "complex / Odin"),
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Source", GUILayout.Width(48));
                var opts = new List<string> { "(skip)" };
                opts.AddRange(c.Sources);
                int cur = 0;
                if (!string.IsNullOrEmpty(c.Action) && c.Action != "skip")
                {
                    int ix = opts.IndexOf(c.Action);
                    if (ix >= 0) cur = ix;
                }
                int next = EditorGUILayout.Popup(cur, opts.ToArray(), GUILayout.Width(160));
                c.Action = next <= 0 ? "skip" : opts[next];
                using (new EditorGUI.DisabledScope(c.Action == "skip" || inPatch == 0))
                {
                    if (GUILayout.Button(
                            inPatch > 0 ? $"Apply to Patch ({inPatch})" : "Apply to Patch (none in Patch)",
                            GUILayout.Width(160)))
                        ApplyOnePattern(c);
                }
                EditorGUILayout.EndHorizontal();
                if (inPatch == 0)
                    EditorGUILayout.LabelField("Import winners into Patch/ first.", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        void ApplyOnePattern(MassChange.Candidate c)
        {
            if (c == null || c.Action == "skip") return;
            var stats = MassChange.Apply(
                new[] { c },
                BuildPatchPathByName(),
                null,
                ElemKey);
            ScanPatch();
            _patternsDirty = true;
            _viewDirty = true;
            ApplyFilter();
            RebuildTypeGroups();
            _viewDirty = false;
            EditorUtility.DisplayDialog("Mass Change",
                $"Applied {stats.Applied}, already OK {stats.SkippedAlready}, not in Patch {stats.SkippedNotInPatch}, failed {stats.Failed}, unsupported {stats.Unsupported}.",
                "OK");
            Repaint();
        }

        void OpenCompareForType(string typeName, ElementRow focus)
        {
            var g = _typeGroups.FirstOrDefault(x => x.TypeName == typeName);
            if (g == null || g.Rows.Count == 0) return;

            // Cancel any pending type→patterns switch so it doesn't tear Compare down after we open it.
            _typeSwitchGen++;
            _typeSwitchPending = false;

            // Capture BEFORE mutating _selectedType: the fast reselect path below is only valid when
            // the already-embedded Compare is for THIS type. Reading _selectedType after the
            // assignment would make it always match, reselecting a stale index into the old type's
            // items (loading the wrong element on a cross-type click).
            bool sameTypeEmbedded = _rightPaneCompare && _hostedCompare != null
                && string.Equals(_selectedType, typeName, StringComparison.Ordinal);

            if (_selectedType != typeName)
            {
                _selectedType = typeName;
                _patternsDirty = true;
            }

            EnsureSortedRows(g);
            var items = new List<CompatCompareWindow.CompareItem>();
            int idx = 0;
            foreach (var r in g.SortedRows)
            {
                var item = BuildCompareItem(r);
                if (item == null) continue;
                if (focus != null && (r == focus || string.Equals(r.Name, focus.Name, StringComparison.Ordinal)))
                    idx = items.Count;
                items.Add(item);
            }
            if (items.Count == 0)
            {
                EditorUtility.DisplayDialog("Compat Patcher",
                    "Could not open Compare for this type (no staged versions).", "OK");
                return;
            }

            // Same type already embedded → just switch selection (don't tear down session repo).
            if (sameTypeEmbedded)
            {
                int byName = focus != null ? _hostedCompare.FindHostedIndexByName(focus.Name) : idx;
                _hostedCompare.SelectHostedIndex(byName >= 0 ? byName : idx);
                _selectedElementName = focus?.Name ?? items[Mathf.Clamp(idx, 0, items.Count - 1)].name;
                Repaint();
                return;
            }

            ExitEmbeddedCompare();
            _hostedCompare = CompatCompareWindow.CreateHosted(
                items, idx, OnCompareResolveAsWinner, _unlockIndex, OnCompareImported);
            _rightPaneCompare = true;
            _selectedElementName = focus?.Name ?? items[Mathf.Clamp(idx, 0, items.Count - 1)].name;
            // Keep the type expanded so the highlight is visible.
            _expandedTypes.Add(typeName);
            Repaint();
        }

        void AutoExpandHazardCardsAfterCompare()
        {
            if (_findings.Count > 0 || !string.IsNullOrEmpty(_validationNote))
            {
                _showValidation = true;
                EditorPrefs.SetBool(PrefShowValidation, true);
            }
            if (_patchOrphans.Count > 0)
            {
                _showPatchOrphans = true;
                EditorPrefs.SetBool(PrefShowOrphans, true);
            }
        }
    }
}
