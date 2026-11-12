using System;

namespace DeformableImage
{
    /// <summary>Original-image exterior only: internal holes and later cuts are not anchors.</summary>
    public static class OriginalOutlineMath
    {
        public static int[] BuildExteriorIntegral(byte[] alpha, int width, int height)
        {
            if (alpha == null || width < 1 || height < 1 || alpha.Length != width * height)
                throw new ArgumentException("Invalid source alpha dimensions.");
            const byte solid = 16;
            var exterior = new bool[alpha.Length];
            var queue = new int[alpha.Length];
            int head = 0, tail = 0;
            for (int y = 0; y < height; y++)
            {
                Enqueue(y * width, alpha, exterior, queue, ref tail, solid);
                Enqueue(y * width + width - 1, alpha, exterior, queue, ref tail, solid);
            }
            for (int x = 0; x < width; x++)
            {
                Enqueue(x, alpha, exterior, queue, ref tail, solid);
                Enqueue((height - 1) * width + x, alpha, exterior, queue, ref tail, solid);
            }
            while (head < tail)
            {
                int index = queue[head++], x = index % width, y = index / width;
                if (x > 0) Enqueue(index - 1, alpha, exterior, queue, ref tail, solid);
                if (x + 1 < width) Enqueue(index + 1, alpha, exterior, queue, ref tail, solid);
                if (y > 0) Enqueue(index - width, alpha, exterior, queue, ref tail, solid);
                if (y + 1 < height) Enqueue(index + width, alpha, exterior, queue, ref tail, solid);
            }
            int stride = width + 1;
            var integral = new int[stride * (height + 1)];
            for (int y = 0; y < height; y++)
            {
                int rowCount = 0;
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    bool boundary = alpha[index] >= solid && (x == 0 || x == width - 1
                        || y == 0 || y == height - 1 || exterior[index - 1] || exterior[index + 1]
                        || exterior[index - width] || exterior[index + width]);
                    if (boundary) rowCount++;
                    integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + rowCount;
                }
            }
            return integral;
        }

        // Conservative triangle bounding-box query. Fixing all three support vertices
        // preserves the alpha silhouette even when it crosses the middle of a triangle.
        public static bool TouchesExterior(int[] integral, int width, int height,
            float minU, float minV, float maxU, float maxV)
        {
            int x0 = Math.Max(0, Math.Min(width, (int)Math.Floor(minU * width) - 1));
            int y0 = Math.Max(0, Math.Min(height, (int)Math.Floor(minV * height) - 1));
            int x1 = Math.Max(0, Math.Min(width, (int)Math.Ceiling(maxU * width) + 1));
            int y1 = Math.Max(0, Math.Min(height, (int)Math.Ceiling(maxV * height) + 1));
            int stride = width + 1;
            return integral[y1 * stride + x1] - integral[y0 * stride + x1]
                - integral[y1 * stride + x0] + integral[y0 * stride + x0] > 0;
        }

        private static void Enqueue(int index, byte[] alpha, bool[] exterior,
            int[] queue, ref int tail, byte solid)
        {
            if (alpha[index] >= solid || exterior[index]) return;
            exterior[index] = true;
            queue[tail++] = index;
        }
    }
}
