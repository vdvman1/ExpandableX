using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// One member's contribution to the join graph: its identity, its family, and the set of faces
    /// it joins on. Two members are adjacent iff they share a face <b>and</b> the same family (so
    /// pieces of different families never fuse, even if their join geometry lined up — ADR-0012's
    /// family-keyed adjacency). A <typeparamref name="TFace"/> is the <i>shared</i> identity of a
    /// face: the two pieces meeting at a face must produce the same key from their own sides (the
    /// game adapter canonicalises a join pivot and its counterpart to one key).
    /// </summary>
    public readonly record struct JoinMember<TMember, TFace, TFamily>(
        TMember Id,
        TFamily Family,
        IReadOnlyCollection<TFace> Faces)
        where TMember : notnull
        where TFace : notnull
        where TFamily : notnull;

    /// <summary>
    /// What one <see cref="JoinNetworkGraph{TMember,TFace,TFamily}.Apply"/> changed: the networks
    /// whose membership changed are reported as <see cref="Dissolved"/> (their old member sets) and
    /// <see cref="Formed"/> (the new member sets that replace them). Networks not touched by the
    /// change appear in neither. A grow surfaces as one dissolved + one (larger) formed; a merge as
    /// several dissolved + one formed; a split as one dissolved + several formed; the last member
    /// leaving as one dissolved + zero formed.
    /// </summary>
    public sealed record NetworkDelta<TMember>(
        IReadOnlyList<IReadOnlyCollection<TMember>> Dissolved,
        IReadOnlyList<IReadOnlyCollection<TMember>> Formed)
        where TMember : notnull;

    /// <summary>
    /// Maintains the connected components ("networks") of join-adjacent members under incremental
    /// add/remove, reporting the minimal set of networks that changed each time. Pure graph logic
    /// with no game dependency (so it is unit-testable in isolation, ADR-0012); the game-facing
    /// <see cref="JoinNetworkSystem"/> adapts placed buildings into <see cref="JoinMember{A,B,C}"/>s.
    ///
    /// Adjacency is "share a (face, family) key". A network is just its set of members, tracked by
    /// the set object's own identity — there is no synthetic network id to allocate or overflow.
    /// Each change recomputes only the affected region: the existing networks a change touches are
    /// dissolved, and connected components are recomputed over just their members plus the additions.
    /// The involved set is adjacency-closed (anything adjacent to an involved member is itself
    /// involved), so the recompute is exact.
    /// </summary>
    public sealed class JoinNetworkGraph<TMember, TFace, TFamily>
        where TMember : notnull
        where TFace : notnull
        where TFamily : notnull
    {
        private readonly record struct MemberData(TFamily Family, IReadOnlyCollection<TFace> Faces);

        private readonly record struct AdjacencyKey(TFace Face, TFamily Family);

        private readonly Dictionary<TMember, MemberData> _members = new();
        private readonly Dictionary<AdjacencyKey, HashSet<TMember>> _membersByKey = new();

        // Each network is its member set; a member maps to the very set object it belongs to, and
        // _networks holds those sets by reference identity.
        private readonly Dictionary<TMember, HashSet<TMember>> _networkOf = new();
        private readonly HashSet<HashSet<TMember>> _networks = new();

        /// <summary>The current networks (connected components). Live view for tests/introspection.</summary>
        public IEnumerable<IReadOnlyCollection<TMember>> Networks => _networks;

        /// <summary>Apply a batch of additions and removals, returning the networks that changed.</summary>
        public NetworkDelta<TMember> Apply(
            IReadOnlyCollection<JoinMember<TMember, TFace, TFamily>> added,
            IReadOnlyCollection<TMember> removed)
        {
            // Reference-identity set of the existing networks the change touches.
            var affected = new HashSet<HashSet<TMember>>();

            // Removals: each removed member's current network is affected (it may shrink or split).
            foreach (TMember m in removed)
            {
                if (_networkOf.TryGetValue(m, out HashSet<TMember>? net))
                {
                    affected.Add(net);
                }
            }

            // Additions: an added member is affected, and so is every existing network it touches
            // (its faces line up with an existing member of the same family — a grow or a merge).
            foreach (JoinMember<TMember, TFace, TFamily> a in added)
            {
                foreach (TFace face in a.Faces)
                {
                    if (_membersByKey.TryGetValue(new AdjacencyKey(face, a.Family), out HashSet<TMember>? at))
                    {
                        foreach (TMember neighbour in at)
                        {
                            if (_networkOf.TryGetValue(neighbour, out HashSet<TMember>? net))
                            {
                                affected.Add(net);
                            }
                        }
                    }
                }
            }

            // Dissolve the affected networks and gather the involved members that must be re-grouped.
            var dissolved = new List<IReadOnlyCollection<TMember>>();
            var involved = new HashSet<TMember>();
            foreach (HashSet<TMember> net in affected)
            {
                dissolved.Add(net);
                foreach (TMember m in net)
                {
                    involved.Add(m);
                    _networkOf.Remove(m);
                }
                _networks.Remove(net);
            }

            // Mutate the underlying graph: drop removed members, register added ones.
            foreach (TMember m in removed)
            {
                involved.Remove(m);
                RemoveMember(m);
            }
            foreach (JoinMember<TMember, TFace, TFamily> a in added)
            {
                AddMember(a);
                involved.Add(a.Id);
            }

            // Recompute connected components over just the involved members. The set is
            // adjacency-closed, so a BFS confined to it is exact.
            var formed = new List<IReadOnlyCollection<TMember>>();
            var seen = new HashSet<TMember>();
            var queue = new Queue<TMember>();
            foreach (TMember start in involved)
            {
                if (!seen.Add(start))
                {
                    continue;
                }

                var component = new HashSet<TMember>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    TMember m = queue.Dequeue();
                    component.Add(m);
                    foreach (TMember neighbour in Neighbours(m))
                    {
                        if (seen.Add(neighbour))
                        {
                            queue.Enqueue(neighbour);
                        }
                    }
                }

                _networks.Add(component);
                foreach (TMember m in component)
                {
                    _networkOf[m] = component;
                }
                formed.Add(component);
            }

            return new NetworkDelta<TMember>(dissolved, formed);
        }

        private IEnumerable<TMember> Neighbours(TMember member)
        {
            MemberData data = _members[member];
            foreach (TFace face in data.Faces)
            {
                if (_membersByKey.TryGetValue(new AdjacencyKey(face, data.Family), out HashSet<TMember>? at))
                {
                    foreach (TMember other in at)
                    {
                        if (!EqualityComparer<TMember>.Default.Equals(other, member))
                        {
                            yield return other;
                        }
                    }
                }
            }
        }

        private void AddMember(JoinMember<TMember, TFace, TFamily> member)
        {
            // A re-add of an already-present member replaces it (defensive; the game adds once).
            if (_members.ContainsKey(member.Id))
            {
                RemoveMember(member.Id);
            }

            _members[member.Id] = new MemberData(member.Family, member.Faces);
            foreach (TFace face in member.Faces)
            {
                var key = new AdjacencyKey(face, member.Family);
                if (!_membersByKey.TryGetValue(key, out HashSet<TMember>? at))
                {
                    at = new HashSet<TMember>();
                    _membersByKey[key] = at;
                }
                at.Add(member.Id);
            }
        }

        private void RemoveMember(TMember member)
        {
            if (!_members.TryGetValue(member, out MemberData data))
            {
                return;
            }

            _members.Remove(member);
            foreach (TFace face in data.Faces)
            {
                var key = new AdjacencyKey(face, data.Family);
                if (_membersByKey.TryGetValue(key, out HashSet<TMember>? at) && at.Remove(member) && at.Count == 0)
                {
                    _membersByKey.Remove(key);
                }
            }
        }
    }
}
