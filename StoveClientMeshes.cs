using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StainsStovetop;

/// <summary>
/// Client meshes for door + firebox fuel/flame (Kevin-style firepit shapes inside the stove).
/// Firepit shapes reuse this block's texture codes (ember/ashes/fire/hay/birch/...).
/// </summary>
public class StoveClientMeshes
{
    private readonly ICoreClientAPI capi;
    private bool generated;

    public MeshData? DoorClosed;
    public MeshData? DoorOpen;
    public MeshData? FireEmpty;
    public MeshData? FireWithFuel;
    public MeshData? FireLit;

    public StoveClientMeshes(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void EnsureGenerated(Block block)
    {
        if (generated) return;
        generated = true;

        float rotY = (block.Shape?.rotateY ?? 0) * GameMath.DEG2RAD;
        var origin = new Vec3f(0.5f, 0, 0.5f);

        DoorClosed = GenShapeMesh(block, new AssetLocation("stainsstovetop:shapes/block/stovedoorclosed.json"));
        DoorOpen = GenShapeMesh(block, new AssetLocation("stainsstovetop:shapes/block/stovedooropen.json"));
        FireEmpty = GenShapeMesh(block, new AssetLocation("game:shapes/block/wood/firepit/extinct-normal.json"));
        FireWithFuel = GenShapeMesh(block, new AssetLocation("game:shapes/block/wood/firepit/construct5-normal.json"));
        FireLit = GenShapeMesh(block, new AssetLocation("game:shapes/block/wood/firepit/lit-normal.json"));

        DoorClosed?.Rotate(origin, 0, rotY, 0);
        DoorOpen?.Rotate(origin, 0, rotY, 0);

        // Firepit mesh sits in the firebox cavity, slightly inset and scaled to fit behind the door glass.
        void PlaceFire(MeshData? mesh)
        {
            if (mesh == null) return;
            mesh.Scale(Vec3f.Zero, 0.72f, 0.72f, 0.72f)
                .Translate(0.14f, 0.04f, 0.14f)
                .Rotate(origin, 0, rotY, 0);
        }

        PlaceFire(FireEmpty);
        PlaceFire(FireWithFuel);
        PlaceFire(FireLit);
    }

    private MeshData? GenShapeMesh(Block block, AssetLocation loc)
    {
        IAsset? asset = capi.Assets.TryGet(loc);
        if (asset == null) return null;
        Shape? shape = asset.ToObject<Shape>();
        if (shape == null) return null;
        capi.Tesselator.TesselateShape(block, shape, out MeshData mesh);
        return mesh;
    }

    /// <summary>
    /// Burner pot transforms — matches Kevin Furniture <c>genTransformationMatrices</c>
    /// (facing offsets + RotateYDeg(90*facing) + Translate; scale 1 so position stays centered).
    /// </summary>
    public static float[][] GenPotMatrices(int facing)
    {
        var mats = new float[4][];
        for (int i = 0; i < 4; i++)
        {
            float tx;
            float tz;
            switch (i)
            {
                case 0: tx = -0.25f; tz = 0.25f; break;
                case 1: tx = -0.25f; tz = -0.25f; break;
                case 2: tx = 0.25f; tz = 0.25f; break;
                default: tx = 0.25f; tz = -0.25f; break;
            }

            // Kevin facing remap: 1 west, 2 south, 3 east
            switch (facing)
            {
                case 1:
                    tx -= 1;
                    break;
                case 2:
                    tx -= 1;
                    tz -= 1;
                    break;
                case 3:
                    tz -= 1;
                    break;
            }

            // Do not Scale here: Matrixf.Scale after Translate pulls toward origin and shifts pots.
            mats[i] = new Matrixf()
                .RotateYDeg(90 * facing)
                .Translate(tx, 1f, tz)
                .Values;
        }
        return mats;
    }
}
