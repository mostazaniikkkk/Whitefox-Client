// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Meshing
{
    /// <summary>
    /// Which faces of a section can see each other through non opaque blocks (Minosoft's SectionOcclusion, vanilla
    /// VisGraph). Stored as a 6x6 bit matrix: bit (a * 6 + b) is set when face a is connected to face b.
    /// </summary>
    public static class OcclusionGraph
    {
        /// <summary>Every face sees every other face (empty, unknown or barely filled sections).</summary>
        public const ulong All = (1UL << 36) - 1;

        /// <summary>Vanilla: with fewer opaque blocks than one full layer the section can not block anything.</summary>
        private const int MinOpaque = 256;

        public static bool Connected(ulong graph, Direction from, Direction to) => (graph & (1UL << ((int)from * 6 + (int)to))) != 0;

        /// <param name="opaque">Returns whether the block at section local x, y, z (0..15) is fully opaque.</param>
        public static ulong Compute(Func<int, int, int, bool> opaque)
        {
            var solid = new bool[4096];
            var count = 0;
            for (var i = 0; i < 4096; i++)
            {
                if (!opaque(i & 0x0F, i >> 8, (i >> 4) & 0x0F)) continue;
                solid[i] = true;
                count++;
            }
            if (count < MinOpaque) return All;
            if (count == 4096) return 0;

            ulong graph = 0;
            var visited = new bool[4096];
            var stack = new int[4096];
            for (var start = 0; start < 4096; start++)
            {
                if (solid[start] || visited[start]) continue;
                // flood fill one connected air pocket and collect the faces it touches
                var faces = 0;
                var size = 0;
                stack[size++] = start;
                visited[start] = true;
                while (size > 0)
                {
                    var index = stack[--size];
                    int x = index & 0x0F, y = index >> 8, z = (index >> 4) & 0x0F;
                    if (x == 0) faces |= 1 << (int)Direction.West;
                    if (x == 15) faces |= 1 << (int)Direction.East;
                    if (y == 0) faces |= 1 << (int)Direction.Down;
                    if (y == 15) faces |= 1 << (int)Direction.Up;
                    if (z == 0) faces |= 1 << (int)Direction.North;
                    if (z == 15) faces |= 1 << (int)Direction.South;
                    if (x > 0) Push(index - 1);
                    if (x < 15) Push(index + 1);
                    if (y > 0) Push(index - 256);
                    if (y < 15) Push(index + 256);
                    if (z > 0) Push(index - 16);
                    if (z < 15) Push(index + 16);
                }
                for (var a = 0; a < 6; a++)
                {
                    if ((faces & (1 << a)) == 0) continue;
                    for (var b = 0; b < 6; b++)
                    {
                        if ((faces & (1 << b)) != 0) graph |= 1UL << (a * 6 + b);
                    }
                }
                if (graph == All) break;

                void Push(int next)
                {
                    if (solid[next] || visited[next]) return;
                    visited[next] = true;
                    stack[size++] = next;
                }
            }
            return graph;
        }

        public static ulong Compute(BlockState[] states16)
        {
            return Compute((x, y, z) =>
            {
                var state = states16[(y << 8) | (z << 4) | x];
                return state != null && state.FullOpaque;
            });
        }
    }

    /// <summary>
    /// Cave culling (Minosoft's OcclusionCuller, vanilla LevelRenderer section graph): a breadth first walk from the
    /// camera section that only crosses a section from the face it entered through to faces connected to it, and never
    /// turns back toward the camera. Sections that are not reached are hidden behind opaque blocks.
    /// </summary>
    public sealed class OcclusionCuller
    {
        private struct Node
        {
            public SectionPos Position;
            /// <summary>Face of this section the walk came in through, -1 for the start.</summary>
            public int Entry;
            /// <summary>Bit mask of every direction stepped so far.</summary>
            public int Directions;
        }

        private readonly Queue<Node> queue = new Queue<Node>();

        public readonly HashSet<SectionPos> Visible = new HashSet<SectionPos>();

        /// <param name="loaded">Whether the chunk column of the section is loaded (the walk stops at unloaded chunks).</param>
        /// <param name="graph">Occlusion graph of a section; <see cref="OcclusionGraph.All"/> when unknown or empty.</param>
        public void Compute(SectionPos camera, int minSection, int maxSection, int viewDistance, Func<ChunkPos, bool> loaded, Func<SectionPos, ulong> graph)
        {
            Visible.Clear();
            queue.Clear();

            bool InRange(SectionPos position) => Math.Abs(position.X - camera.X) <= viewDistance && Math.Abs(position.Z - camera.Z) <= viewDistance && loaded(position.Chunk);

            if (camera.Y > maxSection || camera.Y < minSection)
            {
                // outside of the world: start with the whole top (or bottom) layer, entered from outside
                var y = camera.Y > maxSection ? maxSection : minSection;
                var entry = camera.Y > maxSection ? Direction.Up : Direction.Down;
                for (var x = camera.X - viewDistance; x <= camera.X + viewDistance; x++)
                {
                    for (var z = camera.Z - viewDistance; z <= camera.Z + viewDistance; z++)
                    {
                        var position = new SectionPos(x, y, z);
                        if (!InRange(position)) continue;
                        Visible.Add(position);
                        queue.Enqueue(new Node { Position = position, Entry = (int)entry, Directions = 1 << (int)entry.Opposite() });
                    }
                }
            }
            else
            {
                Visible.Add(camera);
                queue.Enqueue(new Node { Position = camera, Entry = -1 });
            }

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                var connections = node.Entry < 0 ? OcclusionGraph.All : graph(node.Position);
                for (var d = 0; d < 6; d++)
                {
                    if ((node.Directions & (1 << (d ^ 1))) != 0) continue; // never back toward the camera
                    if (node.Entry >= 0 && (connections & (1UL << (node.Entry * 6 + d))) == 0) continue;
                    var direction = (Direction)d;
                    var next = new SectionPos(node.Position.X + direction.X(), node.Position.Y + direction.Y(), node.Position.Z + direction.Z());
                    if (next.Y < minSection || next.Y > maxSection || !InRange(next) || !Visible.Add(next)) continue;
                    queue.Enqueue(new Node { Position = next, Entry = d ^ 1, Directions = node.Directions | (1 << d) });
                }
            }
        }
    }
}
