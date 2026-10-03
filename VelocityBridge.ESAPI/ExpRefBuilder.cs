using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dicom;

namespace AutoFLC
{
    /// <summary>Result of an ExpRef (expiration reference copy) build.</summary>
    public class ExpRefResult
    {
        public string NewSeriesUid { get; set; }
        public string NewFrameOfReferenceUid { get; set; }
        public string StudyUid { get; set; }
        public List<string> Files { get; set; }
        public int InputSlices { get; set; }
        public int OutputSlices { get; set; }
        public double TargetThickness { get; set; }
        public bool WasUniform { get; set; }        // keep-if-uniform branch
        public bool Resampled { get; set; }
        public double MeanAbsDeltaHu { get; set; }  // self-check on common z positions
        public double MaxAbsDeltaHu { get; set; }
        /// <summary>Remapped RTSTRUCT path (null when no source structure set).</summary>
        public string RtstructPath { get; set; }
        /// <summary>Output z grid (for contour z snapping).</summary>
        public List<double> OutputZ { get; set; }
        /// <summary>Old slice SOP UID -> new ExpRef SOP UID (for RTSTRUCT remapping).</summary>
        public Dictionary<string, string> OldToNewSopUid { get; set; }
    }

    /// <summary>
    /// Builds the "ExpRef" series for the Eclipse DIR branch: a disposable
    /// expiration reference copy that gives Eclipse an independent geometric
    /// entity (new Frame of Reference) to register, with non-uniform slice
    /// thickness rebuilt to a uniform default target (DICOM world coordinates
    /// and in-plane geometry are unchanged).
    ///
    /// UID rules:
    ///   StudyInstanceUID    kept   (shows as a new series in the same study)
    ///   SeriesInstanceUID   new
    ///   SOPInstanceUID      new per slice
    ///   FrameOfReferenceUID new    (the key: same-FoR series are treated as
    ///                               pre-aligned; a new FoR makes rigid then
    ///                               deformable registration available)
    /// The source expiration series is preserved untouched; ExpRef is only a
    /// registration input, never the output grid (Step 2 output stays on the
    /// original expiration CT).
    /// </summary>
    public static class ExpRefBuilder
    {
        /// <summary>
        /// Build the ExpRef series from an expiration CT directory.
        /// </summary>
        /// <param name="ctDir">Source expiration CT directory.</param>
        /// <param name="outDir">Output directory for the ExpRef DICOM files.</param>
        /// <param name="targetThicknessMm">Uniform target thickness used when
        /// the source spacing is non-uniform (ignored otherwise).</param>
        /// <param name="rtstructSourcePath">Optional source RTSTRUCT (the
        /// expiration structure set). When given, a copy remapped to the
        /// ExpRef series is written next to the CT so the pair triggers
        /// Eclipse's automatic 3D reconstruction on import.</param>
        /// <param name="sourceImageId">Aria image ID of the selected
        /// expiration series (e.g. "CT_50_1"); the pushed CT and structure
        /// set are named "&lt;id&gt;-E" so the reconstructed image in Eclipse
        /// traces back to the selected phase.</param>
        /// <param name="log">Optional log sink.</param>
        public static ExpRefResult Build(string ctDir, string outDir, double targetThicknessMm,
            Action<string> log, string rtstructSourcePath = null, string sourceImageId = null)
        {
            Action<string> L = log ?? delegate { };
            // Clear stale files from previous runs: each Build generates new
            // UIDs, and leftover CT slices from older runs would be mixed
            // into the C-STORE batch with mismatched FoR/Series UIDs.
            if (Directory.Exists(outDir))
            {
                foreach (string f in Directory.GetFiles(outDir, "*.dcm"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            string rtDir = Path.Combine(Directory.GetParent(outDir).FullName, "RTSTRUCT");
            if (Directory.Exists(rtDir))
            {
                foreach (string f in Directory.GetFiles(rtDir, "*.dcm"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            Directory.CreateDirectory(outDir);

            List<CtSlice> slices = ReadSlices(ctDir);
            if (slices.Count < 2)
                throw new InvalidOperationException("Need at least 2 CT slices to build ExpRef.");

            // Same majority-grid guard as CtResampler: drop slices whose
            // in-plane geometry deviates from the series norm so the output
            // series stays homogeneous (a mixed series fails downstream
            // imports/converters, e.g. plastimatch "wrong length").
            slices = FilterToMajorityGeometry(slices, L);
            if (slices.Count < 2)
            {
                L("Warning: too few slices on the majority grid; building from all slices as-is.");
                slices = ReadSlices(ctDir);
                if (slices.Count < 2)
                    throw new InvalidOperationException("Need at least 2 CT slices to build ExpRef.");
            }
            L(string.Format("E1: read {0} expiration slices from {1}", slices.Count, ctDir));

            // Uniformity check (same criterion as the Velocity-side export).
            double maxSp = 0, minSp = double.MaxValue;
            for (int i = 1; i < slices.Count; i++)
            {
                double sp = Math.Abs(slices[i].Z - slices[i - 1].Z);
                if (sp < 1e-6) continue;
                if (sp > maxSp) maxSp = sp;
                if (sp < minSp) minSp = sp;
            }
            bool uniform = minSp >= double.MaxValue || minSp <= 1e-6 || maxSp <= 3.0 * minSp;

            string newSeriesUid = DicomUID.Generate().UID;
            // Eclipse/Aria derives the reconstructed image's name from the
            // pushed tags (structure-set label wins when CT + RTSTRUCT arrive
            // as a pair), so every pushed name traces back to the selected
            // expiration image instead of a fixed label.
            string expRefName = MakeExpRefName(sourceImageId);
            // NEW Frame of Reference UID: this is the core purpose of ExpRef.
            // A new FoR makes the ExpRef an independent geometric entity in
            // Eclipse, enabling rigid + deformable registration to the
            // inspiration series. (Same-FoR series are treated as pre-aligned
            // by Eclipse and cannot be registered against each other.)
            string newForUid = DicomUID.Generate().UID;
            string studyUid = GetStr(slices[0].Dataset, DicomTag.StudyInstanceUID);

            ExpRefResult res = new ExpRefResult();
            res.NewSeriesUid = newSeriesUid;
            res.NewFrameOfReferenceUid = newForUid;
            res.StudyUid = studyUid;
            res.InputSlices = slices.Count;
            res.Files = new List<string>();
            // Parity with the Velocity-side uniformization (CtResampler): the
            // rebuild target is the series' own maximum adjacent spacing; the
            // caller's target thickness is only a degenerate fallback (no
            // measurable spacing). Uniform inputs keep their z grid as-is.
            double effectiveTarget = maxSp > 1e-6 ? maxSp : targetThicknessMm;
            res.TargetThickness = effectiveTarget;
            res.WasUniform = uniform;
            res.Resampled = !uniform;

            // Build the output z grid.
            double zStart = slices[0].Z;
            double zEnd = slices[slices.Count - 1].Z;
            int sign = zEnd >= zStart ? 1 : -1;
            List<double> outZ;
            if (uniform)
            {
                outZ = new List<double>();
                for (int i = 0; i < slices.Count; i++) outZ.Add(slices[i].Z);
            }
            else
            {
                int n = (int)Math.Round(Math.Abs(zEnd - zStart) / targetThicknessMm) + 1;
                outZ = new List<double>(n);
                for (int i = 0; i < n; i++) outZ.Add(zStart + i * effectiveTarget * sign);
            }
            res.OutputSlices = outZ.Count;
            L(string.Format("E2: FoR {0}...{1} | name '{2}' | target thickness {3:F2} mm | output slices {4}{5}",
                newForUid.Substring(0, 12), newForUid.Substring(newForUid.Length - 6),
                expRefName,
                res.TargetThickness, outZ.Count, uniform ? " (uniform input, z kept)" : " (rebuilt)"));

            double huSumDelta = 0; double huMaxDelta = 0; long huCount = 0;
            double slope0 = GetDouble(slices[0].Dataset, DicomTag.RescaleSlope, 1.0);
            double inter0 = GetDouble(slices[0].Dataset, DicomTag.RescaleIntercept, 0.0);

            List<ushort[]> outPixels = new List<ushort[]>(outZ.Count);
            Dictionary<string, string> oldToNewSopUid = new Dictionary<string, string>();
            for (int zi = 0; zi < outZ.Count; zi++)
            {
                double zt = outZ[zi];
                int lo = NeighborLow(slices, zt);
                int hi = Math.Min(lo + 1, slices.Count - 1);
                double zLo = slices[lo].Z, zHi = slices[hi].Z;
                double t = Math.Abs(zHi - zLo) > 1e-9 ? (zt - zLo) / (zHi - zLo) : 0.0;
                if (t < 0) t = 0; if (t > 1) t = 1; // nearest fill beyond the ends

                ushort[] pa = slices[lo].Pixels, pb = slices[hi].Pixels;
                ushort[] px = new ushort[pa.Length];
                for (int i = 0; i < pa.Length; i++)
                    px[i] = (ushort)Math.Round(pa[i] + t * (pb[i] - pa[i]));
                outPixels.Add(px);

                // Template dataset: nearest source slice (keeps per-slice tags).
                DicomDataset tmpl = t < 0.5 ? slices[lo].Dataset : slices[hi].Dataset;
                DicomDataset ds = new DicomDataset(tmpl);
                string sopUid = DicomUID.Generate().UID;

                ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                ds.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeriesUid);
                ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, newForUid);
                // Renumber along the output grid so an ordered bulk C-STORE
                // assembles the series correctly in Eclipse/ARIA.
                ds.AddOrUpdate(DicomTag.InstanceNumber, zi + 1);
                ds.AddOrUpdate(DicomTag.SeriesDescription, expRefName);
                // Distinct SeriesNumber so the ExpRef is visually separate from
                // the source series in the Eclipse series list (the cloned
                // template would otherwise inherit the source's number).
                ds.AddOrUpdate(DicomTag.SeriesNumber, "990");
                ds.AddOrUpdate(DicomTag.SliceThickness, Math.Round(res.TargetThickness, 4).ToString("F4"));
                // Restate the reference grid explicitly (16-bit buffer is
                // written below) so every output file agrees on geometry.
                ds.AddOrUpdate(DicomTag.Rows, (ushort)slices[0].Rows);
                ds.AddOrUpdate(DicomTag.Columns, (ushort)slices[0].Cols);
                ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);

                double[] ipp = GetDoubleArray(tmpl, DicomTag.ImagePositionPatient, 3);
                if (ipp != null)
                    ds.AddOrUpdate(DicomTag.ImagePositionPatient, ipp[0], ipp[1], Math.Round(zt, 4));

                // 16-bit pixel data must be OW (Other Word) per DICOM PS3.5;
                // a byte[] write produces VR=OB which strict readers reject.
                ds.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, px));

                string path = Path.Combine(outDir, "ExpRef_" + sopUid.Replace(".", "") + ".dcm");
                DicomFile df = new DicomFile(ds);
                df.FileMetaInfo.MediaStorageSOPInstanceUID =
                    DicomUIDGenerator.GenerateDerivedFromUUID();
                df.Save(path);
                res.Files.Add(path);

                // SOP mapping for the RTSTRUCT remap: both interpolation
                // neighbours map to this new slice (same convention as the
                // Velocity-side CtResampler).
                string loUid = GetStr(slices[lo].Dataset, DicomTag.SOPInstanceUID);
                string hiUid = GetStr(slices[hi].Dataset, DicomTag.SOPInstanceUID);
                if (!string.IsNullOrEmpty(loUid)) oldToNewSopUid[loUid] = sopUid;
                if (!string.IsNullOrEmpty(hiUid)) oldToNewSopUid[hiUid] = sopUid;
            }
            res.OutputZ = outZ;
            res.OldToNewSopUid = oldToNewSopUid;

            
            // Self-check: interpolate the OUTPUT grid at each original slice
            // position; the trilinear rebuild must reproduce the source HU
            // (|dHU| ~ 0; acceptance <= 1 HU per design DoD).
            for (int si = 0; si < slices.Count; si += Math.Max(1, slices.Count / 8))
            {
                double zs = slices[si].Z;
                int lo = NeighborLowZ(outZ, zs);
                int hi = Math.Min(lo + 1, outZ.Count - 1);
                double zLo = outZ[lo], zHi = outZ[hi];
                double t = Math.Abs(zHi - zLo) > 1e-9 ? (zs - zLo) / (zHi - zLo) : 0.0;
                if (t < 0) t = 0; if (t > 1) t = 1;
                ushort[] pa = outPixels[lo], pb = outPixels[hi];
                ushort[] src = slices[si].Pixels;
                double sum = 0, mx = 0;
                long cnt = 0;
                for (int i = 0; i < src.Length; i += 97)
                {
                    double rebuilt = pa[i] + t * (pb[i] - pa[i]);
                    double d = Math.Abs(rebuilt - src[i]);
                    sum += d; if (d > mx) mx = d; cnt++;
                }
                huSumDelta += sum / Math.Max(1, cnt); huCount++;
                if (mx > huMaxDelta) huMaxDelta = mx;
            }
            res.MeanAbsDeltaHu = huCount > 0 ? huSumDelta / huCount : 0.0;
            res.MaxAbsDeltaHu = huMaxDelta;
            if (huCount > 0)
                L(string.Format("E4: self-check on {0} source slice positions: mean |dHU| = {1:F4}, max sampled |dHU| = {2:F1}",
                    huCount, res.MeanAbsDeltaHu, res.MaxAbsDeltaHu));
            else
                L("E4: self-check skipped (uniform input, no interpolation performed)");

            // Remap the source RTSTRUCT onto the ExpRef series so that the
            // daemon receives CT + structure as a pair (required for
            // auto-reconstruction). All UID references are updated to point
            // at the new ExpRef series / SOPs / FoR.
            if (!string.IsNullOrEmpty(rtstructSourcePath) && File.Exists(rtstructSourcePath))
            {
                try
                {
                    string rtOutDir = Path.Combine(Directory.GetParent(outDir).FullName, "RTSTRUCT");
                    Directory.CreateDirectory(rtOutDir);
                    string rtOut = Path.Combine(rtOutDir, "ExpRef_rtstruct.dcm");
                    RemapRtstruct(rtstructSourcePath, rtOut, res, expRefName);
                    res.RtstructPath = rtOut;
                    L("E2: RTSTRUCT remapped to ExpRef: " + rtOut);
                }
                catch (Exception rtEx)
                {
                    L("Warning: RTSTRUCT remap failed: " + rtEx.Message);
                }
            }

            return res;
        }

        /// <summary>
        /// Remap a source RTSTRUCT onto the ExpRef series: new SOP/Series UIDs
        /// for the RTSTRUCT itself, FrameOfReferenceUID replaced, all
        /// ContourImageSequence SOP references remapped via the old-to-new SOP
        /// map built during CT reconstruction.
        /// The structure-set label is renamed to the ExpRef name as well:
        /// Aria names the auto-reconstructed image after the structure set
        /// when CT + RTSTRUCT arrive as a pair, so keeping the source label
        /// (e.g. the user's "AutoFLC" set) would leak that name onto the
        /// reconstructed image.
        /// </summary>
        private static void RemapRtstruct(string srcPath, string destPath, ExpRefResult res, string expRefName)
        {
            DicomFile df = DicomFile.Open(srcPath, FileReadOption.ReadAll);
            DicomDataset ds = df.Dataset;

            ds.AddOrUpdate(DicomTag.SOPInstanceUID, DicomUID.Generate().UID);
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);
            ds.AddOrUpdate(DicomTag.SeriesNumber, "991");
            ds.AddOrUpdate(DicomTag.SeriesDescription, expRefName);
            // StructureSetLabel is SH (max 16 chars); Name and Description are LO.
            string label = expRefName.Length > 16 ? expRefName.Substring(0, 16) : expRefName;
            ds.AddOrUpdate(DicomTag.StructureSetLabel, label);
            ds.AddOrUpdate(DicomTag.StructureSetName, label);

            // 0. Top-level FrameOfReferenceUID: some Varian-generated RTSTRUCTs
            // include this tag; the daemon validates it against the CT series.
            // Must be updated to the ExpRef's new FoR to avoid "Import of
            // 'Frame of Reference UID' failed".
            if (ds.Contains(DicomTag.FrameOfReferenceUID))
            {
                ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, res.NewFrameOfReferenceUid);
            }

