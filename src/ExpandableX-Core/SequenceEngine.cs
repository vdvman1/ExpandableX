using System;
using System.Collections.Generic;

namespace ExpandableX.Core
{
    public enum ExpansionKind { Expand, Shrink }

    /// <summary>
    /// One expand or shrink the player could attempt from the current layout. A non-null
    /// <see cref="BlockedReason"/> means it's blocked (and supplies the UI text); <see cref="Available"/>
    /// is just its absence. <see cref="SkippedLayoutIds"/> names locked intermediate steps that were
    /// skipped over to reach <see cref="TargetLayout"/>.
    /// </summary>
    public sealed record ExpansionOption(
        ExpansionKind Kind,
        TileDirection Direction,
        Layout TargetLayout,
        string? BlockedReason,
        IReadOnlyList<string> SkippedLayoutIds)
    {
        public bool Available => BlockedReason is null;
    }

    /// <summary>
    /// Computes the expand/shrink options for a building currently at a given <see cref="Layout"/>,
    /// across a registration's <see cref="Expansion.Sequence"/>s. Conditions gate whole sequences and
    /// individual steps; a locked intermediate step is skipped over rather than blocking the ones past
    /// it. Chains (dynamic layouts) are handled separately and come with the AND gate.
    /// </summary>
    public static class SequenceEngine
    {
        public static IReadOnlyList<ExpansionOption> OptionsFor(Registration registration, Layout currentLayout)
        {
            var options = new List<ExpansionOption>();
            foreach (Expansion expansion in registration.Expansions)
            {
                if (expansion is Expansion.Sequence sequence)
                {
                    AddSequenceOptions(options, sequence, currentLayout);
                }
            }
            return options;
        }

        private static void AddSequenceOptions(List<ExpansionOption> options, Expansion.Sequence sequence, Layout currentLayout)
        {
            // A whole-sequence condition (e.g. "hex scenario") that isn't met means this sequence
            // doesn't apply here at all — skip it silently, rather than offer a blocked option. Two
            // sequences can share a layout (Half is in both square and hex); only the applicable one
            // should surface. Per-step locks (e.g. research) are handled below as skip-locked/blocked.
            if (FirstUnmet(sequence.Conditions) is not null)
            {
                return;
            }

            int index = IndexOfStep(sequence.Steps, currentLayout);
            if (index < 0)
            {
                return; // current layout isn't part of this sequence
            }

            AddDirection(options, sequence, index, +1, ExpansionKind.Expand);
            AddDirection(options, sequence, index, -1, ExpansionKind.Shrink);
        }

        private static void AddDirection(
            List<ExpansionOption> options, Expansion.Sequence sequence, int from, int step, ExpansionKind kind)
        {
            int immediate = from + step;
            if (immediate < 0 || immediate >= sequence.Steps.Count)
            {
                return; // already at the end of the sequence in this direction
            }

            // Scan for the first reachable step in this direction, skipping locked intermediates.
            var skipped = new List<string>();
            int target = -1;
            for (int k = immediate; k >= 0 && k < sequence.Steps.Count; k += step)
            {
                if (FirstUnmet(sequence.Steps[k].Conditions) is null)
                {
                    target = k;
                    break;
                }

                skipped.Add(sequence.Steps[k].Layout.LayoutId);
            }

            if (target < 0)
            {
                string reason = FirstUnmet(sequence.Steps[immediate].Conditions) ?? "no reachable step";
                options.Add(new ExpansionOption(kind, sequence.Direction, sequence.Steps[immediate].Layout, reason, skipped));
                return;
            }

            options.Add(new ExpansionOption(kind, sequence.Direction, sequence.Steps[target].Layout, null, skipped));
        }

        private static string? FirstUnmet(IReadOnlyList<IExpansionCondition> conditions)
        {
            foreach (IExpansionCondition condition in conditions)
            {
                if (!condition.IsMet())
                {
                    return condition.Describe();
                }
            }

            return null;
        }

        private static int IndexOfStep(IReadOnlyList<SequenceStep> steps, Layout currentLayout)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Layout.LayoutId == currentLayout.LayoutId)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
