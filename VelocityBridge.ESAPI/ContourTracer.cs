using System;
using System.Collections.Generic;

namespace AutoFLC
{
    /// <summary>
    /// Marching-squares contour tracing with Douglas-Peucker simplification.
    /// </summary>
    public static class ContourTracer
    {
        /// <summary>
        /// Trace the level=0.5 iso-contours of a 2D boolean mask.
        /// Returns contours as lists of (row, col) float vertices.
        /// </summary>
        public static List<List<float[]>> FindContours(bool[,] mask)
        {
            int ny = mask.GetLength(0);
            int nx = mask.GetLength(1);

            // Marching-squares cell convention. Corners are labelled
            //
            //   tl (r+1,c) --- tr (r+1,c+1)
            //       |              |
            //       L    cell      R
            //       |              |
            //   bl (r, c)  --- br (r, c+1)
            //
            // Edge midpoint offsets within the cell:
            //   B = ( 0,  0.5)    R = ( 0.5,  1)
            //   T = ( 1,  0.5)    L = ( 0.5,  0)
            //
            // The case index is (tl << 3) | (tr << 2) | (br << 1) | bl.
            // Cases 0 and 15 produce no segment; cases 5 and 10 are saddles
            // producing two segments; all others produce one straight segment.

            List<Tuple<float, float, float, float>> segments = new List<Tuple<float, float, float, float>>();

            for (int row = 0; row < ny - 1; row++)
            {
                for (int col = 0; col < nx - 1; col++)
                {
                    int bl = mask[row, col] ? 1 : 0;
                    int br = mask[row, col + 1] ? 1 : 0;
                    int tr = mask[row + 1, col + 1] ? 1 : 0;
                    int tl = mask[row + 1, col] ? 1 : 0;

                    int caseIndex = (tl << 3) | (tr << 2) | (br << 1) | bl;

                    switch (caseIndex)
                    {
                        case 1:  // 0001 BL: bottom to left
                            AddSegment(segments, row, col + 0.5f, row + 0.5f, col);
                            break;
                        case 2:  // 0010 BR: bottom to right
                            AddSegment(segments, row, col + 0.5f, row + 0.5f, col + 1f);
                            break;
                        case 3:  // 0011 BL+BR: right to left
                            AddSegment(segments, row + 0.5f, col + 1f, row + 0.5f, col);
                            break;
                        case 4:  // 0100 TR: right to top
                            AddSegment(segments, row + 0.5f, col + 1f, row + 1f, col + 0.5f);
                            break;
                        case 5:  // 0101 BL+TR saddle: bottom-top + left-right
                            AddSegment(segments, row, col + 0.5f, row + 1f, col + 0.5f);
                            AddSegment(segments, row + 0.5f, col, row + 0.5f, col + 1f);
                            break;
                        case 6:  // 0110 BR+TR: bottom to top
                            AddSegment(segments, row, col + 0.5f, row + 1f, col + 0.5f);
                            break;
                        case 7:  // 0111 BL+BR+TR: top to left
                            AddSegment(segments, row + 1f, col + 0.5f, row + 0.5f, col);
                            break;
                        case 8:  // 1000 TL: top to left
                            AddSegment(segments, row + 1f, col + 0.5f, row + 0.5f, col);
                            break;
                        case 9:  // 1001 BL+TL: bottom to top
                            AddSegment(segments, row, col + 0.5f, row + 1f, col + 0.5f);
                            break;
                        case 10: // 1010 BR+TL saddle: bottom-right + left-top
                            AddSegment(segments, row, col + 0.5f, row + 0.5f, col + 1f);
                            AddSegment(segments, row + 0.5f, col, row + 1f, col + 0.5f);
                            break;
                        case 11: // 1011 BL+BR+TL: right to top
                            AddSegment(segments, row + 0.5f, col + 1f, row + 1f, col + 0.5f);
                            break;
                        case 12: // 1100 TR+TL: right to left
                            AddSegment(segments, row + 0.5f, col + 1f, row + 0.5f, col);
                            break;
                        case 13: // 1101 BL+TR+TL: bottom to right
                            AddSegment(segments, row, col + 0.5f, row + 0.5f, col + 1f);
                            break;
                        case 14: // 1110 BR+TR+TL: bottom to left
                            AddSegment(segments, row, col + 0.5f, row + 0.5f, col);
                            break;
                        // cases 0 and 15: no segments
                    }
                }
            }

            return ChainSegments(segments);
        }

        /// <summary>
        /// Douglas-Peucker polyline simplification.
        /// </summary>
        public static List<float[]> SimplifyPolyline(List<float[]> polyline, double tolerance)
        {
            if (polyline == null || polyline.Count <= 2)
                return new List<float[]>(polyline);

            return DouglasPeucker(polyline, 0, polyline.Count - 1, tolerance);
        }

        /// <summary>
        /// Compute the polygon signed area using the shoelace formula.
        /// Positive for counter-clockwise, negative for clockwise.
        /// </summary>
        public static double PolygonArea(List<float[]> polyline)
        {
            if (polyline == null || polyline.Count < 3)
                return 0.0;

            double area = 0.0;
            int n = polyline.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                area += polyline[i][1] * polyline[j][0];
                area -= polyline[j][1] * polyline[i][0];
            }
            return Math.Abs(area) / 2.0;
        }

