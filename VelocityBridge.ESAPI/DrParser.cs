using System;
using System.IO;
using Dicom;

namespace AutoFLC
{
    /// <summary>
    /// Parser for DICOM Deformable Spatial Registration (DR) objects exported
    /// by Eclipse DIR, producing an engine-agnostic DvfField.
    ///
    /// Conventions (validated against the reference DICOM DR exports used
    /// during development):
    ///   - Vector Grid Data layout: x varies fastest, then y, then z, with
    ///     (dx, dy, dz) triplets interleaved -> directly loads into
    ///     float[nz, ny, nx, 3].
    ///   - Direction semantics: the DR frame of reference is the fixed
    ///     (expiration) image; vectors map the target point to the source
    ///     (inspiration) frame via p_insp = M_pre * (p_exp + v(p_exp)), where
    ///     M_pre is the embedded pre-deformation rigid matrix (identity for
    ///     Velocity BDF exports).
    ///   - A non-identity post-deformation matrix is rejected explicitly
    ///     (combined rigid exports are not supported; re-export the
    ///     deformable registration only).
    /// </summary>
    public static class DrParser
    {
        private const double IdentityTolerance = 1e-6;

        // DICOM PS3.4: Deformable Spatial Registration Storage.
        private const string DeformableRegistrationSopClass =
            "1.2.840.10008.5.1.4.1.1.66.3";

