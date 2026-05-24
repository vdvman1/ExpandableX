using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    public class ConnectorSlot
    {
        public string Id { get; }
        public IReadOnlyList<SlotRole> AllowedRoles { get; }
        public SlotRole DefaultRole { get; }

        public ConnectorSlot(string id, IEnumerable<SlotRole> allowedRoles, SlotRole defaultRole)
        {
            Id = id;
            AllowedRoles = allowedRoles.ToList();
            DefaultRole = defaultRole;
        }
    }
}
