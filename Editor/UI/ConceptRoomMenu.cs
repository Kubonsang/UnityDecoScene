using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class ConceptRoomMenu
    {
        [MenuItem("GameObject/Concept Room Decorator/Create Room Setup", false, 10)]
        public static void CreateRoomSetup(MenuCommand command)
        {
            var root = command.context as GameObject ?? Selection.activeGameObject;
            if (root == null)
            {
                root = new GameObject("Concept Room");
                Undo.RegisterCreatedObjectUndo(root, "Create Concept Room");
            }

            var bounds = root.GetComponent<BoxCollider>();
            if (bounds == null) bounds = Undo.AddComponent<BoxCollider>(root);
            bounds.isTrigger = true;
            bounds.size = new Vector3(8f, 4f, 8f);

            var room = root.GetComponent<ConceptRoom>();
            if (room == null) room = Undo.AddComponent<ConceptRoom>(root);

            var observationObject = new GameObject("Observation - Entrance");
            Undo.RegisterCreatedObjectUndo(observationObject, "Create room observation point");
            observationObject.transform.SetParent(root.transform, false);
            observationObject.transform.localPosition = new Vector3(0f, 1.6f, -3.5f);
            observationObject.transform.localRotation = Quaternion.identity;
            Undo.AddComponent<RoomObservationPoint>(observationObject);

            var keepClearObject = new GameObject("Keep Clear - Entrance");
            Undo.RegisterCreatedObjectUndo(keepClearObject, "Create keep clear zone");
            keepClearObject.transform.SetParent(root.transform, false);
            keepClearObject.transform.localPosition = new Vector3(0f, 1f, -3f);
            var keepCollider = Undo.AddComponent<BoxCollider>(keepClearObject);
            keepCollider.isTrigger = true;
            keepCollider.size = new Vector3(2f, 2f, 2f);
            Undo.AddComponent<KeepClearZone>(keepClearObject);

            room.RefreshChildren();
            EditorUtility.SetDirty(room);
            Selection.activeGameObject = root;
        }

        [MenuItem("Tools/Concept Room Decorator/Refresh Selected Room Children")]
        public static void RefreshSelectedRoom()
        {
            var room = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<ConceptRoom>() : null;
            if (room == null) return;
            Undo.RecordObject(room, "Refresh room child references");
            room.RefreshChildren();
            EditorUtility.SetDirty(room);
        }
    }
}
