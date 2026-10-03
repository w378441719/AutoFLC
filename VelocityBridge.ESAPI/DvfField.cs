using System;

namespace AutoFLC
{
    /// <summary>
    /// Engine-agnostic deformation vector field (DVF) on its native grid.
    ///
    /// BDF and DICOM DR are both just carrier formats for a DVF; this class is
    /// the common representation consumed by the analysis core (smoothing,
    /// Jacobian, HU metric). Format names only appear in the adapters
    /// (BdfParser / DrParser).
    /// </summary>
    public class DvfField
    {
        /// <summary>DVF components (dx, dy, dz) in mm, indexed [z, y, x, c].</summary>
        public float[,,,] Vectors { get; set; }

        /// <summary>Grid dimensions (nx, ny, nz).</summary>
        public int Nx { get; set; }
        public int Ny { get; set; }
        public int Nz { get; set; }

        /// <summary>Grid spacing (sx, sy, sz) in mm.</summary>
        public double Sx { get; set; }
        public double Sy { get; set; }
        public double Sz { get; set; }

        /// <summary>
        /// World-coordinate origin of voxel (0,0,0) (DICOM patient coordinates).
        /// For BDF exports this equals the reference CT origin; for DR exports
        /// it is the grid's own ImagePositionPatient.
        /// </summary>
        public double Ox { get; set; }
        public double Oy { get; set; }
        public double Oz { get; set; }

        /// <summary>
        /// Full point mapping direction. BDF vectors already address the
        /// inspiration position directly: p_insp = p + v(p). DR vectors are
        /// expressed in the pre-matrix frame: p_insp = M_pre * (p + v(p)).
        /// This matrix is identity for BDF sources.
        /// </summary>
        public double[,] PreMatrix { get; set; }

        /// <summary>"BDF" or "DR".</summary>
        public string SourceType { get; set; }

        /// <summary>Source file the field was parsed from.</summary>
        public string SourcePath { get; set; }

        public double[] SpacingArray()
        {
            return new double[] { Sx, Sy, Sz };
        }

        /// <summary>
        /// Trilinear sample of the DVF at a world point (clipped into the
        /// grid), followed by the full point mapping
        /// p_source = M_pre * (p + v(p)).
        /// </summary>
        public void MapToSource(double wx, double wy, double wz, double[] outPt)
        {
            double fx = (wx - Ox) / Sx;
            double fy = (wy - Oy) / Sy;
            double fz = (wz - Oz) / Sz;
            if (fx < 0) fx = 0; if (fx > Nx - 1) fx = Nx - 1;
            if (fy < 0) fy = 0; if (fy > Ny - 1) fy = Ny - 1;
            if (fz < 0) fz = 0; if (fz > Nz - 1) fz = Nz - 1;

            int z0 = (int)Math.Floor(fz); int z1 = Math.Min(z0 + 1, Nz - 1); if (z0 > Nz - 1) z0 = Nz - 1;
            int y0 = (int)Math.Floor(fy); int y1 = Math.Min(y0 + 1, Ny - 1); if (y0 > Ny - 1) y0 = Ny - 1;
            int x0 = (int)Math.Floor(fx); int x1 = Math.Min(x0 + 1, Nx - 1); if (x0 > Nx - 1) x0 = Nx - 1;
            double tz = Clamp01(fz - z0), ty = Clamp01(fy - y0), tx = Clamp01(fx - x0);

            double[] c = new double[3];
            for (int comp = 0; comp < 3; comp++)
            {
                double c00 = Vectors[z0, y0, x0, comp] * (1 - tx) + Vectors[z0, y0, x1, comp] * tx;
                double c10 = Vectors[z0, y1, x0, comp] * (1 - tx) + Vectors[z0, y1, x1, comp] * tx;
                double c01 = Vectors[z1, y0, x0, comp] * (1 - tx) + Vectors[z1, y0, x1, comp] * tx;
                double c11 = Vectors[z1, y1, x0, comp] * (1 - tx) + Vectors[z1, y1, x1, comp] * tx;
                double c0 = c00 * (1 - ty) + c10 * ty;
                double c1 = c01 * (1 - ty) + c11 * ty;
                c[comp] = c0 * (1 - tz) + c1 * tz;
            }

            double px = wx + c[0];
            double py = wy + c[1];
            double pz = wz + c[2];

            if (PreMatrix != null)
            {
                outPt[0] = PreMatrix[0, 0] * px + PreMatrix[0, 1] * py + PreMatrix[0, 2] * pz + PreMatrix[0, 3];
                outPt[1] = PreMatrix[1, 0] * px + PreMatrix[1, 1] * py + PreMatrix[1, 2] * pz + PreMatrix[1, 3];
                outPt[2] = PreMatrix[2, 0] * px + PreMatrix[2, 1] * py + PreMatrix[2, 2] * pz + PreMatrix[2, 3];
            }
            else
            {
                outPt[0] = px;
                outPt[1] = py;
                outPt[2] = pz;
            }
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
