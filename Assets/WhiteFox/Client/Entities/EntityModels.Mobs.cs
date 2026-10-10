// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace WhiteFox.Client.Entities
{
    /// <summary>More vanilla mob models (their LayerDefinitions): allay, vex, endermite, silverfish, magma cube, phantom, hoglin, ravager, panda, strider, shulker, tadpole, breeze, giant.</summary>
    public static partial class EntityModels
    {
        private static readonly string[] ShulkerColors = { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };
        private static readonly string[] PandaGenes = { "panda", "lazy_panda", "worried_panda", "playful_panda", "brown_panda", "weak_panda", "aggressive_panda" };

        /// <summary>The mob models of this file by entity type, or null.</summary>
        private static EntityModel CreateMob(string type)
        {
            switch (type)
            {
                case "allay": return Allay();
                case "vex": return Vex();
                case "endermite": return Segments("entity/endermite", 64, 32, new[,] { { 4, 3, 2 }, { 6, 4, 5 }, { 3, 3, 1 }, { 1, 2, 1 } }, new[,] { { 0, 0 }, { 0, 5 }, { 0, 14 }, { 0, 18 } }, false);
                case "silverfish": return Segments("entity/silverfish", 64, 32, new[,] { { 3, 2, 2 }, { 4, 3, 2 }, { 6, 4, 3 }, { 3, 3, 3 }, { 2, 2, 3 }, { 2, 1, 2 }, { 1, 1, 2 } }, new[,] { { 0, 0 }, { 0, 4 }, { 0, 9 }, { 0, 16 }, { 0, 22 }, { 11, 0 }, { 13, 4 } }, true);
                case "magma_cube": return MagmaCube();
                case "phantom": return Phantom();
                case "hoglin": return Hoglin("entity/hoglin/hoglin");
                case "zoglin": return Hoglin("entity/hoglin/zoglin");
                case "ravager": return Ravager();
                case "panda": return Panda();
                case "strider": return Strider();
                case "shulker": return Shulker();
                case "tadpole": return Tadpole();
                case "breeze": return Breeze();
                case "giant": return Humanoid("entity/zombie/zombie", 64, 64, ModelKind.Zombie, 6);
                default: return null;
            }
        }

        /// <summary>Textures of the mob variants: panda genes, shulker colors, the shivering strider.</summary>
        private static void MobVariant(EntityModel model, string type, int variant)
        {
            switch (type)
            {
                case "panda":
                    Texture(model, "entity/panda/" + Pick(PandaGenes, variant));
                    break;
                case "shulker":
                    if (variant >= 0 && variant < 16) Texture(model, "entity/shulker/shulker_" + ShulkerColors[variant]);
                    break;
                case "strider":
                    if (variant == 1) Texture(model, "entity/strider/strider_cold");
                    break;
            }
        }

        /// <summary>AllayModel: a floating head and body with arms and two wings.</summary>
        private static EntityModel Allay()
        {
            var model = new EntityModel { Kind = ModelKind.Allay };
            var layer = Layer("entity/allay/allay", 32, 32);
            var body = new ModelPart("body", 0, -4, 0).Box(0, 10, -1.5f, 0, -1, 3, 4, 2).Box(0, 16, -1.5f, 0, -1, 3, 5, 2, -0.2f)
                .Child(new ModelPart("right_arm", -1.75f, 0.5f, 0).Box(23, 0, -0.75f, -0.5f, -1, 1, 4, 2, -0.01f))
                .Child(new ModelPart("left_arm", 1.75f, 0.5f, 0).Box(23, 6, -0.25f, -0.5f, -1, 1, 4, 2, -0.01f))
                .Child(new ModelPart("right_wing", -0.5f, 0, 0.6f).Box(16, 14, 0, 1, 0, 0, 5, 8))
                .Child(new ModelPart("left_wing", 0.5f, 0, 0.6f).Box(16, 14, 0, 1, 0, 0, 5, 8));
            layer.Parts.Add(new ModelPart("root", 0, 23.5f, 0).Child(new ModelPart("head", 0, -3.99f, 0).Box(0, 0, -2.5f, -5, -2.5f, 5, 5, 5)).Child(body));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>VexModel (1.19.3+, the allay-like shape).</summary>
        private static EntityModel Vex()
        {
            var model = new EntityModel { Kind = ModelKind.Allay };
            var layer = Layer("entity/illager/vex", 32, 32);
            var body = new ModelPart("body", 0, 20, 0).Box(0, 10, -1.5f, 0, -1, 3, 4, 2).Box(0, 16, -1.5f, 1, -1, 3, 5, 2, -0.2f)
                .Child(new ModelPart("right_arm", -1.75f, 0.25f, 0).Box(23, 0, -1.25f, -0.5f, -1, 2, 4, 2, -0.1f))
                .Child(new ModelPart("left_arm", 1.75f, 0.25f, 0).Box(23, 6, -0.75f, -0.5f, -1, 2, 4, 2, -0.1f))
                .Child(new ModelPart("left_wing", 0.5f, 1, 1).Box(16, 14, 0, 0, 0, 0, 5, 8, 0, true))
                .Child(new ModelPart("right_wing", -0.5f, 1, 1).Box(16, 14, 0, 0, 0, 0, 5, 8));
            layer.Parts.Add(new ModelPart("root", 0, -2.5f, 0).Child(new ModelPart("head", 0, 20, 0).Box(0, 0, -2.5f, -5, -2.5f, 5, 5, 5)).Child(body));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>EndermiteModel / SilverfishModel: body segments one after the other, the silverfish with its fur layers.</summary>
        private static EntityModel Segments(string texture, int width, int height, int[,] sizes, int[,] uvs, bool silverfish)
        {
            var model = new EntityModel { Kind = ModelKind.Segments };
            var layer = Layer(texture, width, height);
            var count = sizes.GetLength(0);
            var placements = new float[count];
            var z = -3.5f;
            for (var i = 0; i < count; i++)
            {
                layer.Parts.Add(new ModelPart("segment" + i, 0, 24 - sizes[i, 1], z).Box(uvs[i, 0], uvs[i, 1], sizes[i, 0] * -0.5f, 0, sizes[i, 2] * -0.5f, sizes[i, 0], sizes[i, 1], sizes[i, 2]));
                placements[i] = z;
                if (i < count - 1) z += (sizes[i, 2] + sizes[i + 1, 2]) * 0.5f;
            }
            if (silverfish)
            {
                layer.Parts.Add(new ModelPart("layer0", 0, 16, placements[2]).Box(20, 0, -5, 0, sizes[2, 2] * -0.5f, 10, 8, sizes[2, 2]));
                layer.Parts.Add(new ModelPart("layer1", 0, 20, placements[4]).Box(20, 11, -3, 0, sizes[4, 2] * -0.5f, 6, 4, sizes[4, 2]));
                layer.Parts.Add(new ModelPart("layer2", 0, 19, placements[1]).Box(20, 18, -3, 0, sizes[4, 2] * -0.5f, 6, 5, sizes[1, 2]));
            }
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>MagmaCubeModel: eight slices around a small core (scaled by the size like slimes).</summary>
        private static EntityModel MagmaCube()
        {
            var model = new EntityModel { Kind = ModelKind.Slime };
            var layer = Layer("entity/slime/magmacube", 64, 32);
            for (var i = 0; i < 8; i++)
            {
                int u = 0, v = i;
                if (i == 2)
                {
                    u = 24;
                    v = 10;
                }
                else if (i == 3)
                {
                    u = 24;
                    v = 19;
                }
                layer.Parts.Add(new ModelPart("cube" + i, 0, 0, 0).Box(u, v, -4, 16 + i, -4, 8, 1, 8));
            }
            layer.Parts.Add(new ModelPart("inside_cube", 0, 0, 0).Box(0, 16, -2, 18, -2, 4, 4, 4));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PhantomModel (PhantomRenderer: translate(0, 1.3125, 0.1875), scale 1 + 0.15 * size).</summary>
        private static EntityModel Phantom()
        {
            var model = new EntityModel { Kind = ModelKind.Phantom, Offset = new Vector3(0, 1.3125f, 0.1875f) };
            var layer = Layer("entity/phantom", 64, 64);
            var body = new ModelPart("body", 0, 0, 0, -0.1f).Box(0, 8, -3, -2, -8, 5, 3, 9)
                .Child(new ModelPart("tail_base", 0, -2, 1).Box(3, 20, -2, 0, 0, 3, 2, 6)
                    .Child(new ModelPart("tail_tip", 0, 0.5f, 6).Box(4, 29, -1, 0, 0, 1, 1, 6)))
                .Child(new ModelPart("left_wing_base", 2, -2, -8, 0, 0, 0.1f).Box(23, 12, 0, 0, 0, 6, 2, 9)
                    .Child(new ModelPart("left_wing_tip", 6, 0, 0, 0, 0, 0.1f).Box(16, 24, 0, 0, 0, 13, 1, 9)))
                .Child(new ModelPart("right_wing_base", -3, -2, -8, 0, 0, -0.1f).Box(23, 12, -6, 0, 0, 6, 2, 9, 0, true)
                    .Child(new ModelPart("right_wing_tip", -6, 0, 0, 0, 0, -0.1f).Box(16, 24, -13, 0, 0, 13, 1, 9, 0, true)))
                .Child(new ModelPart("head", 0, 1, -7, 0.2f).Box(0, 0, -4, -2, -5, 7, 3, 5));
            layer.Parts.Add(body);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>HoglinModel (the zoglin is the same model).</summary>
        private static EntityModel Hoglin(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Hoglin };
            var layer = Layer(texture, 128, 64);
            layer.Parts.Add(new ModelPart("body", 0, 7, 0).Box(1, 1, -8, -7, -13, 16, 14, 26)
                .Child(new ModelPart("mane", 0, -14, -5).Box(90, 33, 0, 0, -9, 0, 10, 19, 0.001f)));
            layer.Parts.Add(new ModelPart("head", 0, 2, -12, 0.87266463f).Box(61, 1, -7, -3, -19, 14, 6, 19)
                .Child(new ModelPart("right_ear", -6, -2, -3, 0, 0, -0.6981317f).Box(1, 1, -6, -1, -2, 6, 1, 4))
                .Child(new ModelPart("left_ear", 6, -2, -3, 0, 0, 0.6981317f).Box(1, 6, 0, -1, -2, 6, 1, 4))
                .Child(new ModelPart("right_horn", -7, 2, -12).Box(10, 13, -1, -11, -1, 2, 11, 2))
                .Child(new ModelPart("left_horn", 7, 2, -12).Box(1, 13, -1, -11, -1, 2, 11, 2)));
            layer.Parts.Add(new ModelPart("right_front_leg", -4, 10, -8.5f).Box(66, 42, -3, 0, -3, 6, 14, 6));
            layer.Parts.Add(new ModelPart("left_front_leg", 4, 10, -8.5f).Box(41, 42, -3, 0, -3, 6, 14, 6));
            layer.Parts.Add(new ModelPart("right_hind_leg", -5, 13, 10).Box(21, 45, -2.5f, 0, -2.5f, 5, 11, 5));
            layer.Parts.Add(new ModelPart("left_hind_leg", 5, 13, 10).Box(0, 45, -2.5f, 0, -2.5f, 5, 11, 5));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>RavagerModel.</summary>
        private static EntityModel Ravager()
        {
            var model = new EntityModel { Kind = ModelKind.Hoglin };
            var layer = Layer("entity/illager/ravager", 128, 128);
            var head = new ModelPart("head", 0, 16, -17).Box(0, 0, -8, -20, -14, 16, 20, 16).Box(0, 0, -2, -6, -18, 4, 8, 4)
                .Child(new ModelPart("right_horn", -10, -14, -8, 1.0995574f).Box(74, 55, 0, -14, -2, 2, 14, 4))
                .Child(new ModelPart("left_horn", 8, -14, -8, 1.0995574f).Box(74, 55, 0, -14, -2, 2, 14, 4, 0, true))
                .Child(new ModelPart("mouth", 0, -2, 2).Box(0, 36, -8, 0, -16, 16, 3, 16));
            layer.Parts.Add(new ModelPart("neck", 0, -7, 5.5f).Box(68, 73, -5, -1, -18, 10, 10, 18).Child(head));
            layer.Parts.Add(new ModelPart("body", 0, 1, 2, Pi / 2).Box(0, 55, -7, -10, -7, 14, 16, 20).Box(0, 91, -6, 6, -7, 12, 13, 18));
            layer.Parts.Add(new ModelPart("right_hind_leg", -8, -13, 18).Box(96, 0, -4, 0, -4, 8, 37, 8));
            layer.Parts.Add(new ModelPart("left_hind_leg", 8, -13, 18).Box(96, 0, -4, 0, -4, 8, 37, 8, 0, true));
            layer.Parts.Add(new ModelPart("right_front_leg", -8, -13, -5).Box(64, 0, -4, 0, -4, 8, 37, 8));
            layer.Parts.Add(new ModelPart("left_front_leg", 8, -13, -5).Box(64, 0, -4, 0, -4, 8, 37, 8, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PandaModel (texture by the displayed gene).</summary>
        private static EntityModel Panda()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/panda/panda", 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 11.5f, -17).Box(0, 6, -6.5f, -5, -4, 13, 10, 9).Box(45, 16, -3.5f, 0, -6, 7, 5, 2)
                .Box(52, 25, -8.5f, -8, -1, 5, 4, 1).Box(52, 25, 3.5f, -8, -1, 5, 4, 1));
            layer.Parts.Add(new ModelPart("body", 0, 10, 0, Pi / 2).Box(0, 25, -9.5f, -13, -6.5f, 19, 26, 13));
            layer.Parts.Add(new ModelPart("right_hind_leg", -5.5f, 15, 9).Box(40, 0, -3, 0, -3, 6, 9, 6));
            layer.Parts.Add(new ModelPart("left_hind_leg", 5.5f, 15, 9).Box(40, 0, -3, 0, -3, 6, 9, 6));
            layer.Parts.Add(new ModelPart("right_front_leg", -5.5f, 15, -9).Box(40, 0, -3, 0, -3, 6, 9, 6));
            layer.Parts.Add(new ModelPart("left_front_leg", 5.5f, 15, -9).Box(40, 0, -3, 0, -3, 6, 9, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>StriderModel with its six bristles; the saddle is the second layer (shown when saddled).</summary>
        private static EntityModel Strider()
        {
            var model = new EntityModel { Kind = ModelKind.Strider };
            var layer = Layer("entity/strider/strider", 64, 128);
            StriderParts(layer);
            model.Layers.Add(layer);
            var saddle = Layer("entity/strider/strider_saddle", 64, 128);
            StriderParts(saddle, 0.01f);
            model.Layers.Add(saddle);
            return model;
        }

        private static void StriderParts(ModelLayer layer, float inflate = 0)
        {
            layer.Parts.Add(new ModelPart("right_leg", -4, 8, 0).Box(0, 32, -2, 0, -2, 4, 16, 4, inflate));
            layer.Parts.Add(new ModelPart("left_leg", 4, 8, 0).Box(0, 55, -2, 0, -2, 4, 16, 4, inflate));
            layer.Parts.Add(new ModelPart("body", 0, 1, 0).Box(0, 0, -8, -6, -8, 16, 14, 16, inflate)
                .Child(new ModelPart("right_bottom_bristle", -8, 4, -8, 0, 0, -1.2217305f).Box(16, 65, -12, 0, 0, 12, 0, 16, 0, true))
                .Child(new ModelPart("right_middle_bristle", -8, -1, -8, 0, 0, -1.134464f).Box(16, 49, -12, 0, 0, 12, 0, 16, 0, true))
                .Child(new ModelPart("right_top_bristle", -8, -5, -8, 0, 0, -0.8726646f).Box(16, 33, -12, 0, 0, 12, 0, 16, 0, true))
                .Child(new ModelPart("left_top_bristle", 8, -6, -8, 0, 0, 0.8726646f).Box(16, 33, 0, 0, 0, 12, 0, 16))
                .Child(new ModelPart("left_middle_bristle", 8, -2, -8, 0, 0, 1.134464f).Box(16, 49, 0, 0, 0, 12, 0, 16))
                .Child(new ModelPart("left_bottom_bristle", 8, 3, -8, 0, 0, 1.2217305f).Box(16, 65, 0, 0, 0, 12, 0, 16)));
        }

        /// <summary>ShulkerModel: base, lid (opened by the peek amount) and head; the color picks the texture.</summary>
        private static EntityModel Shulker()
        {
            var model = new EntityModel { Kind = ModelKind.Shulker };
            var layer = Layer("entity/shulker/shulker", 64, 64);
            layer.Parts.Add(new ModelPart("lid", 0, 24, 0).Box(0, 0, -8, -16, -8, 16, 12, 16));
            layer.Parts.Add(new ModelPart("base", 0, 24, 0).Box(0, 28, -8, -8, -8, 16, 8, 16));
            layer.Parts.Add(new ModelPart("head", 0, 12, 0).Box(0, 52, -3, 0, -3, 6, 6, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>TadpoleModel (the tail swings like a fish tail).</summary>
        private static EntityModel Tadpole()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/tadpole/tadpole", 16, 16);
            layer.Parts.Add(new ModelPart("body", 0, 22, -3).Box(0, 0, -1.5f, -1, 0, 3, 2, 3));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 22, 0).Box(0, 0, 0, -1, 0, 0, 2, 7));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>BreezeModel (1.20.4 experimental): head and three spinning rods (the wind layer is not drawn).</summary>
        private static EntityModel Breeze()
        {
            var model = new EntityModel { Kind = ModelKind.Breeze };
            var layer = Layer("entity/breeze/breeze", 32, 32);
            var rods = new ModelPart("rods", 0, 8, 0)
                .Child(new ModelPart("rod_1", 2.5981f, -3, 1.5f, -2.7489f, -1.0472f, 3.1416f).Box(0, 17, -1, 0, -3, 2, 8, 2))
                .Child(new ModelPart("rod_2", -2.5981f, -3, 1.5f, -2.7489f, 1.0472f, 3.1416f).Box(0, 17, -1, 0, -3, 2, 8, 2))
                .Child(new ModelPart("rod_3", 0, -3, -3, 0.3927f).Box(0, 17, -1, 0, -3, 2, 8, 2));
            var head = new ModelPart("head", 0, 4, 0).Box(4, 24, -5, -5, -4.2f, 10, 3, 4).Box(0, 0, -4, -8, -4, 8, 8, 8);
            layer.Parts.Add(new ModelPart("body", 0, 8, 0).Child(rods).Child(head));
            model.Layers.Add(layer);
            return model;
        }
    }
}
