using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [CustomEditor(typeof(DecorAssetDescriptor))]
    public sealed class DecorAssetDescriptorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var descriptor = (DecorAssetDescriptor)target;
            DrawDefaultInspector();
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Geometry Review", EditorStyles.boldLabel);

            var geometry = descriptor.Geometry;
            if (geometry == null)
            {
                EditorGUILayout.HelpBox("No Spatial Manifest v2 geometry draft is available.", MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                $"Source: {geometry.source}  |  Proxies: {geometry.collisionProxies?.Count ?? 0}  |  Confidence: {geometry.inferenceConfidence:P0}\n" +
                "Confirm the asset's semantic forward axis and the physical back/bottom contact faces before approval.",
                geometry.reviewed ? MessageType.Info : MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Forward +Z")) SetForward(descriptor, Vector3.forward, Vector3.up);
                if (GUILayout.Button("Forward -Z")) SetForward(descriptor, Vector3.back, Vector3.up);
                if (GUILayout.Button("Forward +X")) SetForward(descriptor, Vector3.right, Vector3.up);
                if (GUILayout.Button("Forward -X")) SetForward(descriptor, Vector3.left, Vector3.up);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Back -Z")) SetBackFace(descriptor, Vector3.back);
                if (GUILayout.Button("Back +Z")) SetBackFace(descriptor, Vector3.forward);
                if (GUILayout.Button("Back -X")) SetBackFace(descriptor, Vector3.left);
                if (GUILayout.Button("Back +X")) SetBackFace(descriptor, Vector3.right);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Draft")) Rebuild(descriptor);
                using (new EditorGUI.DisabledScope(geometry.collisionProxies == null || geometry.collisionProxies.Count == 0))
                {
                    if (GUILayout.Button(geometry.reviewed ? "Mark Unreviewed" : "Approve Geometry"))
                    {
                        Undo.RecordObject(descriptor, "Review decor geometry");
                        geometry.reviewed = !geometry.reviewed;
                        geometry.Normalize();
                        EditorUtility.SetDirty(descriptor);
                    }
                }
            }
        }

        private static void SetForward(DecorAssetDescriptor descriptor, Vector3 forward, Vector3 up)
        {
            Undo.RecordObject(descriptor, "Set decor forward axis");
            descriptor.Geometry.forwardAxis = forward;
            descriptor.Geometry.upAxis = up;
            descriptor.Geometry.reviewed = false;
            descriptor.Geometry.Normalize();
            EditorUtility.SetDirty(descriptor);
        }

        private static void SetBackFace(DecorAssetDescriptor descriptor, Vector3 normal)
        {
            Undo.RecordObject(descriptor, "Set decor back contact face");
            var bounds = new Bounds(descriptor.LocalBoundsCenter, descriptor.LocalBoundsSize);
            var extent = new Vector3(
                normal.x * bounds.extents.x,
                normal.y * bounds.extents.y,
                normal.z * bounds.extents.z);
            var horizontal = Mathf.Abs(normal.x) > 0.5f;
            descriptor.Geometry.backContact = new ContactFrame
            {
                frameId = "back",
                localPoint = bounds.center + extent,
                localNormal = normal,
                localTangent = horizontal ? Vector3.forward : Vector3.right,
                size = horizontal ? new Vector2(bounds.size.z, bounds.size.y) : new Vector2(bounds.size.x, bounds.size.y)
            };
            descriptor.Geometry.reviewed = false;
            descriptor.Geometry.Normalize();
            EditorUtility.SetDirty(descriptor);
        }

        private static void Rebuild(DecorAssetDescriptor descriptor)
        {
            if (descriptor.Prefab == null) return;
            Undo.RecordObject(descriptor, "Rebuild decor geometry draft");
            descriptor.ConfigureGeometry(DecorAssetScanner.BuildGeometryProfile(descriptor.Prefab));
            EditorUtility.SetDirty(descriptor);
        }
    }
}
