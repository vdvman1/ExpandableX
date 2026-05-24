using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    public class StaticLayout : Layout
    {
        public override string GroupId { get; }
        public IReadOnlyList<ConnectorSlot> Slots { get; }

        public StaticLayout(string groupId, IEnumerable<ConnectorSlot> slots)
        {
            GroupId = groupId;
            Slots = slots.ToList();
        }
    }
}
