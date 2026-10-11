// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Entities
{
    /// <summary>
    /// Entity data of the display entities (vanilla Display, BlockDisplay, ItemDisplay, TextDisplay; 1.20.4 indices):
    /// the transformation (translation, left rotation, scale, right rotation) interpolated over a number of ticks,
    /// the billboard mode, brightness override and the block, item or text they show.
    /// </summary>
    public sealed class DisplayData
    {
        public struct Transform
        {
            public Vec3d Translation;
            public Vec3d Scale;
            public float[] Left;  // quaternion x, y, z, w
            public float[] Right;

            public static Transform Identity => new Transform
            {
                Translation = Vec3d.Zero,
                Scale = new Vec3d(1, 1, 1),
                Left = new float[] { 0, 0, 0, 1 },
                Right = new float[] { 0, 0, 0, 1 },
            };
        }

        public Transform Current = Transform.Identity;
        /// <summary>The transformation the interpolation starts from.</summary>
        public Transform Previous = Transform.Identity;
        /// <summary>Game tick when the interpolation to <see cref="Current"/> starts, and its length in ticks.</summary>
        public long InterpolationStart;
        public int InterpolationDuration;
        private int interpolationDelay;
        private bool transformChanged;

        /// <summary>0 fixed, 1 vertical, 2 horizontal, 3 center (faces the camera).</summary>
        public int Billboard;
        /// <summary>Packed light (block &lt;&lt; 4 | sky &lt;&lt; 20), -1 to use the light where the entity is.</summary>
        public int Brightness = -1;
        public float ViewRange = 1;
        public int GlowColor = -1;

        // block_display
        public BlockState Block;
        // item_display
        public ItemStack Item;
        /// <summary>0 none, 1/2 third person left/right, 3/4 first person left/right, 5 head, 6 gui, 7 ground, 8 fixed.</summary>
        public int ItemContext;
        // text_display
        public string Text = "";
        public int LineWidth = 200;
        public int Background = 0x40000000;
        public int TextOpacity = -1;
        /// <summary>0x01 shadow, 0x02 see through, 0x04 default background, 0x08 align left, 0x10 align right.</summary>
        public byte TextFlags;
        /// <summary>Changes whenever the text or its style changes (the renderer rebuilds its texture then).</summary>
        public int TextVersion;

        /// <summary>Applies one entity data value; false if the index is not a display value.</summary>
        public bool Apply(string kind, int index, object value, long tick)
        {
            switch (index)
            {
                case 8 when value is int delay:
                    interpolationDelay = delay;
                    return true;
                case 9 when value is int duration:
                    InterpolationDuration = duration;
                    return true;
                case 10:
                    return true; // position/rotation interpolation: the entity interpolation does that
                case 11 when value is Vec3d translation:
                    Changing(tick);
                    Current.Translation = translation;
                    return true;
                case 12 when value is Vec3d scale:
                    Changing(tick);
                    Current.Scale = scale;
                    return true;
                case 13 when value is float[] left:
                    Changing(tick);
                    Current.Left = left;
                    return true;
                case 14 when value is float[] right:
                    Changing(tick);
                    Current.Right = right;
                    return true;
                case 15 when value is byte billboard:
                    Billboard = billboard;
                    return true;
                case 16 when value is int brightness:
                    Brightness = brightness;
                    return true;
                case 17 when value is float range:
                    ViewRange = range;
                    return true;
                case 18:
                case 19:
                case 20:
                case 21:
                    return true; // shadow radius/strength, culling width/height
                case 22 when value is int glow:
                    GlowColor = glow;
                    return true;
            }
            switch (kind)
            {
                case "block_display" when index == 23:
                    Block = value as BlockState;
                    return true;
                case "item_display" when index == 23:
                    Item = value as ItemStack;
                    return true;
                case "item_display" when index == 24 && value is byte context:
                    ItemContext = context;
                    return true;
                case "text_display":
                    switch (index)
                    {
                        case 23 when value is string text:
                            Text = text;
                            break;
                        case 24 when value is int width:
                            LineWidth = width;
                            break;
                        case 25 when value is int background:
                            Background = background;
                            break;
                        case 26 when value is byte opacity:
                            TextOpacity = (sbyte)opacity;
                            break;
                        case 27 when value is byte flags:
                            TextFlags = flags;
                            break;
                        default:
                            return false;
                    }
                    TextVersion++;
                    return true;
            }
            return false;
        }

        /// <summary>The first transformation value of a data packet: the new interpolation starts from what is shown now.</summary>
        private void Changing(long tick)
        {
            if (transformChanged) return;
            transformChanged = true;
            var t = Progress(tick, 0);
            Previous = t >= 1 ? Current : Lerp(Previous, Current, t);
        }

        /// <summary>After all values of a data packet: the interpolation starts after the delay (vanilla Display).</summary>
        public void EndUpdate(long tick)
        {
            if (!transformChanged) return;
            transformChanged = false;
            if (InterpolationDuration <= 0) Previous = Current;
            InterpolationStart = tick + interpolationDelay;
        }

        /// <summary>The transformation shown at the given time.</summary>
        public Transform Shown(long tick, float partial)
        {
            var t = Progress(tick, partial);
            return t >= 1 ? Current : Lerp(Previous, Current, t);
        }

        /// <summary>Progress 0..1 of the transformation interpolation at the given time.</summary>
        public float Progress(long tick, float partial)
        {
            if (InterpolationDuration <= 0) return 1;
            var t = (tick - InterpolationStart + partial) / InterpolationDuration;
            return t < 0 ? 0 : t > 1 ? 1 : (float)t;
        }

        public static Transform Lerp(Transform a, Transform b, float t)
        {
            return new Transform
            {
                Translation = Mix(a.Translation, b.Translation, t),
                Scale = Mix(a.Scale, b.Scale, t),
                Left = Slerp(a.Left, b.Left, t),
                Right = Slerp(a.Right, b.Right, t),
            };
        }

        private static Vec3d Mix(Vec3d a, Vec3d b, float t) => new Vec3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        private static float[] Slerp(float[] a, float[] b, float t)
        {
            if (a == null || b == null) return b ?? a ?? new float[] { 0, 0, 0, 1 };
            var dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            var sign = dot < 0 ? -1f : 1f;
            dot *= sign;
            float wa, wb;
            if (dot > 0.9995f)
            {
                wa = 1 - t;
                wb = t * sign;
            }
            else
            {
                var angle = (float)Math.Acos(dot);
                var sin = (float)Math.Sin(angle);
                wa = (float)Math.Sin((1 - t) * angle) / sin;
                wb = (float)Math.Sin(t * angle) / sin * sign;
            }
            var result = new float[4];
            var length = 0f;
            for (var i = 0; i < 4; i++)
            {
                result[i] = a[i] * wa + b[i] * wb;
                length += result[i] * result[i];
            }
            length = (float)Math.Sqrt(length);
            if (length > 1e-6f)
            {
                for (var i = 0; i < 4; i++) result[i] /= length;
            }
            return result;
        }
    }
}
