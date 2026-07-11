using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [DisallowMultipleComponent]
    public sealed class PreviewPlacementMarker : MonoBehaviour
    {
        public string placementId;
        public string elementId;
        public bool locked;
    }
}
