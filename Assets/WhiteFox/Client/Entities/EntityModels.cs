// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;

namespace WhiteFox.Client.Entities
{
    /// <summary>A box of a model part in vanilla model units (pixels, y down, front = -z), with box UV at (U, V).</summary>
    public sealed class ModelCube
    {
        public Vector3 From;
        public Vector3 Size;
        public int U;
        public int V;
        public float Inflate;
        public bool Mirror;
    }

    /// <summary>A model part (vanilla PartDefinition): pivot, rotation (radians, applied Z, Y, X) and children.</summary>
    public sealed class ModelPart
    {
        public string Name;
        public Vector3 Pivot;
        public Vector3 Rotation;
        public readonly List<ModelCube> Cubes = new List<ModelCube>();
        public readonly List<ModelPart> Children = new List<ModelPart>();

        public ModelPart(string name, float x, float y, float z, float rx = 0, float ry = 0, float rz = 0)
        {
            Name = name;
            Pivot = new Vector3(x, y, z);
            Rotation = new Vector3(rx, ry, rz);
        }

        public ModelPart Box(int u, int v, float x, float y, float z, float sx, float sy, float sz, float inflate = 0, bool mirror = false)
        {
            Cubes.Add(new ModelCube { U = u, V = v, From = new Vector3(x, y, z), Size = new Vector3(sx, sy, sz), Inflate = inflate, Mirror = mirror });
            return this;
        }

        public ModelPart Child(ModelPart child)
        {
            Children.Add(child);
            return this;
        }
    }

    /// <summary>One texture with its parts (an entity can have several, like the sheep wool).</summary>
    public sealed class ModelLayer
    {
        public string Texture;
        public int TextureWidth = 64;
        public int TextureHeight = 32;
        public Color Tint = Color.white;
        public readonly List<ModelPart> Parts = new List<ModelPart>();
    }

    public enum ModelKind
    {
        Biped,
        Zombie,
        Quadruped,
        Chicken,
        Spider,
        Villager,
        Slime,
        Boat,
        Minecart,
        Golem,
        Bee,
        Fish,
        Squid,
        Blaze,
        Ghast,
        Horse,
        ArmorStand,
        Bat,
        Guardian,
        Wither,
        Dragon,
    }

    public sealed class EntityModel
    {
        public ModelKind Kind;
        public float Scale = 1;
        /// <summary>Translation after the scale (model units are blocks; rabbits sit one block lower at 60%).</summary>
        public Vector3 Offset;
        public readonly List<ModelLayer> Layers = new List<ModelLayer>();
    }

    /// <summary>
    /// Vanilla entity models, transcribed from the game's LayerDefinitions (Minosoft only ships player/cow/pig as
    /// skeletal models; the others are added here). Coordinates are the vanilla ones.
    /// </summary>
    public static partial class EntityModels
    {
        private const float Pi = Mathf.PI;

        public static EntityModel Create(string type, bool slim = false)
        {
            switch (type)
            {
                case "player": return Player(slim);
                case "zombie": return Humanoid("entity/zombie/zombie", 64, 64, ModelKind.Zombie);
                case "husk": return Humanoid("entity/zombie/husk", 64, 64, ModelKind.Zombie, 1.0625f);
                case "drowned": return Humanoid("entity/zombie/drowned", 64, 64, ModelKind.Zombie);
                case "zombie_villager": return ZombieVillager();
                case "skeleton": return Skeleton("entity/skeleton/skeleton", 1);
                case "stray": return Skeleton("entity/skeleton/stray", 1);
                case "wither_skeleton": return Skeleton("entity/skeleton/wither_skeleton", 1.2f);
                case "creeper": return Creeper();
                case "spider": return Spider("entity/spider/spider", 1);
                case "cave_spider": return Spider("entity/spider/cave_spider", 0.7f);
                case "cow": return Cow("entity/cow/cow");
                case "mooshroom": return Cow("entity/cow/red_mooshroom");
                case "pig": return Pig();
                case "sheep": return Sheep();
                case "chicken": return Chicken();
                case "enderman": return Enderman();
                case "villager": return Villager("entity/villager/villager");
                case "wandering_trader": return Villager("entity/wandering_trader");
                case "witch": return Villager("entity/witch");
                case "slime": return Slime("entity/slime/slime");
                case "boat":
                case "chest_boat": return Boat();
                case "minecart":
                case "chest_minecart":
                case "furnace_minecart":
                case "hopper_minecart":
                case "tnt_minecart":
                case "spawner_minecart":
                case "command_block_minecart": return Minecart();
                case "wolf": return Wolf();
                case "cat": return Feline("entity/cat/tabby", 0.8f);
                case "ocelot": return Feline("entity/cat/ocelot", 1);
                case "iron_golem": return IronGolem();
                case "snow_golem": return SnowGolem();
                case "piglin": return Piglin("entity/piglin/piglin", ModelKind.Biped);
                case "piglin_brute": return Piglin("entity/piglin/piglin_brute", ModelKind.Biped);
                case "zombified_piglin": return Piglin("entity/piglin/zombified_piglin", ModelKind.Zombie);
                case "pillager": return Illager("entity/illager/pillager");
                case "vindicator": return Illager("entity/illager/vindicator");
                case "evoker": return Illager("entity/illager/evoker");
                case "illusioner": return Illager("entity/illager/illusioner");
                case "llama": return Llama("entity/llama/creamy");
                case "trader_llama": return Llama("entity/llama/creamy");
                case "bee": return Bee();
                case "cod": return Cod();
                case "salmon": return Salmon();
                case "tropical_fish": return TropicalFish();
                case "pufferfish": return Pufferfish();
                case "squid": return Squid("entity/squid/squid");
                case "glow_squid": return Squid("entity/squid/glow_squid");
                case "bat": return Bat();
                case "fox": return Fox();
                case "polar_bear": return PolarBear();
                case "blaze": return Blaze();
                case "ghast": return Ghast();
                case "horse": return Horse("entity/horse/horse_brown", 1.1f);
                case "donkey": return Horse("entity/horse/donkey", 0.87f);
                case "mule": return Horse("entity/horse/mule", 0.92f);
                case "skeleton_horse": return Horse("entity/horse/horse_skeleton", 1);
                case "zombie_horse": return Horse("entity/horse/horse_zombie", 1);
                case "armor_stand": return ArmorStand();
                case "turtle": return Turtle();
                case "dolphin": return Dolphin();
                case "axolotl": return Axolotl();
                case "frog": return Frog();
                case "goat": return Goat();
                case "rabbit": return Rabbit();
                case "parrot": return Parrot();
                case "camel": return Camel();
                case "sniffer": return Sniffer();
                case "warden": return Warden();
                case "guardian": return Guardian(false);
                case "elder_guardian": return Guardian(true);
                case "wither": return Wither();
                case "ender_dragon": return EnderDragon();
                default: return null;
            }
        }

