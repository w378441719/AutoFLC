using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dicom;

namespace AutoFLC
{
    /// <summary>
    /// CT slice-thickness uniformity checks, resampling to a uniform grid, and
    /// RTSTRUCT UID remapping.
    ///
    /// Velocity AI requires uniform slice spacing for deformable registration,
    /// but some Eclipse 4DCT protocols alternate slice thickness (e.g. 1.25 and
    /// 2.5 mm), which causes the Velocity import to fail. This class resamples
    /// such series to a uniform grid before registration.
    /// </summary>
    public static class CtResampler
    {
        /// <summary>
        /// Check whether a CT series has uniform slice spacing.
        /// </summary>
        /// <param name="ctDir">Directory containing DICOM CT files.</param>
        /// <param name="maxSpacing">Output: maximum spacing found.</param>
        /// <param name="minSpacing">Output: minimum spacing found.</param>
        /// <returns>True if spacing is uniform (max &lt;= 3x min).</returns>
        public static bool IsUniform(string ctDir, out double maxSpacing, out double minSpacing)
        {
            maxSpacing = 0.0;
            minSpacing = double.MaxValue;

            List<double> zLocs = GetSortedZPositions(ctDir);
            if (zLocs.Count < 2)
            {
                maxSpacing = 0.0;
                minSpacing = 0.0;
                return true; // a single slice is trivially uniform
            }

            for (int i = 1; i < zLocs.Count; i++)
            {
                double sp = Math.Abs(zLocs[i] - zLocs[i - 1]);
                if (sp < 1e-6) continue; // skip duplicate Z positions
                if (sp > maxSpacing) maxSpacing = sp;
                if (sp < minSpacing) minSpacing = sp;
            }

            if (minSpacing >= double.MaxValue || minSpacing <= 1e-6)
            {
                minSpacing = maxSpacing;
                return true;
            }

            return maxSpacing <= 3.0 * minSpacing;
        }

