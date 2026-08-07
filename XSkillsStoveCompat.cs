using System;
using System.Reflection;
using Vintagestory.API.Common;

namespace StainsStovetop;

/// <summary>
/// Soft integration with xSkills / xSkills Fork cooking Ownable + cook-time multipliers.
/// No hard assembly reference — resolves types only when the mod is loaded.
/// Contract: Fork <c>BlockCookingContainerPatch.DoSmeltPostfix</c> needs
/// <c>ISlotProvider</c> that is also <see cref="InventoryBase"/> with <c>Pos</c>,
/// plus <c>BlockEntityBehaviorOwnable.Owner</c> set (firepit: InventorySmelting.OnInvOpened).
/// Source: xSkills Fork 1.0.82 CookingUtil / InventorySmeltingPatch / BlockEntityBehaviorOwnable.
/// </summary>
public static class XSkillsStoveCompat
{
    private static MethodInfo? cookTimeMultMethod;
    private static bool cookTimeMultResolved;
    private static PropertyInfo? ownableOwnerProp;
    private static bool ownableOwnerResolved;

    /// <summary>
    /// Firepit-equivalent: claim Ownable for the interacting player when idle or unowned.
    /// Does not steal ownership while any burner is actively cooking.
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
        // Mirror InventorySmeltingPatch.OnInvOpened: set if null or not cooking.
        if (current != null && anyBurnerCooking) return;

        ownableOwnerProp.SetValue(beh, player);
        be.MarkDirty(true);
    }

    /// <summary>
    /// Soft-call <c>XSkills.CookingUtil.GetCookingTimeMultiplier(BlockEntity)</c> (Fast Food / Well Done).
    /// Returns 1 when xSkills is absent or reflection fails.
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
