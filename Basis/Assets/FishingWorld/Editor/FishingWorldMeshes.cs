using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flat-shaded low-poly meshes for the fishing world, generated from code: lumpy icospheres for
/// tree tops, bushes, rocks and clouds, cones for pines, n-gon discs for the pond, and the
/// ground with the pond dug into it.
/// </summary>
public static class FishingWorldMeshes
{
    /// <summary>A subdivided icosahedron with its vertices pushed in and out a little.</summary>
    public static Mesh Blob(string name, int subdivisions, float lumpiness, int seed)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        List<Vector3> verts = new List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
        List<int> tris = new List<int>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        for (int s = 0; s < subdivisions; s++)
        {
            Dictionary<long, int> midpoints = new Dictionary<long, int>();
            List<int> next = new List<int>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Midpoint(verts, midpoints, a, b);
                int bc = Midpoint(verts, midpoints, b, c);
                int ca = Midpoint(verts, midpoints, c, a);
                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            tris = next;
        }

        System.Random random = new System.Random(seed);
        for (int i = 0; i < verts.Count; i++)
        {
            float push = 1f + ((float)random.NextDouble() * 2f - 1f) * lumpiness;
            verts[i] *= push;
        }

        return FlatShaded(name, verts, tris);
    }

    private static int Midpoint(List<Vector3> verts, Dictionary<long, int> cache, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (cache.TryGetValue(key, out int index)) return index;
        verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
        index = verts.Count - 1;
        cache[key] = index;
        return index;
    }

    /// <summary>A cone with its base at y=0 and its point at y=1, radius 1.</summary>
    public static Mesh Cone(string name, int sides)
    {
        List<Vector3> verts = new List<Vector3> { new Vector3(0, 1, 0), Vector3.zero };
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            verts.Add(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)));
        }
        List<int> tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            int a = 2 + i, b = 2 + (i + 1) % sides;
            tris.AddRange(new[] { 0, b, a, 1, a, b });
        }
        return FlatShaded(name, verts, tris);
    }

    /// <summary>A prism with n sides, radius 1, from y=-0.5 to y=0.5.</summary>
    public static Mesh Prism(string name, int sides)
    {
        List<Vector3> verts = new List<Vector3> { new Vector3(0, 0.5f, 0), new Vector3(0, -0.5f, 0) };
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            verts.Add(new Vector3(Mathf.Cos(a), 0.5f, Mathf.Sin(a)));
            verts.Add(new Vector3(Mathf.Cos(a), -0.5f, Mathf.Sin(a)));
        }
        List<int> tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            int top = 2 + i * 2, bottom = top + 1;
            int nextTop = 2 + ((i + 1) % sides) * 2, nextBottom = nextTop + 1;
            tris.AddRange(new[] { 0, nextTop, top, 1, bottom, nextBottom, top, nextTop, bottom, bottom, nextTop, nextBottom });
        }
        return FlatShaded(name, verts, tris);
    }

    /// <summary>The ground: a grid following <paramref name="height"/>, flat shaded.</summary>
    public static Mesh Ground(string name, float halfSize, float cell, System.Func<float, float, float> height, int seed)
    {
        int count = Mathf.RoundToInt(halfSize * 2f / cell);
        System.Random random = new System.Random(seed);
        Vector3[,] grid = new Vector3[count + 1, count + 1];
        for (int z = 0; z <= count; z++)
        {
            for (int x = 0; x <= count; x++)
            {
                float px = -halfSize + x * cell;
                float pz = -halfSize + z * cell;
                // Jitter inner points sideways for a hand-made look; the height follows the jittered spot.
                if (x > 0 && x < count && z > 0 && z < count)
                {
                    px += ((float)random.NextDouble() - 0.5f) * cell * 0.35f;
                    pz += ((float)random.NextDouble() - 0.5f) * cell * 0.35f;
                }
                grid[x, z] = new Vector3(px, height(px, pz), pz);
            }
        }

        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();
        for (int z = 0; z < count; z++)
        {
            for (int x = 0; x < count; x++)
            {
                int i = verts.Count;
                verts.Add(grid[x, z]);
                verts.Add(grid[x + 1, z]);
                verts.Add(grid[x, z + 1]);
                verts.Add(grid[x + 1, z + 1]);
                // Alternate the diagonal for less obvious rows.
                if (((x + z) & 1) == 0) tris.AddRange(new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 });
                else tris.AddRange(new[] { i, i + 2, i + 3, i, i + 3, i + 1 });
            }
        }
        return FlatShaded(name, verts, tris, true);
    }

    /// <summary>Gives every triangle its own vertices so it shades as one flat facet.</summary>
    public static Mesh FlatShaded(string name, List<Vector3> verts, List<int> tris, bool faceUp = false)
    {
        // Face every triangle outward (closed convex shapes) or up (the ground), whichever
        // way it was listed, so the winding of the builders above does not matter.
        Vector3 center = Vector3.zero;
        for (int i = 0; i < verts.Count; i++) center += verts[i];
        center /= Mathf.Max(1, verts.Count);
        Vector3[] v = new Vector3[tris.Count];
        int[] t = new int[tris.Count];
        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            Vector3 outward = faceUp ? Vector3.up : (a + b + c) / 3f - center;
            if (Vector3.Dot(normal, outward) < 0f)
            {
                Vector3 swap = b;
                b = c;
                c = swap;
            }
            v[i] = a;
            v[i + 1] = b;
            v[i + 2] = c;
            t[i] = i;
            t[i + 1] = i + 1;
            t[i + 2] = i + 2;
        }
        Mesh mesh = new Mesh { name = name };
        if (v.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = v;
        mesh.triangles = t;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
