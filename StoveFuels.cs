using Vintagestory.API.Common;

namespace StainsStovetop;

/// <summary>
/// Allowed fuels: charcoal, brown coal (lignite), black coal (bituminous).
/// Burn duration/temp come from each item's vanilla combustibleProps.
/// </summary>
public static class StoveFuels
{
    public static bool IsAllowed(ItemStack? stack)
    {
        if (stack?.Collectible?.Code == null) return false;
        string path = stack.Collectible.Code.Path;
        return path == "charcoal"
               || path == "ore-lignite"
               || path == "ore-bituminouscoal";
    }

    public static CombustibleProperties? GetFuelProps(IWorldAccessor world, ItemStack? stack)
    {
        if (!IsAllowed(stack)) return null;
        return stack!.Collectible.GetCombustibleProperties(world, stack, null);
    }
}
