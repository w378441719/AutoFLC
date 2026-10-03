using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dicom;

namespace AutoFLC
{
    /// <summary>
    /// Writes binary 3D masks to a DICOM RTSTRUCT file using fo-dicom.
    /// </summary>
    public static class RtstructWriter
    {
        private static readonly DicomUID RtstructSopClassUid = DicomUID.RTStructureSetStorage;
        private static readonly DicomUID CtSopClassUid = DicomUID.CTImageStorage;

        private static readonly Dictionary<string, int[]> DefaultColors = new Dictionary<string, int[]>
        {
            { "FunctionalLung_High", new int[] { 0, 255, 0 } },
            { "FunctionalLung_Intermediate", new int[] { 255, 255, 0 } },
            { "FunctionalLung_Low", new int[] { 255, 0, 0 } },
            { "FunctionalLung_HU_High", new int[] { 0, 200, 120 } },
            { "FunctionalLung_HU_Intermediate", new int[] { 200, 220, 0 } },
            { "FunctionalLung_HU_Low", new int[] { 255, 100, 60 } },
            { "Lung_R", new int[] { 255, 128, 0 } },
            { "Lung_L", new int[] { 0, 128, 255 } },
        };

        /// <summary>
        /// Write masks to a DICOM RTSTRUCT file.
        /// </summary>
        /// <param name="ctDatasets">List of DICOM CT datasets (metadata-only, sorted by z).</param>
        /// <param name="masks">Dictionary of ROI name -> boolean 3D mask [nz, ny, nx].</param>
        /// <param name="outputPath">Output file path.</param>
        public static void Write(List<DicomDataset> ctDatasets, Dictionary<string, bool[,,]> masks, string outputPath)
        {
            if (ctDatasets == null || ctDatasets.Count == 0)
                throw new ArgumentException("CT datasets cannot be empty.");

            DicomDataset ds = BuildRtstructDataset(ctDatasets);

            int roiNumber = 1;

            // Pre-compute the geometry from the first slice.
            DicomDataset refDs = ctDatasets[0];
            double[] iop = GetDoubleArray(refDs, DicomTag.ImageOrientationPatient, 6);
            double[] rowDir = new double[] { iop[0], iop[1], iop[2] };
            double[] colDir = new double[] { iop[3], iop[4], iop[5] };
            double[] ps = GetDoubleArray(refDs, DicomTag.PixelSpacing, 2);
            double rowSpacing = ps[0];
            double colSpacing = ps[1];

            foreach (KeyValuePair<string, bool[,,]> kvp in masks)
            {
                string name = kvp.Key;
                bool[,,] mask = kvp.Value;

                if (!AnyVoxel(mask))
                    continue;

                int[] color;
                if (!DefaultColors.TryGetValue(name, out color))
                    color = new int[] { 128, 128, 128 };

                AddRoi(ds, ctDatasets, mask, name, roiNumber, color, rowDir, colDir, rowSpacing, colSpacing);
                roiNumber++;
            }

            // Build file meta.
            DicomFile df = new DicomFile(ds);
            df.FileMetaInfo.MediaStorageSOPClassUID = RtstructSopClassUid;
            df.FileMetaInfo.MediaStorageSOPInstanceUID = ds.GetSingleValue<DicomUID>(DicomTag.SOPInstanceUID);
            df.FileMetaInfo.TransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
            df.FileMetaInfo.ImplementationClassUID = new DicomUID("1.2.826.0.1.3680043.2.11.1", null, DicomUidType.Unknown);
            df.FileMetaInfo.ImplementationVersionName = "AutoFLC_10";

            string outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            df.Save(outputPath);
        }

