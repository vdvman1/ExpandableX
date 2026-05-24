using System;
using System.Collections.Generic;
using Game.Core.Serialization;

namespace ExpandableX.Core
{
    public class ExpandableXBuildingConfiguration
        : IBuildingConfiguration,
          IEquatable<IEntityConfiguration>,
          IEquatable<ExpandableXBuildingConfiguration>
    {
        private Dictionary<string, SlotRole> _SlotRoles = new Dictionary<string, SlotRole>();

        public IReadOnlyDictionary<string, SlotRole> SlotRoles => _SlotRoles;

        public void SetRole(string slotId, SlotRole role)
        {
            _SlotRoles[slotId] = role;
        }

        public bool TryGetRole(string slotId, out SlotRole role)
        {
            return _SlotRoles.TryGetValue(slotId, out role);
        }

        public void Sync(ISerializationVisitor visitor)
        {
            int count = _SlotRoles.Count;
            visitor.SyncInt_4(ref count);

            if (visitor.Writing)
            {
                foreach (KeyValuePair<string, SlotRole> kv in _SlotRoles)
                {
                    string key = kv.Key;
                    byte value = (byte)kv.Value;
                    visitor.SyncString_4(ref key);
                    visitor.SyncByte_1(ref value);
                }
            }
            else
            {
                _SlotRoles.Clear();
                for (int i = 0; i < count; i++)
                {
                    string key = null;
                    byte value = 0;
                    visitor.SyncString_4(ref key);
                    visitor.SyncByte_1(ref value);
                    _SlotRoles[key] = (SlotRole)value;
                }
            }
        }

        public bool Equals(IEntityConfiguration other)
        {
            return other is ExpandableXBuildingConfiguration cfg && Equals(cfg);
        }

        public bool Equals(ExpandableXBuildingConfiguration other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (_SlotRoles.Count != other._SlotRoles.Count) return false;
            foreach (KeyValuePair<string, SlotRole> kv in _SlotRoles)
            {
                if (!other._SlotRoles.TryGetValue(kv.Key, out SlotRole otherRole) || otherRole != kv.Value)
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is IEntityConfiguration cfg && Equals(cfg);
        }

        public override int GetHashCode()
        {
            int hash = 0;
            foreach (KeyValuePair<string, SlotRole> kv in _SlotRoles)
            {
                hash ^= unchecked(kv.Key.GetHashCode() * 31 + (int)kv.Value);
            }
            return hash;
        }
    }
}
