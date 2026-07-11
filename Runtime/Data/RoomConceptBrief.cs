using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [CreateAssetMenu(fileName = "RoomConceptBrief", menuName = "Concept Room Decorator/Room Concept Brief")]
    public sealed class RoomConceptBrief : ScriptableObject
    {
        [SerializeField] private string conceptTitle = "Untitled Room";
        [TextArea(2, 5)] [SerializeField] private string roomPurpose;
        [TextArea(2, 5)] [SerializeField] private string occupantsAndFaction;
        [TextArea(2, 6)] [SerializeField] private string storyOrEvidence;
        [SerializeField] private List<string> moodKeywords = new();
        [SerializeField] private List<string> materialKeywords = new();
        [SerializeField] private List<string> colorKeywords = new();
        [SerializeField] private string heroSubject;
        [SerializeField] private List<string> requiredMotifs = new();
        [SerializeField] private List<string> forbiddenMotifs = new();
        [SerializeField] private List<Texture2D> referenceImages = new();
        [Range(0, 100)] [SerializeField] private int minimumMoodScore = 70;
        [Range(0, 100)] [SerializeField] private int minimumStyleScore = 70;
        [Range(0, 100)] [SerializeField] private int minimumStoryScore = 70;
        [Range(0, 100)] [SerializeField] private int minimumCompositionScore = 70;

        public string ConceptTitle => conceptTitle;
        public string RoomPurpose => roomPurpose;
        public string OccupantsAndFaction => occupantsAndFaction;
        public string StoryOrEvidence => storyOrEvidence;
        public IReadOnlyList<string> MoodKeywords => moodKeywords;
        public IReadOnlyList<string> MaterialKeywords => materialKeywords;
        public IReadOnlyList<string> ColorKeywords => colorKeywords;
        public string HeroSubject => heroSubject;
        public IReadOnlyList<string> RequiredMotifs => requiredMotifs;
        public IReadOnlyList<string> ForbiddenMotifs => forbiddenMotifs;
        public IReadOnlyList<Texture2D> ReferenceImages => referenceImages;
        public int MinimumMoodScore => minimumMoodScore;
        public int MinimumStyleScore => minimumStyleScore;
        public int MinimumStoryScore => minimumStoryScore;
        public int MinimumCompositionScore => minimumCompositionScore;
    }
}
