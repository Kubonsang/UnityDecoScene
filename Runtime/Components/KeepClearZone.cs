using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
    public sealed class KeepClearZone : MonoBehaviour
    {
        [SerializeField] private string reason = "Keep clear";
        [SerializeField] private BoxCollider volume;

        public string Reason => reason;
        public BoxCollider Volume => volume;

        public Bounds WorldBounds => volume != null ? volume.bounds : new Bounds(transform.position, Vector3.zero);

        private void Reset()
        {
            volume = GetComponent<BoxCollider>();
            volume.isTrigger = true;
        }

        private void OnValidate()
        {
            if (volume == null) volume = GetComponent<BoxCollider>();
            if (volume != null) volume.isTrigger = true;
        }

        private void OnDrawGizmosSelected()
        {
            if (volume == null) return;
            var oldMatrix = Gizmos.matrix;
            Gizmos.matrix = volume.transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.35f, 0.1f, 0.65f);
            Gizmos.DrawWireCube(volume.center, volume.size);
            Gizmos.matrix = oldMatrix;
        }
    }
}
