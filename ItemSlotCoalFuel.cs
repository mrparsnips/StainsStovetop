using Vintagestory.API.Common;

namespace StainsStovetop;

public class ItemSlotCoalFuel : ItemSlotSurvival
{
    public ItemSlotCoalFuel(InventoryBase inventory) : base(inventory)
    {
    }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return StoveFuels.IsAllowed(sourceSlot.Itemstack) && base.CanHold(sourceSlot);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        return StoveFuels.IsAllowed(sourceSlot.Itemstack) && base.CanTakeFrom(sourceSlot, priority);
    }
}
