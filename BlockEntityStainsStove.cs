using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// 4-burner coal/charcoal stove. Cooking uses BlockCookingContainer.DoSmelt (xSkills path).
/// Pots place on burner pads in-world; firebox shows fuel/flame meshes.
/// </summary>
public class BlockEntityStainsStove : BlockEntityOpenableContainer, IHeatSource
{
    private InventoryStainsStove inventory = null!;
    private GuiDialogStainsStove? clientDialog;
    private StoveClientMeshes? clientMeshes;
    private StovePotsRenderer? potsRenderer;
    private float[][]? potMatrices;
    private int openDialogBurner;
    private bool isDoorOpen;
    private bool dialogOpenedFromWindow;

    public float prevFurnaceTemperature = 20;
    public float furnaceTemperature = 20;
    public int maxTemperature;
    public float fuelBurnTime;
    public float maxFuelBurnTime;
    public float smokeLevel;
    public bool canIgniteFuel;
    public double extinguishedTotalHours = -99;
    public float[] burnerCookingTime = new float[InventoryStainsStove.BurnerCount];
    private bool lastLitForLight;

    public bool IsBurning => fuelBurnTime > 0;
    public bool IsSmoldering => canIgniteFuel;

    public override string InventoryClassName => "stainsstovetop";
    public override InventoryBase Inventory => inventory;
    public virtual string DialogTitle => Lang.Get("stainsstovetop:dialog-title");

    public BlockEntityStainsStove()
    {
        inventory = new InventoryStainsStove(null!, null);
        inventory.SlotModified += OnSlotModified;
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        inventory.Pos = Pos;
        inventory.LateInitialize(InventoryClassName + "-" + Pos.X + "/" + Pos.Y + "/" + Pos.Z, api);
        // Firepit: InventorySmeltingPatch hooks OnInventoryOpened to set Ownable.
        inventory.OnInventoryOpened += OnInventoryOpenedClaimOwnable;
        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
            inventory.UpdateCookingSlotsFromPot(b);

        RegisterGameTickListener(OnBurnTick, 100);
        RegisterGameTickListener(On500msTick, 500);

        // Ensure chunk light matches restored burn state after load.
        if (api.Side == EnumAppSide.Server && IsBurning)
        {
            lastLitForLight = false;
            RefreshLitLightIfNeeded();
        }

        if (api is ICoreClientAPI capi)
        {
            clientMeshes = new StoveClientMeshes(capi);
            int facing = Block.Attributes?["facing"]?.AsInt(0) ?? 0;
            potMatrices = StoveClientMeshes.GenPotMatrices(facing);
            potsRenderer = new StovePotsRenderer(capi, Pos, potMatrices);
            capi.Event.RegisterRenderer(potsRenderer, EnumRenderStage.Opaque, "stainsstovepots");
            RegisterGameTickListener(OnClientTick, 50);
            UpdatePotRenderers();
        }
    }

    private void OnSlotModified(int slotId)
    {
        if (Api?.Side == EnumAppSide.Client)
        {
            UpdatePotRenderers();
            if (clientDialog != null && clientDialog.IsOpened())
            {
                SetDialogValues(clientDialog.Attributes);
                clientDialog.Update();
            }
        }
        MarkDirty(true);
    }

    private void OnClientTick(float dt)
    {
        UpdatePotRenderers();
    }

    private void On500msTick(float dt)
    {
        if (Api is ICoreServerAPI && (IsBurning || Math.Abs(prevFurnaceTemperature - furnaceTemperature) > 0.1f))
            MarkDirty();
        prevFurnaceTemperature = furnaceTemperature;
    }

