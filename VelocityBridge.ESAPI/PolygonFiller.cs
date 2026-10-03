using System;

namespace AutoFLC
{
    /// <summary>
    /// Scan-line polygon rasterizer. For each integer scanline the algorithm
    /// intersects the polygon edges with the horizontal line at that row and
    /// fills every pixel whose centre lies between paired intersections
    /// (even-odd rule). Edge/scanline handling uses the half-open convention
    /// (r1 &lt;= y &lt; r2) || (r2 &lt;= y &lt; r1) so that local extrema do not
    /// produce duplicate intersections.
    /// </summary>
    public static class PolygonFiller
    {
        /// <summary>
        /// Rasterize a closed polygon into a 2-D boolean mask.
        /// </summary>
        /// <param name="vertsR">Vertex row coordinates (length n, may be fractional).</param>
        /// <param name="vertsC">Vertex column coordinates (length n).</param>
        /// <param name="mask">Output mask; pixels inside the polygon (or on its boundary) are set.</param>
        public static void Fill(double[] vertsR, double[] vertsC, bool[,] mask)
        {
            if (vertsR == null || vertsC == null || mask == null)
                return;

            int n = vertsR.Length;
            if (n < 3 || n != vertsC.Length)
                return;

            int H = mask.GetLength(0);
            int W = mask.GetLength(1);

            // Determine the row extent from the vertices, snapped outward so that
            // partially covered rows are still visited.
            double yMinD = vertsR[0];
            double yMaxD = vertsR[0];
            for (int i = 1; i < n; i++)
            {
                if (vertsR[i] < yMinD) yMinD = vertsR[i];
                if (vertsR[i] > yMaxD) yMaxD = vertsR[i];
            }

            int yMin = (int)Math.Floor(yMinD);
            int yMax = (int)Math.Ceiling(yMaxD);
            if (yMin < 0) yMin = 0;
            if (yMax > H - 1) yMax = H - 1;
            if (yMin > yMax)
                return;

            double[] xs = new double[n];

            for (int y = yMin; y <= yMax; y++)
            {
                int nInt = 0;

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    double r1 = vertsR[i];
                    double r2 = vertsR[j];
                    double c1 = vertsC[i];
                    double c2 = vertsC[j];

                    // Skip horizontal edges entirely.
                    if (Math.Abs(r2 - r1) < 1e-12)
                        continue;

                    // Half-open scanline test: the lower endpoint is included,
                    // the upper endpoint is excluded.
                    bool include = (r1 <= y && y < r2) || (r2 <= y && y < r1);
                    if (!include)
                        continue;

                    double t = (y - r1) / (r2 - r1);
                    double x = c1 + t * (c2 - c1);
                    xs[nInt++] = x;
                }

                if (nInt < 2)
                    continue;

                Array.Sort(xs, 0, nInt);

                for (int k = 0; k + 1 < nInt; k += 2)
                {
                    double xL = xs[k];
                    double xR = xs[k + 1];

                    // The pixel centres covered are ceil(xL) .. floor(xR) inclusive.
                    int xStart = (int)Math.Ceiling(xL);
                    int xEnd = (int)Math.Floor(xR);

                    if (xStart < 0) xStart = 0;
                    if (xEnd > W - 1) xEnd = W - 1;

                    for (int x = xStart; x <= xEnd; x++)
                    {
                        mask[y, x] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Overload that takes integer vertices.
        /// </summary>
        public static void Fill(int[] vertsR, int[] vertsC, bool[,] mask)
        {
            if (vertsR == null || vertsC == null)
                return;

            int n = vertsR.Length;
            if (n < 3 || n != vertsC.Length)
                return;

            double[] dr = new double[n];
            double[] dc = new double[n];
            for (int i = 0; i < n; i++)
            {
                dr[i] = vertsR[i];
                dc[i] = vertsC[i];
            }

            Fill(dr, dc, mask);
        }
    }
}
