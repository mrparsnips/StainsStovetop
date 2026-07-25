using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Client renderer for pots on all 4 burners. Mirrors <see cref="PotInFirepitRenderer"/>:
/// cooking pot + rattling lid + <c>sounds/effect/cooking.ogg</c>, then cooked meal mesh when done.
/// Source: VSSurvivalMod PotInFirepitRenderer / BlockEntityFirepit.UpdateRenderer.
/// </summary>
public class StovePotsRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly BlockPos pos;
    private readonly float[][] potMatrices;
    private readonly BurnerVisual[] burners = new BurnerVisual[InventoryStainsStove.BurnerCount];

    public double RenderOrder => 0.5;
    public int RenderRange => 48;

    public StovePotsRenderer(ICoreClientAPI capi, BlockPos pos, float[][] potMatrices)
    {
        this.capi = capi;
        this.pos = pos;
        this.potMatrices = potMatrices;
        for (int i = 0; i < burners.Length; i++)
            burners[i] = new BurnerVisual();
    }

    public void SetBurnerContents(int burner, ItemStack? stack, bool isInOutputSlot, float temperature, bool activelyCooking)
    {
        BurnerVisual vis = burners[burner];
        string? key = StackKey(stack, isInOutputSlot, activelyCooking);
        if (key != vis.ContentKey)
        {
            DisposeBurnerMeshes(vis);
            vis.ContentKey = key;
            vis.IsInOutputSlot = isInOutputSlot;
            vis.ActivelyCooking = activelyCooking;
            if (stack != null)
                BuildMeshes(vis, stack, isInOutputSlot, activelyCooking);
        }

        vis.Temp = temperature;
        float vol = activelyCooking ? GameMath.Clamp((temperature - 50f) / 50f, 0, 1) : 0;
        SetCookingSoundVolume(vis, vol);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        IRenderAPI rapi = capi.Render;
        Vec3d camPos = capi.World.Player.Entity.CameraPos;

        for (int b = 0; b < burners.Length; b++)
        {
            BurnerVisual vis = burners[b];
            if (vis.PotWithFoodRef == null && vis.PotRef == null) continue;

            IStandardShaderProgram prog = rapi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;

            var baseMat = new Matrixf()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);
            Mat4f.Mul(baseMat.Values, baseMat.Values, potMatrices[b]);

            if (vis.IsInOutputSlot && vis.PotWithFoodRef != null)
            {
                prog.ModelMatrix = baseMat.Values;
                rapi.RenderMultiTextureMesh(vis.PotWithFoodRef, "tex");
                prog.Stop();
                continue;
            }

            if (vis.PotRef != null)
            {
                prog.ModelMatrix = baseMat.Values;
                rapi.RenderMultiTextureMesh(vis.PotRef, "tex");
            }

            // Lid + rattle only while actively cooking (idle empty pots are a plain pot mesh).
            if (vis.ActivelyCooking && vis.LidRef != null)
            {
                float shake = GameMath.Clamp((vis.Temp - 50f) / 50f, 0, 1);
                float ms = capi.World.ElapsedMilliseconds;
                float dx = GameMath.Sin(ms / 300f) * 5f / 16f;
                float dz = GameMath.Cos(ms / 300f) * 5f / 16f;
                float ang = shake * GameMath.Sin(ms / 50f) / 60f;

                // pot-opened-empty rim tops at 4.5/16; lid mesh sits at y=0 in its shape.
                // Firepit uses 6.5/16 because its pot is also nudged +1/16 — without that, 6.5 floats.
                var lidMat = new Matrixf()
                    .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);
                Mat4f.Mul(lidMat.Values, lidMat.Values, potMatrices[b]);
                lidMat
                    .Translate(0, 4.5f / 16f, 0)
                    .Translate(-dx, 0, -dz)
                    .RotateX(ang)
                    .RotateZ(ang)
                    .Translate(dx, 0, dz);

                prog.ModelMatrix = lidMat.Values;
                rapi.RenderMultiTextureMesh(vis.LidRef, "tex");
            }

            prog.Stop();
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < burners.Length; i++)
        {
            SetCookingSoundVolume(burners[i], 0);
            DisposeBurnerMeshes(burners[i]);
        }
    }

    private void BuildMeshes(BurnerVisual vis, ItemStack stack, bool isInOutputSlot, bool activelyCooking)
    {
        Block? shapeBlock = capi.World.GetBlock(stack.Collectible.CodeWithVariant("type", "cooked"));
        if (shapeBlock is not BlockCookedContainer && stack.Block is BlockCookedContainer cooked)
            shapeBlock = cooked;
        if (shapeBlock == null)
            shapeBlock = stack.Block;
        if (shapeBlock == null) return;

        if (isInOutputSlot && shapeBlock is BlockCookedContainerBase cookedBase)
        {
            var cache = capi.ModLoader.GetModSystem<MealMeshCache>();
            CookingRecipe? recipe = cookedBase.GetCookingRecipe(capi.World, stack);
            ItemStack[] contents = (shapeBlock as BlockContainer)?.GetNonEmptyContents(capi.World, stack)
                                   ?? Array.Empty<ItemStack>();
            if (recipe != null)
            {
                vis.PotWithFoodRef = cache.GetOrCreateMealInContainerMeshRef(
                    shapeBlock,
                    recipe,
                    contents,
                    new Vec3f(0, 2.5f / 16f, 0));
            }
            else
            {
                capi.Tesselator.TesselateBlock(shapeBlock, out MeshData mesh);
                vis.PotWithFoodRef = capi.Render.UploadMultiTextureMesh(mesh);
                vis.OwnsFoodMesh = true;
            }
            return;
        }

        // Idle pot, or open-fire spit item (meat/fish). Cooking pot uses lid path below.
        if (!activelyCooking)
        {
            MeshData mesh;
            if (stack.Class == EnumItemClass.Item)
            {
                capi.Tesselator.TesselateItem(stack.Item, out mesh);
                // FirepitContentsRenderer uses inFirePitProps.Transform; shrink further for burner pads.
                InFirePitProps? props = BlockEntityFirepit.GetRenderProps(stack);
                if (props?.Transform != null)
                {
                    mesh.ModelTransform(props.Transform);
                    mesh.Scale(Vec3f.Zero, 0.55f, 0.55f, 0.55f);
                }
                else
                {
                    mesh.Scale(Vec3f.Zero, 0.25f, 0.25f, 0.25f);
                }
            }
            else
            {
                capi.Tesselator.TesselateBlock(stack.Block, out mesh);
            }
            vis.PotRef = capi.Render.UploadMultiTextureMesh(mesh);
            return;
        }

        Shape? potShape = Shape.TryGet(capi, "shapes/block/clay/pot-opened-empty.json");
        Shape? lidShape = Shape.TryGet(capi, "shapes/block/clay/pot-part-lid.json");
        if (potShape != null)
        {
            capi.Tesselator.TesselateShape(shapeBlock, potShape, out MeshData potMesh);
            vis.PotRef = capi.Render.UploadMultiTextureMesh(potMesh);
        }
        if (lidShape != null)
        {
            capi.Tesselator.TesselateShape(shapeBlock, lidShape, out MeshData lidMesh);
            vis.LidRef = capi.Render.UploadMultiTextureMesh(lidMesh);
        }
    }

    private void SetCookingSoundVolume(BurnerVisual vis, float volume)
    {
        if (volume > 0)
        {
            if (vis.CookingSound == null)
            {
                vis.CookingSound = capi.World.LoadSound(new SoundParams
                {
                    Location = new AssetLocation("sounds/effect/cooking.ogg"),
                    ShouldLoop = true,
                    Position = pos.ToVec3f().Add(0.5f, 1f, 0.5f),
                    DisposeOnFinish = false,
                    Range = 10f,
                    ReferenceDistance = 3f,
                    Volume = volume
                });
                vis.CookingSound?.Start();
            }
            else
            {
                vis.CookingSound.SetVolume(volume);
            }
        }
        else if (vis.CookingSound != null)
        {
            vis.CookingSound.Stop();
            vis.CookingSound.Dispose();
            vis.CookingSound = null;
        }
    }

    private static void DisposeBurnerMeshes(BurnerVisual vis)
    {
        vis.PotRef?.Dispose();
        vis.LidRef?.Dispose();
        // MealMeshCache-owned refs must not be disposed; only dispose our fallback upload.
        if (vis.OwnsFoodMesh)
            vis.PotWithFoodRef?.Dispose();
        vis.PotRef = null;
        vis.LidRef = null;
        vis.PotWithFoodRef = null;
        vis.OwnsFoodMesh = false;
    }

    private static string? StackKey(ItemStack? stack, bool inOutput, bool cooking)
    {
        if (stack == null) return null;
        return stack.Collectible.Code + "|" + inOutput + "|" + cooking + "|" + (stack.Attributes?.ToJsonToken() ?? "");
    }

    private class BurnerVisual
    {
        public string? ContentKey;
        public bool IsInOutputSlot;
        public bool ActivelyCooking;
        public float Temp;
        public bool OwnsFoodMesh;
        public MultiTextureMeshRef? PotRef;
        public MultiTextureMeshRef? LidRef;
        public MultiTextureMeshRef? PotWithFoodRef;
        public ILoadedSound? CookingSound;
    }
}
