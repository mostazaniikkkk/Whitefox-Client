// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>
    /// Dynamic lights (an improvement over vanilla, like OptiFine's): a torch, lantern or glowstone held by the player or
    /// by other players and mobs, luminous dropped items and burning entities light up their surroundings. Modern: point
    /// lights through <see cref="LightSourceManager"/>; classic: the block shaders raise the block light by distance.
    /// </summary>
    public sealed class DynamicLights
    {
        public const int Max = 8;

        private static readonly int LightsId = Shader.PropertyToID("_WF_DynamicLights");
        private static readonly int CountId = Shader.PropertyToID("_WF_DynamicLightCount");

        private readonly ClientResources resources;
        private readonly Dictionary<string, BlockState> itemLights = new Dictionary<string, BlockState>();
        private readonly List<KeyValuePair<Vec3d, BlockState>> candidates = new List<KeyValuePair<Vec3d, BlockState>>();
        private readonly List<KeyValuePair<Vector3, BlockState>> lights = new List<KeyValuePair<Vector3, BlockState>>();
        private readonly Vector4[] shaderLights = new Vector4[Max];
        private readonly BlockState fire;

        public bool Enabled = true;

        public DynamicLights(ClientResources resources)
        {
            this.resources = resources;
            fire = resources.Blocks.Get("fire")?.DefaultState;
            Shader.SetGlobalVectorArray(LightsId, shaderLights); // fixes the array size
            Shader.SetGlobalFloat(CountId, 0);
        }

        /// <summary>The light an item gives when held or dropped: its block's luminance (lava bucket: lava).</summary>
        public BlockState LightOf(ItemStack stack)
        {
            var name = stack?.Item?.Name.Path;
            if (name == null) return null;
            if (itemLights.TryGetValue(name, out var cached)) return cached;
            var block = resources.Blocks.Get(name == "lava_bucket" ? "lava" : name);
            var state = block?.DefaultState;
            if (state != null && state.Luminance <= 0) state = null;
            itemLights[name] = state;
            return state;
        }

        public void Update(GameSession session, Vec3d camera, float partial, LightSourceManager manager)
        {
            candidates.Clear();
            if (Enabled && session != null)
            {
                // the own player: main hand, then the off hand
                var player = session.Player;
                var slots = session.Inventory.Player.Slots;
                var held = LightOf(slots[36 + session.SelectedSlot]) ?? LightOf(slots[45]);
                if (held != null)
                {
                    var position = Vec3d.Lerp(player.PreviousPosition, player.Position, partial);
                    candidates.Add(new KeyValuePair<Vec3d, BlockState>(new Vec3d(position.X, position.Y + player.EyeHeight - 0.3, position.Z), held));
                }
                foreach (var entity in session.Entities.Entities.Values)
                {
                    BlockState light = null;
                    if (entity.OnFire) light = fire;
                    if (entity.Type?.Name.Path == "item") light = LightOf(entity.Item);
                    else if (entity.IsLiving) light = LightOf(entity.Equipment[0]) ?? LightOf(entity.Equipment[1]) ?? light;
                    if (light == null) continue;
                    var position = entity.Interpolated(partial);
                    candidates.Add(new KeyValuePair<Vec3d, BlockState>(new Vec3d(position.X, position.Y + entity.Height * 0.6, position.Z), light));
                }
                if (candidates.Count > Max)
                {
                    candidates.Sort((a, b) => (a.Key - camera).LengthSquared.CompareTo((b.Key - camera).LengthSquared));
                    candidates.RemoveRange(Max, candidates.Count - Max);
                }
            }

            lights.Clear();
            for (var i = 0; i < Max; i++)
            {
                if (i >= candidates.Count)
                {
                    shaderLights[i] = Vector4.zero;
                    continue;
                }
                var p = candidates[i].Key;
                var render = RenderOrigin.ToRender(p.X, p.Y, p.Z);
                shaderLights[i] = new Vector4(render.x, render.y, render.z, candidates[i].Value.Luminance);
                lights.Add(new KeyValuePair<Vector3, BlockState>(render, candidates[i].Value));
            }
            Shader.SetGlobalVectorArray(LightsId, shaderLights);
            Shader.SetGlobalFloat(CountId, candidates.Count);
            manager?.SetDynamicLights(lights, Time.time);
        }

        public void Clear()
        {
            for (var i = 0; i < Max; i++) shaderLights[i] = Vector4.zero;
            Shader.SetGlobalVectorArray(LightsId, shaderLights);
            Shader.SetGlobalFloat(CountId, 0);
        }
    }
}