            // 1. ReferencedFrameOfReferenceSequence chain
            DicomSequence refForSeq = ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence)
                ? ds.Get<DicomSequence>(DicomTag.ReferencedFrameOfReferenceSequence) : null;
            if (refForSeq != null)
            {
                foreach (DicomDataset frame in refForSeq.Items)
                {
                    frame.AddOrUpdate(DicomTag.FrameOfReferenceUID, res.NewFrameOfReferenceUid);
                    if (!frame.Contains(DicomTag.RTReferencedStudySequence)) continue;
                    DicomSequence studySeq = frame.Get<DicomSequence>(DicomTag.RTReferencedStudySequence);
                    if (studySeq == null) continue;
                    foreach (DicomDataset study in studySeq.Items)
                    {
                        DicomSeriesRemap(study, res);
                    }
                }
            }

            // 2. Per-ROI FoR references
            DicomSequence ssRoiSeq = ds.Contains(DicomTag.StructureSetROISequence) ? ds.Get<DicomSequence>(DicomTag.StructureSetROISequence) : null;
            if (ssRoiSeq != null)
                foreach (DicomDataset roi in ssRoiSeq.Items)
                    roi.AddOrUpdate(DicomTag.ReferencedFrameOfReferenceUID, res.NewFrameOfReferenceUid);

