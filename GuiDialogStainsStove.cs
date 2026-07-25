using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Firepit-style cooking UI for one burner, or a compact fuel-only firebox UI.
/// Cooking layout mirrors <see cref="GuiDialogBlockEntityFirepit"/>.
/// </summary>
public class GuiDialogStainsStove : GuiDialogBlockEntity
{
    private readonly int burner;
    private readonly bool fuelOnly;
    private bool haveCookingContainer;
    private string currentOutputText = "";
    private ElementBounds cookingSlotsSlotBounds = null!;
    private long lastRedrawMs;
    private EnumPosFlag screenPos;

    protected override double FloatyDialogPosition => 0.6;
    protected override double FloatyDialogAlign => 0.8;
    public override double DrawOrder => 0.2;

    public int BurnerIndex => burner;
    public bool FuelOnly => fuelOnly;

    public GuiDialogStainsStove(
        string dlgTitle,
        InventoryBase inventory,
        BlockPos bePos,
        SyncedTreeAttribute tree,
        ICoreClientAPI capi,
        int burner,
        bool fuelOnly = false)
        : base(dlgTitle, inventory, bePos, capi)
    {
        if (IsDuplicate) return;
        this.burner = GameMath.Clamp(burner, 0, InventoryStainsStove.BurnerCount - 1);
        this.fuelOnly = fuelOnly;
        tree.OnModified.Add(new TreeModifiedListener { listener = OnAttributesModified });
        Attributes = tree;
    }

    public void Update() => SetupDialog();

    private void OnInventorySlotModified(int slotid)
    {
        // Fuel-only dialog never swaps layout based on pot slots.
        if (fuelOnly) return;
        capi.Event.EnqueueMainThreadTask(SetupDialog, "setupstainsstovedlg");
    }

    private void SetupDialog()
    {
        if (fuelOnly)
        {
            SetupFuelOnlyDialog();
            return;
        }

        ItemSlot? hoveredSlot = capi.World.Player.InventoryManager.CurrentHoveredSlot;
        if (hoveredSlot != null && hoveredSlot.Inventory?.InventoryID != Inventory?.InventoryID)
            hoveredSlot = null;

        string newOutputText = Attributes.GetString("outputText", "");
        bool newHaveCookingContainer = Attributes.GetInt("haveCookingContainer") > 0;

        if (haveCookingContainer == newHaveCookingContainer && SingleComposer != null)
        {
            var outputTextElem = SingleComposer.GetDynamicText("outputText");
            outputTextElem.Font.WithFontSize(14);
            outputTextElem.SetNewText(newOutputText, true);
            SingleComposer.GetCustomDraw("symbolDrawer").Redraw();

            haveCookingContainer = newHaveCookingContainer;
            currentOutputText = newOutputText;

            outputTextElem.Bounds.fixedOffsetY = 0;
            if (outputTextElem.QuantityTextLines > 2)
            {
                outputTextElem.Bounds.fixedOffsetY =
                    -outputTextElem.Font.GetFontExtents().Height / RuntimeEnv.GUIScale * 0.65;
                outputTextElem.Font.WithFontSize(12);
                outputTextElem.RecomposeText();
            }
            outputTextElem.Bounds.CalcWorldBounds();
            return;
        }

        haveCookingContainer = newHaveCookingContainer;
        currentOutputText = newOutputText;

        int qCookingSlots = Attributes.GetInt("quantityCookingSlots", 4);

        ElementBounds stoveBounds = ElementBounds.Fixed(0, 0, 210, 250);

        cookingSlotsSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30 + 45, 4, Math.Max(1, qCookingSlots / 4));
        cookingSlotsSlotBounds.fixedHeight += 10;

        double top = cookingSlotsSlotBounds.fixedHeight + cookingSlotsSlotBounds.fixedY;

