using System;
using System.Collections.Generic;

namespace AutoFLC
{
    /// <summary>
    /// DIR QA statistics for a single region (whole image or lung).
    /// </summary>
    public class DirQaRegionStats
    {
        public long TotalVoxels { get; set; }
        public long FoldingCount { get; set; }
        public double FoldingPercent { get; set; }
        public long ContractionCount { get; set; }
        public double ContractionPercent { get; set; }
        public long IdentityCount { get; set; }
        public double IdentityPercent { get; set; }
        public long ExpansionCount { get; set; }
        public double ExpansionPercent { get; set; }
        public long NonfiniteCount { get; set; }
        public double NonfinitePercent { get; set; }
        public double MinDetJ { get; set; }
        public double MaxDetJ { get; set; }
        public double MeanDetJ { get; set; }
        public double StdDetJ { get; set; }
    }

    /// <summary>
    /// DIR QA results container matching the JSON structure expected by the UI.
    /// </summary>
    public class DirQaResult
    {
        public DirQaRegionStats WholeImage { get; set; }
        public DirQaRegionStats Lung { get; set; }
    }

    /// <summary>
    /// Functional lung masks produced by CreateFunctionalMasks.
    /// </summary>
    public class FunctionalMasks
    {
        public bool[,,] LungExclGtv { get; set; }
        public bool[,,] High { get; set; }
        public bool[,,] Intermediate { get; set; }
        public bool[,,] Low { get; set; }
    }

