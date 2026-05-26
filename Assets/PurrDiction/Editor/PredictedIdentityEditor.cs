using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PurrNet.Prediction.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(PredictedIdentity), true)]
#if TRI_INSPECTOR_PACKAGE
    public class PredictedIdentityEditor : TriInspector.Editors.TriEditor
#elif ODIN_INSPECTOR
    public class PredictedIdentityEditor : Sirenix.OdinInspector.Editor.OdinEditor
#else
    public class PredictedIdentityEditor : UnityEditor.Editor
#endif
    {
        // --- Palette --------------------------------------------------------------------------------
        static readonly Color BgColor             = new Color(0.17f, 0.17f, 0.17f);
        static readonly Color VerifiedColor       = new Color(0.42f, 0.85f, 0.52f);
        static readonly Color SimulatedColor      = new Color(0.96f, 0.84f, 0.32f);
        static readonly Color ExtrapolatedColor   = new Color(0.95f, 0.45f, 0.45f);
        static readonly Color HeldColor           = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color NowMarkerColor      = new Color(0.92f, 0.92f, 0.92f);
        static readonly Color ViewStateColor      = new Color(0.55f, 0.85f, 1.00f);
        static readonly Color DimTextColor        = new Color(0.60f, 0.60f, 0.60f);

        // --- Cached styles --------------------------------------------------------------------------
        static GUIStyle _runtimeBox;
        static GUIStyle _modeTitleStyle;
        static GUIStyle _modeSubtitleStyle;
        static GUIStyle _markerLabelStyle;
        static GUIStyle _markerSublabelStyle;
        static GUIStyle _arrowLabelStyle;

        bool _showPredictedModules = false;

        static void EnsureStyles()
        {
            _runtimeBox ??= new GUIStyle("helpbox")
            {
                wordWrap = true,
                stretchWidth = true,
                stretchHeight = true,
                alignment = TextAnchor.UpperLeft
            };
            _modeTitleStyle ??= new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
            };
            _modeSubtitleStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 10,
                normal = { textColor = DimTextColor },
            };
            _markerLabelStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 10,
            };
            _markerSublabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 9,
                normal = { textColor = DimTextColor },
            };
            _arrowLabelStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
            };
        }

        public override VisualElement CreateInspectorGUI()
        {
            return null;
        }

        // -----------------------------------------------------------------------------------------
        public override void OnInspectorGUI()
        {
            EnsureStyles();
            serializedObject.Update();

            bool anyHasInput = false;
            foreach (var t in targets)
            {
                if (t is PredictedIdentity pi && pi.hasInput)
                {
                    anyHasInput = true;
                    break;
                }
            }

            var forwardInputProp     = anyHasInput ? serializedObject.FindProperty("_forwardInput") : null;
            var simulateForwardProp  = serializedObject.FindProperty("_simulateForward");
            var extrapolateStateProp = serializedObject.FindProperty("_extrapolateState");
            var maxStateExtrapProp   = serializedObject.FindProperty("_maxStateExtrapolationTicks");
            var interpDelayProp      = serializedObject.FindProperty("_interpolationDelayTicks");

            DrawModeSection(forwardInputProp, simulateForwardProp, extrapolateStateProp, maxStateExtrapProp, interpDelayProp);
            EditorGUILayout.Space(4);

            bool forwardInput     = forwardInputProp == null || forwardInputProp.boolValue;
            bool simulateForward  = simulateForwardProp  == null || simulateForwardProp.boolValue;
            bool hasExtrap        = extrapolateStateProp != null;
            bool extrapolateState = hasExtrap && extrapolateStateProp.boolValue;
            int  interpDelayTicks = interpDelayProp != null ? interpDelayProp.intValue : 0;
            int  maxExtrapTicks   = maxStateExtrapProp != null ? maxStateExtrapProp.intValue : 0;

            DrawTimelineDiagram(anyHasInput, forwardInput, simulateForward, hasExtrap, extrapolateState,
                                interpDelayTicks, maxExtrapTicks);

            EditorGUILayout.Space(6);
            DrawRemainingProperties();

            serializedObject.ApplyModifiedProperties();

            DrawRuntimeStatePanel();
        }

        // -----------------------------------------------------------------------------------------
        // Mode section — contextual show/hide
        // -----------------------------------------------------------------------------------------

        void DrawModeSection(
            SerializedProperty fwdInput,
            SerializedProperty simFwd,
            SerializedProperty extrap,
            SerializedProperty maxExtrap,
            SerializedProperty interpDelay)
        {
            GUILayout.Label("Mode", EditorStyles.boldLabel);

            if (fwdInput != null)
            {
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                    EditorGUILayout.PropertyField(fwdInput);
            }

            bool fwdInputOn = fwdInput == null || fwdInput.boolValue;

            if (simFwd != null && fwdInputOn)
                EditorGUILayout.PropertyField(simFwd);

            bool simFwdOn = simFwd == null || simFwd.boolValue;
            bool isModeA  = fwdInputOn && simFwdOn;

            if (extrap != null && !isModeA)
            {
                EditorGUILayout.PropertyField(extrap);
                if (extrap.boolValue && maxExtrap != null)
                    EditorGUILayout.PropertyField(maxExtrap);
            }

            if (interpDelay != null)
                EditorGUILayout.PropertyField(interpDelay);
        }

        // -----------------------------------------------------------------------------------------
        // Timeline diagram
        // -----------------------------------------------------------------------------------------

        void DrawTimelineDiagram(
            bool hasInput, bool forwardInput, bool simulateForward,
            bool hasExtrap, bool extrapolateState,
            int interpDelayTicks, int maxExtrapTicks)
        {
            string modeName, modeSubtitle;
            Color  modeColor;
            bool   anchoredAtPresent = forwardInput && simulateForward;
            bool   effectiveExtrap = hasExtrap && extrapolateState;
            if (!hasInput)
            {
                if (simulateForward)
                {
                    modeName     = "FORWARD SIM";
                    modeColor    = SimulatedColor;
                    modeSubtitle = "State · every tick";
                }
                else
                {
                    modeName     = effectiveExtrap ? "EXTRAPOLATED" : "VERIFIED ONLY";
                    modeColor    = effectiveExtrap ? ExtrapolatedColor : HeldColor;
                    modeSubtitle = effectiveExtrap ? "State · verified + extrapolated" : "State · verified notify";
                }
            }
            else if (!forwardInput)
            {
                modeName     = "STATE ONLY";
                modeColor    = effectiveExtrap ? ExtrapolatedColor : HeldColor;
                modeSubtitle = effectiveExtrap ? "State only · extrapolated" : "State only · verified notify";
            }
            else if (!simulateForward)
            {
                modeName     = "VERIFIED REPLAY";
                modeColor    = SimulatedColor;
                modeSubtitle = effectiveExtrap ? "Verified replay · extrapolated" : "Verified replay · held";
            }
            else
            {
                modeName     = "FULL PREDICTION";
                modeColor    = VerifiedColor;
                modeSubtitle = "Full prediction";
            }

            Color pastColor;
            if (anchoredAtPresent)    pastColor = SimulatedColor;
            else if (effectiveExtrap) pastColor = ExtrapolatedColor;
            else                      pastColor = HeldColor;

            int interpDelay  = interpDelayTicks > 0 ? interpDelayTicks : 1;
            int extrapBudget = maxExtrapTicks  > 0 ? maxExtrapTicks  : 1;

            const float pxPerTick = 14f;

            const float panelHeight = 126f;
            var fullRect = GUILayoutUtility.GetRect(0, panelHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(fullRect, BgColor);

            var inner = new Rect(fullRect.x + 14, fullRect.y + 10, fullRect.width - 28, fullRect.height - 20);

            var nameContent = new GUIContent(modeName);
            var nameSize = _modeTitleStyle.CalcSize(nameContent);
            {
                var old = GUI.color;
                GUI.color = modeColor;
                GUI.Label(new Rect(inner.x, inner.y, nameSize.x, 16), nameContent, _modeTitleStyle);
                GUI.color = old;
            }
            GUI.Label(new Rect(inner.x + nameSize.x + 8f, inner.y + 1f, inner.width - nameSize.x - 8f, 14f),
                      modeSubtitle, _modeSubtitleStyle);

            float timelineLeft  = inner.x;
            float timelineRight = inner.x + inner.width;
            float verifiedX     = timelineLeft + (timelineRight - timelineLeft) * 0.32f;
            float nowX          = timelineRight - 4f;

            const float leftDottedWidth = 22f;
            float leftDottedStart = timelineLeft;
            float leftDottedEnd   = timelineLeft + leftDottedWidth;
            float leftDottedMid   = (leftDottedStart + leftDottedEnd) * 0.5f;

            float anchorX;
            float extrapTipX = verifiedX;
            bool  drawForwardArrow = false;
            if (anchoredAtPresent)
            {
                anchorX = nowX;
            }
            else if (effectiveExtrap)
            {
                extrapTipX = verifiedX + extrapBudget * pxPerTick;
                if (extrapTipX > nowX - 12f) extrapTipX = nowX - 12f;
                anchorX = extrapTipX;
                drawForwardArrow = true;
            }
            else
            {
                anchorX = verifiedX;
            }

            float naturalViewX = anchorX - interpDelay * pxPerTick;
            float viewStateX;
            if (naturalViewX < leftDottedEnd)
            {
                float excess = leftDottedEnd - naturalViewX;
                viewStateX = leftDottedEnd
                           + (leftDottedMid - leftDottedEnd)
                           * (1f - Mathf.Exp(-excess / 18f));
            }
            else
            {
                viewStateX = naturalViewX;
            }
            if (anchorX - viewStateX < 18f)
                viewStateX = anchorX - 18f;

            float forwardArrowY = inner.y + 30f;
            float barY          = inner.y + 46f;
            const float barH    = 7f;
            float upTriY        = barY + barH + 6f;
            float markerLblY    = upTriY + 11f;
            float subLabelY     = markerLblY + 13f;
            float backArrowY    = subLabelY + 15f;

            DrawDottedRect(new Rect(leftDottedStart, barY, leftDottedEnd - leftDottedStart, barH), VerifiedColor);
            EditorGUI.DrawRect(new Rect(leftDottedEnd, barY, verifiedX - leftDottedEnd, barH), VerifiedColor);
            if (anchoredAtPresent)
            {
                EditorGUI.DrawRect(new Rect(verifiedX, barY, nowX - verifiedX, barH), pastColor);
            }
            else if (effectiveExtrap)
            {
                float wSolid = extrapTipX - verifiedX;
                if (wSolid > 0f)
                    EditorGUI.DrawRect(new Rect(verifiedX, barY, wSolid, barH), pastColor);
                float wDotted = nowX - extrapTipX;
                if (wDotted > 0f)
                    DrawDottedRect(new Rect(extrapTipX, barY, wDotted, barH), pastColor);
            }
            else
            {
                DrawDottedRect(new Rect(verifiedX, barY, nowX - verifiedX, barH), pastColor);
            }

            Handles.BeginGUI();
            try
            {
                if (anchoredAtPresent)
                    DrawTriangle(new Vector2(nowX, barY - 7f), 6f, false, NowMarkerColor);
                DrawTriangle(new Vector2(verifiedX, upTriY), 6f, true, VerifiedColor);
            }
            finally
            {
                Handles.EndGUI();
            }

            if (drawForwardArrow)
                DrawArrowWithLabel(verifiedX, extrapTipX, forwardArrowY,
                                   extrapBudget.ToString(), ExtrapolatedColor, ArrowDir.Right);

            DrawCenteredLabel(verifiedX, markerLblY, "Verified", _markerLabelStyle, VerifiedColor);
            DrawCenteredLabel(verifiedX, subLabelY,
                              hasInput && forwardInput ? "Input + State" : "State only",
                              _markerLabelStyle, Color.white);

            DrawArrowWithLabel(viewStateX, anchorX, backArrowY,
                               interpDelay.ToString(), ViewStateColor, ArrowDir.Left);
        }

        // -----------------------------------------------------------------------------------------
        // Drawing primitives
        // -----------------------------------------------------------------------------------------

        static void DrawDottedRect(Rect r, Color color)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            const float dashW = 3f;
            const float gapW  = 3f;
            float x = r.x;
            while (x < r.x + r.width - 0.5f)
            {
                float w = Mathf.Min(dashW, r.x + r.width - x);
                EditorGUI.DrawRect(new Rect(x, r.y, w, r.height), color);
                x += dashW + gapW;
            }
        }

        static void DrawTriangle(Vector2 center, float size, bool pointUp, Color color)
        {
            Handles.color = color;
            Vector3 a, b, c;
            if (pointUp)
            {
                a = new Vector3(center.x,        center.y - size, 0f);
                b = new Vector3(center.x - size, center.y + size * 0.85f, 0f);
                c = new Vector3(center.x + size, center.y + size * 0.85f, 0f);
            }
            else
            {
                a = new Vector3(center.x,        center.y + size, 0f);
                b = new Vector3(center.x - size, center.y - size * 0.85f, 0f);
                c = new Vector3(center.x + size, center.y - size * 0.85f, 0f);
            }
            Handles.DrawAAConvexPolygon(a, b, c);
        }

        enum ArrowDir { Left, Right }

        static void DrawArrowWithLabel(float xLeft, float xRight, float y, string label, Color color, ArrowDir dir)
        {
            if (xRight - xLeft < 16f) return;
            const float arrowheadInset = 6f;
            const float labelPad = 5f;

            float midX = (xLeft + xRight) * 0.5f;
            var content = new GUIContent(label);
            var labelSize = _arrowLabelStyle.CalcSize(content);
            float labelHalfW = labelSize.x * 0.5f + labelPad;

            float leftLineStart, leftLineEnd, rightLineStart, rightLineEnd;
            if (dir == ArrowDir.Right)
            {
                leftLineStart  = xLeft;
                leftLineEnd    = midX - labelHalfW;
                rightLineStart = midX + labelHalfW;
                rightLineEnd   = xRight - arrowheadInset;
            }
            else
            {
                leftLineStart  = xLeft + arrowheadInset;
                leftLineEnd    = midX - labelHalfW;
                rightLineStart = midX + labelHalfW;
                rightLineEnd   = xRight;
            }

            if (leftLineEnd  > leftLineStart)
                EditorGUI.DrawRect(new Rect(leftLineStart,  y - 0.75f, leftLineEnd  - leftLineStart, 1.5f), color);
            if (rightLineEnd > rightLineStart)
                EditorGUI.DrawRect(new Rect(rightLineStart, y - 0.75f, rightLineEnd - rightLineStart, 1.5f), color);

            Handles.BeginGUI();
            try
            {
                Handles.color = color;
                if (dir == ArrowDir.Right)
                {
                    Handles.DrawAAConvexPolygon(
                        new Vector3(xRight,      y,         0f),
                        new Vector3(xRight - 6f, y - 3.5f,  0f),
                        new Vector3(xRight - 6f, y + 3.5f,  0f));
                }
                else
                {
                    Handles.DrawAAConvexPolygon(
                        new Vector3(xLeft,       y,         0f),
                        new Vector3(xLeft + 6f,  y - 3.5f,  0f),
                        new Vector3(xLeft + 6f,  y + 3.5f,  0f));
                }
            }
            finally
            {
                Handles.EndGUI();
            }

            var labelRect = new Rect(midX - labelSize.x * 0.5f,
                                     y - labelSize.y * 0.5f,
                                     labelSize.x, labelSize.y);
            var oldColor = GUI.color;
            GUI.color = color;
            GUI.Label(labelRect, content, _arrowLabelStyle);
            GUI.color = oldColor;
        }

        static void DrawCenteredLabel(float centerX, float y, string text, GUIStyle style, Color? tint = null)
        {
            var content = new GUIContent(text);
            var size = style.CalcSize(content);
            var rect = new Rect(centerX - size.x * 0.5f, y, size.x, size.y);
            if (tint.HasValue)
            {
                var old = GUI.color;
                GUI.color = tint.Value;
                GUI.Label(rect, content, style);
                GUI.color = old;
            }
            else
            {
                GUI.Label(rect, content, style);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Remaining (non-mode) properties + runtime panel
        // -----------------------------------------------------------------------------------------

        void DrawRemainingProperties()
        {
            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(iterator);
                    continue;
                }
                if (iterator.propertyPath == "_forwardInput"
                    || iterator.propertyPath == "_simulateForward"
                    || iterator.propertyPath == "_extrapolateState"
                    || iterator.propertyPath == "_maxStateExtrapolationTicks"
                    || iterator.propertyPath == "_interpolationDelayTicks")
                    continue;
                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        void DrawRuntimeStatePanel()
        {
            GUILayout.Space(10);
            GUILayout.Label("Predicted State", EditorStyles.boldLabel);

            for (int i = 0; i < targets.Length; i++)
            {
                if (!targets[i] || targets[i] is not PredictedIdentity predictedIdentity)
                    continue;

                var extraContent  = predictedIdentity.GetExtraString();
                var content       = predictedIdentity.ToString();
                bool bothNonEmpty = !string.IsNullOrEmpty(extraContent) && !string.IsNullOrEmpty(content);

                if (Application.isPlaying)
                {
                    EditorGUILayout.BeginHorizontal("box", GUILayout.ExpandWidth(false));
                    try
                    {
                        GUILayout.Label($"ID: {predictedIdentity.id}", GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(
                            $"Owner ID: {(predictedIdentity.owner.HasValue ? predictedIdentity.owner.Value.ToString() : "None")}",
                            GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        var pm = predictedIdentity.predictionManager;
                        GUILayout.Label(
                            pm
                                ? $"Local Player: {(pm.localPlayer.HasValue ? pm.localPlayer.Value.ToString() : "None")}"
                                : "Not ready",
                            GUILayout.ExpandWidth(false));
                    }
                    catch
                    {
                        GUILayout.Label("Not Spawned", GUILayout.ExpandWidth(false));
                    }
                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
                if (!string.IsNullOrEmpty(extraContent))
                {
                    if (bothNonEmpty) GUILayout.Box(extraContent, _runtimeBox, GUILayout.MinWidth(1));
                    else              GUILayout.Box(extraContent, _runtimeBox);
                }
                if (!string.IsNullOrEmpty(content))
                {
                    if (bothNonEmpty) GUILayout.Box(content, _runtimeBox, GUILayout.MinWidth(1));
                    else              GUILayout.Box(content, _runtimeBox);
                }
                GUILayout.EndHorizontal();

                DrawPredictedModules(predictedIdentity);

                if (predictedIdentity.GetType().GetCustomAttributes(typeof(PredictionUnsafeAttribute), true).Length > 0)
                {
                    EditorGUILayout.HelpBox(
                        "This identity is marked as PredictionUnsafe, which means use at your own risk.\n" +
                        "It may not behave as expected or works against prediction.",
                        MessageType.Warning);
                }
            }
        }

        private void DrawPredictedModules(PredictedIdentity predictedIdentity)
        {
            var modules = predictedIdentity.modules;
            if (modules == null || modules.Count == 0)
                return;

            GUILayout.Space(5);
            _showPredictedModules = EditorGUILayout.Foldout(
                _showPredictedModules,
                $"Predicted Modules ({modules.Count})",
                true);

            if (!_showPredictedModules)
                return;

            for (int i = 0; i < modules.Count; i++)
            {
                var module = modules[i];
                if (module == null)
                    continue;

                var content = module.ToString();
                var moduleName = module.GetType().Name;
                if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(moduleName))
                    continue;

                EditorGUILayout.BeginVertical("box");

                var moduleDisplayName = $"{moduleName} [#{module.moduleIndex}]";
                EditorGUILayout.LabelField(moduleDisplayName, EditorStyles.boldLabel);
                GUILayout.Box(content, _runtimeBox, GUILayout.ExpandWidth(true));

                EditorGUILayout.EndVertical();
            }
        }
    }
}
