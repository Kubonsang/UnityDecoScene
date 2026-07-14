using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [Serializable]
    public sealed class OrientedBoxProxy
    {
        public string proxyId;
        public Vector3 localCenter;
        public Vector3 size = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;

        public OrientedBoxProxy() { }

        public OrientedBoxProxy(string id, Vector3 center, Vector3 proxySize, Quaternion rotation)
        {
            proxyId = id;
            localCenter = center;
            size = proxySize;
            localRotation = rotation;
        }
    }

    [Serializable]
    public sealed class ContactFrame
    {
        public string frameId;
        public Vector3 localPoint;
        public Vector3 localNormal = Vector3.up;
        public Vector3 localTangent = Vector3.right;
        public Vector2 size = Vector2.one;
    }

    [Serializable]
    public sealed class ContactRules
    {
        public string ruleId;
        public string frameId;
        public ContactRequirement requirement = ContactRequirement.FloorSupported;
        [Min(0f)] public float minimumGap;
        [Min(0f)] public float maximumGap = 0.01f;
        [Min(0f)] public float maximumPenetration;
        [Range(0f, 1f)] public float minimumSupportCoverage = 0.6f;

        public static ContactRules Defaults(ContactRequirement requirement)
        {
            return requirement switch
            {
                ContactRequirement.WallMounted => new ContactRules { requirement = requirement, minimumGap = 0.005f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.WallBacked => new ContactRules { requirement = requirement, minimumGap = 0.01f, maximumGap = 0.05f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.CeilingMounted => new ContactRules { requirement = requirement, minimumGap = 0f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.FreeStanding => new ContactRules { requirement = requirement, minimumGap = 0f, maximumGap = float.MaxValue, maximumPenetration = 0f, minimumSupportCoverage = 0f },
                _ => new ContactRules { requirement = ContactRequirement.FloorSupported, minimumGap = 0f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f }
            };
        }
    }

    [Serializable]
    public sealed class DecorGeometryProfile
    {
        public const int ContractVersion = 2;

        public int version = ContractVersion;
        public GeometrySource source = GeometrySource.Unknown;
        public List<OrientedBoxProxy> collisionProxies = new();
        public Vector3 forwardAxis = Vector3.forward;
        public Vector3 upAxis = Vector3.up;
        public Vector3 pivotOffset;
        public ContactFrame bottomContact = new() { frameId = "bottom", localNormal = Vector3.down, localTangent = Vector3.right };
        public ContactFrame backContact = new() { frameId = "back", localNormal = Vector3.back, localTangent = Vector3.right };
        public ContactRules contact = ContactRules.Defaults(ContactRequirement.FloorSupported);
        public List<ContactRules> contacts = new();
        [Range(0f, 1f)] public float inferenceConfidence;
        public bool reviewed;
        public string dependencyHash;

        public bool IsUsable => version == ContractVersion && reviewed && collisionProxies != null && collisionProxies.Count > 0;

        public IReadOnlyList<ContactRules> EffectiveContacts
        {
            get
            {
                if (contacts != null && contacts.Count > 0) return contacts;
                return contact != null ? new[] { contact } : Array.Empty<ContactRules>();
            }
        }

        public void Normalize()
        {
            version = ContractVersion;
            forwardAxis = NormalizeAxis(forwardAxis, Vector3.forward);
            upAxis = NormalizeAxis(upAxis, Vector3.up);
            if (Mathf.Abs(Vector3.Dot(forwardAxis, upAxis)) > 0.999f) upAxis = Vector3.up;
            inferenceConfidence = Mathf.Clamp01(inferenceConfidence);
            collisionProxies ??= new List<OrientedBoxProxy>();
            foreach (var proxy in collisionProxies)
            {
                if (proxy == null) continue;
                proxy.size = new Vector3(Mathf.Max(0.001f, Mathf.Abs(proxy.size.x)), Mathf.Max(0.001f, Mathf.Abs(proxy.size.y)), Mathf.Max(0.001f, Mathf.Abs(proxy.size.z)));
                if (proxy.localRotation == default) proxy.localRotation = Quaternion.identity;
            }
            contact ??= ContactRules.Defaults(ContactRequirement.FloorSupported);
            NormalizeContact(contact);
            contacts ??= new List<ContactRules>();
            contacts.RemoveAll(value => value == null);
            foreach (var rules in contacts) NormalizeContact(rules);
            if (contacts.Count > 0) contact = contacts[0];
            bottomContact ??= new ContactFrame { frameId = "bottom", localNormal = Vector3.down, localTangent = Vector3.right };
            backContact ??= new ContactFrame { frameId = "back", localNormal = Vector3.back, localTangent = Vector3.right };
        }

        public ContactFrame FrameFor(ContactRules rules)
        {
            if (rules == null) return null;
            if (string.Equals(rules.frameId, "back", StringComparison.OrdinalIgnoreCase)) return backContact;
            if (string.Equals(rules.frameId, "bottom", StringComparison.OrdinalIgnoreCase)) return bottomContact;
            return rules.requirement is ContactRequirement.WallBacked or ContactRequirement.WallMounted ? backContact : bottomContact;
        }

        private static void NormalizeContact(ContactRules rules)
        {
            rules.minimumGap = Mathf.Max(0f, rules.minimumGap);
            rules.maximumGap = Mathf.Max(rules.minimumGap, rules.maximumGap);
            rules.maximumPenetration = Mathf.Max(0f, rules.maximumPenetration);
            rules.minimumSupportCoverage = Mathf.Clamp01(rules.minimumSupportCoverage);
        }

        private static Vector3 NormalizeAxis(Vector3 axis, Vector3 fallback) => axis.sqrMagnitude < 0.0001f ? fallback : axis.normalized;
    }
}
