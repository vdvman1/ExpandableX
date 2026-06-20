using System.Collections.Generic;
using System.Linq;
using ExpandableX.Core;
using Xunit;

namespace ExpandableX.Core.Tests
{
    /// <summary>
    /// Connected-components grouping for the join network, exercised with plain int members / string
    /// faces / string families (the real system adapts placed buildings into the same shape). A face
    /// key is the <i>shared</i> identity of the seam between two pieces: a line A-B-C shares face
    /// "ab" between A and B and "bc" between B and C (each piece's outer faces carry no join face —
    /// border closing).
    /// </summary>
    public class JoinNetworkGraphTests
    {
        private const string F = "fam";

        // Explicit family; distinct name so `M(id, "face1", "face2")` can't bind here with the first
        // face mistaken for the family (a params/normal-form overload-resolution trap).
        private static JoinMember<int, string, string> MF(int id, string family, params string[] faces) =>
            new JoinMember<int, string, string>(id, family, faces);

        // Default family.
        private static JoinMember<int, string, string> M(int id, params string[] faces) => MF(id, F, faces);

        private static IReadOnlyCollection<JoinMember<int, string, string>> Add(params JoinMember<int, string, string>[] m) => m;

        private static readonly IReadOnlyCollection<int> NoneRemoved = new int[0];
        private static readonly IReadOnlyCollection<JoinMember<int, string, string>> NoneAdded =
            new JoinMember<int, string, string>[0];

        private static HashSet<int> Set(params int[] xs) => new HashSet<int>(xs);

        private static bool ContainsSet(IReadOnlyList<IReadOnlyCollection<int>> groups, params int[] members)
        {
            HashSet<int> want = Set(members);
            return groups.Any(g => want.SetEquals(g));
        }

        private static List<HashSet<int>> NetworksOf(JoinNetworkGraph<int, string, string> g) =>
            g.Networks.Select(n => new HashSet<int>(n)).ToList();

        [Fact]
        public void TwoAdjacentSameFamilyPieces_FormOneNetwork()
        {
            var g = new JoinNetworkGraph<int, string, string>();

            NetworkDelta<int> delta = g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);

            Assert.Empty(delta.Dissolved);
            Assert.Single(delta.Formed);
            Assert.True(ContainsSet(delta.Formed, 1, 2));

            List<HashSet<int>> nets = NetworksOf(g);
            Assert.Single(nets);
            Assert.True(nets[0].SetEquals(Set(1, 2)));
        }

        [Fact]
        public void PiecesOfDifferentFamilies_DoNotFuse_EvenSharingAFace()
        {
            var g = new JoinNetworkGraph<int, string, string>();

            // Same face key "ab", but different families: must stay two separate one-piece networks.
            NetworkDelta<int> delta = g.Apply(Add(MF(1, "famA", "ab"), MF(2, "famB", "ab")), NoneRemoved);

            Assert.Equal(2, delta.Formed.Count);
            Assert.True(ContainsSet(delta.Formed, 1));
            Assert.True(ContainsSet(delta.Formed, 2));
        }

