using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace StainsStovetop;

public class BlockStainsStove : Block, IIgnitable
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityStainsStove stove)
        {
            return stove.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
    {
        if (byEntity.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityStainsStove stove)
            return EnumIgniteState.NotIgnitablePreventDefault;

        if (stove.IsBurning) return EnumIgniteState.NotIgnitablePreventDefault;
        if (stove.Inventory[0].Empty || !StoveFuels.IsAllowed(stove.Inventory[0].Itemstack))
            return EnumIgniteState.NotIgnitablePreventDefault;

        return secondsIgniting > 2 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
    }

    public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
    {
        handling = EnumHandling.PreventDefault;
        if (byEntity.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityStainsStove stove)
        {
            stove.TryManualIgnite();
        }
    }

    public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
    {
        return EnumIgniteState.NotIgnitable;
    }

    /// <summary>
    /// Warm ember glow while fuel is burning. Firepit-lit uses V=16; we use a milder V=11.
    /// Source: survival firepit.json lightHsvByType + BlockLantern.GetLightHsv BE pattern.
    /// </summary>
    public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack? stack = null)
    {
        if (pos != null
            && blockAccessor.GetBlockEntity(pos) is BlockEntityStainsStove stove
            && stove.IsBurning)
        {
            return new byte[] { 7, 7, 11 };
        }
        return base.GetLightHsv(blockAccessor, pos, stack);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        return new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "stainsstovetop:blockhelp-placepot",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = GetExamplePots(world)
            },
            new WorldInteraction
            {
                ActionLangCode = "stainsstovetop:blockhelp-cook",
                MouseButton = EnumMouseButton.Right
            },
            new WorldInteraction
            {
                ActionLangCode = "stainsstovetop:blockhelp-takepot",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "sneak"
            },
            new WorldInteraction
            {
                ActionLangCode = "stainsstovetop:blockhelp-fueldoor",
                MouseButton = EnumMouseButton.Right
            },
            new WorldInteraction
            {
                ActionLangCode = "stainsstovetop:blockhelp-ignite",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "sneak"
            }
        }.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    private static ItemStack[] GetExamplePots(IWorldAccessor world)
    {
        Block? pot = world.GetBlock(new AssetLocation("game:claypot-blue-fired"));
        return pot == null ? System.Array.Empty<ItemStack>() : new[] { new ItemStack(pot) };
    }
}
