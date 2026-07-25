using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace StainsStovetop;

public class StainsStovetopModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("BlockStainsStove", typeof(BlockStainsStove));
        api.RegisterBlockEntityClass("BlockEntityStainsStove", typeof(BlockEntityStainsStove));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Logger.Notification("Stain's Stovetop loaded (server).");
        if (api.ModLoader.IsModEnabled("kevinsfurniture"))
        {
            api.Logger.Notification("Kevin's Furniture detected — stove overwrite patches should apply.");
        }
    }
}
