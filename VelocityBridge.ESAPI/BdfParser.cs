using System;
using System.IO;

namespace AutoFLC
{
    /// <summary>
    /// Header information parsed from a Velocity BDF (Binary Deformation Field) file.
    /// </summary>
    public class BdfHeader
    {
        public int Nx { get; set; }
        public int Ny { get; set; }
        public int Nz { get; set; }
        public double Sx { get; set; }
        public double Sy { get; set; }
        public double Sz { get; set; }
    }

    /// <summary>
    /// Parses Velocity BDF binary deformation field files.
    ///
    /// File format (little-endian):
    ///   3 x int32  : nx, ny, nz
    ///   3 x float32: sx, sy, sz  (voxel spacing in mm)
    ///   float32[nz * ny * nx * 3] : DVF triples (dx, dy, dz) in mm.
    /// </summary>
    public static class BdfParser
    {
        /// <summary>
        /// Parse a BDF file and return the header and DVF array.
        /// </summary>
        /// <param name="path">Path to the .bdf file.</param>
        /// <param name="header">Output BDF header.</param>
        /// <param name="dvf">Output DVF array of shape [nz, ny, nx, 3].</param>
        public static void Parse(string path, out BdfHeader header, out float[,,,] dvf)
        {
            byte[] data = File.ReadAllBytes(path);

            if (data.Length < 24)
                throw new InvalidDataException(
                    string.Format("BDF file '{0}' is shorter than the 24-byte header.", path));

            // Velocity BDF is little-endian on disk; decode explicitly so the
            // parser is not affected by host byte order.
            int nx = ReadInt32LE(data, 0);
            int ny = ReadInt32LE(data, 4);
            int nz = ReadInt32LE(data, 8);

            float sx = ReadSingleLE(data, 12);
            float sy = ReadSingleLE(data, 16);
            float sz = ReadSingleLE(data, 20);

            header = new BdfHeader
            {
                Nx = nx,
                Ny = ny,
                Nz = nz,
                Sx = sx,
                Sy = sy,
                Sz = sz
            };

            long expectedFloats = (long)nx * ny * nz * 3;
            long expectedBytes = 24 + expectedFloats * 4;
            if (data.Length < expectedBytes)
                throw new InvalidDataException(string.Format(
                    "BDF file '{0}' is truncated: expected {1} bytes, got {2}.",
                    path, expectedBytes, data.Length));

            dvf = new float[nz, ny, nx, 3];

            int offset = 24;
            int floatSize = sizeof(float);
            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        dvf[z, y, x, 0] = ReadSingleLE(data, offset); offset += floatSize;
                        dvf[z, y, x, 1] = ReadSingleLE(data, offset); offset += floatSize;
                        dvf[z, y, x, 2] = ReadSingleLE(data, offset); offset += floatSize;
                    }
                }
            }
        }

        private static int ReadInt32LE(byte[] data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }

        private static float ReadSingleLE(byte[] data, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToSingle(data, offset);
            }

            byte[] buf = new byte[4];
            buf[0] = data[offset + 3];
            buf[1] = data[offset + 2];
            buf[2] = data[offset + 1];
            buf[3] = data[offset];
            return BitConverter.ToSingle(buf, 0);
        }
    }
}