        private static DicomDataset BuildRtstructDataset(List<DicomDataset> ctDatasets)
        {
            DicomDataset refDs = ctDatasets[0];

            DicomDataset ds = new DicomDataset();

            ds.Add(DicomTag.SOPClassUID, RtstructSopClassUid.UID);
            ds.Add(DicomTag.SOPInstanceUID, DicomUID.Generate().UID);
            ds.Add(DicomTag.Modality, "RTSTRUCT");
            ds.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");

            // Patient information (copied from CT).
            CopyTag(ds, refDs, DicomTag.PatientName);
            CopyTag(ds, refDs, DicomTag.PatientID);
            CopyTag(ds, refDs, DicomTag.PatientBirthDate);
            CopyTag(ds, refDs, DicomTag.PatientSex);

            // Study information (copied from CT).
            CopyTag(ds, refDs, DicomTag.StudyInstanceUID);
            CopyTag(ds, refDs, DicomTag.StudyID);
            CopyTag(ds, refDs, DicomTag.StudyDate);
            CopyTag(ds, refDs, DicomTag.StudyTime);
            CopyTag(ds, refDs, DicomTag.StudyDescription);

            // Series.
            ds.Add(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);
            ds.Add(DicomTag.SeriesNumber, "991");
            ds.Add(DicomTag.SeriesDescription, "AutoFLC Functional Lung Contouring");
            ds.Add(DicomTag.StructureSetLabel, "AutoFLC");
            ds.Add(DicomTag.StructureSetName, "FunctionalLungContouring");
            ds.Add(DicomTag.StructureSetDescription, "Functional lung contours generated by AutoFLC");
            ds.Add(DicomTag.InstanceNumber, "1");

            string frameOfRefUid = GetString(refDs, DicomTag.FrameOfReferenceUID);
            if (string.IsNullOrEmpty(frameOfRefUid))
                frameOfRefUid = DicomUID.Generate().UID;

            // ReferencedFrameOfReferenceSequence: ContourImageSequence lists
            // every referenced CT slice.
            DicomSequence contourImageSeq = new DicomSequence(DicomTag.ContourImageSequence);
            foreach (DicomDataset ctDs in ctDatasets)
            {
                DicomDataset cid = new DicomDataset();
                cid.Add(DicomTag.ReferencedSOPClassUID, GetString(ctDs, DicomTag.SOPClassUID, CtSopClassUid.UID));
                cid.Add(DicomTag.ReferencedSOPInstanceUID, GetString(ctDs, DicomTag.SOPInstanceUID, DicomUID.Generate().UID));
                contourImageSeq.Items.Add(cid);
            }

            DicomDataset rtRefdSeries = new DicomDataset();
            string seriesUid = GetString(refDs, DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);
            rtRefdSeries.Add(DicomTag.SeriesInstanceUID, seriesUid);
            rtRefdSeries.Add(contourImageSeq);

            DicomSequence rtRefdSeriesSeq = new DicomSequence(DicomTag.RTReferencedSeriesSequence);
            rtRefdSeriesSeq.Items.Add(rtRefdSeries);

            DicomDataset rtRefdStudy = new DicomDataset();
            rtRefdStudy.Add(DicomTag.ReferencedSOPClassUID, "1.2.840.10008.3.1.2.3.2");
            string studyUid = GetString(refDs, DicomTag.StudyInstanceUID, DicomUID.Generate().UID);
            rtRefdStudy.Add(DicomTag.ReferencedSOPInstanceUID, studyUid);
            rtRefdStudy.Add(rtRefdSeriesSeq);

            DicomSequence rtRefdStudySeq = new DicomSequence(DicomTag.RTReferencedStudySequence);
            rtRefdStudySeq.Items.Add(rtRefdStudy);

            DicomDataset frameOfRef = new DicomDataset();
            frameOfRef.Add(DicomTag.FrameOfReferenceUID, frameOfRefUid);
            frameOfRef.Add(rtRefdStudySeq);

            DicomSequence frameOfRefSeq = new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence);
            frameOfRefSeq.Items.Add(frameOfRef);
            ds.Add(frameOfRefSeq);