        [Fact]
        public void Grow_ReportsTheGrownNetworkDissolvedAndReformedLarger()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);

            // Piece 3 joins piece 2 on face "bc"; piece 2 now carries both faces.
            NetworkDelta<int> delta = g.Apply(Add(M(3, "bc"), M(2, "ab", "bc")), NoneRemoved);

            Assert.True(ContainsSet(delta.Dissolved, 1, 2));
            Assert.True(ContainsSet(delta.Formed, 1, 2, 3));

            List<HashSet<int>> nets = NetworksOf(g);
            Assert.Single(nets);
            Assert.True(nets[0].SetEquals(Set(1, 2, 3)));
        }

        [Fact]
        public void Merge_BridgingPieceFusesTwoNetworksIntoOne()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            // Two separate networks: {1,2} on faces ab, and {4,5} on faces de.
            g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);
            g.Apply(Add(M(4, "de"), M(5, "de")), NoneRemoved);

            // Piece 3 bridges 2 (face bc) and 4 (face cd); 2 and 4 gain the bridging faces.
            NetworkDelta<int> delta = g.Apply(
                Add(M(3, "bc", "cd"), M(2, "ab", "bc"), M(4, "cd", "de")),
                NoneRemoved);

            Assert.Equal(2, delta.Dissolved.Count);
            Assert.True(ContainsSet(delta.Dissolved, 1, 2));
            Assert.True(ContainsSet(delta.Dissolved, 4, 5));
            Assert.Single(delta.Formed);
            Assert.True(ContainsSet(delta.Formed, 1, 2, 3, 4, 5));
        }

        [Fact]
        public void Split_RemovingABridgePieceSplitsTheNetwork()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            // Line 1-2-3: 2 is the bridge (faces ab and bc).
            g.Apply(Add(M(1, "ab"), M(2, "ab", "bc"), M(3, "bc")), NoneRemoved);

            NetworkDelta<int> delta = g.Apply(NoneAdded, new[] { 2 });

            Assert.True(ContainsSet(delta.Dissolved, 1, 2, 3));
            Assert.Equal(2, delta.Formed.Count);
            Assert.True(ContainsSet(delta.Formed, 1));
            Assert.True(ContainsSet(delta.Formed, 3));
        }

        [Fact]
        public void RemovingTheLastConnection_ShrinksWithoutSplitting()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab", "bc"), M(3, "bc")), NoneRemoved);

            // Remove the end piece 3; the rest stays connected as {1,2}.
            NetworkDelta<int> delta = g.Apply(NoneAdded, new[] { 3 });

            Assert.True(ContainsSet(delta.Dissolved, 1, 2, 3));
            Assert.Single(delta.Formed);
            Assert.True(ContainsSet(delta.Formed, 1, 2));
        }

        [Fact]
        public void RemovingEveryMember_LeavesNoNetwork()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);

            NetworkDelta<int> delta = g.Apply(NoneAdded, new[] { 1, 2 });

            Assert.True(ContainsSet(delta.Dissolved, 1, 2));
            Assert.Empty(delta.Formed);
            Assert.Empty(NetworksOf(g));
        }

        [Fact]
        public void UnrelatedNetwork_IsNotReportedWhenAnotherChanges()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);   // network A
            g.Apply(Add(M(4, "de"), M(5, "de")), NoneRemoved);   // network B (untouched below)

            // Grow only network A.
            NetworkDelta<int> delta = g.Apply(Add(M(3, "bc"), M(2, "ab", "bc")), NoneRemoved);

            // Network B ({4,5}) must appear in neither dissolved nor formed.
            Assert.DoesNotContain(delta.Dissolved, g => Set(4, 5).SetEquals(g));
            Assert.DoesNotContain(delta.Formed, g => Set(4, 5).SetEquals(g));
            // It still exists in the graph.
            Assert.Contains(NetworksOf(g), n => n.SetEquals(Set(4, 5)));
        }

        [Fact]
        public void PasteOrderIndependence_MiddlePieceLast_StillOneNetwork()
        {
            // Simulate an unbatched paste arriving ends-first: 1, then 3, then the bridging 2.
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab")), NoneRemoved);                 // dangling end
            g.Apply(Add(M(3, "bc")), NoneRemoved);                 // separate dangling end
            Assert.Equal(2, NetworksOf(g).Count);

            NetworkDelta<int> delta = g.Apply(Add(M(2, "ab", "bc")), NoneRemoved);

            // Adding the middle merges the two dangling ends into one network.
            Assert.Single(delta.Formed);
            Assert.True(ContainsSet(delta.Formed, 1, 2, 3));
            Assert.Single(NetworksOf(g));
        }

        [Fact]
        public void PasteAsOneBatch_FormsOneNetworkRegardlessOfOrder()
        {
            // The same three pieces delivered as a single bunch-edit batch.
            var g = new JoinNetworkGraph<int, string, string>();

            NetworkDelta<int> delta = g.Apply(
                Add(M(3, "bc"), M(1, "ab"), M(2, "ab", "bc")),
                NoneRemoved);

            Assert.Empty(delta.Dissolved);
            Assert.Single(delta.Formed);
            Assert.True(ContainsSet(delta.Formed, 1, 2, 3));
        }

        [Fact]
        public void TryGetNetwork_ReturnsTheComponentContainingAMember()
        {
            // The membership lookup grow/shrink re-validation reads: any member resolves to its whole
            // network (a line 1-2-3 — every member returns {1,2,3}), and a member of a different network
            // resolves only to its own.
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab", "bc"), M(3, "bc")), NoneRemoved);
            g.Apply(Add(M(4, "de"), M(5, "de")), NoneRemoved);

            Assert.True(g.TryGetNetwork(2, out IReadOnlyCollection<int>? net123));
            Assert.True(Set(net123!.ToArray()).SetEquals(Set(1, 2, 3)));

            Assert.True(g.TryGetNetwork(4, out IReadOnlyCollection<int>? net45));
            Assert.True(Set(net45!.ToArray()).SetEquals(Set(4, 5)));
        }

        [Fact]
        public void TryGetNetwork_ReturnsFalseForAnUntrackedMember()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab")), NoneRemoved);

            Assert.False(g.TryGetNetwork(99, out IReadOnlyCollection<int>? net));
            Assert.Null(net);
        }

        [Fact]
        public void TryGetNetwork_AfterRemoval_NoLongerResolvesTheRemovedMember()
        {
            var g = new JoinNetworkGraph<int, string, string>();
            g.Apply(Add(M(1, "ab"), M(2, "ab", "bc"), M(3, "bc")), NoneRemoved);

            g.Apply(NoneAdded, new[] { 3 });

            Assert.False(g.TryGetNetwork(3, out _));
            Assert.True(g.TryGetNetwork(1, out IReadOnlyCollection<int>? net));
            Assert.True(Set(net!.ToArray()).SetEquals(Set(1, 2)));
        }
    }
}