        ElementBounds inputSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, top, 1, 1);
        ElementBounds fuelSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 110 + top, 1, 1);
        ElementBounds outputSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 153, top, 1, 1);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(stoveBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedAlignmentOffset(IsRight(screenPos) ? -GuiStyle.DialogToScreenPadding : GuiStyle.DialogToScreenPadding, 0)
            .WithAlignment(IsRight(screenPos) ? EnumDialogArea.RightMiddle : EnumDialogArea.LeftMiddle);

        if (!capi.Settings.Bool["immersiveMouseMode"])
        {
            dialogBounds.fixedOffsetY += (stoveBounds.fixedHeight + 65 + (haveCookingContainer ? 25 : 0)) * YOffsetMul(screenPos);
            dialogBounds.fixedOffsetX += (stoveBounds.fixedWidth + 10) * XOffsetMul(screenPos);
        }

        int potSlot = InventoryStainsStove.PotIndex(burner);
        int outSlot = InventoryStainsStove.OutputIndex(burner);
        int cookStart = InventoryStainsStove.CookingStartIndex(burner);
        int[] cookingSlotIds = new int[qCookingSlots];
        for (int i = 0; i < qCookingSlots; i++)
            cookingSlotIds[i] = cookStart + i;

        SingleComposer = capi.Gui
            .CreateCompo("stainsstovetop-" + BlockEntityPosition + "-b" + burner, dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
            .BeginChildElements(bgBounds)
            .AddDynamicCustomDraw(stoveBounds, OnBgDraw, "symbolDrawer")
            .AddDynamicText("", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, 30, 210, 45), "outputText")
            .AddIf(haveCookingContainer)
            .AddItemSlotGrid(Inventory, SendInvPacket, 4, cookingSlotIds, cookingSlotsSlotBounds, "ingredientSlots")
            .EndIf()
            .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { 0 }, fuelSlotBounds, "fuelslot")
            .AddDynamicText("", CairoFont.WhiteDetailText(), fuelSlotBounds.RightCopy(17, 16).WithFixedSize(60, 30), "fueltemp")
            .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { potSlot }, inputSlotBounds, "oreslot")
            .AddDynamicText("", CairoFont.WhiteDetailText(), inputSlotBounds.RightCopy(23, 16).WithFixedSize(60, 30), "oretemp")
            .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { outSlot }, outputSlotBounds, "outputslot")
            .EndChildElements()
            .Compose();

        lastRedrawMs = capi.ElapsedMilliseconds;

        if (hoveredSlot != null)
            SingleComposer.OnMouseMove(new MouseEvent(capi.Input.MouseX, capi.Input.MouseY));

        var outText = SingleComposer.GetDynamicText("outputText");
        outText.SetNewText(currentOutputText, true);
        outText.Bounds.fixedOffsetY = 0;
        if (outText.QuantityTextLines > 2)
        {
            outText.Bounds.fixedOffsetY = -outText.Font.GetFontExtents().Height / RuntimeEnv.GUIScale * 0.65;
            outText.Font.WithFontSize(12);
            outText.RecomposeText();
        }
        outText.Bounds.CalcWorldBounds();

        OnAttributesModified();
    }

    private void SetupFuelOnlyDialog()
    {
        if (SingleComposer != null)
        {
            SingleComposer.GetCustomDraw("symbolDrawer")?.Redraw();
            OnAttributesModified();
            return;
        }

        ItemSlot? hoveredSlot = capi.World.Player.InventoryManager.CurrentHoveredSlot;
        if (hoveredSlot != null && hoveredSlot.Inventory?.InventoryID != Inventory?.InventoryID)
            hoveredSlot = null;

        // Leave headroom under the title bar so the flame icon doesn't overlap it.
        ElementBounds stoveBounds = ElementBounds.Fixed(0, 0, 120, 160);
        ElementBounds fuelSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 75, 1, 1);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(stoveBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedAlignmentOffset(IsRight(screenPos) ? -GuiStyle.DialogToScreenPadding : GuiStyle.DialogToScreenPadding, 0)
            .WithAlignment(IsRight(screenPos) ? EnumDialogArea.RightMiddle : EnumDialogArea.LeftMiddle);

        if (!capi.Settings.Bool["immersiveMouseMode"])
        {
            dialogBounds.fixedOffsetY += (stoveBounds.fixedHeight + 40) * YOffsetMul(screenPos);
            dialogBounds.fixedOffsetX += (stoveBounds.fixedWidth + 10) * XOffsetMul(screenPos);
        }

        SingleComposer = capi.Gui
            .CreateCompo("stainsstovetop-" + BlockEntityPosition + "-fuel", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
            .BeginChildElements(bgBounds)
            .AddDynamicCustomDraw(stoveBounds, OnFuelBgDraw, "symbolDrawer")
            .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { 0 }, fuelSlotBounds, "fuelslot")
            .AddDynamicText("", CairoFont.WhiteDetailText(), fuelSlotBounds.RightCopy(17, 16).WithFixedSize(60, 30), "fueltemp")
            .EndChildElements()
            .Compose();

        lastRedrawMs = capi.ElapsedMilliseconds;

        if (hoveredSlot != null)
            SingleComposer.OnMouseMove(new MouseEvent(capi.Input.MouseX, capi.Input.MouseY));

        OnAttributesModified();
    }

    private void OnAttributesModified()
    {
        if (!IsOpened() || SingleComposer == null) return;

        float ftemp = Attributes.GetFloat("furnaceTemperature");
        string fuelTemp = ftemp.ToString("#");
        fuelTemp += fuelTemp.Length > 0 ? "°C" : "";
        if (ftemp > 0 && ftemp <= 20) fuelTemp = Lang.Get("Cold");
        SingleComposer.GetDynamicText("fueltemp")?.SetNewText(fuelTemp);

        if (!fuelOnly)
        {
            float otemp = Attributes.GetFloat("oreTemperature");
            string oreTemp = otemp.ToString("#");
            oreTemp += oreTemp.Length > 0 ? "°C" : "";
            if (otemp > 0 && otemp <= 20) oreTemp = Lang.Get("Cold");
            SingleComposer.GetDynamicText("oretemp")?.SetNewText(oreTemp);
        }

        if (capi.ElapsedMilliseconds - lastRedrawMs > 500)
        {
            SingleComposer.GetCustomDraw("symbolDrawer")?.Redraw();
            lastRedrawMs = capi.ElapsedMilliseconds;
        }
    }

    private void OnFuelBgDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        ctx.Save();
        Matrix m = ctx.Matrix;
        // Sit just above the fuel slot (slot at y=75); keep clear of the title bar.
        m.Translate(GuiElement.scaled(5), GuiElement.scaled(28));
        m.Scale(GuiElement.scaled(0.25), GuiElement.scaled(0.25));
        ctx.Matrix = m;
        capi.Gui.Icons.DrawFlame(ctx);

        double maxBurn = Math.Max(0.001f, Attributes.GetFloat("maxFuelBurnTime", 1));
        double dy = 210 - 210 * (Attributes.GetFloat("fuelBurnTime", 0) / maxBurn);
        ctx.Rectangle(0, dy, 200, 210 - dy);
        ctx.Clip();
        var gradient = new LinearGradient(0, GuiElement.scaled(250), 0, 0);
        gradient.AddColorStop(0, new Color(1, 1, 0, 1));
        gradient.AddColorStop(1, new Color(1, 0, 0, 1));
        ctx.SetSource(gradient);
        capi.Gui.Icons.DrawFlame(ctx, 0, false, false);
        gradient.Dispose();
        ctx.Restore();
    }

    private void OnBgDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        double top = cookingSlotsSlotBounds.fixedHeight + cookingSlotsSlotBounds.fixedY;

        ctx.Save();
        Matrix m = ctx.Matrix;
        m.Translate(GuiElement.scaled(5), GuiElement.scaled(53 + top));
        m.Scale(GuiElement.scaled(0.25), GuiElement.scaled(0.25));
        ctx.Matrix = m;
        capi.Gui.Icons.DrawFlame(ctx);

        double maxBurn = Math.Max(0.001f, Attributes.GetFloat("maxFuelBurnTime", 1));
        double dy = 210 - 210 * (Attributes.GetFloat("fuelBurnTime", 0) / maxBurn);
        ctx.Rectangle(0, dy, 200, 210 - dy);
        ctx.Clip();
        var gradient = new LinearGradient(0, GuiElement.scaled(250), 0, 0);
        gradient.AddColorStop(0, new Color(1, 1, 0, 1));
        gradient.AddColorStop(1, new Color(1, 0, 0, 1));
        ctx.SetSource(gradient);
        capi.Gui.Icons.DrawFlame(ctx, 0, false, false);
        gradient.Dispose();
        ctx.Restore();

        ctx.Save();
        m = ctx.Matrix;
        m.Translate(GuiElement.scaled(63), GuiElement.scaled(top + 2));
        m.Scale(GuiElement.scaled(0.6), GuiElement.scaled(0.6));
        ctx.Matrix = m;
        capi.Gui.Icons.DrawArrowRight(ctx, 2);

        float maxCook = Math.Max(0.001f, Attributes.GetFloat("maxOreCookingTime", 1));
        double cookingRel = GameMath.Clamp(Attributes.GetFloat("oreCookingTime") / maxCook, 0, 1);
        ctx.Rectangle(5, 0, 125 * cookingRel, 100);
        ctx.Clip();
        gradient = new LinearGradient(0, 0, 200, 0);
        gradient.AddColorStop(0, new Color(0, 0.4, 0, 1));
        gradient.AddColorStop(1, new Color(0.2, 0.6, 0.2, 1));
        ctx.SetSource(gradient);
        capi.Gui.Icons.DrawArrowRight(ctx, 0, false, false);
        gradient.Dispose();
        ctx.Restore();
    }

    private void SendInvPacket(object packet)
    {
        capi.Network.SendBlockEntityPacket(BlockEntityPosition.X, BlockEntityPosition.Y, BlockEntityPosition.Z, packet);
    }

    private void OnTitleBarClose() => TryClose();

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Inventory.SlotModified += OnInventorySlotModified;
        screenPos = GetFreePos("smallblockgui");
        OccupyPos("smallblockgui", screenPos);
        SetupDialog();
    }

    public override void OnGuiClosed()
    {
        Inventory.SlotModified -= OnInventorySlotModified;
        SingleComposer?.GetSlotGrid("fuelslot")?.OnGuiClosed(capi);
        SingleComposer?.GetSlotGrid("oreslot")?.OnGuiClosed(capi);
        SingleComposer?.GetSlotGrid("outputslot")?.OnGuiClosed(capi);
        SingleComposer?.GetSlotGrid("ingredientSlots")?.OnGuiClosed(capi);
        base.OnGuiClosed();
        FreePos("smallblockgui", screenPos);
    }
}