        /// <summary>
        /// Convert a contour from (row, col) pixel coordinates to DICOM patient
        /// coordinates using the slice geometry of the reference image.
        /// </summary>
        public static List<float[]> ToWorldCoordinates(List<float[]> contour, double[] ipp, double[] rowDir, double[] colDir, double rowSpacing, double colSpacing)
        {
            List<float[]> world = new List<float[]>(contour.Count);
            foreach (float[] pt in contour)
            {
                float row = pt[0];
                float col = pt[1];
                double wx = ipp[0] + row * rowSpacing * rowDir[0] + col * colSpacing * colDir[0];
                double wy = ipp[1] + row * rowSpacing * rowDir[1] + col * colSpacing * colDir[1];
                double wz = ipp[2] + row * rowSpacing * rowDir[2] + col * colSpacing * colDir[2];
                world.Add(new float[] { (float)wx, (float)wy, (float)wz });
            }
            return world;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private static void AddSegment(List<Tuple<float, float, float, float>> segments,
            float r1, float c1, float r2, float c2)
        {
            segments.Add(Tuple.Create(r1, c1, r2, c2));
        }

        private static List<List<float[]>> ChainSegments(List<Tuple<float, float, float, float>> segments)
        {
            // Build an endpoint index that maps each endpoint to its segments.
            Dictionary<Tuple<float, float>, List<int>> endpointMap = new Dictionary<Tuple<float, float>, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                Tuple<float, float, float, float> s = segments[i];
                Tuple<float, float> p1 = Tuple.Create(s.Item1, s.Item2);
                Tuple<float, float> p2 = Tuple.Create(s.Item3, s.Item4);

                AddEndpoint(endpointMap, p1, i);
                AddEndpoint(endpointMap, p2, i);
            }

            bool[] used = new bool[segments.Count];
            List<List<float[]>> contours = new List<List<float[]>>();

            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;

                List<float[]> contour = new List<float[]>();
                Tuple<float, float, float, float> s = segments[i];
                float nextR = s.Item3;
                float nextC = s.Item4;

                contour.Add(new float[] { s.Item1, s.Item2 });
                contour.Add(new float[] { nextR, nextC });
                used[i] = true;

                // Walk forward from the end of the current polyline.
                bool extended;
                do
                {
                    extended = false;
                    Tuple<float, float> endPt = Tuple.Create(nextR, nextC);
                    if (endpointMap.ContainsKey(endPt))
                    {
                        foreach (int idx in endpointMap[endPt])
                        {
                            if (used[idx]) continue;
                            Tuple<float, float, float, float> seg = segments[idx];

                            float aR = 0, aC = 0;
                            if (NearlyEqual(seg.Item1, nextR) && NearlyEqual(seg.Item2, nextC))
                            {
                                aR = seg.Item3; aC = seg.Item4;
                            }
                            else if (NearlyEqual(seg.Item3, nextR) && NearlyEqual(seg.Item4, nextC))
                            {
                                aR = seg.Item1; aC = seg.Item2;
                            }
                            else
                            {
                                continue;
                            }

                            used[idx] = true;
                            contour.Add(new float[] { aR, aC });
                            nextR = aR;
                            nextC = aC;
                            extended = true;
                            break;
                        }
                    }
                } while (extended);

                if (contour.Count >= 3)
                    contours.Add(contour);
            }

            return contours;
        }

        private static void AddEndpoint(Dictionary<Tuple<float, float>, List<int>> map, Tuple<float, float> pt, int idx)
        {
            if (!map.ContainsKey(pt))
                map[pt] = new List<int>();
            if (!map[pt].Contains(idx))
                map[pt].Add(idx);
        }

        private static bool NearlyEqual(float a, float b)
        {
            return Math.Abs(a - b) < 1e-4f;
        }

        private static double Distance(float r1, float c1, float r2, float c2)
        {
            double dr = r1 - r2;
            double dc = c1 - c2;
            return Math.Sqrt(dr * dr + dc * dc);
        }

        private static List<float[]> DouglasPeucker(List<float[]> points, int start, int end, double tolerance)
        {
            List<float[]> result = new List<float[]>();

            if (end - start <= 1)
            {
                result.Add(points[start]);
                result.Add(points[end]);
                return result;
            }

            // Find the point farthest from the (start, end) chord.
            double maxDist = 0.0;
            int maxIdx = start;

            float[] p1 = points[start];
            float[] p2 = points[end];

            for (int i = start + 1; i < end; i++)
            {
                double d = PerpendicularDistance(points[i], p1, p2);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxDist > tolerance)
            {
                List<float[]> left = DouglasPeucker(points, start, maxIdx, tolerance);
                List<float[]> right = DouglasPeucker(points, maxIdx, end, tolerance);
                for (int i = 0; i < left.Count - 1; i++)
                    result.Add(left[i]);
                for (int i = 0; i < right.Count; i++)
                    result.Add(right[i]);
            }
            else
            {
                result.Add(p1);
                result.Add(p2);
            }

            return result;
        }

        private static double PerpendicularDistance(float[] pt, float[] lineStart, float[] lineEnd)
        {
            double dx = lineEnd[0] - lineStart[0];
            double dy = lineEnd[1] - lineStart[1];
            double len2 = dx * dx + dy * dy;
            if (len2 == 0.0)
                return Distance(pt[0], pt[1], lineStart[0], lineStart[1]);

            double t = ((pt[0] - lineStart[0]) * dx + (pt[1] - lineStart[1]) * dy) / len2;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double projX = lineStart[0] + t * dx;
            double projY = lineStart[1] + t * dy;
            return Distance(pt[0], pt[1], (float)projX, (float)projY);
        }
    }
}
