// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Timing;
using osu.Game.Rulesets.Edit;
using osu.Game.Rulesets.Objects;
using osu.Game.Skinning;

namespace osu.Game.Screens.Edit
{
    public class EditorBeatmap : Component, IBeatmap, IBeatSnapProvider
    {
        /// <summary>
        /// Invoked when a <see cref="HitObject"/> is added to this <see cref="EditorBeatmap"/>.
        /// </summary>
        public event Action<HitObject> HitObjectAdded;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> is removed from this <see cref="EditorBeatmap"/>.
        /// </summary>
        public event Action<HitObject> HitObjectRemoved;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> is updated.
        /// </summary>
        public event Action<HitObject> HitObjectUpdated;

        /// <summary>
        /// All currently selected <see cref="HitObject"/>s.
        /// </summary>
        public readonly BindableList<HitObject> SelectedHitObjects = new BindableList<HitObject>();

        /// <summary>
        /// The current placement. Null if there's no active placement.
        /// </summary>
        public readonly Bindable<HitObject> PlacementObject = new Bindable<HitObject>();

        public readonly IBeatmap PlayableBeatmap;

        public readonly ISkin BeatmapSkin;

        [Resolved]
        private BindableBeatDivisor beatDivisor { get; set; }

        private readonly IBeatmapProcessor beatmapProcessor;

        private readonly Dictionary<HitObject, Bindable<double>> startTimeBindables = new Dictionary<HitObject, Bindable<double>>();

        public EditorBeatmap(IBeatmap playableBeatmap, ISkin beatmapSkin = null)
        {
            PlayableBeatmap = playableBeatmap;
            BeatmapSkin = beatmapSkin;

            beatmapProcessor = playableBeatmap.BeatmapInfo.Ruleset?.CreateInstance().CreateBeatmapProcessor(PlayableBeatmap);

            foreach (var obj in HitObjects)
                trackStartTime(obj);
        }

        public BeatmapInfo BeatmapInfo
        {
            get => PlayableBeatmap.BeatmapInfo;
            set => PlayableBeatmap.BeatmapInfo = value;
        }

        public BeatmapMetadata Metadata => PlayableBeatmap.Metadata;

        public ControlPointInfo ControlPointInfo => PlayableBeatmap.ControlPointInfo;

        public List<BreakPeriod> Breaks => PlayableBeatmap.Breaks;

        public double TotalBreakTime => PlayableBeatmap.TotalBreakTime;

        public IReadOnlyList<HitObject> HitObjects => PlayableBeatmap.HitObjects;

        public IEnumerable<BeatmapStatistic> GetStatistics() => PlayableBeatmap.GetStatistics();

        public IBeatmap Clone() => (EditorBeatmap)MemberwiseClone();

        private IList mutableHitObjects => (IList)PlayableBeatmap.HitObjects;

        private readonly HashSet<HitObject> pendingUpdates = new HashSet<HitObject>();

        private bool isBatchApplying;

        /// <summary>
        /// Adds a collection of <see cref="HitObject"/>s to this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="hitObjects">The <see cref="HitObject"/>s to add.</param>
        public void AddRange(IEnumerable<HitObject> hitObjects)
        {
            ApplyBatchChanges(_ =>
            {
                foreach (var h in hitObjects)
                    Add(h);
            });
        }

        /// <summary>
        /// Adds a <see cref="HitObject"/> to this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> to add.</param>
        public void Add(HitObject hitObject)
        {
            // Preserve existing sorting order in the beatmap
            var insertionIndex = findInsertionIndex(PlayableBeatmap.HitObjects, hitObject.StartTime);
            Insert(insertionIndex + 1, hitObject);
        }

        /// <summary>
        /// Inserts a <see cref="HitObject"/> into this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <remarks>
        /// It is the invoker's responsibility to make sure that <see cref="HitObject"/> sorting order is maintained.
        /// </remarks>
        /// <param name="index">The index to insert the <see cref="HitObject"/> at.</param>
        /// <param name="hitObject">The <see cref="HitObject"/> to insert.</param>
        public void Insert(int index, HitObject hitObject)
        {
            trackStartTime(hitObject);

            mutableHitObjects.Insert(index, hitObject);

            if (isBatchApplying)
                batchPendingInserts.Add(hitObject);
            else
            {
                // must be run after any change to hitobject ordering
                beatmapProcessor?.PreProcess();
                processHitObject(hitObject);
                beatmapProcessor?.PostProcess();

                HitObjectAdded?.Invoke(hitObject);
            }
        }

        /// <summary>
        /// Updates a <see cref="HitObject"/>, invoking <see cref="HitObject.ApplyDefaults"/> and re-processing the beatmap.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> to update.</param>
        public void UpdateHitObject([NotNull] HitObject hitObject)
        {
            pendingUpdates.Add(hitObject);
        }