    private void OnBurnTick(float dt)
    {
        if (Api is ICoreClientAPI) return;

        if (IsBurning)
        {
            fuelBurnTime -= dt;
            if (fuelBurnTime <= 0)
            {
                fuelBurnTime = 0;
                maxFuelBurnTime = 0;
                extinguishedTotalHours = Api.World.Calendar.TotalHours;
                canIgniteFuel = furnaceTemperature > 50;
                // Consume next charcoal/coal while still hot (firepit igniteFuel — not gated on cookables).
                if (canIgniteFuel)
                    TryIgniteFuel();
            }
        }

        if (IsBurning)
            furnaceTemperature = ChangeTemperature(furnaceTemperature, maxTemperature, dt * 2);
        else
        {
            furnaceTemperature = ChangeTemperature(furnaceTemperature, EnvironmentTemperature(), dt / 4);
            if (furnaceTemperature <= EnvironmentTemperature() + 1)
                canIgniteFuel = false;
        }

        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
        {
            if (CanHeatInput(b)) HeatInput(b, dt);
            if (CanSmeltInput(b) && burnerCookingTime[b] > MaxCookingTime(b))
                SmeltBurner(b);
        }

        // Keep the firebox fed while smoldering / hot enough, even with empty pots.
        if (!IsBurning && canIgniteFuel)
            TryIgniteFuel();

        RefreshLitLightIfNeeded();
        MarkDirty(false);
    }

    /// <summary>
    /// Re-query block light when lit state flips (same pattern as firepit setBlockState → ExchangeBlock).
    /// </summary>
    private void RefreshLitLightIfNeeded()
    {
        bool lit = IsBurning;
        if (lit == lastLitForLight) return;
        lastLitForLight = lit;
        Api.World.BlockAccessor.ExchangeBlock(Block.BlockId, Pos);
    }

    public int EnvironmentTemperature() => 20;

    public float ChangeTemperature(float fromTemp, float toTemp, float dt)
    {
        float diff = Math.Abs(fromTemp - toTemp);
        dt += dt * (diff / 30);
        if (diff < dt) return toTemp;
        return fromTemp + (fromTemp > toTemp ? -dt : dt);
    }

    public bool HasAnyCookable()
    {
        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
            if (CanSmeltInput(b)) return true;
        return false;
    }

    public bool CanHeatInput(int burner)
    {
        ItemStack? pot = inventory.PotSlot(burner).Itemstack;
        if (pot == null) return false;
        return CanSmeltInput(burner)
               || (InventoryStainsStove.SafeItemAttributes(pot)?["allowHeating"].Exists == true
                   && InventoryStainsStove.SafeItemAttributes(pot)!["allowHeating"].AsBool());
    }

    private void OnInventoryOpenedClaimOwnable(IPlayer player)
        => XSkillsStoveCompat.TryClaimOwnable(this, player, AnyBurnerCooking());

    private bool AnyBurnerCooking()
    {
        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
            if (burnerCookingTime[b] > 0) return true;
        return false;
    }

    public bool CanSmeltInput(int burner)
    {
        ItemSlot potSlot = inventory.PotSlot(burner);
        ItemStack? pot = potSlot.Itemstack;
        if (pot == null) return false;

        inventory.PrepareBurnerForSmelt(burner);
        pot.Collectible.OnSmeltAttempt(inventory);

        CombustibleProperties? props = pot.Collectible.GetCombustibleProperties(Api.World, pot, null);
        return pot.Collectible.CanSmelt(Api.World, inventory, pot, inventory.OutputSlot(burner).Itemstack)
               && (props == null || !props.RequiresContainer);
    }

    public float MaxCookingTime(int burner)
    {
        ItemSlot potSlot = inventory.PotSlot(burner);
        if (potSlot.Itemstack == null) return 30f;
        inventory.PrepareBurnerForSmelt(burner);
        float baseTime = potSlot.Itemstack.Collectible.GetMeltingDuration(Api.World, inventory, potSlot);
        // Firepit-only Harmony in xSkills; soft-apply Fast Food / Well Done here.
        return baseTime * XSkillsStoveCompat.GetCookingTimeMultiplier(this);
    }