        private static ModelLayer Layer(string texture, int width, int height)
        {
            return new ModelLayer { Texture = "assets/minecraft/textures/" + texture + ".png", TextureWidth = width, TextureHeight = height };
        }

        /// <summary>Armor model (HumanoidArmorModel): the humanoid mesh inflated by 1 (outer) or 0.5 (inner, leggings), 64x32.</summary>
        public static ModelLayer Armor(bool inner)
        {
            var layer = new ModelLayer { TextureWidth = 64, TextureHeight = 32 };
            HumanoidParts(layer, inner ? 0.5f : 1.0f);
            return layer;
        }

        /// <summary>HumanoidModel.createMesh.</summary>
        private static void HumanoidParts(ModelLayer layer, float inflate = 0)
        {
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -8, -4, 8, 8, 8, inflate).Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -8, -4, 8, 8, 8, inflate + 0.5f)));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 16, -4, 0, -2, 8, 12, 4, inflate));
            layer.Parts.Add(new ModelPart("right_arm", -5, 2, 0).Box(40, 16, -3, -2, -2, 4, 12, 4, inflate));
            layer.Parts.Add(new ModelPart("left_arm", 5, 2, 0).Box(40, 16, -1, -2, -2, 4, 12, 4, inflate, true));
            layer.Parts.Add(new ModelPart("right_leg", -1.9f, 12, 0).Box(0, 16, -2, 0, -2, 4, 12, 4, inflate));
            layer.Parts.Add(new ModelPart("left_leg", 1.9f, 12, 0).Box(0, 16, -2, 0, -2, 4, 12, 4, inflate, true));
        }

        private static EntityModel Humanoid(string texture, int width, int height, ModelKind kind, float scale = 1)
        {
            var model = new EntityModel { Kind = kind, Scale = scale };
            var layer = Layer(texture, width, height);
            HumanoidParts(layer);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PlayerModel.createMesh: own left limbs, second skin layer (hat, jacket, sleeves, pants), slim arms.</summary>
        private static EntityModel Player(bool slim)
        {
            var model = new EntityModel { Kind = ModelKind.Biped };
            var layer = Layer("entity/player/wide/steve", 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -8, -4, 8, 8, 8).Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -8, -4, 8, 8, 8, 0.5f)));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 16, -4, 0, -2, 8, 12, 4).Child(new ModelPart("jacket", 0, 0, 0).Box(16, 32, -4, 0, -2, 8, 12, 4, 0.25f)));
            var armWidth = slim ? 3 : 4;
            var armY = slim ? 2.5f : 2;
            layer.Parts.Add(new ModelPart("right_arm", -5, armY, 0).Box(40, 16, slim ? -2 : -3, -2, -2, armWidth, 12, 4)
                .Child(new ModelPart("right_sleeve", 0, 0, 0).Box(40, 32, slim ? -2 : -3, -2, -2, armWidth, 12, 4, 0.25f)));
            layer.Parts.Add(new ModelPart("left_arm", 5, armY, 0).Box(32, 48, -1, -2, -2, armWidth, 12, 4)
                .Child(new ModelPart("left_sleeve", 0, 0, 0).Box(48, 48, -1, -2, -2, armWidth, 12, 4, 0.25f)));
            layer.Parts.Add(new ModelPart("right_leg", -1.9f, 12, 0).Box(0, 16, -2, 0, -2, 4, 12, 4).Child(new ModelPart("right_pants", 0, 0, 0).Box(0, 32, -2, 0, -2, 4, 12, 4, 0.25f)));
            layer.Parts.Add(new ModelPart("left_leg", 1.9f, 12, 0).Box(16, 48, -2, 0, -2, 4, 12, 4).Child(new ModelPart("left_pants", 0, 0, 0).Box(0, 48, -2, 0, -2, 4, 12, 4, 0.25f)));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>SkeletonModel: humanoid with 2x12x2 limbs.</summary>
        private static EntityModel Skeleton(string texture, float scale)
        {
            var model = new EntityModel { Kind = ModelKind.Biped, Scale = scale };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -8, -4, 8, 8, 8).Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -8, -4, 8, 8, 8, 0.5f)));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 16, -4, 0, -2, 8, 12, 4));
            layer.Parts.Add(new ModelPart("right_arm", -5, 2, 0).Box(40, 16, -1, -2, -1, 2, 12, 2));
            layer.Parts.Add(new ModelPart("left_arm", 5, 2, 0).Box(40, 16, -1, -2, -1, 2, 12, 2, 0, true));
            layer.Parts.Add(new ModelPart("right_leg", -2, 12, 0).Box(0, 16, -1, 0, -1, 2, 12, 2));
            layer.Parts.Add(new ModelPart("left_leg", 2, 12, 0).Box(0, 16, -1, 0, -1, 2, 12, 2, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Creeper()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/creeper/creeper", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 6, 0).Box(0, 0, -4, -8, -4, 8, 8, 8));
            layer.Parts.Add(new ModelPart("body", 0, 6, 0).Box(16, 16, -4, 0, -2, 8, 12, 4));
            layer.Parts.Add(new ModelPart("right_hind_leg", -2, 18, 4).Box(0, 16, -2, 0, -2, 4, 6, 4));
            layer.Parts.Add(new ModelPart("left_hind_leg", 2, 18, 4).Box(0, 16, -2, 0, -2, 4, 6, 4));
            layer.Parts.Add(new ModelPart("right_front_leg", -2, 18, -4).Box(0, 16, -2, 0, -2, 4, 6, 4));
            layer.Parts.Add(new ModelPart("left_front_leg", 2, 18, -4).Box(0, 16, -2, 0, -2, 4, 6, 4));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>QuadrupedModel legs (legHeight pixels tall).</summary>
        private static void QuadrupedLegs(ModelLayer layer, int legHeight, float frontZ, float hindZ, float x, bool mirrorLeft)
        {
            layer.Parts.Add(new ModelPart("right_hind_leg", -x, 24 - legHeight, hindZ).Box(0, 16, -2, 0, -2, 4, legHeight, 4));
            layer.Parts.Add(new ModelPart("left_hind_leg", x, 24 - legHeight, hindZ).Box(0, 16, -2, 0, -2, 4, legHeight, 4, 0, mirrorLeft));
            layer.Parts.Add(new ModelPart("right_front_leg", -x, 24 - legHeight, frontZ).Box(0, 16, -2, 0, -2, 4, legHeight, 4));
            layer.Parts.Add(new ModelPart("left_front_leg", x, 24 - legHeight, frontZ).Box(0, 16, -2, 0, -2, 4, legHeight, 4, 0, mirrorLeft));
        }

        private static EntityModel Cow(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 4, -8).Box(0, 0, -4, -4, -6, 8, 8, 6).Box(22, 0, -5, -5, -4, 1, 3, 1).Box(22, 0, 4, -5, -4, 1, 3, 1));
            layer.Parts.Add(new ModelPart("body", 0, 5, 2, Pi / 2).Box(18, 4, -6, -10, -7, 12, 18, 10).Box(52, 0, -2, 2, -8, 4, 6, 1));
            QuadrupedLegs(layer, 12, -6, 7, 4, true);
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Pig()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/pig/pig", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 12, -6).Box(0, 0, -4, -4, -8, 8, 8, 8).Box(16, 16, -2, 0, -9, 4, 3, 1));
            layer.Parts.Add(new ModelPart("body", 0, 11, 2, Pi / 2).Box(28, 8, -5, -10, -7, 10, 16, 8));
            QuadrupedLegs(layer, 6, -5, 7, 3, false);
            model.Layers.Add(layer);

            // saddle layer (vanilla SaddleLayer: the pig model inflated by 0.5)
            var saddle = Layer("entity/pig/pig_saddle", 64, 32);
            saddle.Parts.Add(new ModelPart("head", 0, 12, -6).Box(0, 0, -4, -4, -8, 8, 8, 8, 0.5f).Box(16, 16, -2, 0, -9, 4, 3, 1, 0.5f));
            saddle.Parts.Add(new ModelPart("body", 0, 11, 2, Pi / 2).Box(28, 8, -5, -10, -7, 10, 16, 8, 0.5f));
            saddle.Parts.Add(new ModelPart("right_hind_leg", -3, 18, 7).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            saddle.Parts.Add(new ModelPart("left_hind_leg", 3, 18, 7).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            saddle.Parts.Add(new ModelPart("right_front_leg", -3, 18, -5).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            saddle.Parts.Add(new ModelPart("left_front_leg", 3, 18, -5).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            model.Layers.Add(saddle);
            return model;
        }

        /// <summary>BoatModel.createBodyModel (oak texture; the variant is chosen by the renderer).</summary>
        private static EntityModel Boat()
        {
            var model = new EntityModel { Kind = ModelKind.Boat };
            var layer = Layer("entity/boat/oak", 128, 64);
            layer.Parts.Add(new ModelPart("bottom", 0, 3, 1, Pi / 2).Box(0, 0, -14, -9, -3, 28, 16, 3));
            layer.Parts.Add(new ModelPart("back", -15, 4, 4, 0, Pi * 1.5f).Box(0, 19, -13, -7, -1, 18, 6, 2));
            layer.Parts.Add(new ModelPart("front", 15, 4, 0, 0, Pi / 2).Box(0, 27, -8, -7, -1, 16, 6, 2));
            layer.Parts.Add(new ModelPart("right", 0, 4, -9, 0, Pi).Box(0, 35, -14, -7, -1, 28, 6, 2));
            layer.Parts.Add(new ModelPart("left", 0, 4, 9).Box(0, 43, -14, -7, -1, 28, 6, 2));
            layer.Parts.Add(new ModelPart("left_paddle", 3, -5, 9, 0, 0, Pi / 16).Box(62, 0, -1, 0, -5, 2, 2, 18).Box(62, 0, -1.001f, -3, 8, 1, 6, 7));
            layer.Parts.Add(new ModelPart("right_paddle", 3, -5, -9, 0, Pi, Pi / 16).Box(62, 20, -1, 0, -5, 2, 2, 18).Box(62, 20, -1.001f, -3, 8, 1, 6, 7));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>MinecartModel.createBodyLayer.</summary>
        private static EntityModel Minecart()
        {
            var model = new EntityModel { Kind = ModelKind.Minecart };
            var layer = Layer("entity/minecart", 64, 32);
            layer.Parts.Add(new ModelPart("bottom", 0, 4, 0, Pi / 2).Box(0, 10, -10, -8, -1, 20, 16, 2));
            layer.Parts.Add(new ModelPart("front", -9, 4, 0, 0, Pi * 1.5f).Box(0, 0, -8, -9, -1, 16, 8, 2));
            layer.Parts.Add(new ModelPart("back", 9, 4, 0, 0, Pi / 2).Box(0, 0, -8, -9, -1, 16, 8, 2));
            layer.Parts.Add(new ModelPart("left", 0, 4, -7, 0, Pi).Box(0, 0, -8, -9, -1, 16, 8, 2));
            layer.Parts.Add(new ModelPart("right", 0, 4, 7).Box(0, 0, -8, -9, -1, 16, 8, 2));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Sheep()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/sheep/sheep", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 6, -8).Box(0, 0, -3, -4, -6, 6, 6, 8));
            layer.Parts.Add(new ModelPart("body", 0, 5, 2, Pi / 2).Box(28, 8, -4, -10, -7, 8, 16, 6));
            QuadrupedLegs(layer, 12, -5, 7, 3, false);
            model.Layers.Add(layer);

            var fur = Layer("entity/sheep/sheep_fur", 64, 32);
            fur.Parts.Add(new ModelPart("head", 0, 6, -8).Box(0, 0, -3, -4, -4, 6, 6, 6, 0.6f));
            fur.Parts.Add(new ModelPart("body", 0, 5, 2, Pi / 2).Box(28, 8, -4, -10, -7, 8, 16, 6, 1.75f));
            fur.Parts.Add(new ModelPart("right_hind_leg", -3, 12, 7).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            fur.Parts.Add(new ModelPart("left_hind_leg", 3, 12, 7).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            fur.Parts.Add(new ModelPart("right_front_leg", -3, 12, -5).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            fur.Parts.Add(new ModelPart("left_front_leg", 3, 12, -5).Box(0, 16, -2, 0, -2, 4, 6, 4, 0.5f));
            model.Layers.Add(fur);
            return model;
        }

        private static EntityModel Chicken()
        {
            var model = new EntityModel { Kind = ModelKind.Chicken };
            var layer = Layer("entity/chicken", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 15, -4).Box(0, 0, -2, -6, -2, 4, 6, 3).Box(14, 0, -2, -4, -4, 4, 2, 2).Box(14, 4, -1, -2, -3, 2, 2, 2));
            layer.Parts.Add(new ModelPart("body", 0, 16, 0, Pi / 2).Box(0, 9, -3, -4, -3, 6, 8, 6));
            layer.Parts.Add(new ModelPart("right_leg", -2, 19, 1).Box(26, 0, -1, 0, -3, 3, 5, 3));
            layer.Parts.Add(new ModelPart("left_leg", 1, 19, 1).Box(26, 0, -1, 0, -3, 3, 5, 3));
            layer.Parts.Add(new ModelPart("right_wing", -4, 13, 0).Box(24, 13, 0, 0, -3, 1, 4, 6));
            layer.Parts.Add(new ModelPart("left_wing", 4, 13, 0).Box(24, 13, -1, 0, -3, 1, 4, 6));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Spider(string texture, float scale)
        {
            var model = new EntityModel { Kind = ModelKind.Spider, Scale = scale };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 15, -3).Box(32, 4, -4, -4, -8, 8, 8, 8));
            layer.Parts.Add(new ModelPart("body0", 0, 15, 0).Box(0, 0, -3, -3, -3, 6, 6, 6));
            layer.Parts.Add(new ModelPart("body1", 0, 15, 9).Box(0, 12, -5, -4, -6, 10, 8, 12));
            const float q = Pi / 4;
            const float middle = 0.58119464f;
            const float spread = 0.3926991f;
            layer.Parts.Add(new ModelPart("right_hind_leg", -4, 15, 2, 0, q, -q).Box(18, 0, -15, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("left_hind_leg", 4, 15, 2, 0, -q, q).Box(18, 0, -1, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("right_middle_hind_leg", -4, 15, 1, 0, spread, -middle).Box(18, 0, -15, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("left_middle_hind_leg", 4, 15, 1, 0, -spread, middle).Box(18, 0, -1, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("right_middle_front_leg", -4, 15, 0, 0, -spread, -middle).Box(18, 0, -15, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("left_middle_front_leg", 4, 15, 0, 0, spread, middle).Box(18, 0, -1, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("right_front_leg", -4, 15, -1, 0, -q, -q).Box(18, 0, -15, -1, -1, 16, 2, 2));
            layer.Parts.Add(new ModelPart("left_front_leg", 4, 15, -1, 0, q, q).Box(18, 0, -1, -1, -1, 16, 2, 2));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Enderman()
        {
            var model = new EntityModel { Kind = ModelKind.Biped };
            var layer = Layer("entity/enderman/enderman", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, -13, 0).Box(0, 0, -4, -8, -4, 8, 8, 8).Child(new ModelPart("hat", 0, 0, 0).Box(0, 16, -4, -8, -4, 8, 8, 8, -0.5f)));
            layer.Parts.Add(new ModelPart("body", 0, -14, 0).Box(32, 16, -4, 0, -2, 8, 12, 4));
            layer.Parts.Add(new ModelPart("right_arm", -5, -12, 0).Box(56, 0, -1, -2, -1, 2, 30, 2));
            layer.Parts.Add(new ModelPart("left_arm", 5, -12, 0).Box(56, 0, -1, -2, -1, 2, 30, 2, 0, true));
            layer.Parts.Add(new ModelPart("right_leg", -2, -5, 0).Box(56, 0, -1, 0, -1, 2, 30, 2));
            layer.Parts.Add(new ModelPart("left_leg", 2, -5, 0).Box(56, 0, -1, 0, -1, 2, 30, 2, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Villager(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Villager };
            var layer = Layer(texture, 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -10, -4, 8, 10, 8)
                .Child(new ModelPart("nose", 0, -2, 0).Box(24, 0, -1, -1, -6, 2, 4, 2))
                // VillagerModel hat and brim: empty in the base texture, used by profession overlays
                .Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -10, -4, 8, 10, 8, 0.51f)
                    .Child(new ModelPart("hat_rim", 0, 0, 0, -Pi / 2).Box(30, 47, -8, -8, -6, 16, 16, 1))));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 20, -4, 0, -3, 8, 12, 6).Child(new ModelPart("jacket", 0, 0, 0).Box(0, 38, -4, 0, -3, 8, 20, 6, 0.5f)));
            layer.Parts.Add(new ModelPart("arms", 0, 3, -1, -0.75f).Box(44, 22, -8, -2, -2, 4, 8, 4).Box(44, 22, 4, -2, -2, 4, 8, 4, 0, true).Box(40, 38, -4, 2, -2, 8, 4, 4));
            layer.Parts.Add(new ModelPart("right_leg", -2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4));
            layer.Parts.Add(new ModelPart("left_leg", 2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        private static EntityModel Slime(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Slime };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("cube", 0, 0, 0).Box(0, 16, -3, 17, -3, 6, 6, 6).Box(32, 0, -3.25f, 18, -3.5f, 2, 2, 2).Box(32, 4, 1.25f, 18, -3.5f, 2, 2, 2).Box(32, 8, 0, 21, -3.5f, 1, 1, 1));
            layer.Parts.Add(new ModelPart("outer", 0, 0, 0).Box(0, 0, -4, 16, -4, 8, 8, 8));
            model.Layers.Add(layer);
            return model;
        }
        // ------------------------------------------------------------------ more vanilla models (1.20.4 LayerDefinitions)

        private static void Legs(ModelLayer layer, int u, int v, float x0, float sizeX, float sizeY, float sizeZ, float y, float frontZ, float hindZ, float spread, float boxZ = -1)
        {
            layer.Parts.Add(new ModelPart("right_hind_leg", -spread, y, hindZ).Box(u, v, x0, 0, boxZ, sizeX, sizeY, sizeZ));
            layer.Parts.Add(new ModelPart("left_hind_leg", spread, y, hindZ).Box(u, v, x0, 0, boxZ, sizeX, sizeY, sizeZ));
            layer.Parts.Add(new ModelPart("right_front_leg", -spread, y, frontZ).Box(u, v, x0, 0, boxZ, sizeX, sizeY, sizeZ));
            layer.Parts.Add(new ModelPart("left_front_leg", spread, y, frontZ).Box(u, v, x0, 0, boxZ, sizeX, sizeY, sizeZ));
        }

        /// <summary>WolfModel.</summary>
        private static EntityModel Wolf()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/wolf/wolf", 64, 32);
            layer.Parts.Add(new ModelPart("head", -1, 13.5f, -7).Child(new ModelPart("real_head", 0, 0, 0)
                .Box(0, 0, -2, -3, -2, 6, 6, 4).Box(16, 14, -2, -5, 0, 2, 2, 1).Box(16, 14, 2, -5, 0, 2, 2, 1).Box(0, 10, -0.5f, -0.001f, -5, 3, 3, 4)));
            layer.Parts.Add(new ModelPart("body", 0, 14, 2, Pi / 2).Box(18, 14, -3, -2, -3, 6, 9, 6));
            layer.Parts.Add(new ModelPart("upper_body", -1, 14, -3, Pi / 2).Box(21, 0, -3, -3, -3, 8, 6, 7));
            layer.Parts.Add(new ModelPart("right_hind_leg", -2.5f, 16, 7).Box(0, 18, 0, 0, -1, 2, 8, 2));
            layer.Parts.Add(new ModelPart("left_hind_leg", 0.5f, 16, 7).Box(0, 18, 0, 0, -1, 2, 8, 2));
            layer.Parts.Add(new ModelPart("right_front_leg", -2.5f, 16, -4).Box(0, 18, 0, 0, -1, 2, 8, 2));
            layer.Parts.Add(new ModelPart("left_front_leg", 0.5f, 16, -4).Box(0, 18, 0, 0, -1, 2, 8, 2));
            layer.Parts.Add(new ModelPart("tail", -1, 12, 8, Pi / 5).Child(new ModelPart("real_tail", 0, 0, 0).Box(9, 18, 0, 0, -1, 2, 8, 2)));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>OcelotModel (cats use it too, scaled 0.8).</summary>
        private static EntityModel Feline(string texture, float scale)
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped, Scale = scale };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 15, -9).Box(0, 0, -2.5f, -2, -3, 5, 4, 5).Box(0, 24, -1.5f, -0.001f, -4, 3, 2, 2).Box(0, 10, -2, -3, 0, 1, 1, 2).Box(6, 10, 1, -3, 0, 1, 1, 2));
            layer.Parts.Add(new ModelPart("body", 0, 12, -10, Pi / 2).Box(20, 0, -2, 3, -8, 4, 16, 6));
            layer.Parts.Add(new ModelPart("tail1", 0, 15, 8, 0.9f).Box(0, 15, -0.5f, 0, 0, 1, 8, 1));
            layer.Parts.Add(new ModelPart("tail2", 0, 20, 14).Box(4, 15, -0.5f, 0, 0, 1, 8, 1));
            layer.Parts.Add(new ModelPart("left_hind_leg", 1.1f, 18, 5).Box(8, 13, -1, 0, 1, 2, 6, 2));
            layer.Parts.Add(new ModelPart("right_hind_leg", -1.1f, 18, 5).Box(8, 13, -1, 0, 1, 2, 6, 2));
            layer.Parts.Add(new ModelPart("left_front_leg", 1.2f, 14.1f, -5).Box(40, 0, -1, 0, 0, 2, 10, 2));
            layer.Parts.Add(new ModelPart("right_front_leg", -1.2f, 14.1f, -5).Box(40, 0, -1, 0, 0, 2, 10, 2));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>IronGolemModel (128x128).</summary>
        private static EntityModel IronGolem()
        {
            var model = new EntityModel { Kind = ModelKind.Golem };
            var layer = Layer("entity/iron_golem/iron_golem", 128, 128);
            layer.Parts.Add(new ModelPart("head", 0, -7, -2).Box(0, 0, -4, -12, -5.5f, 8, 10, 8).Box(24, 0, -1, -5, -7.5f, 2, 4, 2));
            layer.Parts.Add(new ModelPart("body", 0, -7, 0).Box(0, 40, -9, -2, -6, 18, 12, 11).Box(0, 70, -4.5f, 10, -3, 9, 5, 6, 0.5f));
            layer.Parts.Add(new ModelPart("right_arm", 0, -7, 0).Box(60, 21, -13, -2.5f, -3, 4, 30, 6));
            layer.Parts.Add(new ModelPart("left_arm", 0, -7, 0).Box(60, 58, 9, -2.5f, -3, 4, 30, 6));
            layer.Parts.Add(new ModelPart("right_leg", -4, 11, 0).Box(37, 0, -3.5f, -3, -3, 6, 16, 5));
            layer.Parts.Add(new ModelPart("left_leg", 5, 11, 0).Box(60, 0, -3.5f, -3, -3, 6, 16, 5, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>SnowGolemModel (without the pumpkin).</summary>
        private static EntityModel SnowGolem()
        {
            var model = new EntityModel { Kind = ModelKind.Golem };
            var layer = Layer("entity/snow_golem", 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 4, 0).Box(0, 0, -4, -8, -4, 8, 8, 8, -0.5f));
            layer.Parts.Add(new ModelPart("right_arm", 5, 6, 1, 0, 0, 1).Box(32, 0, -1, 0, -1, 12, 2, 2, -0.5f));
            layer.Parts.Add(new ModelPart("left_arm", -5, 6, -1, 0, Pi, -1).Box(32, 0, -1, 0, -1, 12, 2, 2, -0.5f));
            layer.Parts.Add(new ModelPart("upper_body", 0, 13, 0).Box(0, 16, -5, -10, -5, 10, 10, 10, -0.5f));
            layer.Parts.Add(new ModelPart("lower_body", 0, 24, 0).Box(0, 36, -6, -12, -6, 12, 12, 12, -0.5f));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PiglinModel: the player model with a wide snout head and ears.</summary>
        private static EntityModel Piglin(string texture, ModelKind kind)
        {
            var model = Player(false);
            model.Kind = kind;
            var layer = model.Layers[0];
            layer.Texture = "assets/minecraft/textures/" + texture + ".png";
            var head = new ModelPart("head", 0, 0, 0).Box(0, 0, -5, -8, -4, 10, 8, 8).Box(31, 1, -2, -4, -5, 4, 4, 1).Box(2, 4, 2, -2, -5, 1, 2, 1).Box(2, 0, -3, -2, -5, 1, 2, 1);
            head.Child(new ModelPart("left_ear", 4.5f, -6, 0, 0, 0, -Pi / 6).Box(51, 6, 0, 0, -2, 1, 5, 4));
            head.Child(new ModelPart("right_ear", -4.5f, -6, 0, 0, 0, Pi / 6).Box(39, 6, -1, 0, -2, 1, 5, 4));
            layer.Parts[0] = head;
            return model;
        }

        /// <summary>IllagerModel with crossed arms.</summary>
        private static EntityModel Illager(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Villager, Scale = 0.9375f };
            var layer = Layer(texture, 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -10, -4, 8, 10, 8).Child(new ModelPart("nose", 0, -2, 0).Box(24, 0, -1, -1, -6, 2, 4, 2)));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(16, 20, -4, 0, -3, 8, 12, 6).Box(0, 38, -4, 0, -3, 8, 20, 6, 0.5f));
            layer.Parts.Add(new ModelPart("arms", 0, 3, -1, -0.75f).Box(44, 22, -8, -2, -2, 4, 8, 4).Box(40, 38, -4, 2, -2, 8, 4, 4).Box(44, 22, 4, -2, -2, 4, 8, 4, 0, true));
            layer.Parts.Add(new ModelPart("right_leg", -2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4));
            layer.Parts.Add(new ModelPart("left_leg", 2, 12, 0).Box(0, 22, -2, 0, -2, 4, 12, 4, 0, true));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>LlamaModel (without chests).</summary>
        private static EntityModel Llama(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer(texture, 128, 64);
            layer.Parts.Add(new ModelPart("head", 0, 7, -6).Box(0, 0, -2, -14, -10, 4, 4, 9).Box(0, 14, -4, -16, -6, 8, 18, 6).Box(17, 0, -4, -19, -4, 3, 3, 2).Box(17, 0, 1, -19, -4, 3, 3, 2));
            layer.Parts.Add(new ModelPart("body", 0, 5, 2, Pi / 2).Box(29, 0, -6, -10, -7, 12, 18, 10));
            Legs(layer, 29, 29, -2, 4, 14, 4, 10, -5, 6, 3.5f, -2);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>BeeModel.</summary>
        private static EntityModel Bee()
        {
            var model = new EntityModel { Kind = ModelKind.Bee };
            var layer = Layer("entity/bee/bee", 64, 64);
            var bone = new ModelPart("bone", 0, 19, 0);
            var body = new ModelPart("body", 0, 0, 0).Box(0, 0, -3.5f, -4, -5, 7, 7, 10);
            body.Child(new ModelPart("stinger", 0, 0, 0).Box(26, 7, 0, -1, 5, 0, 1, 2));
            body.Child(new ModelPart("left_antenna", 0, -2, -5).Box(2, 0, 1.5f, -2, -3, 1, 2, 3));
            body.Child(new ModelPart("right_antenna", 0, -2, -5).Box(2, 3, -2.5f, -2, -3, 1, 2, 3));
            bone.Child(body);
            bone.Child(new ModelPart("right_wing", -1.5f, -4, -3, 0, -0.2618f, 0).Box(0, 18, -9, 0, 0, 9, 0, 6));
            bone.Child(new ModelPart("left_wing", 1.5f, -4, -3, 0, 0.2618f, 0).Box(0, 18, 0, 0, 0, 9, 0, 6, 0, true));
            bone.Child(new ModelPart("front_legs", 1.5f, 3, -2).Box(26, 1, -5, 0, 0, 7, 2, 0));
            bone.Child(new ModelPart("middle_legs", 1.5f, 3, 0).Box(26, 3, -5, 0, 0, 7, 2, 0));
            bone.Child(new ModelPart("back_legs", 1.5f, 3, 2).Box(26, 5, -5, 0, 0, 7, 2, 0));
            layer.Parts.Add(bone);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>CodModel.</summary>
        private static EntityModel Cod()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/fish/cod", 32, 32);
            layer.Parts.Add(new ModelPart("body", 0, 22, 0).Box(0, 0, -1, -2, 0, 2, 4, 7));
            layer.Parts.Add(new ModelPart("head", 0, 22, 0).Box(11, 0, -1, -2, -3, 2, 4, 3));
            layer.Parts.Add(new ModelPart("nose", 0, 22, -3).Box(0, 0, -1, -2, -1, 2, 3, 1));
            layer.Parts.Add(new ModelPart("right_fin", -1, 23, 0, 0, 0, -Pi / 4).Box(22, 1, -2, 0, -1, 2, 0, 2));
            layer.Parts.Add(new ModelPart("left_fin", 1, 23, 0, 0, 0, Pi / 4).Box(22, 4, 0, 0, -1, 2, 0, 2));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 22, 7).Box(22, 3, 0, -2, 0, 0, 4, 4));
            layer.Parts.Add(new ModelPart("top_fin", 0, 20, 0).Box(20, -6, 0, -1, -1, 0, 1, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>SalmonModel.</summary>
        private static EntityModel Salmon()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/fish/salmon", 32, 32);
            layer.Parts.Add(new ModelPart("body_front", 0, 20, 0).Box(0, 0, -1.5f, -2.5f, 0, 3, 5, 8).Child(new ModelPart("top_front_fin", 0, -4.5f, 5).Box(2, 1, 0, 0, 0, 0, 2, 3)));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 20, 8).Box(0, 13, -1.5f, -2.5f, 0, 3, 5, 8)
                .Child(new ModelPart("back_fin", 0, 0, 8).Box(20, 10, 0, -2.5f, 0, 0, 5, 6))
                .Child(new ModelPart("top_back_fin", 0, -4.5f, -1).Box(0, 2, 0, 0, 0, 0, 2, 4)));
            layer.Parts.Add(new ModelPart("head", 0, 20, 0).Box(22, 0, -1, -2, -3, 2, 4, 3));
            layer.Parts.Add(new ModelPart("right_fin", -1.5f, 21.5f, 0, 0, 0, -Pi / 4).Box(-4, 0, -2, 0, 0, 2, 0, 2));
            layer.Parts.Add(new ModelPart("left_fin", 1.5f, 21.5f, 0, 0, 0, Pi / 4).Box(0, 0, 0, 0, 0, 2, 0, 2));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>TropicalFishModelA (the small shape, untinted).</summary>
        private static EntityModel TropicalFish()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/fish/tropical_a", 32, 32);
            layer.Parts.Add(new ModelPart("body", 0, 22, 0).Box(0, 0, -1, -1.5f, -3, 2, 3, 6));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 22, 3).Box(22, -6, 0, -1.5f, 0, 0, 3, 6));
            layer.Parts.Add(new ModelPart("right_fin", -1, 22.5f, 0, 0, Pi / 4).Box(2, 16, -2, -1, 0, 2, 2, 0));
            layer.Parts.Add(new ModelPart("left_fin", 1, 22.5f, 0, 0, -Pi / 4).Box(2, 12, 0, -1, 0, 2, 2, 0));
            layer.Parts.Add(new ModelPart("top_fin", 0, 20.5f, -3).Box(10, -5, 0, -3, 0, 0, 3, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PufferfishSmallModel.</summary>
        private static EntityModel Pufferfish()
        {
            var model = new EntityModel { Kind = ModelKind.Fish };
            var layer = Layer("entity/fish/pufferfish", 32, 32);
            layer.Parts.Add(new ModelPart("body", 0, 23, 0).Box(0, 27, -1.5f, -2, -1.5f, 3, 2, 3));
            layer.Parts.Add(new ModelPart("right_eye", 0, 20, 0).Box(24, 6, -1.5f, 0, -1.5f, 1, 1, 1));
            layer.Parts.Add(new ModelPart("left_eye", 0, 20, 0).Box(28, 6, 0.5f, 0, -1.5f, 1, 1, 1));
            layer.Parts.Add(new ModelPart("tail_fin", 0, 22, 1.5f).Box(-3, 0, -1.5f, 0, 0, 3, 0, 3));
            layer.Parts.Add(new ModelPart("right_fin", -1.5f, 22, -1.5f).Box(25, 0, -1, 0, 0, 1, 0, 2));
            layer.Parts.Add(new ModelPart("left_fin", 1.5f, 22, -1.5f).Box(25, 0, 0, 0, 0, 1, 0, 2));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>SquidModel: body and 8 tentacles around it.</summary>
        private static EntityModel Squid(string texture)
        {
            var model = new EntityModel { Kind = ModelKind.Squid };
            var layer = Layer(texture, 64, 32);
            layer.Parts.Add(new ModelPart("body", 0, 8, 0).Box(0, 0, -6, -8, -6, 12, 16, 12));
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Pi * 2 / 8;
                layer.Parts.Add(new ModelPart("tentacle" + i, Mathf.Cos(angle) * 5, 15, Mathf.Sin(angle) * 5, 0, i * Pi * -2 / 8 + Pi / 2).Box(48, 0, -1, 0, -1, 2, 18, 2));
            }
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>BatModel (1.20.3).</summary>
        private static EntityModel Bat()
        {
            var model = new EntityModel { Kind = ModelKind.Bat };
            var layer = Layer("entity/bat", 32, 32);
            var head = new ModelPart("head", 0, 17, 0).Box(0, 0, -2, -3, -1, 4, 3, 2);
            head.Child(new ModelPart("right_ear", -1.5f, -2, 0).Box(1, 15, -2.5f, -4, 0, 3, 5, 0));
            head.Child(new ModelPart("left_ear", 1.1f, -3, 0).Box(8, 15, -0.1f, -3, 0, 3, 5, 0));
            var body = new ModelPart("body", 0, 17, 0).Box(0, 7, -1.5f, 0, -1, 3, 5, 2);
            body.Child(new ModelPart("right_wing", -1.5f, 0, 0).Box(12, 0, -2, -2, 0, 2, 7, 0).Child(new ModelPart("right_wing_tip", -2, 0, 0).Box(16, 0, -6, -2, 0, 6, 8, 0)));
            body.Child(new ModelPart("left_wing", 1.5f, 0, 0).Box(12, 7, 0, -2, 0, 2, 7, 0).Child(new ModelPart("left_wing_tip", 2, 0, 0).Box(16, 8, 0, -2, 0, 6, 8, 0)));
            body.Child(new ModelPart("feet", 0, 5, 0).Box(16, 16, -1.5f, 0, 0, 3, 2, 0));
            layer.Parts.Add(head);
            layer.Parts.Add(body);
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>FoxModel.</summary>
        private static EntityModel Fox()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/fox/fox", 48, 32);
            layer.Parts.Add(new ModelPart("head", -1, 16.5f, -3).Box(1, 5, -3, -2, -5, 8, 6, 6).Box(8, 1, -3, -4, -4, 2, 2, 1).Box(15, 1, 3, -4, -4, 2, 2, 1).Box(6, 18, -1, 2.01f, -8, 4, 2, 3));
            layer.Parts.Add(new ModelPart("body", 0, 16, -6, Pi / 2).Box(24, 15, -3, 3.999f, -3.5f, 6, 11, 6)
                .Child(new ModelPart("tail", -4, 15, -1, -0.05235988f).Box(30, 0, 2, 0, -1, 4, 9, 5)));
            layer.Parts.Add(new ModelPart("right_hind_leg", -5, 17.5f, 7).Box(13, 24, 2, 0.5f, -1, 2, 6, 2, -0.001f));
            layer.Parts.Add(new ModelPart("left_hind_leg", -1, 17.5f, 7).Box(4, 24, 2, 0.5f, -1, 2, 6, 2, -0.001f));
            layer.Parts.Add(new ModelPart("right_front_leg", -5, 17.5f, 0).Box(13, 24, 2, 0.5f, -1, 2, 6, 2, -0.001f));
            layer.Parts.Add(new ModelPart("left_front_leg", -1, 17.5f, 0).Box(4, 24, 2, 0.5f, -1, 2, 6, 2, -0.001f));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>PolarBearModel.</summary>
        private static EntityModel PolarBear()
        {
            var model = new EntityModel { Kind = ModelKind.Quadruped };
            var layer = Layer("entity/bear/polarbear", 128, 64);
            layer.Parts.Add(new ModelPart("head", 0, 10, -16).Box(0, 0, -3.5f, -3, -3, 7, 7, 7).Box(0, 44, -2.5f, 1, -6, 5, 3, 3).Box(26, 0, -4.5f, -4, -1, 2, 2, 1).Box(26, 0, 2.5f, -4, -1, 2, 2, 1, 0, true));
            layer.Parts.Add(new ModelPart("body", -2, 9, 12, Pi / 2).Box(0, 19, -5, -13, -7, 14, 14, 11).Box(39, 0, -4, -25, -7, 12, 12, 10));
            layer.Parts.Add(new ModelPart("right_hind_leg", -4.5f, 14, 6).Box(50, 22, -2, 0, -2, 4, 10, 8));
            layer.Parts.Add(new ModelPart("left_hind_leg", 4.5f, 14, 6).Box(50, 22, -2, 0, -2, 4, 10, 8));
            layer.Parts.Add(new ModelPart("right_front_leg", -3.5f, 14, -8).Box(50, 40, -2, 0, -2, 4, 10, 6));
            layer.Parts.Add(new ModelPart("left_front_leg", 3.5f, 14, -8).Box(50, 40, -2, 0, -2, 4, 10, 6));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>BlazeModel: head and 12 rods (placed every frame by the renderer).</summary>
        private static EntityModel Blaze()
        {
            var model = new EntityModel { Kind = ModelKind.Blaze };
            var layer = Layer("entity/blaze", 64, 32);
            layer.Parts.Add(new ModelPart("head", 0, 0, 0).Box(0, 0, -4, -4, -4, 8, 8, 8));
            for (var i = 0; i < 12; i++) layer.Parts.Add(new ModelPart("rod" + i, 0, 0, 0).Box(0, 16, 0, 0, 0, 2, 8, 2));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>GhastModel (scaled 4.5); tentacle lengths like the vanilla random with seed 1660 are not reproduced.</summary>
        private static EntityModel Ghast()
        {
            var model = new EntityModel { Kind = ModelKind.Ghast, Scale = 4.5f };
            var layer = Layer("entity/ghast/ghast", 64, 32);
            layer.Parts.Add(new ModelPart("body", 0, 17.6f, 0).Box(0, 0, -8, -8, -8, 16, 16, 16));
            for (var i = 0; i < 9; i++)
            {
                var x = ((i % 3 - i / 3 % 2 * 0.5f + 0.25f) / 2 * 2 - 1) * 5;
                var z = (i / 3 / 2f * 2 - 1) * 5;
                layer.Parts.Add(new ModelPart("tentacle" + i, x, 24.6f, z).Box(0, 0, -1, 0, -1, 2, 8 + (i * 5 + 3) % 7, 2));
            }
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>HorseModel (horses, donkeys, mules, skeleton and zombie horses); the saddle is in the same texture.</summary>
        private static EntityModel Horse(string texture, float scale)
        {
            var model = new EntityModel { Kind = ModelKind.Horse, Scale = scale };
            var layer = Layer(texture, 64, 64);
            layer.Parts.Add(new ModelPart("body", 0, 11, 5).Box(0, 32, -5, -8, -17, 10, 10, 22, 0.05f)
                .Child(new ModelPart("tail", 0, -5, 2, Pi / 6).Box(42, 36, -1.5f, 0, 0, 3, 14, 4))
                .Child(new ModelPart("saddle", 0, 0, 0).Box(26, 0, -5, -8, -9, 10, 9, 9, 0.5f)));
            var head = new ModelPart("head", 0, 4, -12, Pi / 6).Box(0, 35, -2.05f, -6, -2, 4, 12, 7);
            head.Child(new ModelPart("head_main", 0, 0, 0).Box(0, 13, -3, -11, -2, 6, 5, 7));
            head.Child(new ModelPart("mane", 0, 0, 0).Box(56, 36, -1, -11, 5.01f, 2, 16, 2));
            head.Child(new ModelPart("upper_mouth", 0, 0, 0).Box(0, 25, -2, -11, -7, 4, 5, 5));
            head.Child(new ModelPart("left_ear", 0, 0, 0).Box(19, 16, 0.55f, -13, 4, 2, 3, 1, -0.001f));
            head.Child(new ModelPart("right_ear", 0, 0, 0).Box(19, 16, -2.55f, -13, 4, 2, 3, 1, -0.001f));
            layer.Parts.Add(head);
            layer.Parts.Add(new ModelPart("left_hind_leg", 4, 14, 7).Box(48, 21, -3, -1.01f, -1, 4, 11, 4, 0, true));
            layer.Parts.Add(new ModelPart("right_hind_leg", -4, 14, 7).Box(48, 21, -1, -1.01f, -1, 4, 11, 4));
            layer.Parts.Add(new ModelPart("left_front_leg", 4, 14, -12).Box(48, 21, -3, -1.01f, -1.9f, 4, 11, 4, 0, true));
            layer.Parts.Add(new ModelPart("right_front_leg", -4, 14, -12).Box(48, 21, -1, -1.01f, -1.9f, 4, 11, 4));
            model.Layers.Add(layer);
            return model;
        }

        /// <summary>ArmorStandModel (64x64 wood texture); the pose comes from the entity data.</summary>
        private static EntityModel ArmorStand()
        {
            var model = new EntityModel { Kind = ModelKind.ArmorStand };
            var layer = Layer("entity/armorstand/wood", 64, 64);
            layer.Parts.Add(new ModelPart("head", 0, 1, 0).Box(0, 0, -1, -7, -1, 2, 7, 2));
            layer.Parts.Add(new ModelPart("body", 0, 0, 0).Box(0, 26, -6, 0, -1.5f, 12, 3, 3)
                .Child(new ModelPart("right_body_stick", 0, 0, 0).Box(16, 0, -3, 3, -1, 2, 7, 2))
                .Child(new ModelPart("left_body_stick", 0, 0, 0).Box(48, 16, 1, 3, -1, 2, 7, 2))
                .Child(new ModelPart("shoulder_stick", 0, 0, 0).Box(0, 48, -4, 10, -1, 8, 2, 2)));
            layer.Parts.Add(new ModelPart("right_arm", -5, 2, 0).Box(24, 0, -2, -2, -1, 2, 12, 2));
            layer.Parts.Add(new ModelPart("left_arm", 5, 2, 0).Box(32, 16, 0, -2, -1, 2, 12, 2, 0, true));
            layer.Parts.Add(new ModelPart("right_leg", -1.9f, 12, 0).Box(8, 0, -1, 0, -1, 2, 11, 2));
            layer.Parts.Add(new ModelPart("left_leg", 1.9f, 12, 0).Box(40, 16, -1, 0, -1, 2, 11, 2, 0, true));
            layer.Parts.Add(new ModelPart("base_plate", 0, 12, 0).Box(0, 32, -6, 11, -6, 12, 1, 12));
            model.Layers.Add(layer);
            return model;
        }
    }
}
