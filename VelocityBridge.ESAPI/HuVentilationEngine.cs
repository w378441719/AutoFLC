using System;
using System.Collections.Generic;

namespace AutoFLC
{
    /// <summary>
    /// Density-based (HU) ventilation metric, following Guerrero/Simon in the
    /// Yamamoto (2011) form with the global lung mass correction:
    ///
    ///   V_HU(x) = [HU_ex(x) - HU*_in(x + u(x))] / [H U*_in(x + u(x)) + 1000]
    ///
    /// where HU_in is the inspiration CT deformed into the expiration frame by
    /// the same smoothed DVF used for the Jacobian metric (DR sources apply the
    /// pre-deformation matrix), then mass-corrected:
    ///   f = sum_lung(HU_ex + 1000) / sum_lung(HU_in_w + 1000)
    ///   HU*_in = f * (HU_in_w + 1000) - 1000
    ///
    /// Both CT phases are pre-smoothed with an isotropic Gaussian, following
    /// Yamamoto et al. (Med Phys 2011); the default kernel width
    /// (sigma^2 = 1.5 mm^2) is the research default of this pipeline.
    /// </summary>
    public static class HuVentilationEngine
    {
        /// <summary>
        /// Compute the HU ventilation map on the expiration CT grid.
        /// </summary>
        /// <param name="expHuSmoothed">Smoothed expiration CT [nz, ny, nx] (HU).</param>
        /// <param name="inspHuSmoothed">Smoothed inspiration CT [inz, iny, inx] (HU).</param>
        /// <param name="inspZ">Ascending slice z positions of the inspiration series.</param>
        /// <param name="inspOriginX">ImagePositionPatient x of inspiration slice (0,0).</param>
        /// <param name="inspOriginY">ImagePositionPatient y of inspiration slice (0,0).</param>
        /// <param name="inspSpacingRow">Inspiration pixel spacing along rows (y).</param>
        /// <param name="inspSpacingCol">Inspiration pixel spacing along columns (x).</param>
        /// <param name="field">Smoothed DVF with grid geometry and pre-matrix.</param>
        /// <param name="ctZ">Ascending slice z positions of the expiration series.</param>
        /// <param name="ctOriginX">Expiration IPP x.</param>
        /// <param name="ctOriginY">Expiration IPP y.</param>
        /// <param name="ctSpacingRow">Expiration pixel spacing along rows (y).</param>
        /// <param name="ctSpacingCol">Expiration pixel spacing along columns (x).</param>
        /// <param name="lung">Lung mask on the expiration grid.</param>
        /// <param name="massFactor">Output: applied mass-correction factor f.</param>
        /// <returns>V_HU [nz, ny, nx] (zero outside the lung).</returns>
        public static float[,,] Compute(
            float[,,] expHuSmoothed,
            float[,,] inspHuSmoothed,
            double[] inspZ,
            double inspOriginX, double inspOriginY,
            double inspSpacingRow, double inspSpacingCol,
            DvfField field,
            double[] ctZ,
            double ctOriginX, double ctOriginY,
            double ctSpacingRow, double ctSpacingCol,
            bool[,,] lung,
            out double massFactor)
        {
            int nz = expHuSmoothed.GetLength(0);
            int ny = expHuSmoothed.GetLength(1);
            int nx = expHuSmoothed.GetLength(2);
            int inz = inspHuSmoothed.GetLength(0);

            // Sample the deformed inspiration image at lung voxels only.
            float[,,] huInW = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        huInW[z, y, x] = -1000f;

            double[] pt = new double[3];
            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        if (!lung[z, y, x]) continue;

                        double wx = ctOriginX + x * ctSpacingCol;
                        double wy = ctOriginY + y * ctSpacingRow;
                        double wz = ctZ[z];

                        field.MapToSource(wx, wy, wz, pt);
                        huInW[z, y, x] = SampleInspiration(
                            inspHuSmoothed, inspZ, inspOriginX, inspOriginY,
                            inspSpacingRow, inspSpacingCol, pt[0], pt[1], pt[2]);
                    }
                }
            }

            // Global lung mass correction.
            double sumExp = 0.0, sumInsp = 0.0;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (!lung[z, y, x]) continue;
                        sumExp += expHuSmoothed[z, y, x] + 1000.0;
                        sumInsp += huInW[z, y, x] + 1000.0;
                    }
            massFactor = sumInsp > 0 ? sumExp / sumInsp : 1.0;

            float[,,] vHu = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (!lung[z, y, x]) continue;
                        double den = massFactor * (huInW[z, y, x] + 1000.0);
                        double correctedInsp = den - 1000.0;
                        if (den > 1.0)
                            vHu[z, y, x] = (float)((expHuSmoothed[z, y, x] - correctedInsp) / den);
                    }
            return vHu;
        }

        /// <summary>
        /// Trilinear sampling of the inspiration volume at a world point,
        /// clipped into the volume (matching the Python cross-check).
        /// </summary>
        private static float SampleInspiration(
            float[,,] vol, double[] zGrid,
            double originX, double originY,
            double spacingRow, double spacingCol,
            double wx, double wy, double wz)
        {
            int inz = vol.GetLength(0);
            int iny = vol.GetLength(1);
            int inx = vol.GetLength(2);

            double fx = (wx - originX) / spacingCol;
            double fy = (wy - originY) / spacingRow;
            if (fx < 0) fx = 0; if (fx > inx - 1) fx = inx - 1;
            if (fy < 0) fy = 0; if (fy > iny - 1) fy = iny - 1;

            double zc = wz;
            if (zc < zGrid[0]) zc = zGrid[0];
            if (zc > zGrid[inz - 1]) zc = zGrid[inz - 1];

            // Lower-bound search: largest i with zGrid[i] <= zc.
            int i = LowerBound(zGrid, zc);
            int sl = i + 1;
            if (sl < 1) sl = 1;
            if (sl > inz - 1) sl = inz - 1;
            double fz = (sl - 1) + (zc - zGrid[sl - 1]) /
                Math.Max(1e-12, zGrid[sl] - zGrid[sl - 1]);

            return Trilinear(vol, fz, fy, fx);
        }

        private static int LowerBound(double[] a, double v)
        {
            int lo = 0, hi = a.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (a[mid] <= v) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Trilinear interpolation with clamped integer indices.</summary>
        public static float Trilinear(float[,,] vol, double fz, double fy, double fx)
        {
            int nz = vol.GetLength(0);
            int ny = vol.GetLength(1);
            int nx = vol.GetLength(2);

            int z0 = (int)Math.Floor(fz); int z1 = z0 + 1;
            int y0 = (int)Math.Floor(fy); int y1 = y0 + 1;
            int x0 = (int)Math.Floor(fx); int x1 = x0 + 1;
            if (z0 < 0) z0 = 0; if (z0 > nz - 1) z0 = nz - 1;
            if (z1 < 0) z1 = 0; if (z1 > nz - 1) z1 = nz - 1;
            if (y0 < 0) y0 = 0; if (y0 > ny - 1) y0 = ny - 1;
            if (y1 < 0) y1 = 0; if (y1 > ny - 1) y1 = ny - 1;
            if (x0 < 0) x0 = 0; if (x0 > nx - 1) x0 = nx - 1;
            if (x1 < 0) x1 = 0; if (x1 > nx - 1) x1 = nx - 1;

            double tz = fz - z0, ty = fy - y0, tx = fx - x0;
            if (tz < 0) tz = 0; if (tz > 1) tz = 1;
            if (ty < 0) ty = 0; if (ty > 1) ty = 1;
            if (tx < 0) tx = 0; if (tx > 1) tx = 1;

            double c00 = vol[z0, y0, x0] * (1 - tx) + vol[z0, y0, x1] * tx;
            double c10 = vol[z0, y1, x0] * (1 - tx) + vol[z0, y1, x1] * tx;
            double c01 = vol[z1, y0, x0] * (1 - tx) + vol[z1, y0, x1] * tx;
            double c11 = vol[z1, y1, x0] * (1 - tx) + vol[z1, y1, x1] * tx;
            double c0 = c00 * (1 - ty) + c10 * ty;
            double c1 = c01 * (1 - ty) + c11 * ty;
            return (float)(c0 * (1 - tz) + c1 * tz);
        }

        /// <summary>
        /// Spearman rank correlation with average ranks for ties (matching
        /// scipy.stats.spearmanr semantics).
        /// </summary>
        public static double Spearman(float[] a, float[] b)
        {
            int n = a.Length;
            if (n < 3) return double.NaN;
            double[] ra = AverageRanks(a);
            double[] rb = AverageRanks(b);
            return Pearson(ra, rb);
        }

        private static double[] AverageRanks(float[] values)
        {
            int n = values.Length;
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            double[] keys = new double[n];
            for (int i = 0; i < n; i++) keys[i] = values[i];
            Array.Sort(keys, order);

            double[] ranks = new double[n];
            int i0 = 0;
            while (i0 < n)
            {
                int i1 = i0;
                while (i1 + 1 < n && keys[i1 + 1] == keys[i0]) i1++;
                double avg = 0.5 * (i0 + i1) + 1.0; // 1-based average rank
                for (int k = i0; k <= i1; k++) ranks[order[k]] = avg;
                i0 = i1 + 1;
            }
            return ranks;
        }

        private static double Pearson(double[] x, double[] y)
        {
            int n = x.Length;
            double sx = 0, sy = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; }
            double mx = sx / n, my = sy / n;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - mx, dy = y[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            if (sxx <= 0 || syy <= 0) return double.NaN;
            return sxy / Math.Sqrt(sxx * syy);
        }
    }
}
