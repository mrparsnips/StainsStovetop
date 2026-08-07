using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Soft parity with xSkills Fork's firepit cooking integration (no hard assembly ref).
/// Mirrors InventorySmeltingPatch + BlockEntityFirepitPatch + CookingUtil Ownable contract.
/// Source: xSkills Fork 1.0.82 decompiles under docs/research/_xskills_fork_1.0.82/.
/// </summary>
public static class XSkillsStoveCompat
{
    private static MethodInfo? cookTimeMultMethod;
    private static bool cookTimeMultResolved;
    private static PropertyInfo? ownableOwnerProp;
    private static bool ownableOwnerResolved;
    private static Type? itemSlotCookingType;
    private static Type? inputSlotType;

    /// <summary>
    /// Firepit after xSkills: slot 1 = <c>XSkills.InputSlot</c> (Ownable on ActivateSlot).
    /// Without xSkills: vanilla <see cref="ItemSlotInput"/>.
    /// </summary>
    public static ItemSlot CreateInputSlot(InventoryBase inventory, int outputSlotId)
    {
        EnsureSlotTypesResolved(inventory.Api);
        if (inputSlotType != null)
        {
            try
            {
                return (ItemSlot)Activator.CreateInstance(inputSlotType, inventory)!;
            }
            catch
            {
                inputSlotType = null;
            }
        }
        return new ItemSlotInput(inventory, outputSlotId);
    }

    /// <summary>
    /// Firepit after xSkills: slots 3–6 = <c>XSkills.ItemSlotCooking</c> (Canteen Cook stack size).
    /// Without xSkills: vanilla <see cref="ItemSlotWatertight"/>.
    /// </summary>
    public static ItemSlot CreateCookingSlot(InventoryBase inventory)
    {
        EnsureSlotTypesResolved(inventory.Api);
        if (itemSlotCookingType != null)
        {
            try
            {
                return (ItemSlot)Activator.CreateInstance(itemSlotCookingType, inventory)!;
            }
            catch
            {
                itemSlotCookingType = null;
            }
        }
        return new ItemSlotWatertight(inventory, 6f);
    }

    private static void EnsureSlotTypesResolved(ICoreAPI? api)
    {
        if (itemSlotCookingType != null && inputSlotType != null)
            return;

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            itemSlotCookingType ??= asm.GetType("XSkills.ItemSlotCooking");
            inputSlotType ??= asm.GetType("XSkills.InputSlot");
            if (itemSlotCookingType != null && inputSlotType != null) break;
        }
    }

    /// <summary>
    /// InventorySmeltingPatch.OnInvOpened: set Ownable if null or not cooking.
    /// </summary>
    public static void TryClaimOwnable(BlockEntity be, IPlayer? player, bool anyBurnerCooking)
    {
        if (player == null || be?.Api == null) return;

        Type? ownableType = be.Api.ClassRegistry.GetBlockEntityBehaviorClass("XskillsOwnable");
        if (ownableType == null) return;

        BlockEntityBehavior? beh = null;
        foreach (BlockEntityBehavior b in be.Behaviors)
        {
            if (ownableType.IsInstanceOfType(b))
            {
                beh = b;
                break;
            }
        }
        if (beh == null) return;

        if (!ownableOwnerResolved)
        {
            ownableOwnerResolved = true;
            ownableOwnerProp = ownableType.GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public);
        }
        if (ownableOwnerProp == null || !ownableOwnerProp.CanWrite) return;

        IPlayer? current = ownableOwnerProp.GetValue(beh) as IPlayer;
        if (current != null && anyBurnerCooking) return;

        ownableOwnerProp.SetValue(beh, player);
        be.MarkDirty(true);
    }

    /// <summary>
    /// BlockEntityFirepitPatch.ContainsFood — cook-time mult only applies to food in the pot.
    /// </summary>
    public static bool ContainsFood(ItemStack? inputStack)
    {
        CollectibleObject? input = inputStack?.Collectible;
        if (input == null) return false;

        if (input is BlockCookingContainer || input is BlockBucket || input is BlockLiquidContainerBase)
            return true;

        return input.CombustibleProps?.SmeltedStack?.ResolvedItemstack?.Collectible?.NutritionProps != null;
    }

    /// <summary>
    /// Soft-call <c>CookingUtil.GetCookingTimeMultiplier</c> (Fast Food / Well Done).
    /// </summary>
    public static float GetCookingTimeMultiplier(BlockEntity be)
    {
        if (be?.Api == null) return 1f;

        if (!cookTimeMultResolved)
        {
            cookTimeMultResolved = true;
            cookTimeMultMethod = FindCookingTimeMultiplierMethod(be.Api);
        }
        if (cookTimeMultMethod == null) return 1f;

        try
        {
            object? result = cookTimeMultMethod.Invoke(null, new object[] { be });
            if (result is float f) return f;
        }
        catch (Exception e)
        {
            be.Api.Logger.Warning("[stainsstovetop] CookingUtil.GetCookingTimeMultiplier failed: {0}", e.Message);
            cookTimeMultMethod = null;
        }
        return 1f;
    }

    private static MethodInfo? FindCookingTimeMultiplierMethod(ICoreAPI api)
    {
        foreach (Mod mod in api.ModLoader.Mods)
        {
            string id = mod.Info?.ModID ?? "";
            if (id != "xskills" && id != "xskillsfork") continue;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? util = asm.GetType("XSkills.CookingUtil");
                MethodInfo? m = util?.GetMethod(
                    "GetCookingTimeMultiplier",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(BlockEntity) },
                    null);
                if (m != null) return m;
            }
        }
        return null;
    }
}
