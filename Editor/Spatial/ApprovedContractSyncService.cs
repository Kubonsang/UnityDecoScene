using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public enum ApprovedContractSyncStatus
    {
        Unchanged,
        Ready,
        Applied,
        Missing,
        NotApproved,
        Stale,
        Invalid,
        Ambiguous
    }

    public sealed class ApprovedContractSyncItem
    {
        public DecorAssetDescriptor descriptor;
        public string assetGuid;
        public string contractPath;
        public ApprovedContractSyncStatus status;
        public string message;
    }

    public sealed class ApprovedContractSyncPlan
    {
        public string contractRoot;
        public readonly List<ApprovedContractSyncItem> items = new();
        public int ReadyCount => items.Count(item => item.status == ApprovedContractSyncStatus.Ready);
        public int BlockerCount => items.Count(item => item.status is ApprovedContractSyncStatus.Invalid or ApprovedContractSyncStatus.Ambiguous or ApprovedContractSyncStatus.Stale or ApprovedContractSyncStatus.NotApproved);
    }

    /// <summary>
    /// Fail-closed, two-step synchronization from human-approved JSON contracts to catalog descriptors.
    /// Preview never mutates assets; Apply records every descriptor in one Unity Undo group.
    /// </summary>
    public static class ApprovedContractSyncService
    {
        public static ApprovedContractSyncPlan Preview(DecorCatalog catalog, string contractRoot = "Assets/SpatialContracts/Assets")
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var plan = new ApprovedContractSyncPlan { contractRoot = NormalizeRoot(contractRoot) };
            var files = Directory.Exists(plan.contractRoot)
                ? Directory.GetFiles(plan.contractRoot, "*.spatial.json", SearchOption.TopDirectoryOnly).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            var byGuid = files.GroupBy(PathGuid, StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            foreach (var descriptor in catalog.Assets.Where(item => item != null).OrderBy(item => item.AssetId, StringComparer.Ordinal))
            {
                var item = new ApprovedContractSyncItem { descriptor = descriptor };
                if (descriptor.Prefab == null)
                {
                    item.status = ApprovedContractSyncStatus.Invalid;
                    item.message = "Descriptor prefab is missing.";
                    plan.items.Add(item);
                    continue;
                }

                var prefabPath = AssetDatabase.GetAssetPath(descriptor.Prefab);
                item.assetGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                if (!byGuid.TryGetValue(item.assetGuid, out var candidates) || candidates.Length == 0)
                {
                    item.status = ApprovedContractSyncStatus.Missing;
                    item.message = "No contract exists for this prefab GUID.";
                    plan.items.Add(item);
                    continue;
                }
                if (candidates.Length != 1)
                {
                    item.status = ApprovedContractSyncStatus.Ambiguous;
                    item.message = $"{candidates.Length} contracts map to the same prefab GUID.";
                    plan.items.Add(item);
                    continue;
                }

                item.contractPath = candidates[0];
                SpatialContractDocument document;
                try { document = SpatialContractIO.Load(item.contractPath); }
                catch (Exception exception)
                {
                    item.status = ApprovedContractSyncStatus.Invalid;
                    item.message = exception.Message;
                    plan.items.Add(item);
                    continue;
                }
                if (!document.IsApproved)
                {
                    item.status = ApprovedContractSyncStatus.NotApproved;
                    item.message = "Contract has not passed the human approval gate.";
                    plan.items.Add(item);
                    continue;
                }
                if (!SpatialContractHashUtility.ValidateApproved(document, out var reason))
                {
                    item.status = reason.StartsWith("CONTRACT_HASH_MISMATCH", StringComparison.Ordinal) || reason.StartsWith("CONTRACT_CAPTURE_HASH_MISMATCH", StringComparison.Ordinal)
                        ? ApprovedContractSyncStatus.Stale
                        : ApprovedContractSyncStatus.Invalid;
                    item.message = reason;
                    plan.items.Add(item);
                    continue;
                }
                var dependencyHash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                if (!string.Equals(document.asset.asset_guid, item.assetGuid, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(document.asset.dependency_hash, dependencyHash, StringComparison.Ordinal))
                {
                    item.status = ApprovedContractSyncStatus.Stale;
                    item.message = "Prefab GUID or dependency hash changed after approval.";
                    plan.items.Add(item);
                    continue;
                }
                if (!SpatialContractIO.TryCreateApprovedGeometry(item.contractPath, descriptor, out var imported, out reason))
                {
                    item.status = ApprovedContractSyncStatus.Invalid;
                    item.message = reason;
                    plan.items.Add(item);
                    continue;
                }
                item.status = Equivalent(descriptor.Geometry, imported) ? ApprovedContractSyncStatus.Unchanged : ApprovedContractSyncStatus.Ready;
                item.message = item.status == ApprovedContractSyncStatus.Unchanged ? "Descriptor already matches the approved contract." : "Approved geometry is ready to import.";
                plan.items.Add(item);
            }
            return plan;
        }

        public static int Apply(ApprovedContractSyncPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var ready = plan.items.Where(item => item.status == ApprovedContractSyncStatus.Ready && item.descriptor != null).ToArray();
            if (ready.Length == 0) return 0;
            var imports = new List<(ApprovedContractSyncItem item, DecorGeometryProfile geometry)>();
            foreach (var item in ready)
            {
                if (!SpatialContractIO.TryCreateApprovedGeometry(item.contractPath, item.descriptor, out var geometry, out var reason))
                {
                    item.status = ApprovedContractSyncStatus.Invalid;
                    item.message = reason;
                    throw new InvalidOperationException($"Approved contract changed after preview: {item.assetGuid}: {reason}");
                }
                imports.Add((item, geometry));
            }

            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Sync approved spatial contracts");
            Undo.RecordObjects(imports.Select(value => (UnityEngine.Object)value.item.descriptor).ToArray(), "Sync approved spatial contracts");
            foreach (var value in imports)
            {
                value.item.descriptor.ConfigureGeometry(value.geometry);
                EditorUtility.SetDirty(value.item.descriptor);
                value.item.status = ApprovedContractSyncStatus.Applied;
                value.item.message = "Approved geometry imported.";
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return imports.Count;
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) root = "Assets/SpatialContracts/Assets";
            if (Path.IsPathRooted(root)) return Path.GetFullPath(root);
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", root));
        }

        private static string PathGuid(string path)
        {
            var name = Path.GetFileName(path);
            const string suffix = ".spatial.json";
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var value = name[..^suffix.Length];
            return value.Length == 32 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : string.Empty;
        }

        private static bool Equivalent(DecorGeometryProfile left, DecorGeometryProfile right)
        {
            if (left == null || right == null || !left.reviewed || !string.Equals(left.dependencyHash, right.dependencyHash, StringComparison.Ordinal)) return false;
            if (!Near(left.forwardAxis, right.forwardAxis) || !Near(left.upAxis, right.upAxis) || !Near(left.pivotOffset, right.pivotOffset)) return false;
            if (!Frame(left.bottomContact, right.bottomContact) ||
                !Frame(left.backContact, right.backContact) ||
                !Frame(left.topContact, right.topContact)) return false;
            var leftBoxes = left.collisionProxies ?? new List<OrientedBoxProxy>();
            var rightBoxes = right.collisionProxies ?? new List<OrientedBoxProxy>();
            if (leftBoxes.Count != rightBoxes.Count) return false;
            for (var index = 0; index < leftBoxes.Count; index++)
            {
                var a = leftBoxes[index]; var b = rightBoxes[index];
                if (a.proxyId != b.proxyId || !Near(a.localCenter, b.localCenter) || !Near(a.size, b.size) || Quaternion.Angle(a.localRotation, b.localRotation) > 0.001f) return false;
            }
            var leftContacts = left.EffectiveContacts.OrderBy(item => item.ruleId, StringComparer.Ordinal).ToArray();
            var rightContacts = right.EffectiveContacts.OrderBy(item => item.ruleId, StringComparer.Ordinal).ToArray();
            if (leftContacts.Length != rightContacts.Length) return false;
            for (var index = 0; index < leftContacts.Length; index++)
            {
                var a = leftContacts[index]; var b = rightContacts[index];
                if (a.ruleId != b.ruleId || a.frameId != b.frameId || a.requirement != b.requirement ||
                    !Mathf.Approximately(a.minimumGap, b.minimumGap) || !Mathf.Approximately(a.maximumGap, b.maximumGap) ||
                    !Mathf.Approximately(a.maximumPenetration, b.maximumPenetration) || !Mathf.Approximately(a.minimumSupportCoverage, b.minimumSupportCoverage)) return false;
            }
            return true;
        }

        private static bool Frame(ContactFrame left, ContactFrame right) => left != null && right != null && left.frameId == right.frameId && Near(left.localPoint, right.localPoint) && Near(left.localNormal, right.localNormal) && Near(left.localTangent, right.localTangent) && Vector2.Distance(left.size, right.size) <= 0.00001f;
        private static bool Near(Vector3 left, Vector3 right) => Vector3.Distance(left, right) <= 0.00001f;
    }
}
