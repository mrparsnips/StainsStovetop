using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Soft parity with xSkills Fork's firepit cooking integration (no hard assembly ref).
/// Mirrors InventorySmeltingPatch + BlockEntityFirepitPatch + CookingUtil Ownable contract.
/// Source: xSkills Fork 1.0.82; Ownable must exist on <b>client</b> too (GetOutputText / ItemSlotCooking).
/// </summary>
public static class XSkillsStoveCompat
{
    private static MethodInfo? cookTimeMultMethod;
    private static bool cookTimeMultResolved;
    private static PropertyInfo? ownableOwnerProp;
    private static PropertyInfo? ownableOwnerStringProp;
    private static bool ownablePropsResolved;
    private static Type? itemSlotCookingType;
    private static Type? inputSlotType;

    /// <summary>
    /// Blocktype JSON patches for entityBehaviors often apply server-only (client logs
    /// "stove.json not found"). Ensure Ownable exists on both sides so Canteen Cook /
    /// GetOutputText / ItemSlotCooking can resolve Owner like the firepit.
    /// </summary>
    public static void EnsureOwnableBehavior(BlockEntity be)
    {
        if (be?.Api == null) return;

        Type? ownableType = be.Api.ClassRegistry.GetBlockEntityBehaviorClass("XskillsOwnable");
        if (ownableType == null) return;

        foreach (BlockEntityBehavior existing in be.Behaviors)
        {
            if (ownableType.IsInstanceOfType(existing)) return;
        }

        BlockEntityBehavior beb = be.Api.ClassRegistry.CreateBlockEntityBehavior(be, "XskillsOwnable");
        beb.properties = new JsonObject(new Newtonsoft.Json.Linq.JObject());
        be.Behaviors.Add(beb);
        beb.Initialize(be.Api, beb.properties);
        be.Api.Logger.Notification("[stainsstovetop] Added XskillsOwnable on {0} ({1})", be.Pos, be.Api.Side);
    }

    /// <summary>
    /// After LateInitialize, upgrade pot/cook slots to xSkills types if NewSlot ran too early
    /// (or types were not yet loaded). Preserves ItemStacks. Firepit gets this via Harmony NewSlot.
    /// </summary>
    public static void EnsureFirepitParitySlots(InventoryStainsStove inv)
    {
        if (inv?.Api == null) return;
        EnsureSlotTypesResolved(inv.Api);

        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
        {
            int potIdx = InventoryStainsStove.PotIndex(b);
            int outIdx = InventoryStainsStove.OutputIndex(b);
            ItemSlot pot = inv[potIdx];
            if (inputSlotType != null && !inputSlotType.IsInstanceOfType(pot))
            {
                ItemStack? stack = pot.Itemstack;
                ItemSlot neu = CreateInputSlot(inv, outIdx);
                neu.Itemstack = stack;
                inv[potIdx] = neu;
            }

            int cookStart = InventoryStainsStove.CookingStartIndex(b);
            for (int c = 0; c < 4; c++)
            {
                int idx = cookStart + c;
                ItemSlot cook = inv[idx];
                if (itemSlotCookingType != null && !itemSlotCookingType.IsInstanceOfType(cook))
                {
                    ItemStack? stack = cook.Itemstack;
                    ItemSlot neu = CreateCookingSlot(inv);
                    neu.Itemstack = stack;
                    inv[idx] = neu;
                }
            }
        }
    }

    public static ItemSlot CreateInputSlot(InventoryBase inventory, int outputSlotId)
    {
        EnsureSlotTypesResolved(inventory.Api);
        if (inputSlotType != null)
        {
            try
            {
                return (ItemSlot)Activator.CreateInstance(inputSlotType, inventory)!;
            }
            catch (Exception e)
            {
                inventory.Api?.Logger.Warning("[stainsstovetop] InputSlot create failed: {0}", e.Message);
                inputSlotType = null;
            }
        }
        return new ItemSlotInput(inventory, outputSlotId);
    }

    public static ItemSlot CreateCookingSlot(InventoryBase inventory)
    {
        EnsureSlotTypesResolved(inventory.Api);
        if (itemSlotCookingType != null)
        {
            try
            {
                return (ItemSlot)Activator.CreateInstance(itemSlotCookingType, inventory)!;
            }
            catch (Exception e)
            {
                inventory.Api?.Logger.Warning("[stainsstovetop] ItemSlotCooking create failed: {0}", e.Message);
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

        if (api != null && (itemSlotCookingType == null || inputSlotType == null))
        {
            api.Logger.Notification(
                "[stainsstovetop] xSkills slot types: InputSlot={0} ItemSlotCooking={1}",
                inputSlotType != null, itemSlotCookingType != null);
        }
    }

    public static void TryClaimOwnable(BlockEntity be, IPlayer? player, bool anyBurnerCooking)
    {
        if (player == null || be?.Api == null) return;

        EnsureOwnableBehavior(be);

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

        if (!ownablePropsResolved)
        {
            ownablePropsResolved = true;
            ownableOwnerProp = ownableType.GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public);
            ownableOwnerStringProp = ownableType.GetProperty("OwnerString", BindingFlags.Instance | BindingFlags.Public);
        }
        if (ownableOwnerProp == null || !ownableOwnerProp.CanWrite) return;

        IPlayer? current = ownableOwnerProp.GetValue(beh) as IPlayer;
        if (current != null && anyBurnerCooking) return;

        ownableOwnerProp.SetValue(beh, player);
        // Persist UID for tree sync (ToTreeAttributes prefers live Owner, else OwnerString).
        if (ownableOwnerStringProp != null && ownableOwnerStringProp.CanWrite)
            ownableOwnerStringProp.SetValue(beh, player.PlayerUID);

        be.MarkDirty(true);
    }

    public static bool ContainsFood(ItemStack? inputStack)
    {
        CollectibleObject? input = inputStack?.Collectible;
        if (input == null) return false;

        if (input is BlockCookingContainer || input is BlockBucket || input is BlockLiquidContainerBase)
            return true;

        return input.CombustibleProps?.SmeltedStack?.ResolvedItemstack?.Collectible?.NutritionProps != null;
    }

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
