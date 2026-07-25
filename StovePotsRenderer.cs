using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StainsStovetop;

/// <summary>
/// Client renderer for pots and open-fire spit food on all 4 burners.
/// Pots: <see cref="PotInFirepitRenderer"/> pattern (lid + cooking sound).
/// Meat/fish: <see cref="FirepitContentsRenderer"/> SetContents + OnRenderFrame matrix
/// plus firepit <c>origin spit</c> sticks (from cold-spit.json). Source: VSSurvivalMod.
/// </summary>
public class StovePotsRenderer : IRenderer
{
    /// <summary>
    /// Firepit OnRenderFrame lifts content by 0.6 above block origin; our potMatrices
    /// already place us on the burner pad (y≈1), so use a small pad lift instead.
    /// </summary>
    private const float SpitPadLift = 0.02f;

    /// <summary>Quarter-block burners — shrink spit/meat relative to full firepit (was 0.55; reduced to stop clipping into air).</summary>
    private const float SpitScaleMul = 0.42f;

    private readonly ICoreClientAPI capi;
    private readonly BlockPos pos;
    private readonly float[][] potMatrices;
    private readonly BurnerVisual[] burners = new BurnerVisual[InventoryStainsStove.BurnerCount];
    private readonly ModelTransform defaultSpitTransform;
    private MultiTextureMeshRef? sharedSpitRodRef;

    public double RenderOrder => 0.5;
    public int RenderRange => 48;

    public StovePotsRenderer(ICoreClientAPI capi, BlockPos pos, float[][] potMatrices)
    {
        this.capi = capi;
        this.pos = pos;
        this.potMatrices = potMatrices;
        for (int i = 0; i < burners.Length; i++)
            burners[i] = new BurnerVisual();

        // FirepitContentsRenderer ctor defaults when inFirePitProps is missing.
        defaultSpitTransform = new ModelTransform().EnsureDefaultValues();
        defaultSpitTransform.Origin.Set(0.5f, 0.0625f, 0.5f);
        defaultSpitTransform.Rotation.Set(90f, 90f, 0f);
        defaultSpitTransform.Translation.Set(0f, 0.25f, 0f);
        defaultSpitTransform.ScaleXYZ.Set(0.25f, 0.25f, 0.25f);
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
            vis.IsSpitItem = false;
            vis.SpitTransform = null;
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
            if (vis.PotWithFoodRef == null && vis.PotRef == null && vis.SpitRodRef == null) continue;

            if (vis.IsSpitItem)
            {
                RenderSpitBurner(rapi, camPos, b, vis);
                continue;
            }

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

            if (vis.ActivelyCooking && vis.LidRef != null)
            {
                float shake = GameMath.Clamp((vis.Temp - 50f) / 50f, 0, 1);
                float ms = capi.World.ElapsedMilliseconds;
                float dx = GameMath.Sin(ms / 300f) * 5f / 16f;
                float dz = GameMath.Cos(ms / 300f) * 5f / 16f;
                float ang = shake * GameMath.Sin(ms / 50f) / 60f;

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

    /// <summary>
    /// Exact FirepitContentsRenderer.OnRenderFrame matrix chain, composed after potMatrices,
    /// with firepit block-lift 0.6 replaced by <see cref="SpitPadLift"/> and scale * SpitScaleMul.
    /// </summary>
    private void RenderSpitBurner(IRenderAPI rapi, Vec3d camPos, int burner, BurnerVisual vis)
    {
        ModelTransform tf = vis.SpitTransform ?? defaultSpitTransform;
        tf.EnsureDefaultValues();

        rapi.GlDisableCullFace();
        rapi.GlToggleBlend(true);

        IStandardShaderProgram prog = rapi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
        prog.ViewMatrix = rapi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;

        // Spit sticks: centered on burner pad (firepit embeds these in lit-spit block mesh).
        MultiTextureMeshRef? rod = vis.SpitRodRef ?? sharedSpitRodRef;
        if (rod != null)
        {
            var rodMat = new Matrixf()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);
            Mat4f.Mul(rodMat.Values, rodMat.Values, potMatrices[burner]);
            rodMat
                .Translate(0.5f, 0, 0.5f)
                .Scale(SpitScaleMul, SpitScaleMul, SpitScaleMul)
                .Translate(-0.5f, 0, -0.5f);
            prog.ModelMatrix = rodMat.Values;
            rapi.RenderMultiTextureMesh(rod, "tex");
        }

        if (vis.PotRef != null)
        {
            float sx = tf.ScaleXYZ.X * SpitScaleMul;
            float sy = tf.ScaleXYZ.Y * SpitScaleMul;
            float sz = tf.ScaleXYZ.Z * SpitScaleMul;

            var meatMat = new Matrixf()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);
            Mat4f.Mul(meatMat.Values, meatMat.Values, potMatrices[burner]);
            // FirepitContentsRenderer.OnRenderFrame (VSSurvivalMod) — pad lift replaces 0.6f.
            meatMat
                .Translate(tf.Translation.X, tf.Translation.Y, tf.Translation.Z)
                .Translate(tf.Origin.X, SpitPadLift + tf.Origin.Y, tf.Origin.Z)
                .RotateX(tf.Rotation.X * GameMath.DEG2RAD)
                .RotateY(tf.Rotation.Y * GameMath.DEG2RAD)
                .RotateZ(tf.Rotation.Z * GameMath.DEG2RAD)
                .Scale(sx, sy, sz)
                .Translate(-tf.Origin.X, -tf.Origin.Y, -tf.Origin.Z);

            prog.ModelMatrix = meatMat.Values;
            rapi.RenderMultiTextureMesh(vis.PotRef, "tex");
        }

        prog.Stop();
    }

