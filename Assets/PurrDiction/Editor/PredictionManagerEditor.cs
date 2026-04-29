using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomEditor(typeof(PredictionManager))]
    public class PredictionManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var syncProp = serializedObject.FindProperty("_syncDeterministicData");
            var modeProp = serializedObject.FindProperty("_frameChannelMode");
            bool isUnreliable = modeProp != null &&
                (FrameChannelMode)modeProp.enumValueIndex == FrameChannelMode.Unreliable;
            bool grayValidate = isUnreliable && syncProp != null && syncProp.boolValue;

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

                if (iterator.propertyPath == "_syncDeterministicData")
                    continue;

                if (iterator.propertyPath == "_validateDeterministicData" && grayValidate)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(iterator, true);
                }
                else
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }

                if (iterator.propertyPath == "_frameChannelMode" && isUnreliable && syncProp != null)
                {
                    EditorGUILayout.PropertyField(syncProp);

                    if (syncProp.boolValue)
                    {
                        EditorGUILayout.HelpBox(
                            "Deterministic identities will sync their full state over the wire each " +
                            "tick (like regular predicted identities) instead of relying purely on " +
                            "deterministic simulation. This trades determinism guarantees for " +
                            "resilience to packet loss, at the cost of bandwidth.",
                            MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "Unreliable mode drops packets, which breaks lockstep deterministic " +
                            "simulation. Without 'Sync Deterministic Data' enabled, deterministic " +
                            "identities will desync across peers under packet loss.",
                            MessageType.Error);
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}