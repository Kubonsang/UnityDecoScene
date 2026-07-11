using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [CreateAssetMenu(fileName = "DecorCatalog", menuName = "Concept Room Decorator/Decor Catalog")]
    public sealed class DecorCatalog : ScriptableObject
    {
        [SerializeField] private List<DecorAssetDescriptor> assets = new();

        public IReadOnlyList<DecorAssetDescriptor> Assets => assets;

        public DecorAssetDescriptor Find(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId)) return null;
            return assets.Find(item => item != null && item.AssetId == assetId);
        }

        public void ReplaceAll(IEnumerable<DecorAssetDescriptor> descriptors)
        {
            assets = new List<DecorAssetDescriptor>(descriptors);
        }
    }
}