        /// <summary>
        /// Resample a CT series to a uniform slice spacing (using the maximum
        /// spacing) and remap the associated RTSTRUCT's SOPInstanceUID
        /// references to the new CT series.
        /// </summary>
        /// <param name="ctDir">Source directory with the original CT DICOM files.</param>
        /// <param name="rtstructPath">Source RTSTRUCT file path (may be null).</param>
        /// <param name="outCtDir">Output directory for the resampled CT files.</param>
        /// <param name="outRtstructPath">Output path for the remapped RTSTRUCT (may be null).</param>
        /// <param name="log">Optional log sink for geometry diagnostics.</param>
        /// <returns>List of resampled CT file paths.</returns>
        public static List<string> Resample(string ctDir, string rtstructPath, string outCtDir, string outRtstructPath,
            Action<string> log = null)
        {
            Action<string> L = log ?? delegate { };
            Directory.CreateDirectory(outCtDir);

            List<CtSliceData> slices = ReadCtSlices(ctDir);
            if (slices.Count < 2)
            {
                // Not enough slices to resample; just copy.
                foreach (CtSliceData s in slices)
                {
                    string dest = Path.Combine(outCtDir, Path.GetFileName(s.FilePath));
                    File.Copy(s.FilePath, dest, true);
                }
                if (!string.IsNullOrEmpty(rtstructPath) && !string.IsNullOrEmpty(outRtstructPath))
                {
                    File.Copy(rtstructPath, outRtstructPath, true);
                }
                return new List<string>(Directory.GetFiles(outCtDir, "*.dcm"));
            }

            // Keep only slices on the majority in-plane grid. A series can
            // contain odd slices (different FOV or bit depth); cloning their
            // tags onto output slices produces a series whose members
            // disagree about geometry, which plastimatch rejects with
            // "wrong length (524288 vs. 512 x 512)".
            slices = FilterToMajorityGeometry(slices, L);
            if (slices.Count < 2)
            {
                L("  Warning: too few slices on the majority grid; resampling all slices as-is.");
                slices = ReadCtSlices(ctDir);
            }
            int refRows = slices[0].Rows, refCols = slices[0].Cols;

            // Use the MAXIMUM spacing as the target thickness (same as
            // Velocity's uniformization convention: resample to the
            // coarsest existing thickness so no information is interpolated
            // beyond what the thickest slice already represents).
            double maxSpacing = 0.0;
            for (int i = 1; i < slices.Count; i++)
            {
                double sp = Math.Abs(slices[i].Z - slices[i - 1].Z);
                if (sp > maxSpacing) maxSpacing = sp;
            }
            if (maxSpacing <= 0) maxSpacing = 2.5;

            // Build the new uniform Z grid.
            double zStart = slices[0].Z;
            double zEnd = slices[slices.Count - 1].Z;
            int nNew = (int)Math.Round(Math.Abs(zEnd - zStart) / maxSpacing) + 1;
            List<double> newZ = new List<double>(nNew);
            for (int i = 0; i < nNew; i++)
                newZ.Add(zStart + i * maxSpacing * Math.Sign(zEnd - zStart));

            List<string> newFiles = new List<string>(nNew);
            Dictionary<string, string> oldToNewSopUid = new Dictionary<string, string>();
            string newSeriesUid = DicomUID.Generate().UID;

            for (int zi = 0; zi < nNew; zi++)
            {
                double zTarget = newZ[zi];

                // Find the surrounding source slices.
                int idxLow = 0;
                for (int i = 0; i < slices.Count - 1; i++)
                {
                    if ((zTarget >= slices[i].Z && zTarget <= slices[i + 1].Z) ||
                        (zTarget <= slices[i].Z && zTarget >= slices[i + 1].Z))
                    {
                        idxLow = i;
                        break;
                    }
                }
                if (zi == 0) idxLow = 0;
                if (zi == nNew - 1) idxLow = slices.Count - 2;

                int idxHigh = idxLow + 1;
                double zLow = slices[idxLow].Z;
                double zHigh = slices[idxHigh].Z;
                double t = (Math.Abs(zHigh - zLow) > 1e-8)
                    ? (zTarget - zLow) / (zHigh - zLow)
                    : 0.0;
                t = Math.Max(0.0, Math.Min(1.0, t));

                // Interpolate the pixel data. Guard against source slices
                // with mismatched dimensions (different FOV or truncated
                // reconstruction), which would produce corrupt output.
                ushort[] pixelsLow = slices[idxLow].Pixels;
                ushort[] pixelsHigh = slices[idxHigh].Pixels;
                ushort[] newPixels;
                int nearestIdx;
                if (pixelsLow.Length != pixelsHigh.Length)
                {
                    // Fall back to nearest-neighbor: use whichever slice is
                    // closer to the target z, skipping interpolation entirely.
                    nearestIdx = (t < 0.5) ? idxLow : idxHigh;
                    newPixels = (ushort[])slices[nearestIdx].Pixels.Clone();
                    zTarget = slices[nearestIdx].Z;
                }
                else
                {
                    newPixels = new ushort[pixelsLow.Length];
                    for (int i = 0; i < pixelsLow.Length; i++)
                    {
                        double val = pixelsLow[i] + t * (pixelsHigh[i] - pixelsLow[i]);
                        newPixels[i] = (ushort)Math.Round(val);
                    }
                    nearestIdx = (t < 0.5) ? idxLow : idxHigh;
                }
                DicomDataset srcDs = slices[nearestIdx].Dataset;

                DicomDataset newDs = new DicomDataset(srcDs);
                string newSopUid = DicomUID.Generate().UID;

                newDs.AddOrUpdate(DicomTag.SOPInstanceUID, newSopUid);
                newDs.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeriesUid);

                DicomElement ippElem = srcDs.GetDicomItem<DicomElement>(DicomTag.ImagePositionPatient);
                if (ippElem != null && ippElem.Count >= 3)
                {
                    double x = ippElem.Get<double>(0);
                    double y = ippElem.Get<double>(1);
                    newDs.AddOrUpdate(DicomTag.ImagePositionPatient, x, y, Math.Round(zTarget, 4));
                }

                newDs.AddOrUpdate(DicomTag.SliceThickness, Math.Round(maxSpacing, 4).ToString("F4"));

                // Force one uniform geometry for the whole output series: the
                // template is already a majority-grid slice, but the tags are
                // restated explicitly so every file agrees on Rows/Columns and
                // the 16-bit buffer written below.
                newDs.AddOrUpdate(DicomTag.Rows, (ushort)refRows);
                newDs.AddOrUpdate(DicomTag.Columns, (ushort)refCols);
                newDs.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);