    public void HeatInput(int burner, float dt)
    {
        ItemSlot potSlot = inventory.PotSlot(burner);
        ItemStack? pot = potSlot.Itemstack;
        if (pot == null) return;

        inventory.PrepareBurnerForSmelt(burner);
        float oldTemp = GetBurnerTemp(burner);
        float meltingPoint = pot.Collectible.GetMeltingPoint(Api.World, inventory, potSlot);
        float stackSize = Math.Max(1, pot.StackSize);
        float nowTemp = oldTemp;

        if (oldTemp < furnaceTemperature)
        {
            float f = (1 + GameMath.Clamp((furnaceTemperature - oldTemp) / 30, 0, 1.6f)) * dt;
            if (nowTemp >= meltingPoint) f /= 11;

            float newTemp = ChangeTemperature(oldTemp, furnaceTemperature, f);
            newTemp = (newTemp + (stackSize - 1) * oldTemp) / stackSize;

            CombustibleProperties? combustibleProps = pot.Collectible.GetCombustibleProperties(Api.World, pot, null);
            int maxTemp = Math.Max(
                combustibleProps?.MaxTemperature ?? 0,
                InventoryStainsStove.SafeItemAttributes(pot)?["maxTemperature"].AsInt(0) ?? 0);
            if (maxTemp > 0) newTemp = Math.Min(maxTemp, newTemp);

            if (Math.Abs(oldTemp - newTemp) > 0.01f)
            {
                SetBurnerTemp(burner, newTemp);
                nowTemp = newTemp;
            }
        }

        if (nowTemp >= meltingPoint)
            burnerCookingTime[burner] += GameMath.Clamp((int)(nowTemp / Math.Max(1f, meltingPoint)), 1, 30) * dt;
        else if (burnerCookingTime[burner] > 0)
            burnerCookingTime[burner]--;
    }

    public float GetBurnerTemp(int burner)
    {
        bool have = false;
        float lowest = 0;
        foreach (ItemSlot slot in inventory.CookingSlots(burner))
        {
            if (slot.Itemstack == null) continue;
            float t = slot.Itemstack.Collectible.GetTemperature(Api.World, slot.Itemstack);
            lowest = have ? Math.Min(lowest, t) : t;
            have = true;
        }
        if (have) return lowest;

        ItemStack? pot = inventory.PotSlot(burner).Itemstack;
        return pot == null ? EnvironmentTemperature() : pot.Collectible.GetTemperature(Api.World, pot);
    }

    public void SetBurnerTemp(int burner, float value)
    {
        bool anyCooking = false;
        foreach (ItemSlot slot in inventory.CookingSlots(burner))
        {
            if (slot.Itemstack == null) continue;
            anyCooking = true;
            slot.Itemstack.Collectible.SetTemperature(Api.World, slot.Itemstack, value);
        }
        if (!anyCooking)
        {
            ItemStack? pot = inventory.PotSlot(burner).Itemstack;
            pot?.Collectible.SetTemperature(Api.World, pot, value);
        }
    }

    public void SmeltBurner(int burner)
    {
        ItemSlot potSlot = inventory.PotSlot(burner);
        ItemStack? pot = potSlot.Itemstack;
        if (pot == null) return;

        inventory.PrepareBurnerForSmelt(burner);
        pot.Collectible.DoSmelt(Api.World, inventory, potSlot, inventory.OutputSlot(burner));
        burnerCookingTime[burner] = 0;
        potSlot.MarkDirty();
        MarkDirty(true);
    }

    public void TryIgniteFuel()
    {
        ItemSlot fuel = inventory.FuelSlot;
        if (fuel.Empty || !StoveFuels.IsAllowed(fuel.Itemstack)) return;

        CombustibleProperties? props = StoveFuels.GetFuelProps(Api.World, fuel.Itemstack);
        if (props == null || props.BurnDuration <= 0) return;

        maxFuelBurnTime = fuelBurnTime = props.BurnDuration;
        maxTemperature = (int)props.BurnTemperature;
        smokeLevel = props.SmokeLevel;
        canIgniteFuel = true;

        fuel.TakeOut(1);
        fuel.MarkDirty();
        MarkDirty(true);
    }

    public bool TryManualIgnite()
    {
        if (IsBurning) return false;
        if (inventory.FuelSlot.Empty || !StoveFuels.IsAllowed(inventory.FuelSlot.Itemstack)) return false;
        TryIgniteFuel();
        return IsBurning;
    }

    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        int box = blockSel.SelectionBoxIndex;
        ItemSlot hand = byPlayer.InventoryManager.ActiveHotbarSlot;

