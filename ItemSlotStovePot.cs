using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Burner pot slot — accepts cooking containers only; free take-out like the firepit input slot.
/// Uses Survival (not Input→Output linking) so shift-click returns the pot to the player inventory.
/// </summary>
public class ItemSlotStovePot : ItemSlotSurvival
{
    public ItemSlotStovePot(InventoryBase inventory) : base(inventory)
    {
    }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        if (!base.CanHold(sourceSlot)) return false;
        return IsCookingPot(sourceSlot.Itemstack);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        if (!base.CanTakeFrom(sourceSlot, priority)) return false;
        return IsCookingPot(sourceSlot.Itemstack);
    }

    private static bool IsCookingPot(ItemStack? stack)
    {
        return InventoryStainsStove.HasAttr(stack, "cookingContainerSlots")
               || stack?.Collectible is BlockCookingContainer;
    }
}
