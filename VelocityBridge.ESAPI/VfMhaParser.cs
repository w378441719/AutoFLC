using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoFLC
{
    /// <summary>
    /// Parser for Plastimatch vector-field files (.mha / .mhd), the vf_out
    /// product of "plastimatch register", producing an engine-agnostic
    /// DvfField.
    ///
    /// Conventions (validated against Plastimatch 1.9.0 vf output during
    /// development):
    ///   - DimSize is ordered (nx, ny, nz) with x varying fastest and three
    ///     interleaved float32 components per voxel
    ///     -> directly loads into float[nz, ny, nx, 3].
    ///   - Components are (dx, dy, dz) in mm, LPS, in DICOM patient
    ///     coordinates; Offset equals the fixed (expiration) CT origin.
    ///   - Direction semantics match the Velocity BDF: the vector at a fixed
    ///     (expiration) voxel addresses the corresponding moving
    ///     (inspiration) position, p_insp = p + v(p); the pre-matrix is the
    ///     identity.
    /// </summary>
    public static class VfMhaParser
    {
        /// <summary>
        /// Quick sniff: does the path look like a MetaIO vector field?
        /// Used by the auto-detecting DVF entry.
        /// </summary>
        public static bool LooksLikeVfMha(string path)
        {
            string ext = Path.GetExtension(path);
            if (!string.Equals(ext, ".mha", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".mhd", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                Dictionary<string, string> header;
                return TryReadHeader(path, out header);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parse a Plastimatch vf .mha/.mhd into a DvfField.
        /// </summary>
        /// <param name="path">Vector field file.</param>
        /// <param name="fallbackOrigin">
        /// Origin (ox, oy, oz) used when the header carries none; callers
        /// pass the reference CT origin. May be null when the header origin
        /// is known to exist.</param>
        public static DvfField Parse(string path, double[] fallbackOrigin)
        {
            Dictionary<string, string> header;
            if (!TryReadHeader(path, out header))
                throw new InvalidDataException("Not a MetaIO header file: " + path);

            int nDims = GetInt(header, "NDims", 0);
            if (nDims != 3)
                throw new InvalidDataException(string.Format(
                    "Unsupported vector field NDims={0} (expected 3): {1}", nDims, path));

            int channels = GetInt(header, "ElementNumberOfChannels", 1);
            if (channels != 3)
                throw new InvalidDataException(string.Format(
                    "The MetaIO file has {0} element channel(s); a Plastimatch vector field has 3. " +
                    "This looks like a scalar image (e.g. the CT/ROI .mha), not a vf_out file: {1}",
                    channels, path));

            string elementType = Get(header, "ElementType");
            if (!string.IsNullOrEmpty(elementType) &&
                !string.Equals(elementType, "MET_FLOAT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(string.Format(
                    "Unsupported ElementType '{0}' (expected MET_FLOAT): {1}", elementType, path));

            if (GetInt(header, "CompressedData", 0) == 1)
                throw new InvalidDataException(
                    "Compressed MetaIO vector fields are not supported (re-run with an uncompressed vf_out): " + path);

            if (GetInt(header, "BinaryDataByteOrderMSB", 0) == 1)
                throw new InvalidDataException(
                    "Big-endian (MSB) MetaIO data is not supported: " + path);

            int[] dims = GetInts(header, "DimSize");
            double[] spacing = GetDoubles(header, "ElementSpacing");
            if (dims == null || dims.Length != 3 || spacing == null || spacing.Length != 3)
                throw new InvalidDataException("DimSize/ElementSpacing missing or malformed: " + path);
            int nx = dims[0], ny = dims[1], nz = dims[2];

            // Origin: Offset is the canonical MetaIO key; Origin/Position are
            // legacy synonyms some writers emit.
            double[] origin = GetDoubles(header, "Offset")
                ?? GetDoubles(header, "Origin")
                ?? GetDoubles(header, "Position");
            if (origin == null || origin.Length != 3)
            {
                if (fallbackOrigin == null || fallbackOrigin.Length < 3)
                    throw new InvalidDataException(
                        "The vector field header carries no origin and no CT fallback was given: " + path);
                origin = fallbackOrigin;
            }

            long expectedFloats = (long)nx * ny * nz * 3;
            float[,,,] dvf = new float[nz, ny, nx, 3];
            ReadRawFloats(path, header, expectedFloats, dvf);

            DvfField field = new DvfField();
            field.Vectors = dvf;
            field.Nx = nx;
            field.Ny = ny;
            field.Nz = nz;
            field.Sx = spacing[0];
            field.Sy = spacing[1];
            field.Sz = spacing[2];
            field.Ox = origin[0];
            field.Oy = origin[1];
            field.Oz = origin[2];
            field.PreMatrix = null; // identity: p_insp = p + v(p), BDF semantics
            field.SourceType = "VF";
            field.SourcePath = path;
            return field;
        }

        /// <summary>
        /// Read the header key/value pairs of a MetaIO file. Returns false
        /// when the file does not carry a parseable header.
        /// </summary>
        private static bool TryReadHeader(string path, out Dictionary<string, string> header)
        {
            header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader sr = new StreamReader(fs, Encoding.ASCII, false, 4096))
            {
                string line = sr.ReadLine();
                if (line == null || !line.TrimStart().StartsWith("ObjectType", StringComparison.OrdinalIgnoreCase))
                    return false;

                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    header[key] = value;
                    if (string.Equals(key, "ElementDataFile", StringComparison.OrdinalIgnoreCase))
                        return header.Count > 1;
                }
                return false;
            }
        }

        /// <summary>
        /// Stream the raw float block after the header into the DVF array.
        /// The on-disk order (x fastest, channels interleaved) matches the
        /// layout of float[nz, ny, nx, 3] exactly, so chunked block copies
        /// suffice without a full-size byte buffer.
        /// </summary>
        private static void ReadRawFloats(string path, Dictionary<string, string> header,
            long expectedFloats, float[,,,] dvf)
        {
            string dataFile = Get(header, "ElementDataFile");
            string rawPath = path;
            long dataOffset = 0;

            if (!string.Equals(dataFile, "LOCAL", StringComparison.OrdinalIgnoreCase))
            {
                // .mhd header pointing at a sibling raw file.
                rawPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), dataFile);
                if (!File.Exists(rawPath))
                    throw new InvalidDataException(string.Format(
                        "ElementDataFile '{0}' not found next to the header: {1}", dataFile, path));
            }
            else
            {
                dataOffset = HeaderEndOffset(path);
            }

            long expectedBytes = expectedFloats * 4;
            using (FileStream fs = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length - dataOffset < expectedBytes)
                    throw new InvalidDataException(string.Format(
                        "Vector field '{0}' is truncated: expected {1} raw bytes, got {2}.",
                        path, expectedBytes, fs.Length - dataOffset));

                fs.Seek(dataOffset, SeekOrigin.Begin);
                byte[] chunk = new byte[4 * 1024 * 1024];
                long copied = 0;
                while (copied < expectedBytes)
                {
                    int want = (int)Math.Min(chunk.Length, expectedBytes - copied);
                    int read = fs.Read(chunk, 0, want);
                    if (read <= 0)
                        throw new InvalidDataException("Unexpected end of vector field data: " + path);
                    Buffer.BlockCopy(chunk, 0, dvf, (int)copied, read);
                    copied += read;
                }
            }
        }

        /// <summary>
        /// Byte offset of the first byte after the "ElementDataFile" line
        /// (LOCAL layout: the raw block follows the header).
        /// </summary>
        private static long HeaderEndOffset(string path)
        {
            byte[] probe = new byte[65536];
            int probeLen;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                probeLen = fs.Read(probe, 0, probe.Length);
            }

            string text = Encoding.ASCII.GetString(probe, 0, probeLen);
            int keyIdx = text.IndexOf("ElementDataFile", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0)
                throw new InvalidDataException("ElementDataFile missing from the MetaIO header: " + path);

            int lineEnd = text.IndexOf('\n', keyIdx);
            if (lineEnd < 0)
                throw new InvalidDataException("Malformed MetaIO header (unterminated ElementDataFile line): " + path);
            return lineEnd + 1; // the byte right after the newline
        }

        private static string Get(Dictionary<string, string> header, string key)
        {
            string v;
            return header.TryGetValue(key, out v) ? v : null;
        }

        private static int GetInt(Dictionary<string, string> header, string key, int def)
        {
            string v = Get(header, key);
            if (string.IsNullOrEmpty(v)) return def;
            int r;
            if (int.TryParse(v.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                return r;
            return def;
        }

        private static int[] GetInts(Dictionary<string, string> header, string key)
        {
            double[] d = GetDoubles(header, key);
            if (d == null) return null;
            int[] r = new int[d.Length];
            for (int i = 0; i < d.Length; i++)
                r[i] = (int)d[i];
            return r;
        }

        private static double[] GetDoubles(Dictionary<string, string> header, string key)
        {
            string v = Get(header, key);
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double[] r = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]))
                    return null;
            }
            return r;
        }
    }
}