        // Claim xSkills Ownable on any cook-relevant interact (server applies traits on DoSmelt).
        XSkillsStoveCompat.TryClaimOwnable(this, byPlayer, AnyBurnerCooking());

        // Burner pads: place pot/food in-world, otherwise open cooking GUI for that burner (door stays shut).
        if (box >= 1 && box <= InventoryStainsStove.BurnerCount)
        {
            int burner = box - 1;
            ItemSlot potSlot = inventory.PotSlot(burner);

            // Sneak + empty hand: take pot / food off the burner
            if (hand.Empty && byPlayer.Entity.Controls.ShiftKey)
            {
                if (TryTakePot(byPlayer, burner))
                {
                    Api.World.PlaySoundAt(new AssetLocation("sounds/player/build"), byPlayer.Entity, byPlayer, true, 16);
                    MarkDirty(true);
                }
                return true;
            }

            // Firepit exact: Shift + MeltingPoint > 0 → put into input slot.
            // Source: BlockFirepit.OnBlockInteractStart (shift branch).
            if (!hand.Empty && byPlayer.Entity.Controls.ShiftKey && potSlot.Empty)
            {
                CombustibleProperties? combustibleProps =
                    hand.Itemstack!.Collectible.GetCombustibleProperties(Api.World, hand.Itemstack, null);
                if (combustibleProps != null && combustibleProps.MeltingPoint > 0)
                {
                    var op = new ItemStackMoveOperation(Api.World, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 1);
                    hand.TryPutInto(potSlot, ref op);
                    if (op.MovedQuantity > 0)
                    {
                        (byPlayer as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
                        MarkDirty(true);
                        return true;
                    }
                }
            }

            // Non-shift: place cooking pot on burner (shipped behavior).
            if (!hand.Empty && IsCookingPot(hand.Itemstack) && potSlot.Empty)
            {
                if (hand.TryPutInto(Api.World, potSlot) > 0)
                {
                    Api.World.PlaySoundAt(new AssetLocation("sounds/player/build"), byPlayer.Entity, byPlayer, true, 16);
                    MarkDirty(true);
                    OpenGui(byPlayer, burner, fromWindow: false);
                    return true;
                }
            }

            OpenGui(byPlayer, burner, fromWindow: false);
            return true;
        }

        // Door / window (body): open the firebox door + fuel GUI. Closing the GUI closes the door.
        OpenGui(byPlayer, 0, fromWindow: true);
        return true;
    }

    /// <summary>
    /// Take pot/cooked meal/spit food from a burner into the player inventory.
    /// </summary>
    private bool TryTakePot(IPlayer byPlayer, int burner)
    {
        ItemSlot potSlot = inventory.PotSlot(burner);
        ItemSlot outSlot = inventory.OutputSlot(burner);
        ItemSlot? takeFrom = null;

        if (!potSlot.Empty)
            takeFrom = potSlot;
        else if (!outSlot.Empty)
            takeFrom = outSlot;

        if (takeFrom == null) return false;

        ItemStack? taken = takeFrom.TakeOutWhole();
        if (taken == null) return false;

        if (!byPlayer.InventoryManager.TryGiveItemstack(taken))
            Api.World.SpawnItemEntity(taken, Pos.ToVec3d().Add(0.5, 1.1, 0.5));

        takeFrom.MarkDirty();
        return true;
    }

    private static bool IsCookingPot(ItemStack? stack)
    {
        return InventoryStainsStove.HasAttr(stack, "cookingContainerSlots")
               || stack?.Collectible is BlockCookingContainer;
    }

    private static bool IsCookingVessel(ItemStack? stack)
    {
        return IsCookingPot(stack)
               || stack?.Collectible is BlockCookedContainer
               || stack?.Collectible is BlockCookedContainerBase;
    }

