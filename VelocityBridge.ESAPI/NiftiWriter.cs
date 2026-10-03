using System;
using System.IO;
using System.IO.Compression;

namespace AutoFLC
{
    /// <summary>
    /// Minimal NIfTI-1 writer for 3D float volumes. Supports both uncompressed
    /// (.nii) and gzip-compressed (.nii.gz) output; the extension selects the mode.
    /// </summary>
    public static class NiftiWriter
    {
        /// <summary>
        /// Write a 3D float array as a NIfTI-1 file.
        /// </summary>
        /// <param name="path">Output file path; a trailing ".gz" enables gzip compression.</param>
        /// <param name="data">3D array float[z, y, x].</param>
        /// <param name="spacing">Voxel spacing in mm (sx, sy, sz).</param>
        public static void Write(string path, float[,,] data, double[] spacing)
        {
            int nz = data.GetLength(0);
            int ny = data.GetLength(1);
            int nx = data.GetLength(2);

            byte[] header = BuildHeader(nz, ny, nx, spacing);

            byte[] voxels = new byte[nz * ny * nx * sizeof(float)];
            Buffer.BlockCopy(data, 0, voxels, 0, voxels.Length);

            string lower = path.ToLowerInvariant();
            bool gz = lower.EndsWith(".gz");

            // Write to a temporary file and move it into place so an aborted
            // write does not leave a partial file at the destination.
            string tmpPath = path + ".tmp";
            using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Stream outStream = fs;
                GZipStream gzStream = null;
                if (gz)
                {
                    gzStream = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: false);
                    outStream = gzStream;
                }

                try
                {
                    outStream.Write(header, 0, header.Length);

                    // 4-byte extension indicator (zeros = no extension).
                    byte[] ext = new byte[4];
                    outStream.Write(ext, 0, ext.Length);

                    outStream.Write(voxels, 0, voxels.Length);
                }
                finally
                {
                    if (gzStream != null)
                        gzStream.Dispose();
                }
            }

            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmpPath, path);
        }

        private static byte[] BuildHeader(int nz, int ny, int nx, double[] spacing)
        {
            byte[] header = new byte[348];

            // sizeof_hdr (int32 at offset 0)
            WriteInt32(header, 0, 348);

            // dim (8 x int16 at offset 40): dim[0]=3, dim[1]=nx, dim[2]=ny, dim[3]=nz
            WriteInt16(header, 40, 3);
            WriteInt16(header, 42, (short)nx);
            WriteInt16(header, 44, (short)ny);
            WriteInt16(header, 46, (short)nz);

            // datatype (int16 at offset 70): 16 = float32
            WriteInt16(header, 70, 16);

            // bitpix (int16 at offset 72): 32
            WriteInt16(header, 72, 32);

            // pixdim (8 x float32 at offset 76): pixdim[1]=sx, pixdim[2]=sy, pixdim[3]=sz
            WriteFloat32(header, 80, (float)spacing[0]);
            WriteFloat32(header, 84, (float)spacing[1]);
            WriteFloat32(header, 88, (float)spacing[2]);

            // vox_offset (float32 at offset 108): 352 (348 header + 4 extension bytes)
            WriteFloat32(header, 108, 352.0f);

            // scl_slope (float32 at offset 112): 1.0
            WriteFloat32(header, 112, 1.0f);

            // scl_inter (float32 at offset 116): 0.0
            WriteFloat32(header, 116, 0.0f);

            // xyzt_units (byte at offset 123): 2 (mm = NIFTI_UNITS_MM)
            header[123] = 2;

            // magic (4 bytes at offset 344): "n+1\0"
            header[344] = (byte)'n';
            header[345] = (byte)'+';
            header[346] = (byte)'1';
            header[347] = 0;

            return header;
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteFloat32(byte[] buffer, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buffer[offset] = bytes[0];
            buffer[offset + 1] = bytes[1];
            buffer[offset + 2] = bytes[2];
            buffer[offset + 3] = bytes[3];
        }
    }
}
