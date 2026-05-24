namespace ExpandableX.Core
{
    public class DynamicLayout : Layout
    {
        public override string GroupId { get; }

        public DynamicLayout(string groupId)
        {
            GroupId = groupId;
        }
    }
}
