// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace WhiteFox.Client.Entities
{
    /// <summary>
    /// More vanilla entity models (1.20.4 LayerDefinitions) and the texture variants that come with the entity data
    /// (cat, horse color and markings, llama, parrot, rabbit, fox, axolotl, frog, mooshroom, tropical fish, villager
    /// biome type, profession and level).
    /// </summary>
    public static partial class EntityModels
    {
        private static readonly string[] CatNames = { "tabby", "black", "red", "siamese", "british_shorthair", "calico", "persian", "ragdoll", "white", "jellie", "all_black" };
        private static readonly string[] HorseColors = { "white", "creamy", "chestnut", "brown", "black", "gray", "darkbrown" };
        private static readonly string[] HorseMarkings = { null, "white", "whitefield", "whitedots", "blackdots" };
        private static readonly string[] LlamaNames = { "creamy", "white", "brown", "gray" };
        private static readonly string[] ParrotNames = { "red_blue", "blue", "green", "yellow_blue", "grey" };
        private static readonly string[] RabbitNames = { "brown", "white", "black", "white_splotched", "gold", "salt" };
        private static readonly string[] AxolotlNames = { "lucy", "wild", "gold", "cyan", "blue" };
        private static readonly string[] FrogNames = { "temperate", "warm", "cold" };
        private static readonly string[] VillagerTypes = { "desert", "jungle", "plains", "savanna", "snow", "swamp", "taiga" };
        private static readonly string[] Professions = { "none", "armorer", "butcher", "cartographer", "cleric", "farmer", "fisherman", "fletcher", "leatherworker", "librarian", "mason", "nitwit", "shepherd", "toolsmith", "weaponsmith" };
        private static readonly string[] Levels = { "stone", "iron", "gold", "emerald", "diamond" };

        /// <summary>DyeColor texture diffuse colors (tropical fish).</summary>
        private static readonly int[] Dyes =
        {
            0xF9FFFE, 0xF9801D, 0xC74EBD, 0x3AB3DA, 0xFED83D, 0x80C71F, 0xF38BAA, 0x474F52, 0x9D9D97, 0x169C9C, 0x8932B8, 0x3C44AA, 0x835432, 0x5E7C16, 0xB02E26, 0x1D1D21,
        };

        private static string Pick(string[] names, int index) => names[Mathf.Clamp(index, 0, names.Length - 1)];

        private static void Texture(EntityModel model, string texture) => model.Layers[0].Texture = "assets/minecraft/textures/" + texture + ".png";

        /// <summary>A copy of a part tree, every cube inflated a little more (overlay layers must not z-fight the base).</summary>
        private static ModelPart Clone(ModelPart part, float inflate)
        {
            var copy = new ModelPart(part.Name, part.Pivot.x, part.Pivot.y, part.Pivot.z, part.Rotation.x, part.Rotation.y, part.Rotation.z);
            foreach (var cube in part.Cubes) copy.Box(cube.U, cube.V, cube.From.x, cube.From.y, cube.From.z, cube.Size.x, cube.Size.y, cube.Size.z, cube.Inflate + inflate, cube.Mirror);
            foreach (var child in part.Children) copy.Child(Clone(child, inflate));
            return copy;
        }

        private static ModelLayer Overlay(EntityModel model, string texture, float inflate, Color? tint = null)
        {
            var source = model.Layers[0];
            var layer = new ModelLayer { Texture = "assets/minecraft/textures/" + texture + ".png", TextureWidth = source.TextureWidth, TextureHeight = source.TextureHeight, Tint = tint ?? Color.white };
            foreach (var part in source.Parts) layer.Parts.Add(Clone(part, inflate));
            model.Layers.Add(layer);
            return layer;
        }

        private static Color Dye(int index)
        {
            var rgb = Dyes[Mathf.Clamp(index, 0, 15)];
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }

        /// <summary>The model of an entity with its variant from the entity data.</summary>
        public static EntityModel Create(string type, bool slim, int variant, int[] villager)
        {
            // the entity data only carries values that differ from the default: cats default to black, the rest to 0
            if (variant < 0) variant = type == "cat" ? 1 : type == "shulker" ? 16 : 0;
            if (type == "tropical_fish") return TropicalFishVariant(variant);
            var model = Create(type, slim);
            if (model == null) return null;
            switch (type)
            {
                case "cat":
                    Texture(model, "entity/cat/" + Pick(CatNames, variant));
                    break;
                case "horse":
                    Texture(model, "entity/horse/horse_" + Pick(HorseColors, variant & 0xFF));
                    var markings = Pick(HorseMarkings, (variant >> 8) & 0xFF);
                    if (markings != null) Overlay(model, "entity/horse/horse_markings_" + markings, 0.01f);
                    break;
                case "llama":
                case "trader_llama":
                    Texture(model, "entity/llama/" + Pick(LlamaNames, variant));
                    break;
                case "parrot":
                    Texture(model, "entity/parrot/parrot_" + Pick(ParrotNames, variant));
                    break;
                case "rabbit":
                    Texture(model, variant == 99 ? "entity/rabbit/caerbannog" : "entity/rabbit/" + Pick(RabbitNames, variant));
                    break;
                case "fox":
                    Texture(model, variant == 1 ? "entity/fox/snow_fox" : "entity/fox/fox");
                    break;
                case "axolotl":
                    Texture(model, "entity/axolotl/axolotl_" + Pick(AxolotlNames, variant));
                    break;
                case "frog":
                    Texture(model, "entity/frog/" + Pick(FrogNames, variant) + "_frog");
                    break;
                case "mooshroom":
                    Texture(model, variant == 1 ? "entity/cow/brown_mooshroom" : "entity/cow/red_mooshroom");
                    break;
                case "panda":
                case "shulker":
                case "strider":
                    MobVariant(model, type, variant);
                    break;
                case "villager":
                case "zombie_villager":
                {
                    // vanilla VillagerProfessionLayer: biome type, profession and its level badge over the base; the
                    // default data (plains, no profession, level 1) is not sent
                    if (villager == null || villager.Length < 3) villager = new[] { 2, 0, 1 };
                    var folder = "entity/" + type + "/";
                    Overlay(model, folder + "type/" + Pick(VillagerTypes, villager[0]), 0.01f);
                    var profession = Pick(Professions, villager[1]);
                    if (profession != "none")
                    {
                        Overlay(model, folder + "profession/" + profession, 0.02f);
                        if (profession != "nitwit") Overlay(model, folder + "profession_level/" + Pick(Levels, villager[2] - 1), 0.03f);
                    }
                    break;
                }
            }
            return model;
        }

        /// <summary>Tropical fish: shape A (small) or B (large), base color and pattern with its own color (packed variant).</summary>
        private static EntityModel TropicalFishVariant(int variant)
        {
            var large = (variant & 0xFF) == 1;
            var pattern = Mathf.Clamp((variant >> 8) & 0xFF, 0, 5) + 1;
            var model = large ? TropicalFishB() : TropicalFish();
            model.Layers[0].Tint = Dye((variant >> 16) & 0xFF);
            Overlay(model, "entity/fish/tropical_" + (large ? "b" : "a") + "_pattern_" + pattern, 0.008f, Dye((variant >> 24) & 0xFF));
            return model;
        }

        /// <summary>TropicalFishModelB.</summary>
        private static EntityModel TropicalFishB()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/fish/tropical_b", 32, 32);
            layer.Parts.Add(new ModelPart("body", 0, 19, 0).Box(0, 20, -1, -3, -3, 2, 6, 6));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 19, 3).Box(21, 16, 0, -3, 0, 0, 6, 5));
            layer.Parts.Add(new ModelPart("right_fin", -1, 20, 0, 0, Pi / 4).Box(2, 16, -2, 0, 0, 2, 2, 0));
            layer.Parts.Add(new ModelPart("left_fin", 1, 20, 0, 0, -Pi / 4).Box(2, 12, 0, 0, 0, 2, 2, 0));
            layer.Parts.Add(new ModelPart("top_fin", 0, 16, -3).Box(20, 11, 0, -4, 0, 0, 4, 6));
            layer.Parts.Add(new ModelPart("bottom_fin", 0, 22, -3).Box(20, 21, 0, 0, 0, 0, 4, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>TurtleModel (adult).</summary>
        private static EntityModel Turtle()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/turtle/big_sea_turtle", 128, 64);
            layer.Parts.Add(new ModelPart("head", 0, 19, -10).Box(3, 0, -3, -1, -3, 6, 5, 6));
            layer.Parts.Add(new ModelPart("body", 0, 11, -10, Pi / 2).Box(7, 37, -9.5f, 3, -10, 19, 20, 6).Box(31, 1, -5.5f, 3, -13, 11, 18, 3));
            layer.Parts.Add(new ModelPart("right_hind_leg", -3.5f, 22, 11).Box(1, 23, -2, 0, 0, 4, 1, 10));
            layer.Parts.Add(new ModelPart("left_hind_leg", 3.5f, 22, 11).Box(1, 12, -2, 0, 0, 4, 1, 10));
            layer.Parts.Add(new ModelPart("right_front_leg", -5, 21, -4).Box(27, 30, -13, 0, -2, 13, 1, 5));
            layer.Parts.Add(new ModelPart("left_front_leg", 5, 21, -4).Box(27, 24, 0, 0, -2, 13, 1, 5));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>DolphinModel.</summary>
        private static EntityModel Dolphin()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/dolphin", 64, 64);
            var body = new ModelPart("body", 0, 22, -5).Box(22, 0, -4, -7, 0, 8, 7, 13);
            body.Child(new ModelPart("back_fin", 0, 0, 0, Pi / 3).Box(51, 0, -0.5f, 0, 8, 1, 4, 5));
            body.Child(new ModelPart("left_fin", 2, -2, 4, Pi / 3, 0, Pi * 2 / 3).Box(48, 20, -0.5f, -4, 0, 1, 4, 7, 0, true));
            body.Child(new ModelPart("right_fin", -2, -2, 4, Pi / 3, 0, -Pi * 2 / 3).Box(48, 20, -0.5f, -4, 0, 1, 4, 7));
            body.Child(new ModelPart("tail", 0, -2.5f, 11, -0.10471976f).Box(0, 19, -2, -2.5f, 0, 4, 5, 11)
                .Child(new ModelPart("tail_fin", 0, 0, 9).Box(19, 20, -5, -0.5f, 0, 10, 1, 6)));
            body.Child(new ModelPart("head", 0, -4, -3).Box(0, 0, -4, -3, -3, 8, 7, 6).Box(0, 13, -1, 2, -7, 2, 2, 4));
            layer.Parts.Add(body);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>AxolotlModel.</summary>
        private static EntityModel Axolotl()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/axolotl/axolotl_lucy", 64, 64);
            var body = new ModelPart("body", 0, 20, 5).Box(0, 11, -4, -2, -9, 8, 4, 10).Box(2, 17, 0, -3, -8, 0, 5, 9);
            var head = new ModelPart("head", 0, 0, -9).Box(0, 1, -4, -3, -5, 8, 5, 5);
            head.Child(new ModelPart("top_gills", 0, -3, -1).Box(3, 37, -4, -3, 0, 8, 3, 0));
            head.Child(new ModelPart("left_gills", -4, 0, -1).Box(0, 40, -3, -5, 0, 3, 7, 0));
            head.Child(new ModelPart("right_gills", 4, 0, -1).Box(11, 40, 0, -5, 0, 3, 7, 0));
            body.Child(head);
            body.Child(new ModelPart("right_hind_leg", -3.5f, 1, -1).Box(2, 13, -1, 0, 0, 3, 5, 0));
            body.Child(new ModelPart("left_hind_leg", 3.5f, 1, -1).Box(2, 13, -2, 0, 0, 3, 5, 0));
            body.Child(new ModelPart("right_front_leg", -3.5f, 1, -8).Box(2, 13, -1, 0, 0, 3, 5, 0));
            body.Child(new ModelPart("left_front_leg", 3.5f, 1, -8).Box(2, 13, -2, 0, 0, 3, 5, 0));
            body.Child(new ModelPart("tail", 0, 0, 1).Box(2, 19, 0, -3, 0, 0, 5, 12));
            layer.Parts.Add(body);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>FrogModel (simplified: body, head with eyes, legs).</summary>
        private static EntityModel Frog()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/frog/temperate_frog", 48, 48);
            var body = new ModelPart("body", 0, 22, 4).Box(3, 1, -3.5f, -2, -8, 7, 3, 9).Box(23, 22, -3.5f, -1, -8, 7, 0, 9);
            var head = new ModelPart("head", 0, -2, -1).Box(23, 13, -3.5f, -1, -7, 7, 0, 9).Box(0, 13, -3.5f, -2, -7, 7, 3, 9);
            head.Child(new ModelPart("right_eye", -1.5f, -3, -6.5f).Box(0, 0, -1.5f, -1, -1.5f, 3, 2, 3));
            head.Child(new ModelPart("left_eye", 2.5f, -3, -6.5f).Box(0, 5, -1.5f, -1, -1.5f, 3, 2, 3));
            body.Child(head);
            layer.Parts.Add(body);
            layer.Parts.Add(new ModelPart("left_front_leg", 4, 21, -3).Box(0, 32, -2, 0, -2, 4, 3, 4));
            layer.Parts.Add(new ModelPart("right_front_leg", -4, 21, -3).Box(0, 38, -2, 0, -2, 4, 3, 4));
            layer.Parts.Add(new ModelPart("left_hind_leg", 3.5f, 21, 4).Box(14, 25, -1, 0, -2, 3, 3, 4));
            layer.Parts.Add(new ModelPart("right_hind_leg", -3.5f, 21, 4).Box(0, 25, -2, 0, -2, 3, 3, 4));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>GoatModel.</summary>
        private static EntityModel Goat()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/goat/goat", 64, 64);
            var head = new ModelPart("head", 1, 14, 0, 0.9599f).Box(2, 61, -6, -11, -10, 3, 2, 1, 0, true).Box(2, 61, 2, -11, -10, 3, 2, 1).Box(23, 52, -0.5f, -3, -14, 0, 7, 5).Box(34, 46, -3, -4, -8, 5, 7, 10);
            head.Child(new ModelPart("left_horn", 0, 0, 0).Box(12, 55, -0.01f, -16, 10, 2, 7, 2));
            head.Child(new ModelPart("right_horn", 0, 0, 0).Box(12, 55, -2.99f, -16, 10, 2, 7, 2));
            layer.Parts.Add(head);
            layer.Parts.Add(new ModelPart("body", 0, 24, 0).Box(1, 1, -4, -17, -7, 9, 11, 16).Box(0, 28, -5, -18, -8, 11, 14, 11));
            layer.Parts.Add(new ModelPart("left_hind_leg", 1, 14, 4).Box(36, 29, 0, 4, 0, 3, 6, 3));
            layer.Parts.Add(new ModelPart("right_hind_leg", -3, 14, 4).Box(49, 29, 0, 4, 0, 3, 6, 3));
            layer.Parts.Add(new ModelPart("left_front_leg", 1, 14, -6).Box(49, 2, 0, 0, 0, 3, 10, 3));
            layer.Parts.Add(new ModelPart("right_front_leg", -3, 14, -6).Box(35, 2, 0, 0, 0, 3, 10, 3));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>RabbitModel (adults are drawn at 60%, moved down by one block in model space).</summary>
        private static EntityModel Rabbit()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped, Scale = 0.6f, Offset = new Vector3(0, 1, 0) };
            var layer = Layer("entity/rabbit/brown", 64, 32);
            layer.Parts.Add(new ModelPart("left_hind_foot", 3, 17.5f, 3.7f).Box(26, 24, -1, 5.5f, -3.7f, 2, 1, 7));
            layer.Parts.Add(new ModelPart("right_hind_foot", -3, 17.5f, 3.7f).Box(8, 24, -1, 5.5f, -3.7f, 2, 1, 7));
            layer.Parts.Add(new ModelPart("left_haunch", 3, 17.5f, 3.7f, -0.34906584f).Box(30, 15, -1, 0, 0, 2, 4, 5));
            layer.Parts.Add(new ModelPart("right_haunch", -3, 17.5f, 3.7f, -0.34906584f).Box(16, 15, -1, 0, 0, 2, 4, 5));
            layer.Parts.Add(new ModelPart("body", 0, 19, 8, -0.34906584f).Box(0, 0, -3, -2, -10, 6, 5, 10));
            layer.Parts.Add(new ModelPart("left_front_leg", 3, 17, -1, -0.17453292f).Box(8, 15, -1, 0, -1, 2, 7, 2));
            layer.Parts.Add(new ModelPart("right_front_leg", -3, 17, -1, -0.17453292f).Box(0, 15, -1, 0, -1, 2, 7, 2));
            layer.Parts.Add(new ModelPart("head", 0, 16, -1).Box(32, 0, -2.5f, -4, -5, 5, 4, 5)
                .Child(new ModelPart("right_ear", 0, 0, 0, 0, -0.2617994f).Box(52, 0, -2.5f, -9, -1, 2, 5, 1))
                .Child(new ModelPart("left_ear", 0, 0, 0, 0, 0.2617994f).Box(58, 0, 0.5f, -9, -1, 2, 5, 1))
                .Child(new ModelPart("nose", 0, 0, 0).Box(32, 9, -0.5f, -2.5f, -5.5f, 1, 1, 1)));
            layer.Parts.Add(new ModelPart("tail", 0, 20, 7, -0.3490659f).Box(52, 6, -1.5f, -1.5f, 0, 3, 3, 2));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>ParrotModel (standing).</summary>
        private static EntityModel Parrot()
        {
            var model = new EntityModel { Kind = ModelKind.Chicken };
            var layer = Layer("entity/parrot/parrot_red_blue", 32, 32);
            layer.Parts.Add(new ModelPart("body", 0, 16.5f, -3, 0.4937f).Box(2, 8, -1.5f, 0, -1.5f, 3, 6, 3));
            layer.Parts.Add(new ModelPart("tail", 0, 21.07f, 1.16f, 1.015f).Box(22, 1, -1.5f, -1, -1, 3, 4, 1));
            layer.Parts.Add(new ModelPart("left_wing", 1.5f, 16.94f, -2.76f, -0.6981f, -Pi).Box(19, 8, -0.5f, 0, -1.5f, 1, 5, 3));
            layer.Parts.Add(new ModelPart("right_wing", -1.5f, 16.94f, -2.76f, -0.6981f, -Pi).Box(19, 8, -0.5f, 0, -1.5f, 1, 5, 3));
            layer.Parts.Add(new ModelPart("head", 0, 15.69f, -2.76f).Box(2, 2, -1, -1.5f, -1, 2, 3, 2)
                .Child(new ModelPart("head2", 0, -2, -1).Box(10, 0, -1, -0.5f, -2, 2, 1, 4))
                .Child(new ModelPart("beak1", 0, -0.5f, -1.5f).Box(11, 7, -0.5f, -1, -0.5f, 1, 2, 1))
                .Child(new ModelPart("beak2", 0, -1.75f, -2.45f).Box(16, 7, -0.5f, 0, -0.5f, 1, 2, 1))
                .Child(new ModelPart("feather", 0, -2.15f, 0.15f, -0.2214f).Box(2, 18, 0, -4, -2, 0, 5, 4)));
            layer.Parts.Add(new ModelPart("left_leg", 1, 22, -1.05f).Box(14, 18, -0.5f, 0, -0.5f, 1, 2, 1));
            layer.Parts.Add(new ModelPart("right_leg", -1, 22, -1.05f).Box(14, 18, -0.5f, 0, -0.5f, 1, 2, 1));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>CamelModel (the saddle is in the same texture, shown when saddled).</summary>
        private static EntityModel Camel()
        {
            var model = new EntityModel { Kind = ModelKind.Horse };
            var layer = Layer("entity/camel/camel", 128, 128);
            var body = new ModelPart("body", 0, 4, 9.5f).Box(0, 25, -7.5f, -12, -23.5f, 15, 12, 27);
            body.Child(new ModelPart("hump", 0, -12, -10).Box(74, 0, -4.5f, -5, -5.5f, 9, 5, 11));
            body.Child(new ModelPart("tail", 0, -9, 3.5f).Box(122, 0, -1.5f, 0, 0, 3, 14, 0));
            var head = new ModelPart("head", 0, -3, -19.5f).Box(60, 24, -3.5f, -7, -15, 7, 8, 19).Box(21, 0, -3.5f, -21, -15, 7, 14, 7).Box(50, 0, -2.5f, -21, -21, 5, 5, 6);
            head.Child(new ModelPart("left_ear", 3, -21, -9.5f).Box(45, 0, -0.5f, 0.5f, -1, 3, 1, 2));
            head.Child(new ModelPart("right_ear", -3, -21, -9.5f).Box(67, 0, -2.5f, 0.5f, -1, 3, 1, 2));
            body.Child(head);
            body.Child(new ModelPart("saddle", 0, 0, 0).Box(74, 64, -4.5f, -17, -15.5f, 9, 5, 11, 0.1f).Box(92, 114, -3.5f, -20, -15.5f, 7, 3, 11, 0.1f).Box(0, 89, -7.5f, -12, -23.5f, 15, 12, 27, 0.1f));
            layer.Parts.Add(body);
            layer.Parts.Add(new ModelPart("left_hind_leg", 4.9f, 1, 9.5f).Box(58, 16, -2.5f, 2, -2.5f, 5, 21, 5));
            layer.Parts.Add(new ModelPart("right_hind_leg", -4.9f, 1, 9.5f).Box(94, 16, -2.5f, 2, -2.5f, 5, 21, 5));
            layer.Parts.Add(new ModelPart("left_front_leg", 4.9f, 1, -10.5f).Box(0, 0, -2.5f, 2, -2.5f, 5, 21, 5));
            layer.Parts.Add(new ModelPart("right_front_leg", -4.9f, 1, -10.5f).Box(0, 26, -2.5f, 2, -2.5f, 5, 21, 5));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>SnifferModel.</summary>
        private static EntityModel Sniffer()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/sniffer/sniffer", 192, 192);
            var bone = new ModelPart("bone", 0, 5, 0);
            var body = new ModelPart("body", 0, 0, 0).Box(62, 68, -12.5f, -14, -20, 25, 29, 40).Box(62, 0, -12.5f, -14, -20, 25, 24, 40, 0.5f).Box(87, 68, -12.5f, 12, -20, 25, 0, 40);
            var head = new ModelPart("head", 0, 6.5f, -19.48f).Box(8, 15, -6.5f, -7.5f, -11.5f, 13, 18, 11).Box(8, 4, -6.5f, 7.5f, -11.5f, 13, 0, 11);
            head.Child(new ModelPart("left_ear", 6.51f, -7.5f, -4.51f).Box(2, 0, 0, 0, -3, 1, 19, 7));
            head.Child(new ModelPart("right_ear", -6.51f, -7.5f, -4.51f).Box(48, 0, -1, 0, -3, 1, 19, 7));
            head.Child(new ModelPart("nose", 0, -4.5f, -11.5f).Box(10, 45, -6.5f, -2, -9, 13, 2, 9));
            head.Child(new ModelPart("lower_beak", 0, 2.5f, -12.5f).Box(10, 57, -6.5f, -7, -8, 13, 12, 9));
            body.Child(head);
            bone.Child(body);
            bone.Child(new ModelPart("right_front_leg", -7.5f, 10, -15).Box(32, 87, -3.5f, -1, -4, 7, 10, 8));
            bone.Child(new ModelPart("right_mid_leg", -7.5f, 10, 0).Box(32, 105, -3.5f, -1, -4, 7, 10, 8));
            bone.Child(new ModelPart("right_hind_leg", -7.5f, 10, 15).Box(32, 123, -3.5f, -1, -4, 7, 10, 8));
            bone.Child(new ModelPart("left_front_leg", 7.5f, 10, -15).Box(0, 87, -3.5f, -1, -4, 7, 10, 8));
            bone.Child(new ModelPart("left_mid_leg", 7.5f, 10, 0).Box(0, 105, -3.5f, -1, -4, 7, 10, 8));
            bone.Child(new ModelPart("left_hind_leg", 7.5f, 10, 15).Box(0, 123, -3.5f, -1, -4, 7, 10, 8));
            layer.Parts.Add(bone);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>WardenModel.</summary>
        private static EntityModel Warden()
        {
            var model = new EntityModel { Kind = ModelKind.Golem };
            var layer = Layer("entity/warden/warden", 128, 128);
            var bone = new ModelPart("bone", 0, 24, 0);
            var body = new ModelPart("body", 0, -21, 0).Box(0, 0, -9, -13, -4, 18, 21, 11);
            body.Child(new ModelPart("right_ribcage", -7, -2, -4).Box(90, 11, -2, -11, -0.1f, 9, 21, 0));
            body.Child(new ModelPart("left_ribcage", 7, -2, -4).Box(90, 11, -7, -11, -0.1f, 9, 21, 0, 0, true));
            var head = new ModelPart("head", 0, -13, 0).Box(0, 32, -8, -16, -5, 16, 16, 10);
            head.Child(new ModelPart("right_tendril", -8, -12, 0).Box(52, 32, -16, -13, 0, 16, 16, 0));
            head.Child(new ModelPart("left_tendril", 8, -12, 0).Box(58, 0, 0, -13, 0, 16, 16, 0));
            body.Child(head);
            body.Child(new ModelPart("right_arm", -13, -13, 1).Box(44, 50, -4, 0, -4, 8, 28, 8));
            body.Child(new ModelPart("left_arm", 13, -13, 1).Box(0, 58, -4, 0, -4, 8, 28, 8));
            bone.Child(body);
            bone.Child(new ModelPart("right_leg", -5.9f, -13, 0).Box(76, 48, -3.1f, 0, -3, 6, 13, 6));
            bone.Child(new ModelPart("left_leg", 5.9f, -13, 0).Box(76, 76, -2.9f, 0, -3, 6, 13, 6));
            layer.Parts.Add(bone);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>ZombieVillagerModel: the villager head with nose and hat on a humanoid body with zombie arms.</summary>
        private static EntityModel ZombieVillager()
        {
            var model = new EntityModel { Kind = ModelKind.Zombie };
            var layer = Layer("entity/zombie_villager/zombie_villager", 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -10, -4, 8, 10, 8)
                .Child(new ModelPart("nose", 0, -2, 0).Box(24, 0, -1, -1, -6, 2, 4, 2))
                .Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -10, -4, 8, 10, 8, 0.5f)
                    .Child(new ModelPart("hat_rim", 0, 0, 0, -Pi / 2).Box(30, 47, -8, -8, -6, 16, 16, 1))));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 20, -4, 0, -3, 8, 12, 6).Child(new ModelPart("jacket", 0, 0, 0).Box(0, 38, -4, 0, -3, 8, 20, 6, 0.05f)));
            layer.Parts.Add(new ModelPart("right_arm", -5, 2, 0).Box(44, 22, -3, -2, -2, 4, 12, 4));
            layer.Parts.Add(new ModelPart("left_arm", 5, 2, 0).Box(44, 22, -1, -2, -2, 4, 12, 4, 0, true));
            layer.Parts.Add(new ModelPart("right_leg", -2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4));
            layer.Parts.Add(new ModelPart("left_leg", 2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        // GuardianModel spikes: rotation (in half turns) and direction of each of the twelve spikes
        public static readonly float[] SpikeXRot = { 1.75f, 0.25f, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f, 1.25f, 0.75f, 0, 0 };
        public static readonly float[] SpikeYRot = { 0, 0, 0, 0, 0.25f, 1.75f, 1.25f, 0.75f, 0, 0, 0, 0 };
        public static readonly float[] SpikeZRot = { 0, 0, 0.25f, 1.75f, 0, 0, 0, 0, 0, 0, 0.75f, 1.25f };
        public static readonly float[] SpikeX = { 0, 0, 8, -8, -8, 8, 8, -8, 0, 0, 8, -8 };
        public static readonly float[] SpikeY = { -8, -8, -8, -8, 0, 0, 0, 0, 8, 8, 8, 8 };
        public static readonly float[] SpikeZ = { 8, -8, 0, 0, -8, -8, 8, 8, 8, -8, 0, 0 };

        /// <summary>
        /// GuardianModel with its twelve spikes (moved in and out by the animation); the elder guardian is the same
        /// model, 2.35 times bigger.
        /// </summary>
        private static EntityModel Guardian(bool elder)
        {
            var model = new EntityModel { Kind = ModelKind.Guardian, Scale = elder ? 2.35f : 1 };
            var layer = Layer(elder ? "entity/guardian_elder" : "entity/guardian", 64, 64);
            var head = new ModelPart("head", 0, 0, 0).Box(0, 0, -6, 10, -8, 12, 12, 16).Box(0, 28, -8, 10, -6, 2, 12, 12).Box(0, 28, 6, 10, -6, 2, 12, 12, 0, true)
                .Box(16, 40, -6, 8, -6, 12, 2, 12).Box(16, 40, -6, 22, -6, 12, 2, 12)
                .Child(new ModelPart("eye", 0, 0, -8.25f).Box(8, 0, -1, 15, 0, 2, 2, 1));
            for (var i = 0; i < 12; i++)
            {
                head.Child(new ModelPart("spike" + i, SpikeX[i], 16 + SpikeY[i], SpikeZ[i], Pi * SpikeXRot[i], Pi * SpikeYRot[i], Pi * SpikeZRot[i]).Box(0, 0, -1, -4.5f, -1, 2, 9, 2));
            }
            layer.Parts.Add(head);
            layer.Parts.Add(new ModelPart("tail0", 0, 0, 0).Box(40, 0, -2, 14, 7, 4, 4, 8)
                .Child(new ModelPart("tail1", -1.5f, 0.5f, 14).Box(0, 54, 0, 14, 0, 3, 3, 7)
                    .Child(new ModelPart("tail_fin", 0.5f, 0.5f, 6).Box(41, 32, 0, 14, 0, 2, 2, 6).Box(25, 19, 1, 10.5f, 3, 1, 9, 9))));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>WitherBossModel.</summary>
        private static EntityModel Wither()
        {
            var model = new EntityModel { Kind = ModelKind.Wither };
            var layer = Layer("entity/wither/wither", 64, 64);
            layer.Parts.Add(new ModelPart("shoulders", 0, 0, 0).Box(0, 16, -10, 3.9f, -0.5f, 20, 3, 3));
            layer.Parts.Add(new ModelPart("ribcage", -2, 6.9f, -0.5f, 0.2f).Box(0, 22, 0, 0, 0, 3, 10, 3).Box(24, 22, -4, 1.5f, 0.5f, 11, 2, 2).Box(24, 22, -4, 4, 0.5f, 11, 2, 2).Box(24, 22, -4, 6.5f, 0.5f, 11, 2, 2));
            layer.Parts.Add(new ModelPart("tail", -2, 6.9f + Mathf.Cos(0.2f) * 10, -0.5f + Mathf.Sin(0.2f) * 10, 0.83f).Box(12, 22, 0, 0, 0, 3, 6, 3));
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -4, -4, 8, 8, 8));
            layer.Parts.Add(new ModelPart("right_head", -8, 4, 0).Box(32, 0, -4, -4, -4, 6, 6, 6));
            layer.Parts.Add(new ModelPart("left_head", 10, 4, 0).Box(32, 0, -4, -4, -4, 6, 6, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>
        /// DragonModel (the neck and tail follow the position history, EntityRenderer.AnimateDragonSpine): head with jaw,
        /// five neck segments, body with spines, wings with tips, four legs and twelve tail segments.
        /// </summary>
        private static EntityModel EnderDragon()
        {
            var model = new EntityModel { Kind = ModelKind.Dragon };
            var layer = Layer("entity/enderdragon/dragon", 256, 256);
            for (var i = 0; i < 5; i++)
            {
                layer.Parts.Add(new ModelPart("neck" + i, 0, 10, -10 - i * 10).Box(192, 104, -5, -5, -5, 10, 10, 10).Box(48, 0, -1, -9, -3, 2, 4, 6));
            }
            var head = new ModelPart("head", 0, 10, -58).Box(176, 44, -6, -1, -24, 12, 5, 16).Box(112, 30, -8, -8, -10, 16, 16, 16)
                .Box(112, 0, -5, -12, -4, 2, 4, 6, 0, true).Box(112, 0, 3, -12, -4, 2, 4, 6).Box(0, 0, -5, -3, -22, 2, 2, 4, 0, true).Box(0, 0, 3, -3, -22, 2, 2, 4);
            head.Child(new ModelPart("jaw", 0, 4, -8).Box(176, 65, -6, 0, -16, 12, 4, 16));
            layer.Parts.Add(head);
            layer.Parts.Add(new ModelPart("body", 0, 4, 8).Box(0, 0, -12, 0, -16, 24, 24, 64).Box(220, 53, -1, -6, -10, 2, 6, 12).Box(220, 53, -1, -6, 10, 2, 6, 12).Box(220, 53, -1, -6, 30, 2, 6, 12));
            layer.Parts.Add(new ModelPart("left_wing", 12, 5, 2, 0, 0, -0.3f).Box(112, 88, 0, -4, -4, 56, 8, 8).Box(-56, 88, 0, 0, 2, 56, 0, 56)
                .Child(new ModelPart("left_wing_tip", 56, 0, 0, 0, 0, 0.6f).Box(112, 136, 0, -2, -2, 56, 4, 4).Box(-56, 144, 0, 0, 2, 56, 0, 56)));
            layer.Parts.Add(new ModelPart("right_wing", -12, 5, 2, 0, 0, 0.3f).Box(112, 88, -56, -4, -4, 56, 8, 8, 0, true).Box(-56, 88, -56, 0, 2, 56, 0, 56, 0, true)
                .Child(new ModelPart("right_wing_tip", -56, 0, 0, 0, 0, -0.6f).Box(112, 136, -56, -2, -2, 56, 4, 4, 0, true).Box(-56, 144, -56, 0, 2, 56, 0, 56, 0, true)));
            layer.Parts.Add(new ModelPart("left_front_leg", 12, 20, 2, 1.3f).Box(112, 104, -4, -4, -4, 8, 24, 8)
                .Child(new ModelPart("left_front_leg_tip", 0, 20, -1, -0.5f).Box(226, 138, -3, -1, -3, 6, 24, 6)
                    .Child(new ModelPart("left_front_foot", 0, 23, 0, 0.75f).Box(144, 104, -4, 0, -12, 8, 4, 16))));
            layer.Parts.Add(new ModelPart("right_front_leg", -12, 20, 2, 1.3f).Box(112, 104, -4, -4, -4, 8, 24, 8, 0, true)
                .Child(new ModelPart("right_front_leg_tip", 0, 20, -1, -0.5f).Box(226, 138, -3, -1, -3, 6, 24, 6, 0, true)
                    .Child(new ModelPart("right_front_foot", 0, 23, 0, 0.75f).Box(144, 104, -4, 0, -12, 8, 4, 16, 0, true))));
            layer.Parts.Add(new ModelPart("left_hind_leg", 16, 16, 42, 1.0f).Box(0, 0, -8, -4, -8, 16, 32, 16)
                .Child(new ModelPart("left_hind_leg_tip", 0, 32, -4, 0.5f).Box(196, 0, -6, -2, 0, 12, 32, 12)
                    .Child(new ModelPart("left_hind_foot", 0, 31, 4, 0.75f).Box(112, 0, -9, 0, -20, 18, 6, 24))));
            layer.Parts.Add(new ModelPart("right_hind_leg", -16, 16, 42, 1.0f).Box(0, 0, -8, -4, -8, 16, 32, 16, 0, true)
                .Child(new ModelPart("right_hind_leg_tip", 0, 32, -4, 0.5f).Box(196, 0, -6, -2, 0, 12, 32, 12, 0, true)
                    .Child(new ModelPart("right_hind_foot", 0, 31, 4, 0.75f).Box(112, 0, -9, 0, -20, 18, 6, 24, 0, true))));
            for (var i = 0; i < 12; i++)
            {
                layer.Parts.Add(new ModelPart("tail" + i, 0, 10 + i * 0.5f, 60 + i * 10).Box(192, 104, -5, -5, -5, 10, 10, 10).Box(48, 0, -1, -9, -3, 2, 4, 6));
            }
            model.Layers.Add(layer);
            return model;
        }
    }
}
