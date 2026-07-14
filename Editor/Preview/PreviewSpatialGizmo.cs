using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [InitializeOnLoad]
    internal static class PreviewSpatialGizmo
    {
        static PreviewSpatialGizmo() => SceneView.duringSceneGui += Draw;

        private static void Draw(SceneView _)
        {
            var session = RoomPreviewManager.Current;
            if (session == null) return;
            foreach (var placement in session.Placements.Where(value => value?.descriptor?.Geometry != null))
            {
                var hasOverlap = session.LastValidation?.issues.Any(issue => issue.code == "OBB_OVERLAP" && issue.elementIds.Contains(placement.placementId)) == true;
                var contactError = session.LastValidation?.issues.Any(issue => issue.elementIds.Contains(placement.placementId) && issue.code is "CONTACT_GAP" or "SURFACE_PENETRATION" or "INSUFFICIENT_SUPPORT" or "CONTACT_DIRECTION") == true;
                Handles.color = hasOverlap ? Color.red : contactError ? new Color(1f, 0.55f, 0.05f) : Color.green;
                foreach (var box in SpatialGeometryUtility.BuildWorldObbs(placement.descriptor, placement.position, placement.rotation, placement.scale))
                {
                    var matrix = Matrix4x4.TRS(box.Center, Quaternion.LookRotation(box.AxisZ, box.AxisY), Vector3.one);
                    using (new Handles.DrawingScope(Handles.color, matrix)) Handles.DrawWireCube(Vector3.zero, box.Extents * 2f);
                }
                System.Collections.Generic.IReadOnlyList<ContactEvidence> evidenceSet = placement.contactEvidenceSet != null && placement.contactEvidenceSet.Count > 0
                    ? placement.contactEvidenceSet
                    : placement.contactEvidence != null ? new[] { placement.contactEvidence } : System.Array.Empty<ContactEvidence>();
                foreach (var evidence in evidenceSet)
                {
                    Handles.DrawSolidDisc(evidence.contactPoint, SceneView.currentDrawingSceneView.camera.transform.forward, 0.035f);
                    var surface = session.Plan.Room.Surfaces.FirstOrDefault(value => value != null && value.SurfaceId == evidence.surfaceId);
                    if (surface != null) Handles.DrawLine(evidence.contactPoint, evidence.contactPoint - surface.Normal * evidence.gap);
                }
            }
        }
    }
}