    public void Dispose()
    {
        for (int i = 0; i < burners.Length; i++)
        {
            SetCookingSoundVolume(burners[i], 0);
            DisposeBurnerMeshes(burners[i]);
        }
        sharedSpitRodRef?.Dispose();
        sharedSpitRodRef = null;
    }

    private void BuildMeshes(BurnerVisual vis, ItemStack stack, bool isInOutputSlot, bool activelyCooking)
    {
        // Open-fire spit items: FirepitContentsRenderer path (items; blocks with inFirePitProps
        // that are not IInFirepitRendererSupplier). Must run BEFORE pot shapeBlock gate —
        // items have stack.Block == null and used to early-return with no mesh.
        InFirePitProps? fireProps = BlockEntityFirepit.GetRenderProps(stack);
        bool isPotSupplier = stack.Collectible is IInFirepitRendererSupplier;
        if (!isPotSupplier && (stack.Class == EnumItemClass.Item || fireProps != null))
        {
            BuildSpitMeshes(vis, stack, fireProps);
            return;
        }

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

        if (!activelyCooking)
        {
            MeshData potMesh;
            if (stack.Class == EnumItemClass.Block)
                capi.Tesselator.TesselateBlock(stack.Block, out potMesh);
            else
                capi.Tesselator.TesselateItem(stack.Item, out potMesh);
            vis.PotRef = capi.Render.UploadMultiTextureMesh(potMesh);
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

    private void BuildSpitMeshes(BurnerVisual vis, ItemStack stack, InFirePitProps? fireProps)
    {
        vis.IsSpitItem = true;
        ModelTransform tf = fireProps?.Transform ?? defaultSpitTransform;
        tf.EnsureDefaultValues();
        vis.SpitTransform = tf;

        // FirepitContentsRenderer.SetContents: tessellate item, upload — transform applied at draw.
        if (stack.Class == EnumItemClass.Item)
        {
            capi.Tesselator.TesselateItem(stack.Item, out MeshData meatMesh);
            vis.PotRef = capi.Render.UploadMultiTextureMesh(meatMesh);
        }
        else if (stack.Block != null)
        {
            capi.Tesselator.TesselateBlock(stack.Block, out MeshData blockMesh);
            if (fireProps?.Transform != null)
                blockMesh.ModelTransform(fireProps.Transform);
            vis.PotRef = capi.Render.UploadMultiTextureMesh(blockMesh);
        }

        EnsureSharedSpitRod();
        vis.SpitRodRef = sharedSpitRodRef;
    }

    private void EnsureSharedSpitRod()
    {
        if (sharedSpitRodRef != null) return;

        // Firepit spit sticks only — element "origin spit" inside cold-spit.json (full firepit + spit).
        Shape? full = Shape.TryGet(capi, new AssetLocation("game:shapes/block/wood/firepit/cold-spit.json"));
        if (full == null)
            full = Shape.TryGet(capi, "shapes/block/wood/firepit/cold-spit.json");
        if (full == null) return;

        Shape spitOnly = full.Clone();
        spitOnly.Elements = spitOnly.Elements?.Where(e => e.Name == "origin spit").ToArray()
                            ?? Array.Empty<ShapeElement>();
        if (spitOnly.Elements.Length == 0) return;

        Block? texBlock = capi.World.GetBlock(new AssetLocation("firepit-cold"))
                          ?? capi.World.GetBlock(new AssetLocation("game:firepit-cold"));
        if (texBlock == null) return;

        capi.Tesselator.TesselateShape(texBlock, spitOnly, out MeshData spitMesh);
        sharedSpitRodRef = capi.Render.UploadMultiTextureMesh(spitMesh);
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
        if (vis.OwnsFoodMesh)
            vis.PotWithFoodRef?.Dispose();
        // SpitRodRef is shared — do not dispose per burner.
        vis.PotRef = null;
        vis.LidRef = null;
        vis.PotWithFoodRef = null;
        vis.SpitRodRef = null;
        vis.OwnsFoodMesh = false;
        vis.IsSpitItem = false;
        vis.SpitTransform = null;
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
        public bool IsSpitItem;
        public float Temp;
        public bool OwnsFoodMesh;
        public ModelTransform? SpitTransform;
        public MultiTextureMeshRef? PotRef;
        public MultiTextureMeshRef? LidRef;
        public MultiTextureMeshRef? PotWithFoodRef;
        public MultiTextureMeshRef? SpitRodRef;
        public ILoadedSound? CookingSound;
    }
}