    public void OpenGui(IPlayer byPlayer, int focusBurner, bool fromWindow)
    {
        if (Api.Side != EnumAppSide.Client) return;

        focusBurner = GameMath.Clamp(focusBurner, 0, InventoryStainsStove.BurnerCount - 1);

        if (clientDialog != null && clientDialog.IsOpened()
            && clientDialog.BurnerIndex == focusBurner
            && clientDialog.FuelOnly == fromWindow)
            return;

        if (clientDialog != null && clientDialog.IsOpened())
            clientDialog.TryClose();

        openDialogBurner = focusBurner;
        dialogOpenedFromWindow = fromWindow;
        if (fromWindow)
        {
            isDoorOpen = true;
            MarkDirty(true);
        }

        toggleInventoryDialogClient(byPlayer, () =>
        {
            var dtree = new SyncedTreeAttribute();
            SetDialogValues(dtree);
            clientDialog = new GuiDialogStainsStove(
                DialogTitle, Inventory, Pos, dtree, (ICoreClientAPI)Api, focusBurner, fuelOnly: fromWindow);
            clientDialog.OnClosed += () =>
            {
                clientDialog = null;
                if (dialogOpenedFromWindow)
                {
                    isDoorOpen = false;
                    dialogOpenedFromWindow = false;
                }
                MarkDirty(true);
            };
            MarkDirty(true);
            return clientDialog;
        });
    }

    public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        => OnInteract(byPlayer, blockSel);

    /// <summary>
    /// Writes firepit-compatible dialog attributes for the burner whose GUI is open
    /// (or burner 0 when none). Source: GuiDialogBlockEntityFirepit / BlockEntityFirepit.SetDialogValues.
    /// </summary>
    public void SetDialogValues(ITreeAttribute dialogTree)
    {
        int b = openDialogBurner;
        if (clientDialog != null && clientDialog.IsOpened())
            b = clientDialog.BurnerIndex;

        dialogTree.SetFloat("furnaceTemperature", furnaceTemperature);
        dialogTree.SetInt("maxTemperature", maxTemperature);
        dialogTree.SetFloat("maxFuelBurnTime", maxFuelBurnTime);
        dialogTree.SetFloat("fuelBurnTime", fuelBurnTime);

        dialogTree.SetFloat("oreCookingTime", burnerCookingTime[b]);
        dialogTree.SetFloat("maxOreCookingTime", MaxCookingTime(b));
        dialogTree.SetFloat("oreTemperature", GetBurnerTemp(b));

        dialogTree.SetInt("haveCookingContainer", inventory.HaveCookingContainer(b) ? 1 : 0);
        dialogTree.SetInt("quantityCookingSlots", 4);

        string outputText = "";
        ItemStack? pot = inventory.PotSlot(b).Itemstack;
        if (pot?.Collectible is BlockCookingContainer bcc)
        {
            inventory.PrepareBurnerForSmelt(b);
            outputText = bcc.GetOutputText(Api.World, inventory, inventory.PotSlot(b)) ?? "";
        }
        dialogTree.SetString("outputText", outputText);
    }

    /// <summary>
    /// Drive per-burner pot/meal/spit renderers.
    /// </summary>
    private void UpdatePotRenderers()
    {
        if (potsRenderer == null || potMatrices == null) return;

        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
        {
            ItemStack? pot = inventory.PotSlot(b).Itemstack;
            ItemStack? output = inventory.OutputSlot(b).Itemstack;

            ItemStack? show = null;
            bool inOutput = false;
            bool isPot = false;

            if (output?.Collectible is BlockCookedContainerBase)
            {
                show = output;
                inOutput = true;
                isPot = true;
            }
            else if (pot != null && (IsCookingPot(pot) || pot.Collectible is BlockCookedContainerBase))
            {
                show = pot;
                isPot = true;
            }
            else if (output != null)
            {
                show = output;
                inOutput = true;
            }
            else if (pot != null)
            {
                show = pot;
            }

            bool activelyCooking = isPot && !inOutput && show != null && IsBurning && HasCookingIngredients(b);
            float temp = show == null ? 20 : GetBurnerTemp(b);
            if (isPot && !activelyCooking)
                temp = 20;

            potsRenderer.SetBurnerContents(b, show, inOutput, temp, activelyCooking);
        }
    }