        /// <summary>
        /// Removes a <see cref="HitObject"/> from this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> to remove.</param>
        /// <returns>True if the <see cref="HitObject"/> has been removed, false otherwise.</returns>
        public bool Remove(HitObject hitObject)
        {
            int index = FindIndex(hitObject);

            if (index == -1)
                return false;

            RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Removes a collection of <see cref="HitObject"/>s to this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="hitObjects">The <see cref="HitObject"/>s to remove.</param>
        public void RemoveRange(IEnumerable<HitObject> hitObjects)
        {
            ApplyBatchChanges(_ =>
            {
                foreach (var h in hitObjects)
                    Remove(h);
            });
        }

        /// <summary>
        /// Finds the index of a <see cref="HitObject"/> in this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> to search for.</param>
        /// <returns>The index of <paramref name="hitObject"/>.</returns>
        public int FindIndex(HitObject hitObject) => mutableHitObjects.IndexOf(hitObject);

        /// <summary>
        /// Removes a <see cref="HitObject"/> at an index in this <see cref="EditorBeatmap"/>.
        /// </summary>
        /// <param name="index">The index of the <see cref="HitObject"/> to remove.</param>
        public void RemoveAt(int index)
        {
            var hitObject = (HitObject)mutableHitObjects[index];

            mutableHitObjects.RemoveAt(index);

            var bindable = startTimeBindables[hitObject];
            bindable.UnbindAll();
            startTimeBindables.Remove(hitObject);

            if (isBatchApplying)
                batchPendingDeletes.Add(hitObject);
            else
            {
                // must be run after any change to hitobject ordering
                beatmapProcessor?.PreProcess();
                processHitObject(hitObject);
                beatmapProcessor?.PostProcess();

                HitObjectRemoved?.Invoke(hitObject);
            }
        }

        private readonly List<HitObject> batchPendingInserts = new List<HitObject>();

        private readonly List<HitObject> batchPendingDeletes = new List<HitObject>();

        /// <summary>
        /// Apply a batch of operations in one go, without performing Pre/Postprocessing each time.
        /// </summary>
        /// <param name="applyFunction">The function which will apply the batch changes.</param>
        public void ApplyBatchChanges(Action<EditorBeatmap> applyFunction)
        {
            if (isBatchApplying)
                throw new InvalidOperationException("Attempting to perform a batch application from within an existing batch");

            isBatchApplying = true;

            applyFunction(this);

            beatmapProcessor?.PreProcess();

            foreach (var h in batchPendingDeletes) processHitObject(h);
            foreach (var h in batchPendingInserts) processHitObject(h);

            beatmapProcessor?.PostProcess();

            foreach (var h in batchPendingDeletes) HitObjectRemoved?.Invoke(h);
            foreach (var h in batchPendingInserts) HitObjectAdded?.Invoke(h);

            batchPendingDeletes.Clear();
            batchPendingInserts.Clear();

            isBatchApplying = false;
        }

        /// <summary>
        /// Clears all <see cref="HitObjects"/> from this <see cref="EditorBeatmap"/>.
        /// </summary>
        public void Clear() => RemoveRange(HitObjects.ToArray());

        protected override void Update()
        {
            base.Update();

            // debounce updates as they are common and may come from input events, which can run needlessly many times per update frame.
            if (pendingUpdates.Count > 0)
            {
                beatmapProcessor?.PreProcess();

                foreach (var hitObject in pendingUpdates)
                    processHitObject(hitObject);

                beatmapProcessor?.PostProcess();

                // explicitly needs to be fired after PostProcess
                foreach (var hitObject in pendingUpdates)
                    HitObjectUpdated?.Invoke(hitObject);

                pendingUpdates.Clear();
            }
        }

        private void processHitObject(HitObject hitObject) => hitObject.ApplyDefaults(ControlPointInfo, BeatmapInfo.BaseDifficulty);

        private void trackStartTime(HitObject hitObject)
        {
            startTimeBindables[hitObject] = hitObject.StartTimeBindable.GetBoundCopy();
            startTimeBindables[hitObject].ValueChanged += _ =>
            {
                // For now we'll remove and re-add the hitobject. This is not optimal and can be improved if required.
                mutableHitObjects.Remove(hitObject);

                var insertionIndex = findInsertionIndex(PlayableBeatmap.HitObjects, hitObject.StartTime);
                mutableHitObjects.Insert(insertionIndex + 1, hitObject);

                UpdateHitObject(hitObject);
            };
        }

        private int findInsertionIndex(IReadOnlyList<HitObject> list, double startTime)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].StartTime > startTime)
                    return i - 1;
            }

            return list.Count - 1;
        }

        public double SnapTime(double time, double? referenceTime)
        {
            var timingPoint = ControlPointInfo.TimingPointAt(referenceTime ?? time);
            var beatLength = timingPoint.BeatLength / BeatDivisor;

            return timingPoint.Time + (int)Math.Round((time - timingPoint.Time) / beatLength, MidpointRounding.AwayFromZero) * beatLength;
        }

        public double GetBeatLengthAtTime(double referenceTime) => ControlPointInfo.TimingPointAt(referenceTime).BeatLength / BeatDivisor;

        public int BeatDivisor => beatDivisor?.Value ?? 1;

        /// <summary>
        /// Update all hit objects with potentially changed difficulty or control point data.
        /// </summary>
        public void UpdateBeatmap()
        {
            foreach (var h in HitObjects)
                pendingUpdates.Add(h);
        }
    }
}
