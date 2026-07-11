using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [DisallowMultipleComponent]
    public sealed class RoomObservationPoint : MonoBehaviour
    {
        [SerializeField] private string label = "Entrance";
        [Range(20f, 120f)] [SerializeField] private float fieldOfView = 60f;
        [SerializeField] private bool primary = true;

        public string Label => label;
        public float FieldOfView => fieldOfView;
        public bool Primary => primary;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = primary ? Color.yellow : Color.white;
            Gizmos.DrawRay(transform.position, transform.forward * 2f);
            Gizmos.DrawWireSphere(transform.position, 0.08f);
        }
    }
}