                // 16-bit pixel data must be OW (Other Word) per DICOM PS3.5.
                // Writing a byte[] produces VR=OB, which strict readers
                // (plastimatch: "wrong length (524288 vs. 512 x 512)")
                // reject because OB implies 1 byte per pixel.
                newDs.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, newPixels));

                string fileName = string.Format("CT_{0}.dcm", newSopUid.Replace(".", ""));
                string outPath = Path.Combine(outCtDir, fileName);

                DicomFile df = new DicomFile(newDs);
                df.FileMetaInfo.MediaStorageSOPInstanceUID = DicomUIDGenerator.GenerateDerivedFromUUID();
                df.Save(outPath);
                newFiles.Add(outPath);

                // Record the original-to-new SOP UID mapping.
                string origSopUid = GetSopInstanceUid(srcDs);
                if (!string.IsNullOrEmpty(origSopUid) && !oldToNewSopUid.ContainsKey(origSopUid))
                    oldToNewSopUid[origSopUid] = newSopUid;

                string lowUid = GetSopInstanceUid(slices[idxLow].Dataset);
                string highUid = GetSopInstanceUid(slices[idxHigh].Dataset);
                if (!string.IsNullOrEmpty(lowUid) && !oldToNewSopUid.ContainsKey(lowUid))
                    oldToNewSopUid[lowUid] = newSopUid;
                if (!string.IsNullOrEmpty(highUid) && !oldToNewSopUid.ContainsKey(highUid))
                    oldToNewSopUid[highUid] = newSopUid;
            }

            if (!string.IsNullOrEmpty(rtstructPath) && File.Exists(rtstructPath) && !string.IsNullOrEmpty(outRtstructPath))
            {
                RemapRtstruct(rtstructPath, outRtstructPath, oldToNewSopUid, newSeriesUid);
            }

            return newFiles;
        }

        /// <summary>
        /// Remap SOPInstanceUIDs in an RTSTRUCT to match a resampled CT series.
        /// </summary>
        private static void RemapRtstruct(string srcPath, string destPath,
            Dictionary<string, string> uidMap, string newSeriesUid)
        {
            DicomFile df = DicomFile.Open(srcPath, FileReadOption.ReadAll);
            DicomDataset ds = df.Dataset;

            // Update the ReferencedFrameOfReferenceSequence ->
            // RTReferencedStudySequence -> RTReferencedSeriesSequence ->
            // ContourImageSequence chain. fo-dicom 4's Get<DicomSequence>
            // throws when a tag is absent, and Varian RTSTRUCTs frequently
            // omit optional sequences — every access is guarded with Contains.
            DicomSequence refForSeq = ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence)
                ? ds.Get<DicomSequence>(DicomTag.ReferencedFrameOfReferenceSequence) : null;
            if (refForSeq != null)
            {
                foreach (DicomDataset frame in refForSeq.Items)
                {
                    DicomSequence rtRefStudySeq = frame.Contains(DicomTag.RTReferencedStudySequence)
                        ? frame.Get<DicomSequence>(DicomTag.RTReferencedStudySequence) : null;
                    if (rtRefStudySeq == null) continue;
                    foreach (DicomDataset study in rtRefStudySeq.Items)
                    {
                        DicomSequence rtRefSeriesSeq = study.Contains(DicomTag.RTReferencedSeriesSequence)
                            ? study.Get<DicomSequence>(DicomTag.RTReferencedSeriesSequence) : null;
                        if (rtRefSeriesSeq == null) continue;
                        foreach (DicomDataset series in rtRefSeriesSeq.Items)
                        {
                            series.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeriesUid);

                            DicomSequence ciSeq = series.Contains(DicomTag.ContourImageSequence)
                                ? series.Get<DicomSequence>(DicomTag.ContourImageSequence) : null;
                            if (ciSeq == null) continue;
                            foreach (DicomDataset ci in ciSeq.Items)
                            {
                                string oldUid = GetStringValue(ci, DicomTag.ReferencedSOPInstanceUID);
                                string newUid;
                                if (!string.IsNullOrEmpty(oldUid) && uidMap.TryGetValue(oldUid, out newUid))
                                {
                                    ci.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, newUid);
                                }
                            }
                        }
                    }
                }
            }

            // Update the ROIContourSequence -> ContourSequence ->
            // ContourImageSequence chain.
            DicomSequence roiContourSeq = ds.Contains(DicomTag.ROIContourSequence)
                ? ds.Get<DicomSequence>(DicomTag.ROIContourSequence) : null;
            if (roiContourSeq != null)
            {
                foreach (DicomDataset rc in roiContourSeq.Items)
                {
                    DicomSequence contourSeq = rc.Contains(DicomTag.ContourSequence)
                        ? rc.Get<DicomSequence>(DicomTag.ContourSequence) : null;
                    if (contourSeq == null) continue;
                    foreach (DicomDataset contour in contourSeq.Items)
                    {
                        DicomSequence cImgSeq = contour.Contains(DicomTag.ContourImageSequence)
                            ? contour.Get<DicomSequence>(DicomTag.ContourImageSequence) : null;
                        if (cImgSeq == null) continue;
                        foreach (DicomDataset ci in cImgSeq.Items)
                        {
                            string oldUid = GetStringValue(ci, DicomTag.ReferencedSOPInstanceUID);
                            string newUid;
                            if (!string.IsNullOrEmpty(oldUid) && uidMap.TryGetValue(oldUid, out newUid))
                            {
                                ci.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, newUid);
                            }
                        }
                    }
                }
            }

            string outDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);
            df.Save(destPath);
        }

        /// <summary>
        /// Read CT slices from a directory, sorted by Z position.
        /// </summary>
        private static List<CtSliceData> ReadCtSlices(string ctDir)
        {
            string[] files = Directory.GetFiles(ctDir, "*.dcm");
            List<CtSliceData> slices = new List<CtSliceData>();

            foreach (string f in files)
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.ReadAll);
                    DicomDataset ds = df.Dataset;
                    string modality = ds.Get<string>(DicomTag.Modality);
                    if (modality != "CT") continue;

                    double z = GetImagePositionZ(ds);
                    ushort[] pixels = ds.Get<ushort[]>(DicomTag.PixelData);

                    slices.Add(new CtSliceData
                    {
                        FilePath = f,
                        Z = z,
                        Pixels = pixels,
                        Rows = GetUshort(ds, DicomTag.Rows),
                        Cols = GetUshort(ds, DicomTag.Columns),
                        BitsAllocated = GetUshort(ds, DicomTag.BitsAllocated),
                        PixelRepresentation = GetUshort(ds, DicomTag.PixelRepresentation),
                        Dataset = ds
                    });
                }
                catch { }
            }

            slices.Sort((a, b) => a.Z.CompareTo(b.Z));
            return slices;
        }

        /// <summary>
        /// Get the sorted Z positions of a CT directory.
        /// </summary>
        private static List<double> GetSortedZPositions(string ctDir)
        {
            string[] files = Directory.GetFiles(ctDir, "*.dcm");
            List<double> zLocs = new List<double>();

            foreach (string f in files)
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.Default);
                    double z = GetImagePositionZ(df.Dataset);
                    zLocs.Add(z);
                }
                catch { }
            }

            zLocs.Sort();
            return zLocs;
        }

        private static double GetImagePositionZ(DicomDataset ds)
        {
            DicomElement ipp = ds.GetDicomItem<DicomElement>(DicomTag.ImagePositionPatient);
            if (ipp != null && ipp.Count >= 3)
                return ipp.Get<double>(2);
            return 0.0;
        }

        private static string GetSopInstanceUid(DicomDataset ds)
        {
            try { return ds.Get<string>(DicomTag.SOPInstanceUID); }
            catch { return null; }
        }

        private static string GetStringValue(DicomDataset ds, DicomTag tag)
        {
            try { return ds.Get<string>(tag); }
            catch { return null; }
        }

        private static ushort GetUshort(DicomDataset ds, DicomTag tag)
        {
            try { return ds.Get<ushort>(tag, 0); }
            catch { return 0; }
        }

        /// <summary>
        /// Keep only the slices that share the majority in-plane grid
        /// (Rows, Columns, BitsAllocated, PixelRepresentation) and whose
        /// pixel buffer matches that grid. Deviant slices are dropped so
        /// neither their pixels nor their tags leak into the output series.
        /// </summary>
        private static List<CtSliceData> FilterToMajorityGeometry(List<CtSliceData> slices, Action<string> L)
        {
            var counts = new Dictionary<string, int>();
            var sampleByKey = new Dictionary<string, CtSliceData>();
            foreach (CtSliceData s in slices)
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

            CtSliceData refS = sampleByKey[bestKey];
            long expected = (long)refS.Rows * refS.Cols;
            List<CtSliceData> kept = slices
                .Where(s => s.Rows == refS.Rows && s.Cols == refS.Cols
                    && s.BitsAllocated == refS.BitsAllocated
                    && s.PixelRepresentation == refS.PixelRepresentation
                    && s.Pixels != null && s.Pixels.Length == expected)
                .ToList();

            int dropped = slices.Count - kept.Count;
            if (dropped > 0)
            {
                L(string.Format(
                    "  Geometry check: dropped {0}/{1} slice(s) off the majority grid ({2}x{3}, {4}-bit).",
                    dropped, slices.Count, refS.Rows, refS.Cols, refS.BitsAllocated));
            }
            return kept;
        }

        /// <summary>
        /// Temporary holder for one CT slice.
        /// </summary>
        private class CtSliceData
        {
            public string FilePath { get; set; }
            public double Z { get; set; }
            public ushort[] Pixels { get; set; }
            public ushort Rows { get; set; }
            public ushort Cols { get; set; }
            public ushort BitsAllocated { get; set; }
            public ushort PixelRepresentation { get; set; }
            public DicomDataset Dataset { get; set; }
        }
    }
}
