using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Shared fuel + 4 burners. Each burner: pot, output, 4 cooking slots (firepit layout).
/// Slot map: 0 fuel; burner b uses 1+b*6 .. 6+b*6 (pot, output, cook0-3).
/// Implements <see cref="ISlotProvider"/> so xSkills <c>BlockCookingContainer.DoSmelt</c>
/// postfixes can resolve Ownable via <c>cookingSlotsProvider is InventoryBase</c>
/// (Fork CookingUtil.GetOwnerFromInventory). Set <see cref="ActiveBurnerForSmelt"/> first.
/// </summary>
public class InventoryStainsStove : InventoryBase, ISlotProvider
{
    public const int BurnerCount = 4;
    public const int SlotsPerBurner = 6; // pot, output, 4 cooking
    public const int TotalSlots = 1 + BurnerCount *SlotsPerBurner;

    private readonly ItemSlot[] slots;

    /// <summary>Burner whose cooking slots <see cref="Slots"/> exposes for DoSmelt / melting APIs.</summary>
    public int ActiveBurnerForSmelt { get; set; }

    public InventoryStainsStove(string inventoryId, ICoreAPI? api) : base(inventoryId, api)
    {
        slots = GenEmptySlots(TotalSlots);
        baseWeight = 4f;
    }

    public ItemSlot FuelSlot => slots[0];

    public ItemSlot PotSlot(int burner) => slots[PotIndex(burner)];
    public ItemSlot OutputSlot(int burner) => slots[OutputIndex(burner)];

    public ItemSlot[] CookingSlots(int burner)
    {
        int start = CookingStartIndex(burner);
        return new[] { slots[start], slots[start + 1], slots[start + 2], slots[start + 3] };
    }

    /// <inheritdoc />
    public ItemSlot[] Slots => CookingSlots(GameMath.Clamp(ActiveBurnerForSmelt, 0, BurnerCount - 1));

    public static int PotIndex(int burner) => 1 + burner * SlotsPerBurner;
    public static int OutputIndex(int burner) => 2 + burner * SlotsPerBurner;
    public static int CookingStartIndex(int burner) => 3 + burner * SlotsPerBurner;

    public void PrepareBurnerForSmelt(int burner)
        => ActiveBurnerForSmelt = GameMath.Clamp(burner, 0, BurnerCount - 1);

    public override int Count => slots.Length;

    public override ItemSlot this[int slotId]
    {
        get
        {
            if (slotId < 0 || slotId >= Count) return null!;
            return slots[slotId];
        }
        set
        {
            if (slotId < 0 || slotId >= Count) throw new ArgumentOutOfRangeException(nameof(slotId));
            slots[slotId] = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    protected override ItemSlot NewSlot(int i)
    {
        if (i == 0) return new ItemSlotCoalFuel(this);

        int burner = (i - 1) / SlotsPerBurner;
        int local = (i - 1) % SlotsPerBurner;
        // Firepit input is ItemSlotInput(inventory, outputSlotId) — CanHold is output-stack
        // compat only, not pot filter. Source: InventorySmelting / ItemSlotInput (VSSurvivalMod).
        return local switch
        {
            0 => new ItemSlotInput(this, OutputIndex(burner)),
            1 => new ItemSlotOutput(this),
            _ => new ItemSlotWatertight(this, 6f)
        };
    }

    public override void DidModifyItemSlot(ItemSlot slot, ItemStack? extractedStack = null)
    {
        base.DidModifyItemSlot(slot, extractedStack);

        for (int b = 0; b < BurnerCount; b++)
        {
            if (slot != PotSlot(b)) continue;

            // ItemAttributes NRE if Collectible is unresolved (client BE packet race).
            if (!HasAttr(slot.Itemstack, "storageType"))
                DiscardCookingSlots(b);
            else
                UpdateCookingSlotsFromPot(b);
        }
    }

    public void UpdateCookingSlotsFromPot(int burner)
    {
        ItemStack? pot = PotSlot(burner).Itemstack;
        int storageType = (int)(EnumItemStorageFlags.General | EnumItemStorageFlags.Agriculture |
                                EnumItemStorageFlags.Alchemy | EnumItemStorageFlags.Jewellery |
                                EnumItemStorageFlags.Metallurgy | EnumItemStorageFlags.Outfit);
        float litres = 6f;
        int maxStack = 999;

        // Skip attr read when Collectible is null (unresolved stack during FromTreeAttributes).
        JsonObject? attrs = SafeItemAttributes(pot);
        if (attrs != null)
        {
            if (attrs["storageType"].Exists)
                storageType = attrs["storageType"].AsInt(storageType);
            litres = attrs["cookingSlotCapacityLitres"].AsFloat(6f);
            maxStack = attrs["maxContainerSlotStackSize"].AsInt(999);
        }

        foreach (ItemSlot cook in CookingSlots(burner))
        {
            cook.StorageType = (EnumItemStorageFlags)storageType;
            cook.MaxSlotStackSize = maxStack;
            if (cook is ItemSlotWatertight wt) wt.capacityLitres = litres;
        }
    }

    public void DiscardCookingSlots(int burner)
    {
        if (Api == null || Pos == null) return;
        Vec3d drop = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
        foreach (ItemSlot cook in CookingSlots(burner))
        {
            if (cook.Itemstack == null) continue;
            Api.World.SpawnItemEntity(cook.Itemstack, drop);
            cook.Itemstack = null;
        }
    }

    public bool HaveCookingContainer(int burner)
        => HasAttr(PotSlot(burner).Itemstack, "cookingContainerSlots");

    /// <summary>
    /// ItemStack.ItemAttributes throws if Collectible is null (common during client BE sync).
    /// </summary>
    public static JsonObject? SafeItemAttributes(ItemStack? stack)
        => stack?.Collectible == null ? null : stack.ItemAttributes;

    public static bool HasAttr(ItemStack? stack, string key)
        => SafeItemAttributes(stack)?.KeyExists(key) == true;

    public override void FromTreeAttributes(ITreeAttribute tree)
    {
        var modified = new List<ItemSlot>();
        ItemSlot[]? loaded =SlotsFromTreeAttributes(tree, slots, modified);
        if (loaded != null) Array.Copy(loaded, slots, Math.Min(loaded.Length, slots.Length));

        // Resolve before reading attributes — client packets can leave Collectible null briefly.
        if (Api?.World != null)
        {
            foreach (ItemSlot slot in slots)
                slot.Itemstack?.ResolveBlockOrItem(Api.World);
        }

        foreach (ItemSlot slot in modified) DidModifyItemSlot(slot);
        for (int b = 0; b < BurnerCount; b++) UpdateCookingSlotsFromPot(b);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        SlotsToTreeAttributes(slots, tree);
    }
}