    private bool HasCookingIngredients(int burner)
    {
        foreach (ItemSlot slot in inventory.CookingSlots(burner))
            if (!slot.Empty) return true;
        return false;
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (clientMeshes != null)
        {
            clientMeshes.EnsureGenerated(Block);

            MeshData? door = isDoorOpen ? clientMeshes.DoorOpen : clientMeshes.DoorClosed;
            if (door != null)
                mesher.AddMeshData(door);

            if (IsBurning && clientMeshes.FireLit != null)
                mesher.AddMeshData(clientMeshes.FireLit);
            else if (!inventory.FuelSlot.Empty && clientMeshes.FireWithFuel != null)
                mesher.AddMeshData(clientMeshes.FireWithFuel);
            else if (clientMeshes.FireEmpty != null)
                mesher.AddMeshData(clientMeshes.FireEmpty);

            // Pots/meals are drawn by StovePotsRenderer (animation + cooking sound), not chunk tessellation.
        }

        return base.OnTesselation(mesher, tessThreadTesselator);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        furnaceTemperature = tree.GetFloat("furnaceTemperature");
        maxTemperature = tree.GetInt("maxTemperature");
        fuelBurnTime = tree.GetFloat("fuelBurnTime");
        maxFuelBurnTime = tree.GetFloat("maxFuelBurnTime");
        smokeLevel = tree.GetFloat("smokeLevel");
        canIgniteFuel = tree.GetBool("canIgniteFuel");
        extinguishedTotalHours = tree.GetDouble("extinguishedTotalHours");
        isDoorOpen = tree.GetBool("isDoorOpen");
        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
            burnerCookingTime[b] = tree.GetFloat("cookTime" + b);
        lastLitForLight = IsBurning;

        if (Api?.Side == EnumAppSide.Client)
        {
            UpdatePotRenderers();
            if (clientDialog != null && clientDialog.IsOpened())
                SetDialogValues(clientDialog.Attributes);
        }
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetFloat("furnaceTemperature", furnaceTemperature);
        tree.SetInt("maxTemperature", maxTemperature);
        tree.SetFloat("fuelBurnTime", fuelBurnTime);
        tree.SetFloat("maxFuelBurnTime", maxFuelBurnTime);
        tree.SetFloat("smokeLevel", smokeLevel);
        tree.SetBool("canIgniteFuel", canIgniteFuel);
        tree.SetDouble("extinguishedTotalHours", extinguishedTotalHours);
        tree.SetBool("isDoorOpen", isDoorOpen);
        for (int b = 0; b < InventoryStainsStove.BurnerCount; b++)
            tree.SetFloat("cookTime" + b, burnerCookingTime[b]);
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine(Lang.Get("stainsstovetop:info-temp", (int)furnaceTemperature));
        if (!inventory.FuelSlot.Empty)
            dsc.AppendLine(Lang.Get("stainsstovetop:info-fuel",
                inventory.FuelSlot.Itemstack!.GetName() + " x" + inventory.FuelSlot.StackSize));
        if (IsBurning)
            dsc.AppendLine(Lang.Get("stainsstovetop:info-burning"));
        else if (canIgniteFuel)
            dsc.AppendLine(Lang.Get("stainsstovetop:info-smoldering"));
    }

    public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        => IsBurning ? 10 : (IsSmoldering ? 0.25f : 0);

    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        if (IsBurning)
            Api.World.BlockAccessor.RemoveBlockLight(new byte[] { 7, 7, 11 }, Pos);
        DisposeClientRenderers();
        base.OnBlockBroken(byPlayer);
        clientDialog?.TryClose();
        clientDialog?.Dispose();
        clientDialog = null;
    }

    public override void OnBlockUnloaded()
    {
        DisposeClientRenderers();
        base.OnBlockUnloaded();
    }

    private void DisposeClientRenderers()
    {
        if (Api is ICoreClientAPI capi && potsRenderer != null)
        {
            capi.Event.UnregisterRenderer(potsRenderer, EnumRenderStage.Opaque);
            potsRenderer.Dispose();
            potsRenderer = null;
        }
    }

    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid == (int)EnumBlockEntityPacketId.Close)
        {
            ((IClientWorldAccessor)Api.World).Player.InventoryManager.CloseInventory(Inventory);
            invDialog?.TryClose();
            invDialog?.Dispose();
            invDialog = null;
            clientDialog = null;
        }
    }
}