    /// <summary>
    /// Core computation: DVF gradients, Jacobian determinant, functional masks,
    /// and DIR QA statistics.
    /// </summary>
    public static class JacobianEngine
    {
        /// <summary>
        /// Compute the Jacobian determinant of the deformation field:
        /// J = I + du/dx, detJ = det(J).
        /// </summary>
        /// <param name="dvf">DVF array [nz, ny, nx, 3] with components (dx, dy, dz) in mm.</param>
        /// <param name="spacing">Spacing (sx, sy, sz) in mm.</param>
        /// <returns>detJ array [nz, ny, nx].</returns>
        public static float[,,] ComputeJacobian(float[,,,] dvf, double[] spacing)
        {
            int nz = dvf.GetLength(0);
            int ny = dvf.GetLength(1);
            int nx = dvf.GetLength(2);

            float[,,] ux = ExtractComponent(dvf, 0);
            float[,,] uy = ExtractComponent(dvf, 1);
            float[,,] uz = ExtractComponent(dvf, 2);

            // Gradient3D returns (z, y, x) derivative components, matching the
            // gradient call with spacing (sz, sy, sx).
            float[,,] gradUxZ, gradUxY, gradUxX;
            float[,,] gradUyZ, gradUyY, gradUyX;
            float[,,] gradUzZ, gradUzY, gradUzX;

            Gradient3D(ux, spacing[2], spacing[1], spacing[0], out gradUxZ, out gradUxY, out gradUxX);
            Gradient3D(uy, spacing[2], spacing[1], spacing[0], out gradUyZ, out gradUyY, out gradUyX);
            Gradient3D(uz, spacing[2], spacing[1], spacing[0], out gradUzZ, out gradUzY, out gradUzX);

            // J = I + gradU, with
            //   J[0,0] = 1 + dUx/dx   J[0,1] = dUx/dy   J[0,2] = dUx/dz
            //   J[1,0] = dUy/dx       J[1,1] = 1+dUy/dy J[1,2] = dUy/dz
            //   J[2,0] = dUz/dx       J[2,1] = dUz/dy   J[2,2] = 1+dUz/dz
            //
            // det(J) = J00*(J11*J22-J12*J21)
            //        - J01*(J10*J22-J12*J20)
            //        + J02*(J10*J21-J11*J20)

            float[,,] detJ = new float[nz, ny, nx];

            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        double J00 = 1.0 + gradUxX[z, y, x];
                        double J01 = gradUxY[z, y, x];
                        double J02 = gradUxZ[z, y, x];
                        double J10 = gradUyX[z, y, x];
                        double J11 = 1.0 + gradUyY[z, y, x];
                        double J12 = gradUyZ[z, y, x];
                        double J20 = gradUzX[z, y, x];
                        double J21 = gradUzY[z, y, x];
                        double J22 = 1.0 + gradUzZ[z, y, x];

                        double det = J00 * (J11 * J22 - J12 * J21)
                                   - J01 * (J10 * J22 - J12 * J20)
                                   + J02 * (J10 * J21 - J11 * J20);

                        detJ[z, y, x] = (float)det;
                    }
                }
            }

            return detJ;
        }

        /// <summary>
        /// Create functional lung masks from the Jacobian determinant using
        /// percentile thresholds of the ventilation value detJ - 1.
        /// </summary>
        /// <param name="detJ">Jacobian determinant array.</param>
        /// <param name="lungMask">Lung boolean mask.</param>
        /// <param name="gtvMask">Optional GTV mask to exclude (null to skip).</param>
        /// <param name="percentile">Percentile threshold (e.g. 75).</param>
        /// <returns>FunctionalMasks with high/intermediate/low.</returns>
        public static FunctionalMasks CreateFunctionalMasks(float[,,] detJ, bool[,,] lungMask, bool[,,] gtvMask, int percentile)
        {
            // ventilation = detJ - 1.0 for the Jacobian metric.
            int nz = detJ.GetLength(0);
            int ny = detJ.GetLength(1);
            int nx = detJ.GetLength(2);
            float[,,] vent = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        vent[z, y, x] = detJ[z, y, x] - 1.0f;
            return CreateFunctionalMasksFromVent(vent, lungMask, gtvMask, percentile);
        }

        /// <summary>
        /// Percentile three-class segmentation of an arbitrary ventilation map
        /// (shared by the Jacobian and HU metrics; identical thresholds logic).
        /// </summary>
        public static FunctionalMasks CreateFunctionalMasksFromVent(float[,,] vent, bool[,,] lungMask, bool[,,] gtvMask, int percentile)
        {
            int nz = vent.GetLength(0);
            int ny = vent.GetLength(1);
            int nx = vent.GetLength(2);

            bool[,,] workingLung;
            if (gtvMask != null)
            {
                workingLung = new bool[nz, ny, nx];
                for (int z = 0; z < nz; z++)
                    for (int y = 0; y < ny; y++)
                        for (int x = 0; x < nx; x++)
                            workingLung[z, y, x] = lungMask[z, y, x] && !gtvMask[z, y, x];
            }
            else
            {
                workingLung = lungMask;
            }

            // Collect ventilation values inside the working lung.
            List<double> vals = new List<double>();
            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        if (workingLung[z, y, x])
                        {
                            vals.Add(vent[z, y, x]);
                        }
                    }
                }
            }

            double pHi = 0.0;
            double pLo = 0.0;
            if (vals.Count > 0)
            {
                vals.Sort();
                pHi = Percentile(vals, percentile);
                pLo = Percentile(vals, 100 - percentile);
            }

            bool[,,] high = new bool[nz, ny, nx];
            bool[,,] intermediate = new bool[nz, ny, nx];
            bool[,,] low = new bool[nz, ny, nx];

            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        if (workingLung[z, y, x])
                        {
                            double v = vent[z, y, x];
                            if (v >= pHi)
                                high[z, y, x] = true;
                            else if (v <= pLo)
                                low[z, y, x] = true;
                            else
                                intermediate[z, y, x] = true;
                        }
                    }
                }
            }

            return new FunctionalMasks
            {
                LungExclGtv = workingLung,
                High = high,
                Intermediate = intermediate,
                Low = low
            };
        }

        /// <summary>
        /// Compute DIR QA statistics from detJ and an optional lung mask. The
        /// buckets are exhaustive and mutually exclusive: folding (detJ<0),
        /// contraction (0&lt;=detJ&lt;1), identity (detJ==1, exact), expansion
        /// (detJ>1), plus non-finite (NaN/Inf).
        /// </summary>
        public static DirQaResult ComputeDirQaStats(float[,,] detJ, bool[,,] lungMask)
        {
            DirQaResult result = new DirQaResult();
            result.WholeImage = ComputeRegionStats(detJ, null);
            result.Lung = ComputeRegionStats(detJ, lungMask);
            return result;
        }

        private static DirQaRegionStats ComputeRegionStats(float[,,] detJ, bool[,,] mask)
        {
            int nz = detJ.GetLength(0);
            int ny = detJ.GetLength(1);
            int nx = detJ.GetLength(2);

            List<double> finiteVals = new List<double>();
            long total = 0;
            long nonfinite = 0;

            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        if (mask != null && !mask[z, y, x])
                            continue;

                        total++;
                        double v = detJ[z, y, x];
                        if (double.IsNaN(v) || double.IsInfinity(v))
                        {
                            nonfinite++;
                        }
                        else
                        {
                            finiteVals.Add(v);
                        }
                    }
                }
            }

            DirQaRegionStats stats = new DirQaRegionStats();
            stats.TotalVoxels = total;

            if (total == 0)
            {
                return stats;
            }

            stats.NonfiniteCount = nonfinite;
            stats.NonfinitePercent = 100.0 * nonfinite / total;

            long folding = 0;
            long contraction = 0;
            long identity = 0;
            long expansion = 0;

            double sum = 0.0;
            double minVal = double.MaxValue;
            double maxVal = double.MinValue;

            foreach (double v in finiteVals)
            {
                sum += v;
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;

                if (v < 0.0)
                    folding++;
                else if (v < 1.0)
                    contraction++;
                else if (v == 1.0)
                    identity++;
                else
                    expansion++;
            }

            long nf = finiteVals.Count;
            double pctBase = (nf > 0) ? 100.0 / total : 0.0;

            stats.FoldingCount = folding;
            stats.FoldingPercent = folding * pctBase;
            stats.ContractionCount = contraction;
            stats.ContractionPercent = contraction * pctBase;
            stats.IdentityCount = identity;
            stats.IdentityPercent = identity * pctBase;
            stats.ExpansionCount = expansion;
            stats.ExpansionPercent = expansion * pctBase;

            if (nf > 0)
            {
                stats.MinDetJ = minVal;
                stats.MaxDetJ = maxVal;
                stats.MeanDetJ = sum / nf;

                double varSum = 0.0;
                double mean = stats.MeanDetJ;
                foreach (double v in finiteVals)
                    varSum += (v - mean) * (v - mean);
                stats.StdDetJ = Math.Sqrt(varSum / nf);
            }

            return stats;
        }

        /// <summary>
        /// 3D central-difference gradient with forward/backward differences at
        /// the boundaries. Returns the (z, y, x) derivative components.
        /// </summary>
        private static void Gradient3D(float[,,] arr, double hz, double hy, double hx,
            out float[,,] dz, out float[,,] dy, out float[,,] dx)
        {
            int nz = arr.GetLength(0);
            int ny = arr.GetLength(1);
            int nx = arr.GetLength(2);

            dz = new float[nz, ny, nx];
            dy = new float[nz, ny, nx];
            dx = new float[nz, ny, nx];

            // d/dz
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    dz[0, y, x] = (float)((arr[1, y, x] - arr[0, y, x]) / hz);
                    dz[nz - 1, y, x] = (float)((arr[nz - 1, y, x] - arr[nz - 2, y, x]) / hz);
                    for (int z = 1; z < nz - 1; z++)
                        dz[z, y, x] = (float)((arr[z + 1, y, x] - arr[z - 1, y, x]) / (2.0 * hz));
                }
            }

            // d/dy
            for (int z = 0; z < nz; z++)
            {
                for (int x = 0; x < nx; x++)
                {
                    dy[z, 0, x] = (float)((arr[z, 1, x] - arr[z, 0, x]) / hy);
                    dy[z, ny - 1, x] = (float)((arr[z, ny - 1, x] - arr[z, ny - 2, x]) / hy);
                    for (int y = 1; y < ny - 1; y++)
                        dy[z, y, x] = (float)((arr[z, y + 1, x] - arr[z, y - 1, x]) / (2.0 * hy));
                }
            }

            // d/dx
            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    dx[z, y, 0] = (float)((arr[z, y, 1] - arr[z, y, 0]) / hx);
                    dx[z, y, nx - 1] = (float)((arr[z, y, nx - 1] - arr[z, y, nx - 2]) / hx);
                    for (int x = 1; x < nx - 1; x++)
                        dx[z, y, x] = (float)((arr[z, y, x + 1] - arr[z, y, x - 1]) / (2.0 * hx));
                }
            }
        }

        private static float[,,] ExtractComponent(float[,,,] dvf, int component)
        {
            int nz = dvf.GetLength(0);
            int ny = dvf.GetLength(1);
            int nx = dvf.GetLength(2);
            float[,,] result = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        result[z, y, x] = dvf[z, y, x, component];
            return result;
        }

        /// <summary>
        /// Compute a percentile from a sorted list of values using linear
        /// interpolation.
        /// </summary>
        private static double Percentile(List<double> sorted, int percentile)
        {
            int n = sorted.Count;
            if (n == 0) return 0.0;
            if (percentile <= 0) return sorted[0];
            if (percentile >= 100) return sorted[n - 1];

            double idx = (percentile / 100.0) * (n - 1);
            int lower = (int)Math.Floor(idx);
            int upper = (int)Math.Ceiling(idx);
            if (lower == upper) return sorted[lower];

            double frac = idx - lower;
            return sorted[lower] + frac * (sorted[upper] - sorted[lower]);
        }
    }
}
