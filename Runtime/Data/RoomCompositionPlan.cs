using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [Serializable]
    public sealed class CompositionElement
    {
        public string elementId = Guid.NewGuid().ToString("N");
        public string descriptorId;
        public DecorRole role = DecorRole.Clutter;
        public CompositionRelation relation = CompositionRelation.Independent;
        public string anchorElementId;
        [Min(1)] public int count = 1;
        public PreferredZone preferredZone = PreferredZone.Any;
        public string preferredSurfaceId;
        [Min(0f)] public float spacing = 1f;
        public bool locked;
    }

    [CreateAssetMenu(fileName = "RoomCompositionPlan", menuName = "Concept Room Decorator/Room Composition Plan")]
    public sealed class RoomCompositionPlan : ScriptableObject
    {
        [SerializeField] private ConceptRoom room;
        [SerializeField] private RoomConceptBrief conceptBrief;
        [SerializeField] private DecorCatalog catalog;
        [SerializeField] private int seed = 12345;
        [Range(0f, 1f)] [SerializeField] private float density = 0.5f;
        [SerializeField] private List<CompositionElement> elements = new();

        public ConceptRoom Room => room;
        public RoomConceptBrief ConceptBrief => conceptBrief;
        public DecorCatalog Catalog => catalog;
        public int Seed => seed;
        public float Density => density;
        public IReadOnlyList<CompositionElement> Elements => elements;

        public void Configure(ConceptRoom targetRoom, RoomConceptBrief brief, DecorCatalog sourceCatalog, int randomSeed, float targetDensity, IEnumerable<CompositionElement> sourceElements)
        {
            room = targetRoom;
            conceptBrief = brief;
            catalog = sourceCatalog;
            seed = randomSeed;
            density = Mathf.Clamp01(targetDensity);
            elements = new List<CompositionElement>(sourceElements ?? Array.Empty<CompositionElement>());
        }
    }
}