            // Empty sequences; AddRoi fills them.
            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence));
            ds.Add(new DicomSequence(DicomTag.ROIContourSequence));
            ds.Add(new DicomSequence(DicomTag.RTROIObservationsSequence));

            return ds;
        }

        private static void AddRoi(DicomDataset ds, List<DicomDataset> ctDatasets, bool[,,] mask,
            string name, int roiNumber, int[] color,
            double[] rowDir, double[] colDir, double rowSpacing, double colSpacing)
        {
            DicomDataset ssRoi = new DicomDataset();
            ssRoi.Add(DicomTag.ROINumber, roiNumber.ToString());
            ssRoi.Add(DicomTag.ROIName, name);
            ssRoi.Add(DicomTag.ROIGenerationAlgorithm, "MANUAL");

            string frameOfRefUid = ds.Get<DicomSequence>(DicomTag.ReferencedFrameOfReferenceSequence).Items[0]
                .Get<string>(DicomTag.FrameOfReferenceUID);
            ssRoi.Add(DicomTag.ReferencedFrameOfReferenceUID, frameOfRefUid);

            DicomSequence ssRoiSeq = ds.Get<DicomSequence>(DicomTag.StructureSetROISequence);
            ssRoiSeq.Items.Add(ssRoi);

            DicomDataset obs = new DicomDataset();
            obs.Add(DicomTag.ObservationNumber, roiNumber.ToString());
            obs.Add(DicomTag.ReferencedROINumber, roiNumber.ToString());
            obs.Add(DicomTag.RTROIInterpretedType, "ORGAN");
            obs.Add(DicomTag.ROIInterpreter, "");

            DicomSequence obsSeq = ds.Get<DicomSequence>(DicomTag.RTROIObservationsSequence);
            obsSeq.Items.Add(obs);

            DicomDataset roiContour = new DicomDataset();
            roiContour.Add(DicomTag.ROIDisplayColor, color[0], color[1], color[2]);
            roiContour.Add(DicomTag.ReferencedROINumber, roiNumber.ToString());
            DicomSequence contourSeq = new DicomSequence(DicomTag.ContourSequence);
            roiContour.Add(contourSeq);

            for (int zIdx = 0; zIdx < ctDatasets.Count; zIdx++)
            {
                if (zIdx >= mask.GetLength(0))
                    break;

                DicomDataset sliceDs = ctDatasets[zIdx];

                // Extract the 2D mask for this slice.
                int ny = mask.GetLength(1);
                int nx = mask.GetLength(2);
                bool[,] sliceMask = new bool[ny, nx];
                bool hasAny = false;
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                    {
                        bool v = mask[zIdx, y, x];
                        sliceMask[y, x] = v;
                        if (v) hasAny = true;
                    }
                }

                if (!hasAny)
                    continue;

                List<List<float[]>> contours = ContourTracer.FindContours(sliceMask);
                if (contours == null || contours.Count == 0)
                    continue;

                double[] ipp = GetDoubleArray(sliceDs, DicomTag.ImagePositionPatient, 3);

                foreach (List<float[]> contour in contours)
                {
                    if (contour.Count < 3)
                        continue;

                    List<float[]> simplified;
                    if (name.StartsWith("FunctionalLung"))
                    {
                        // Contours are simplified with a 0.5 px tolerance and
                        // islands smaller than 2 px are dropped.
                        simplified = ContourTracer.SimplifyPolyline(contour, 0.5);
                        if (simplified.Count < 3)
                            continue;

                        double area = ContourTracer.PolygonArea(simplified);
                        if (area < 2.0)
                            continue;
                    }
                    else
                    {
                        simplified = contour;
                    }

                    // Ensure the contour is closed.
                    float[] firstPt = simplified[0];
                    float[] lastPt = simplified[simplified.Count - 1];
                    double closeDist = Math.Sqrt(
                        (firstPt[0] - lastPt[0]) * (firstPt[0] - lastPt[0]) +
                        (firstPt[1] - lastPt[1]) * (firstPt[1] - lastPt[1]));
                    if (closeDist > 0.01)
                    {
                        simplified.Add(new float[] { firstPt[0], firstPt[1] });
                    }

                    List<float[]> worldPts = ContourTracer.ToWorldCoordinates(
                        simplified, ipp, rowDir, colDir, rowSpacing, colSpacing);

                    List<string> contourData = new List<string>();
                    foreach (float[] wp in worldPts)
                    {
                        contourData.Add(FormatDs(wp[0]));
                        contourData.Add(FormatDs(wp[1]));
                        contourData.Add(FormatDs(wp[2]));
                    }

                    DicomDataset contourDs = new DicomDataset();
                    contourDs.Add(DicomTag.ContourGeometricType, "CLOSED_PLANAR");
                    contourDs.Add(DicomTag.NumberOfContourPoints, simplified.Count.ToString());

                    // Reference the CT slice this contour belongs to.
                    DicomDataset cid = new DicomDataset();
                    cid.Add(DicomTag.ReferencedSOPClassUID,
                        GetString(sliceDs, DicomTag.SOPClassUID, CtSopClassUid.UID));
                    cid.Add(DicomTag.ReferencedSOPInstanceUID,
                        GetString(sliceDs, DicomTag.SOPInstanceUID, DicomUID.Generate().UID));

                    DicomSequence cidSeq = new DicomSequence(DicomTag.ContourImageSequence);
                    cidSeq.Items.Add(cid);
                    contourDs.Add(cidSeq);

                    contourDs.Add(DicomTag.ContourData, contourData.ToArray());

                    contourSeq.Items.Add(contourDs);
                }
            }

            DicomSequence roiContourSeq = ds.Get<DicomSequence>(DicomTag.ROIContourSequence);
            roiContourSeq.Items.Add(roiContour);
        }

        private static string FormatDs(double value)
        {
            // Round to 2 decimal places and strip trailing zeros.
            string s = value.ToString("F2", CultureInfo.InvariantCulture);
            if (s.Contains("."))
            {
                s = s.TrimEnd('0').TrimEnd('.');
            }
            if (s == "-0" || s == "")
                s = "0";
            return s;
        }

        private static void CopyTag(DicomDataset target, DicomDataset source, DicomTag tag)
        {
            string val = GetString(source, tag);
            if (!string.IsNullOrEmpty(val))
                target.Add(tag, val);
        }

        private static string GetString(DicomDataset ds, DicomTag tag, string defaultVal = "")
        {
            if (ds.Contains(tag))
            {
                try
                {
                    return ds.Get<string>(tag);
                }
                catch
                {
                    return defaultVal;
                }
            }
            return defaultVal;
        }

        private static double[] GetDoubleArray(DicomDataset ds, DicomTag tag, int expectedLength)
        {
            double[] result = new double[expectedLength];
            if (!ds.Contains(tag))
                return result;

            try
            {
                DicomElement de = ds.GetDicomItem<DicomElement>(tag);
                if (de != null)
                {
                    int count = Math.Min(de.Count, expectedLength);
                    for (int i = 0; i < count; i++)
                    {
                        result[i] = de.Get<double>(i);
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static bool AnyVoxel(bool[,,] mask)
        {
            foreach (bool v in mask)
                if (v) return true;
            return false;
        }
    }
}