            // 3. ROIContourSequence → ContourSequence → ContourImageSequence
            DicomSequence roiContourSeq = ds.Contains(DicomTag.ROIContourSequence) ? ds.Get<DicomSequence>(DicomTag.ROIContourSequence) : null;
            if (roiContourSeq != null)
            {
                foreach (DicomDataset rc in roiContourSeq.Items)
                {
                    if (!rc.Contains(DicomTag.ContourSequence)) continue;
                    DicomSequence contourSeq = rc.Get<DicomSequence>(DicomTag.ContourSequence);
                    if (contourSeq == null) continue;
                    foreach (DicomDataset contour in contourSeq.Items)
                    {
                        RemapContourImageSeq(contour, res.OldToNewSopUid);
                    }
                }
            }

            df.FileMetaInfo.MediaStorageSOPInstanceUID =
                DicomUIDGenerator.GenerateDerivedFromUUID();
            df.Save(destPath);
        }

        /// <summary>
        /// Derive the pushed ExpRef name from the selected expiration image
        /// ID (e.g. "CT_50_1" -> "CT_50_1-E"). Spaces, parentheses and other
        /// characters Aria cannot use in image IDs are stripped.
        /// </summary>
        private static string MakeExpRefName(string sourceImageId)
        {
            if (string.IsNullOrWhiteSpace(sourceImageId))
                return "ExpRef-E";
            var sb = new System.Text.StringBuilder();
            foreach (char c in sourceImageId.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
            }
            return (sb.Length > 0 ? sb.ToString() : "ExpRef") + "-E";
        }

        private static void DicomSeriesRemap(DicomDataset study, ExpRefResult res)
        {
            if (!study.Contains(DicomTag.RTReferencedSeriesSequence)) return;
            DicomSequence seriesSeq = study.Get<DicomSequence>(DicomTag.RTReferencedSeriesSequence);
            if (seriesSeq == null) return;
            foreach (DicomDataset series in seriesSeq.Items)
            {
                series.AddOrUpdate(DicomTag.SeriesInstanceUID, res.NewSeriesUid);
                RemapContourImageSeq(series, res.OldToNewSopUid);
            }
        }

        private static void RemapContourImageSeq(DicomDataset ds, Dictionary<string, string> uidMap)
        {
            if (uidMap == null) return;
            // Some Varian RTSTRUCTs omit per-contour ContourImageSequence
            // (3006,0016); only the series-level references exist. Skip
            // gracefully instead of throwing.
            if (!ds.Contains(DicomTag.ContourImageSequence)) return;
            DicomSequence ciSeq = ds.Get<DicomSequence>(DicomTag.ContourImageSequence);
            if (ciSeq == null) return;
            foreach (DicomDataset ci in ciSeq.Items)
            {
                string oldUid = GetStr(ci, DicomTag.ReferencedSOPInstanceUID);
                string newUid;
                if (!string.IsNullOrEmpty(oldUid) && uidMap.TryGetValue(oldUid, out newUid))
                    ci.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, newUid);
            }
        }

        private static int NeighborLowZ(List<double> zs, double z)
        {
            int lo = 0;
            for (int i = 0; i < zs.Count - 1; i++)
            {
                double a = zs[i], b = zs[i + 1];
                if ((z >= a && z <= b) || (z <= a && z >= b)) return i;
                if ((b > a && z > b) || (b < a && z < b)) lo = i + 1;
            }
            return Math.Min(lo, zs.Count - 2);
        }

        

        

        private static int NeighborLow(List<CtSlice> slices, double z)
        {
            int lo = 0;
            for (int i = 0; i < slices.Count - 1; i++)
            {
                double a = slices[i].Z, b = slices[i + 1].Z;
                if ((z >= a && z <= b) || (z <= a && z >= b)) { lo = i; return lo; }
                if ((b > a && z > b) || (b < a && z < b)) lo = i + 1;
            }
            return Math.Min(lo, slices.Count - 2);
        }

        private static List<CtSlice> ReadSlices(string ctDir)
        {
            List<CtSlice> slices = new List<CtSlice>();
            foreach (string f in Directory.GetFiles(ctDir, "*.dcm"))
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.ReadAll);
                    DicomDataset ds = df.Dataset;
                    if (ds.Get<string>(DicomTag.Modality, null) != "CT") continue;
                    double[] ipp = GetDoubleArray(ds, DicomTag.ImagePositionPatient, 3);
                    if (ipp == null) continue;
                    slices.Add(new CtSlice
                    {
                        Z = ipp[2],
                        Pixels = ds.Get<ushort[]>(DicomTag.PixelData),
                        Rows = (int)GetUshort(ds, DicomTag.Rows),
                        Cols = (int)GetUshort(ds, DicomTag.Columns),
                        BitsAllocated = GetUshort(ds, DicomTag.BitsAllocated),
                        PixelRepresentation = GetUshort(ds, DicomTag.PixelRepresentation),
                        Dataset = ds
                    });
                }
                catch { }
            }
            slices.Sort((a, b) => a.Z.CompareTo(b.Z));
            if (slices.Count == 0)
                throw new InvalidOperationException("No CT slices found in " + ctDir);
            return slices;
        }

        /// <summary>
        /// Keep only the slices that share the majority in-plane grid; see
        /// CtResampler.FilterToMajorityGeometry for the rationale.
        /// </summary>
        private static List<CtSlice> FilterToMajorityGeometry(List<CtSlice> slices, Action<string> L)
        {
            var counts = new Dictionary<string, int>();
            var sampleByKey = new Dictionary<string, CtSlice>();
            foreach (CtSlice s in slices)
            {
                string key = string.Format("{0}x{1}x{2}x{3}", s.Rows, s.Cols, s.BitsAllocated, s.PixelRepresentation);
                if (!counts.ContainsKey(key))
                {
                    counts[key] = 0;
                    sampleByKey[key] = s;
                }
                counts[key]++;
            }

            string bestKey = null;
            int bestCount = 0;
            foreach (var kvp in counts)
            {
                if (kvp.Value > bestCount)
                {
                    bestKey = kvp.Key;
                    bestCount = kvp.Value;
                }
            }
            if (bestKey == null)
                return slices;

            CtSlice refS = sampleByKey[bestKey];
            long expected = (long)refS.Rows * refS.Cols;
            List<CtSlice> kept = slices
                .Where(s => s.Rows == refS.Rows && s.Cols == refS.Cols
                    && s.BitsAllocated == refS.BitsAllocated
                    && s.PixelRepresentation == refS.PixelRepresentation
                    && s.Pixels != null && s.Pixels.Length == expected)
                .ToList();

            int dropped = slices.Count - kept.Count;
            if (dropped > 0)
            {
                L(string.Format(
                    "Geometry check: dropped {0}/{1} slice(s) off the majority grid ({2}x{3}, {4}-bit).",
                    dropped, slices.Count, refS.Rows, refS.Cols, refS.BitsAllocated));
            }
            return kept;
        }

        private static ushort GetUshort(DicomDataset ds, DicomTag tag)
        {
            try { return ds.Get<ushort>(tag, 0); }
            catch { return 0; }
        }

        private static string GetStr(DicomDataset ds, DicomTag tag)
        {
            try { return ds.Get<string>(tag, null); } catch { return null; }
        }

        private static double GetDouble(DicomDataset ds, DicomTag tag, double dflt)
        {
            try { return ds.Get<double>(tag, dflt); } catch { return dflt; }
        }

        private static double[] GetDoubleArray(DicomDataset ds, DicomTag tag, int len)
        {
            try
            {
                var elem = ds.GetDicomItem<DicomElement>(tag);
                if (elem == null) return null;
                int n = Math.Min(elem.Count, len);
                double[] r = new double[n];
                for (int i = 0; i < n; i++) r[i] = elem.Get<double>(i);
                return r;
            }
            catch { return null; }
        }

        private class CtSlice
        {
            public double Z { get; set; }
            public ushort[] Pixels { get; set; }
            public int Rows { get; set; }
            public int Cols { get; set; }
            public ushort BitsAllocated { get; set; }
            public ushort PixelRepresentation { get; set; }
            public DicomDataset Dataset { get; set; }
        }
    }
}
