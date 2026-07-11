using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomSurfaceScanner
    {
        private const string GeneratedRootName = "Room Surfaces (Generated)";

        public static IReadOnlyList<RoomSurface> Scan(ConceptRoom room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (room.AuthoringBounds == null) throw new InvalidOperationException("ConceptRoom needs authoring bounds before surface scanning.");

            Undo.IncrementCurrentGroup();
            var undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Scan Concept Room Surfaces");
            var previous = room.transform.Find(GeneratedRootName);
            if (previous != null) Undo.DestroyObjectImmediate(previous.gameObject);

            var root = new GameObject(GeneratedRootName).transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, "Create room surface container");
            root.SetParent(room.transform, false);
            var result = new List<RoomSurface>();
            AddAuthoringBoxSurfaces(room.AuthoringBounds, root, result);
            foreach (var floor in room.FloorColliders.Where(value => value != null).OrderBy(value => value.name, StringComparer.Ordinal))
                AddFloorSurface(floor, root, result);
            room.RefreshChildren();
            EditorUtility.SetDirty(room);
            Undo.CollapseUndoOperations(undo);
            return result;
        }

        public static void SetReviewed(ConceptRoom room, bool reviewed)
        {
            if (room == null) return;
            foreach (var surface in room.Surfaces)
            {
                if (surface == null || !surface.Supported) continue;
                Undo.RecordObject(surface, reviewed ? "Approve room surface" : "Unapprove room surface");
                surface.SetReviewed(reviewed);
                EditorUtility.SetDirty(surface);
            }
        }

        private static void AddAuthoringBoxSurfaces(BoxCollider box, Transform root, ICollection<RoomSurface> result)
        {
            var c = box.center;
            var e = box.size * 0.5f;
            AddBoxFace("wall-west", RoomSurfaceType.Wall, box, root, box.transform.TransformPoint(c + Vector3.left * e.x), box.transform.TransformDirection(Vector3.right), box.transform.TransformDirection(Vector3.forward), WorldLength(box.transform, Vector3.forward * box.size.z), WorldLength(box.transform, Vector3.up * box.size.y), result);
            AddBoxFace("wall-east", RoomSurfaceType.Wall, box, root, box.transform.TransformPoint(c + Vector3.right * e.x), box.transform.TransformDirection(Vector3.left), box.transform.TransformDirection(Vector3.back), WorldLength(box.transform, Vector3.forward * box.size.z), WorldLength(box.transform, Vector3.up * box.size.y), result);
            AddBoxFace("wall-south", RoomSurfaceType.Wall, box, root, box.transform.TransformPoint(c + Vector3.back * e.z), box.transform.TransformDirection(Vector3.forward), box.transform.TransformDirection(Vector3.right), WorldLength(box.transform, Vector3.right * box.size.x), WorldLength(box.transform, Vector3.up * box.size.y), result);
            AddBoxFace("wall-north", RoomSurfaceType.Wall, box, root, box.transform.TransformPoint(c + Vector3.forward * e.z), box.transform.TransformDirection(Vector3.back), box.transform.TransformDirection(Vector3.left), WorldLength(box.transform, Vector3.right * box.size.x), WorldLength(box.transform, Vector3.up * box.size.y), result);
            AddBoxFace("ceiling", RoomSurfaceType.Ceiling, box, root, box.transform.TransformPoint(c + Vector3.up * e.y), box.transform.TransformDirection(Vector3.down), box.transform.TransformDirection(Vector3.right), WorldLength(box.transform, Vector3.right * box.size.x), WorldLength(box.transform, Vector3.forward * box.size.z), result);
        }

        private static void AddFloorSurface(Collider collider, Transform root, ICollection<RoomSurface> result)
        {
            var reason = "collider type is unsupported";
            if (collider is MeshCollider meshCollider && TryExtractPlanarMesh(meshCollider, out var origin, out var normal, out var tangent, out var size, out reason))
            {
                var meshSurface = CreateSurface($"floor-{collider.name}", root);
                meshSurface.Configure($"floor-{collider.GetInstanceID()}", RoomSurfaceType.Floor, collider, origin, normal, tangent, size, false);
                result.Add(meshSurface);
                return;
            }

            if (collider is not BoxCollider box)
            {
                var unsupported = CreateSurface($"unsupported-{collider.name}", root);
                var detail = collider is MeshCollider ? reason : "only BoxCollider and planar MeshCollider surfaces are supported";
                unsupported.Configure($"unsupported-{collider.GetInstanceID()}", RoomSurfaceType.Floor, collider, collider.bounds.center, Vector3.up, Vector3.right, new Vector2(collider.bounds.size.x, collider.bounds.size.z), false, false, $"UNSUPPORTED_SURFACE: {detail}.");
                result.Add(unsupported);
                return;
            }
            var point = box.transform.TransformPoint(box.center + Vector3.up * box.size.y * 0.5f);
            var surface = CreateSurface($"floor-{collider.name}", root);
            surface.Configure($"floor-{collider.GetInstanceID()}", RoomSurfaceType.Floor, collider, point, box.transform.up, box.transform.right, new Vector2(WorldLength(box.transform, Vector3.right * box.size.x), WorldLength(box.transform, Vector3.forward * box.size.z)), false);
            result.Add(surface);
        }

        private static bool TryExtractPlanarMesh(MeshCollider collider, out Vector3 origin, out Vector3 normal, out Vector3 tangent, out Vector2 size, out string reason)
        {
            origin = collider.bounds.center;
            normal = Vector3.up;
            tangent = Vector3.right;
            size = new Vector2(collider.bounds.size.x, collider.bounds.size.z);
            reason = "mesh is missing or has no triangles";
            var mesh = collider.sharedMesh;
            if (mesh == null || mesh.vertexCount < 3 || mesh.triangles.Length < 3) return false;

            var vertices = mesh.vertices.Select(collider.transform.TransformPoint).ToArray();
            var triangles = mesh.triangles;
            var foundPlane = false;
            var planePoint = Vector3.zero;
            for (var index = 0; index + 2 < triangles.Length; index += 3)
            {
                var a = vertices[triangles[index]];
                var b = vertices[triangles[index + 1]];
                var c = vertices[triangles[index + 2]];
                var candidate = Vector3.Cross(b - a, c - a);
                if (candidate.sqrMagnitude < 1e-8f) continue;
                candidate.Normalize();
                if (!foundPlane)
                {
                    normal = candidate;
                    planePoint = a;
                    foundPlane = true;
                }
                else if (Mathf.Abs(Vector3.Dot(normal, candidate)) < 0.999f)
                {
                    reason = "mesh contains non-coplanar triangle normals";
                    return false;
                }
            }

            if (!foundPlane) return false;
            if (normal.y < 0f) normal = -normal;
            if (Vector3.Dot(normal, Vector3.up) < 0.999f)
            {
                reason = "sloped or vertical MeshCollider is outside the flat-floor scope";
                return false;
            }
            var planeNormal = normal;
            if (vertices.Any(vertex => Mathf.Abs(Vector3.Dot(vertex - planePoint, planeNormal)) > 0.01f))
            {
                reason = "mesh vertices deviate from one plane by more than 1cm";
                return false;
            }

            tangent = Vector3.ProjectOnPlane(collider.transform.right, normal).normalized;
            if (tangent.sqrMagnitude < 0.99f) tangent = Vector3.ProjectOnPlane(Vector3.forward, normal).normalized;
            var bitangent = Vector3.Cross(normal, tangent).normalized;
            var minX = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            foreach (var vertex in vertices)
            {
                var x = Vector3.Dot(vertex, tangent);
                var y = Vector3.Dot(vertex, bitangent);
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }
            var planeDistance = Vector3.Dot(planePoint, normal);
            origin = tangent * ((minX + maxX) * 0.5f) + bitangent * ((minY + maxY) * 0.5f) + normal * planeDistance;
            size = new Vector2(maxX - minX, maxY - minY);
            reason = string.Empty;
            return size.x > 0.001f && size.y > 0.001f;
        }

        private static void AddBoxFace(string id, RoomSurfaceType type, Collider collider, Transform root, Vector3 origin, Vector3 normal, Vector3 tangent, float width, float height, ICollection<RoomSurface> result)
        {
            var surface = CreateSurface(id, root);
            surface.Configure(id, type, collider, origin, normal, tangent, new Vector2(width, height), false);
            result.Add(surface);
        }

        private static RoomSurface CreateSurface(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create room surface");
            gameObject.transform.SetParent(parent, false);
            return gameObject.AddComponent<RoomSurface>();
        }

        private static float WorldLength(Transform transform, Vector3 vector) => transform.TransformVector(vector).magnitude;
    }
}
