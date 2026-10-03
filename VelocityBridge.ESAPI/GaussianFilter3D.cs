using System;

namespace AutoFLC
{
    /// <summary>
    /// Separable 3D Gaussian filter using 1D kernel convolution along each axis.
    /// </summary>
    public static class GaussianFilter3D
    {
        /// <summary>
        /// Apply 3D Gaussian smoothing to a float array.
        /// </summary>
        /// <param name="input">Input array of shape [z, y, x].</param>
        /// <param name="sigmaMm">Isotropic Gaussian sigma in mm.</param>
        /// <param name="spacing">Voxel spacing in mm (sx, sy, sz); spacing[0] maps to the
        /// x-axis (array dimension 2), spacing[2] to the z-axis (array dimension 0).</param>
        /// <returns>Smoothed array of the same shape.</returns>
        public static float[,,] Smooth(float[,,] input, double sigmaMm, double[] spacing)
        {
            int nz = input.GetLength(0);
            int ny = input.GetLength(1);
            int nx = input.GetLength(2);

            // Convert the millimetre sigma to voxel units per axis.
            double sigmaZ = sigmaMm / spacing[2];
            double sigmaY = sigmaMm / spacing[1];
            double sigmaX = sigmaMm / spacing[0];

            float[,,] buffer = new float[nz, ny, nx];
            Array.Copy(input, buffer, input.Length);

            buffer = Convolve1D(buffer, nz, ny, nx, 0, sigmaZ);
            buffer = Convolve1D(buffer, ny, nz, nx, 1, sigmaY);
            buffer = Convolve1D(buffer, nx, nz, ny, 2, sigmaX);

            return buffer;
        }

        /// <summary>
        /// 1D convolution along a single axis.
        /// </summary>
        /// <param name="input">Input array [nz, ny, nx].</param>
        /// <param name="dimLen">Length along the convolution axis.</param>
        /// <param name="len1">Length of dimension 1 (y).</param>
        /// <param name="len2">Length of dimension 2 (x).</param>
        /// <param name="axis">Axis index (0=z, 1=y, 2=x).</param>
        /// <param name="sigma">Voxel-space sigma for the kernel.</param>
        private static float[,,] Convolve1D(float[,,] input, int dimLen, int len1, int len2, int axis, double sigma)
        {
            int nz = input.GetLength(0);
            int ny = input.GetLength(1);
            int nx = input.GetLength(2);

            if (sigma <= 0)
                return input;

            int radius = (int)Math.Ceiling(4.0 * sigma);
            if (radius < 1) radius = 1;
            int kSize = 2 * radius + 1;
            double[] kernel = new double[kSize];
            double sum = 0.0;
            for (int i = -radius; i <= radius; i++)
            {
                double val = Math.Exp(-0.5 * (i * i) / (sigma * sigma));
                kernel[i + radius] = val;
                sum += val;
            }
            for (int i = 0; i < kSize; i++)
                kernel[i] /= sum;

            float[,,] output = new float[nz, ny, nx];

            for (int i1 = 0; i1 < len1; i1++)
            {
                for (int i2 = 0; i2 < len2; i2++)
                {
                    for (int d = 0; d < dimLen; d++)
                    {
                        double acc = 0.0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int idx = d + k;
                            if (idx < 0) idx = 0;
                            if (idx >= dimLen) idx = dimLen - 1;

                            float val;
                            if (axis == 0)
                                val = input[idx, i1, i2];
                            else if (axis == 1)
                                val = input[i1, idx, i2];
                            else
                                val = input[i1, i2, idx];

                            acc += val * kernel[k + radius];
                        }

                        if (axis == 0)
                            output[d, i1, i2] = (float)acc;
                        else if (axis == 1)
                            output[i1, d, i2] = (float)acc;
                        else
                            output[i1, i2, d] = (float)acc;
                    }
                }
            }

            return output;
        }
    }
}