        /// <summary>
        /// Quick sniff: does the file look like a DICOM Deformable Spatial
        /// Registration object? Used by the auto-detecting DVF file entry.
        /// </summary>
        public static bool LooksLikeDr(string path)
        {
            try
            {
                DicomFile df = DicomFile.Open(path, FileReadOption.Default);
                string sopClass = df.Dataset.Get<string>(DicomTag.SOPClassUID, null);
                return sopClass == DeformableRegistrationSopClass;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parse a DICOM DR object into a DvfField.
        /// </summary>
        public static DvfField Parse(string path)
        {
            DicomFile df = DicomFile.Open(path, FileReadOption.ReadAll);
            DicomDataset ds = df.Dataset;

            string sopClass = ds.Get<string>(DicomTag.SOPClassUID, null);
            if (sopClass != DeformableRegistrationSopClass)
                throw new InvalidDataException(string.Format(
                    "Not a Deformable Spatial Registration object (SOPClassUID={0}): {1}",
                    sopClass, path));

            DicomSequence regSeq = ds.Contains(DicomTag.DeformableRegistrationSequence)
                ? ds.Get<DicomSequence>(DicomTag.DeformableRegistrationSequence) : null;
            if (regSeq == null || regSeq.Items.Count == 0)
                throw new InvalidDataException("Deformable Registration Sequence is missing: " + path);
            if (regSeq.Items.Count > 1)
                throw new InvalidDataException(string.Format(
                    "DR object contains {0} registration items; combined/multiple registrations are not "
                    + "supported. Export the deformable registration only.", regSeq.Items.Count));

            DicomDataset reg = regSeq.Items[0];

            DicomSequence gridSeq = reg.Contains(DicomTag.DeformableRegistrationGridSequence)
                ? reg.Get<DicomSequence>(DicomTag.DeformableRegistrationGridSequence) : null;
            if (gridSeq == null || gridSeq.Items.Count == 0)
                throw new InvalidDataException("Deformable Registration Grid Sequence is missing: " + path);
            DicomDataset grid = gridSeq.Items[0];

            // Grid Dimensions are ordered (X, Y, Z) per DICOM PS3.3 C.20.3;
            // Grid Resolution follows the same order.
            uint[] dims = grid.Get<uint[]>(DicomTag.GridDimensions);
            double[] res = grid.Get<double[]>(DicomTag.GridResolution);
            if (dims == null || dims.Length != 3 || res == null || res.Length != 3)
                throw new InvalidDataException("Grid Dimensions/Resolution missing or malformed: " + path);
            int nx = (int)dims[0], ny = (int)dims[1], nz = (int)dims[2];

            double[] ipp = grid.Get<double[]>(DicomTag.ImagePositionPatient);
            double[] iop = grid.Get<double[]>(DicomTag.ImageOrientationPatient);
            if (ipp == null || ipp.Length != 3)
                throw new InvalidDataException("Grid ImagePositionPatient missing: " + path);
            if (iop != null && iop.Length == 6)
            {
                if (Math.Abs(iop[0] - 1) > IdentityTolerance || Math.Abs(iop[4] - 1) > IdentityTolerance ||
                    Math.Abs(iop[1]) > IdentityTolerance || Math.Abs(iop[2]) > IdentityTolerance ||
                    Math.Abs(iop[3]) > IdentityTolerance || Math.Abs(iop[5]) > IdentityTolerance)
                    throw new InvalidDataException(
                        "Unsupported grid orientation (ImageOrientationPatient is not axis-aligned identity): " + path);
            }

            // Vector Grid Data: OF in explicit VR (float[]) or OB bytes in
            // implicit VR (the Eclipse/ARIA export). Both are little-endian
            // float32 triples.
            long expectedFloats = (long)nx * ny * nz * 3;
            float[] vecs = ReadVectorData(grid, expectedFloats, path);

            float[,,,] dvf = new float[nz, ny, nx, 3];
            // Layout [z, y, x, c] with c and x fastest matches the on-disk
            // order exactly, so a block copy suffices.
            Buffer.BlockCopy(vecs, 0, dvf, 0, (int)(expectedFloats * 4));

            double[,] pre = ReadMatrix(reg, DicomTag.PreDeformationMatrixRegistrationSequence, "Pre");
            double[,] post = ReadMatrix(reg, DicomTag.PostDeformationMatrixRegistrationSequence, "Post");
            if (!IsIdentity(post))
                throw new InvalidDataException(
                    "The DR object carries a non-identity post-deformation matrix (combined export). "
                    + "Re-export the deformable registration only and keep the rigid step embedded as the pre matrix.");

            DvfField field = new DvfField();
            field.Vectors = dvf;
            field.Nx = nx;
            field.Ny = ny;
            field.Nz = nz;
            field.Sx = res[0];
            field.Sy = res[1];
            field.Sz = res[2];
            field.Ox = ipp[0];
            field.Oy = ipp[1];
            field.Oz = ipp[2];
            field.PreMatrix = pre;
            field.SourceType = "DR";
            field.SourcePath = path;
            return field;
        }

        /// <summary>
        /// Check that the DR grid (after pre-matrix rotation, which is
        /// near-rigid here) covers the given CT z-extent. Returns the number
        /// of uncovered slices; callers log a warning when it is > 0.
        /// </summary>
        public static int CountSlicesOutsideGrid(DvfField field, double ctZFirst, double ctZLast)
        {
            double gz0 = field.Oz;
            double gz1 = field.Oz + (field.Nz - 1) * field.Sz;
            double lo = Math.Min(gz0, gz1);
            double hi = Math.Max(gz0, gz1);
            int outside = 0;
            for (double z = ctZFirst; z <= ctZLast + 1e-6; z += Math.Abs(field.Sz))
            {
                if (z < lo - 1e-3 || z > hi + 1e-3) outside++;
            }
            return outside;
        }

        private static float[] ReadVectorData(DicomDataset grid, long expectedFloats, string path)
        {
            // Explicit VR: OF element exposes float[] directly.
            try
            {
                float[] floats = grid.Get<float[]>(DicomTag.VectorGridData);
                if (floats != null && floats.LongLength == expectedFloats)
                    return floats;
            }
            catch { }

            // Implicit VR: stored as OB bytes.
            byte[] raw = grid.Get<byte[]>(DicomTag.VectorGridData);
            if (raw == null || raw.LongLength != expectedFloats * 4)
                throw new InvalidDataException(string.Format(
                    "Vector Grid Data size mismatch: expected {0} float32 values in {1}",
                    expectedFloats, path));
            float[] vecs = new float[expectedFloats];
            Buffer.BlockCopy(raw, 0, vecs, 0, raw.Length);
            return vecs;
        }

        private static double[,] ReadMatrix(DicomDataset reg, DicomTag seqTag, string name)
        {
            DicomSequence seq = reg.Contains(seqTag) ? reg.Get<DicomSequence>(seqTag) : null;
            if (seq == null || seq.Items.Count == 0)
                return Identity4x4();

            double[] m = seq.Items[0].Get<double[]>(DicomTag.FrameOfReferenceTransformationMatrix);
            if (m == null || m.Length != 16)
                return Identity4x4();

            double[,] mat = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    mat[r, c] = m[r * 4 + c];
            return mat;
        }

        private static double[,] Identity4x4()
        {
            double[,] m = new double[4, 4];
            for (int i = 0; i < 4; i++) m[i, i] = 1.0;
            return m;
        }

        private static bool IsIdentity(double[,] m)
        {
            if (m == null) return true;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    double expect = (r == c) ? 1.0 : 0.0;
                    if (Math.Abs(m[r, c] - expect) > IdentityTolerance)
                        return false;
                }
            return true;
        }
    }
}
