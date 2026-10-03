using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dicom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoFLC
{
    /// <summary>
    /// Orchestrates BDF processing and functional lung contour generation.
    /// </summary>
    public class BdfProcessor
    {
        private Action<string> _log;

        public BdfProcessor(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        /// <summary>
        /// Run the full BDF processing pipeline.
        /// </summary>
        /// <param name="config">Processing configuration.</param>
        /// <returns>Result object with outputs, statistics and DIR QA data.</returns>
        public JObject Run(BdfProcessingConfig config)
        {
            string dvfPathForManifest = config.EffectiveDvfPath;
            string ctDir = config.CtDir;
            string rtstructPath = config.RtstructPath;
            string lungContoursJson = config.LungContoursJson;
            string outputDir = config.OutputDir;
            BdfOptions opts = config.Options ?? new BdfOptions();

            Directory.CreateDirectory(outputDir);

            // --- Step 1: read the CT series ---
            _log("Reading CT series from " + ctDir);
            DicomCtSeries ctSeries = ReadCtSeries(ctDir, rtstructPath);
            _log(string.Format("CT grid: ({0}, {1}, {2}), spacing: [{3:F6}, {4:F6}, {5:F6}]",
                ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2),
                ctSeries.Spacing[0], ctSeries.Spacing[1], ctSeries.Spacing[2]));

            // --- Step 2: load the lung masks ---
            Dictionary<string, bool[,,]> masks = new Dictionary<string, bool[,,]>();

            if (!string.IsNullOrEmpty(rtstructPath) && File.Exists(rtstructPath))
            {
                _log("Loading RTSTRUCT masks from " + rtstructPath);
                masks = LoadRtstructMasks(rtstructPath, ctSeries);
            }
            else if (!string.IsNullOrEmpty(lungContoursJson) && File.Exists(lungContoursJson))
            {
                _log("Loading lung contours from JSON: " + lungContoursJson);
                masks = LoadContoursJsonMasks(lungContoursJson, ctSeries);
            }

            bool[,,] lungR, lungL, gtv1, gtv2;
            GetMaskOrEmpty(masks, "Lung_R", ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2), out lungR);
            GetMaskOrEmpty(masks, "Lung_L", ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2), out lungL);
            GetMaskOrEmpty(masks, "GTV1", ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2), out gtv1);
            GetMaskOrEmpty(masks, "GTV2", ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2), out gtv2);

            bool[,,] lungMask = OrMasks(lungR, lungL);
            bool[,,] gtvMask = OrMasks(gtv1, gtv2);

            if (!AnyTrue(lungMask))
            {
                throw new InvalidOperationException(
                    "Neither Lung_R nor Lung_L was found in the input RTSTRUCT/JSON contours. " +
                    "Functional lung contours cannot be generated without lung segmentation. " +
                    "Please check that the reference 50%-phase StructureSet contains lung contours " +
                    "with a recognised name (e.g. Lung_L, Lung_R, LeftLung, RightLung).");
            }

            // --- Step 3: load the DVF (BDF or DICOM DR adapter) ---
            string dvfPath = config.EffectiveDvfPath;
            if (string.IsNullOrEmpty(dvfPath) || !File.Exists(dvfPath))
                throw new FileNotFoundException("DVF file not found: " + dvfPath);

            string metric = (config.Metric ?? "jacobian").ToLowerInvariant();
            if (metric != "jacobian" && metric != "hu" && metric != "both")
                throw new ArgumentException("Unknown metric: " + config.Metric +
                    " (expected jacobian, hu or both)");

            string dvfType = (config.DvfType ?? "auto").ToLowerInvariant();
            if (dvfType == "auto")
            {
                if (DrParser.LooksLikeDr(dvfPath))
                    dvfType = "dr";
                else if (VfMhaParser.LooksLikeVfMha(dvfPath))
                    dvfType = "vf";
                else
                    dvfType = "bdf";
            }

            DvfField dvfField;
            BdfHeader bdfHeader = null; // kept for the legacy BDF resample path
            if (dvfType == "vf")
            {
                _log("Parsing Plastimatch VF (.mha): " + dvfPath);
                double[] firstIpp = GetDoubleArray(ctSeries.Datasets[0], DicomTag.ImagePositionPatient, 3);
                double[] fallbackOrigin = new double[] { firstIpp[0], firstIpp[1], GetSliceZ(ctSeries.Datasets[0]) };
                dvfField = VfMhaParser.Parse(dvfPath, fallbackOrigin);
                _log(string.Format("VF grid: ({0}, {1}, {2}), voxel size: ({3:F4}, {4:F4}, {5:F4}), origin: ({6:F2}, {7:F2}, {8:F2})",
                    dvfField.Nx, dvfField.Ny, dvfField.Nz,
                    dvfField.Sx, dvfField.Sy, dvfField.Sz,
                    dvfField.Ox, dvfField.Oy, dvfField.Oz));
                if (dvfField.Nx != ctSeries.Array.GetLength(2) ||
                    dvfField.Ny != ctSeries.Array.GetLength(1) ||
                    dvfField.Nz != ctSeries.Array.GetLength(0))
                {
                    _log("Warning: VF grid does not match the reference CT size; detJ will be resampled through world coordinates.");
                }
            }
            else if (dvfType == "dr")
            {
                _log("Parsing DICOM DR (Eclipse DIR export): " + dvfPath);
                dvfField = DrParser.Parse(dvfPath);
                _log(string.Format("DR grid: ({0}, {1}, {2}), voxel size: ({3:F4}, {4:F4}, {5:F4}), origin: ({6:F2}, {7:F2}, {8:F2})",
                    dvfField.Nx, dvfField.Ny, dvfField.Nz,
                    dvfField.Sx, dvfField.Sy, dvfField.Sz,
                    dvfField.Ox, dvfField.Oy, dvfField.Oz));
                double preT = Math.Sqrt(
                    dvfField.PreMatrix[0, 3] * dvfField.PreMatrix[0, 3] +
                    dvfField.PreMatrix[1, 3] * dvfField.PreMatrix[1, 3] +
                    dvfField.PreMatrix[2, 3] * dvfField.PreMatrix[2, 3]);
                _log(string.Format("DR pre-deformation rigid translation: {0:F2} mm (applied after the deformation)", preT));

                // Coverage check: DR grids are cropped bounding boxes; warn
                // when the reference CT extends outside (values there are
                // clamped to the grid edge).
                double ctZFirst = GetSliceZ(ctSeries.Datasets[0]);
                double ctZLast = GetSliceZ(ctSeries.Datasets[ctSeries.Datasets.Count - 1]);
                int uncovered = DrParser.CountSlicesOutsideGrid(dvfField, ctZFirst, ctZLast);
                if (uncovered > 0)
                    _log(string.Format(
                        "Warning: {0} CT slice position(s) lie outside the DR grid coverage; their ventilation values are edge-clamped.",
                        uncovered));
            }
            else
            {
                _log("Parsing Velocity BDF: " + dvfPath);
                BdfHeader header;
                float[,,,] bdfDvf;
                BdfParser.Parse(dvfPath, out header, out bdfDvf);
                bdfHeader = header;
                _log(string.Format("BDF grid: ({0}, {1}, {2}), voxel size: ({3}, {4}, {5})",
                    header.Nx, header.Ny, header.Nz,
                    header.Sx, header.Sy, header.Sz));
                if (header.Nx != ctSeries.Array.GetLength(2) ||
                    header.Ny != ctSeries.Array.GetLength(1) ||
                    header.Nz != ctSeries.Array.GetLength(0))
                {
                    _log("Warning: BDF grid does not match CT size");
                }
                dvfField = WrapBdf(bdfDvf, header, ctSeries);
            }

            float[,,,] dvf = dvfField.Vectors;

            // Spacing for the Jacobian: the DVF grid's own spacing, falling
            // back to the CT spacing when identical (legacy BDF behaviour).
            double[] spacing = new double[]
            {
                ctSeries.Spacing[0],
                ctSeries.Spacing[1],
                ctSeries.Spacing[2]
            };

            double[] dvfSpacing = dvfField.SpacingArray();
            bool useDvfSpacing = false;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(dvfSpacing[i] - spacing[i]) > 1e-6)
                {
                    useDvfSpacing = true;
                    break;
                }
            }

            double[] ctSpacing = new double[] { spacing[0], spacing[1], spacing[2] };
            if (useDvfSpacing)
            {
                _log(string.Format("Using DVF grid spacing ({0}, {1}, {2}) for Jacobian computation (CT spacing was ({3}, {4}, {5}))",
                    dvfSpacing[0], dvfSpacing[1], dvfSpacing[2],
                    spacing[0], spacing[1], spacing[2]));
                spacing[0] = dvfSpacing[0];
                spacing[1] = dvfSpacing[1];
                spacing[2] = dvfSpacing[2];
            }

            // --- Step 4: smooth the DVF ---
            double smoothSigma = opts.SmoothSigmaMm;
            if (smoothSigma > 0)
            {
                _log(string.Format("Smoothing DVF with sigma={0} mm", smoothSigma));
                float[,,] dx = ExtractComponent3D(dvf, 0);
                float[,,] dy = ExtractComponent3D(dvf, 1);
                float[,,] dz = ExtractComponent3D(dvf, 2);

                dx = GaussianFilter3D.Smooth(dx, smoothSigma, spacing);
                dy = GaussianFilter3D.Smooth(dy, smoothSigma, spacing);
                dz = GaussianFilter3D.Smooth(dz, smoothSigma, spacing);

                for (int z = 0; z < dvf.GetLength(0); z++)
                    for (int y = 0; y < dvf.GetLength(1); y++)
                        for (int x = 0; x < dvf.GetLength(2); x++)
                        {
                            dvf[z, y, x, 0] = dx[z, y, x];
                            dvf[z, y, x, 1] = dy[z, y, x];
                            dvf[z, y, x, 2] = dz[z, y, x];
                        }
            }
            else
            {
                _log("DVF smoothing skipped");
            }

            // --- Step 5: compute the Jacobian determinant ---
            _log("Computing Jacobian");
            float[,,] detJ = JacobianEngine.ComputeJacobian(dvf, spacing);

            // --- Step 6: align detJ from the DVF grid to the CT (output) grid ---
            // The output grid is always the original expiration CT series.
            // BDF keeps the legacy index-scaled path (zero regression); DR
            // grids have their own cropped origin, so the resampling goes
            // through DICOM world coordinates.
            if (detJ.GetLength(0) != ctSeries.Array.GetLength(0) ||
                detJ.GetLength(1) != ctSeries.Array.GetLength(1) ||
                detJ.GetLength(2) != ctSeries.Array.GetLength(2))
            {
                _log(string.Format("Aligning detJ from DVF grid ({0}, {1}, {2}) to CT grid ({3}, {4}, {5}) via {6}",
                    detJ.GetLength(0), detJ.GetLength(1), detJ.GetLength(2),
                    ctSeries.Array.GetLength(0), ctSeries.Array.GetLength(1), ctSeries.Array.GetLength(2),
                    dvfType == "bdf" ? "index scaling (legacy BDF)" : "world coordinates"));

                detJ = (dvfType == "bdf")
                    ? ResampleDetJ(detJ, bdfHeader, ctSeries)
                    : ResampleDetJWorld(detJ, dvfField, ctSeries);
                _log(string.Format("detJ aligned to ({0}, {1}, {2})",
                    detJ.GetLength(0), detJ.GetLength(1), detJ.GetLength(2)));
            }

            bool wantJac = metric == "jacobian" || metric == "both";
            bool wantHu = metric == "hu" || metric == "both";
            int percentile = opts.PercentileThreshold;
            bool excludeGtv = opts.ExcludeGtv;

            // --- Step 7: DIR QA (Jacobian) ---
            DirQaResult dirQa = null;
            if (wantJac)
            {
                try
                {
                    dirQa = JacobianEngine.ComputeDirQaStats(detJ, lungMask);
                }
                catch (Exception qaErr)
                {
                    _log("Warning: DIR QA statistics could not be computed: " + qaErr.Message);
                }
            }

            Dictionary<string, object> outputs = new Dictionary<string, object>();

            // --- Step 8/9/10: Jacobian functional masks, NIfTIs, RTSTRUCT ---
            FunctionalMasks funcMasks = null;
            if (wantJac)
            {
                _log(string.Format("Creating Jacobian functional lung masks (percentile={0})", percentile));
                funcMasks = JacobianEngine.CreateFunctionalMasks(
                    detJ, lungMask, excludeGtv ? gtvMask : null, percentile);

                if (opts.OutputJacobianNifti)
                {
                    string p = Path.Combine(outputDir, "jacobian.nii.gz");
                    _log("Writing NIfTI: " + p);
                    NiftiWriter.Write(p, detJ, ctSpacing);
                    outputs["jacobian_nifti"] = p;
                }

                if (opts.OutputVentilationNifti)
                {
                    // ventilation = detJ - 1.0
                    int nz = detJ.GetLength(0);
                    int ny = detJ.GetLength(1);
                    int nx = detJ.GetLength(2);
                    float[,,] vent = new float[nz, ny, nx];
                    for (int z = 0; z < nz; z++)
                        for (int y = 0; y < ny; y++)
                            for (int x = 0; x < nx; x++)
                                vent[z, y, x] = detJ[z, y, x] - 1.0f;

                    string p = Path.Combine(outputDir, "ventilation.nii.gz");
                    _log("Writing NIfTI: " + p);
                    NiftiWriter.Write(p, vent, ctSpacing);
                    outputs["ventilation_nifti"] = p;
                }

                if (opts.OutputRtstruct)
                {
                    string rtstructOut = Path.Combine(outputDir, "functional_lung_for_eclipse.rtstruct.dcm");
                    _log("Writing RTSTRUCT: " + rtstructOut);

                    Dictionary<string, bool[,,]> rtstructMasks = new Dictionary<string, bool[,,]>
                    {
                        { "FunctionalLung_High", funcMasks.High },
                        { "FunctionalLung_Intermediate", funcMasks.Intermediate },
                        { "FunctionalLung_Low", funcMasks.Low },
                        { "Lung_R", lungR },
                        { "Lung_L", lungL },
                    };

                    RtstructWriter.Write(ctSeries.Datasets, rtstructMasks, rtstructOut);
                    outputs["rtstruct"] = rtstructOut;
                }
            }

            // --- Step 8'/9'/10': HU (density) metric ---
            float[,,] vHu = null;
            FunctionalMasks huMasks = null;
            double huMassFactor = 1.0;
            if (wantHu)
            {
                if (string.IsNullOrEmpty(config.InspCtDir) || !Directory.Exists(config.InspCtDir))
                    throw new ArgumentException(
                        "The HU (density) metric requires the inspiration CT series; " +
                        "set insp_ct_dir (Step 1 exports it as the 0% phase).");

                _log("Reading inspiration CT series from " + config.InspCtDir);
                DicomCtSeries inspSeries = ReadCtSeries(config.InspCtDir, null);
                double ctSigma = Math.Sqrt(opts.CtSigmaMm2 > 0 ? opts.CtSigmaMm2 : 1.5);
                _log(string.Format("Smoothing both CT phases with isotropic Gaussian sigma={0:F4} mm (sigma^2={1:F2} mm^2)",
                    ctSigma, opts.CtSigmaMm2));

                float[,,] expS = GaussianFilter3D.Smooth(ctSeries.Array, ctSigma, ctSeries.Spacing);
                float[,,] inspS = GaussianFilter3D.Smooth(inspSeries.Array, ctSigma, inspSeries.Spacing);

                double[] inspZ = new double[inspSeries.Datasets.Count];
                for (int i = 0; i < inspSeries.Datasets.Count; i++)
                    inspZ[i] = GetSliceZ(inspSeries.Datasets[i]);
                double[] inspIpp = GetDoubleArray(inspSeries.Datasets[0], DicomTag.ImagePositionPatient, 3);

                double[] ctZ = new double[ctSeries.Datasets.Count];
                for (int i = 0; i < ctSeries.Datasets.Count; i++)
                    ctZ[i] = GetSliceZ(ctSeries.Datasets[i]);
                double[] ctIpp = GetDoubleArray(ctSeries.Datasets[0], DicomTag.ImagePositionPatient, 3);

                _log("Computing HU (density) ventilation with lung mass correction");
                vHu = HuVentilationEngine.Compute(
                    expS, inspS,
                    inspZ, inspIpp[0], inspIpp[1], inspSeries.Spacing[0], inspSeries.Spacing[1],
                    dvfField,
                    ctZ, ctIpp[0], ctIpp[1], ctSeries.Spacing[0], ctSeries.Spacing[1],
                    lungMask, out huMassFactor);
                _log(string.Format("HU mass-correction factor f = {0:F4}", huMassFactor));

                _log(string.Format("Creating HU functional lung masks (percentile={0})", percentile));
                huMasks = JacobianEngine.CreateFunctionalMasksFromVent(
                    vHu, lungMask, excludeGtv ? gtvMask : null, percentile);

                if (opts.OutputHuNifti)
                {
                    string p = Path.Combine(outputDir, "hu_ventilation.nii.gz");
                    _log("Writing NIfTI: " + p);
                    NiftiWriter.Write(p, vHu, ctSpacing);
                    outputs["hu_ventilation_nifti"] = p;
                }

                if (opts.OutputRtstruct)
                {
                    string rtstructOut = Path.Combine(outputDir, "functional_lung_hu_for_eclipse.rtstruct.dcm");
                    _log("Writing RTSTRUCT: " + rtstructOut);
                    Dictionary<string, bool[,,]> rtstructMasks = new Dictionary<string, bool[,,]>
                    {
                        { "FunctionalLung_HU_High", huMasks.High },
                        { "FunctionalLung_HU_Intermediate", huMasks.Intermediate },
                        { "FunctionalLung_HU_Low", huMasks.Low },
                        { "Lung_R", lungR },
                        { "Lung_L", lungL },
                    };
                    RtstructWriter.Write(ctSeries.Datasets, rtstructMasks, rtstructOut);
                    outputs["hu_rtstruct"] = rtstructOut;
                }
            }

            // --- Mutual consistency QC (Both mode; no PET required) ---
            double mutualRho = double.NaN;
            if (wantJac && wantHu)
            {
                List<float> a = new List<float>();
                List<float> b = new List<float>();
                for (int z = 0; z < detJ.GetLength(0); z += 2)
                    for (int y = 0; y < detJ.GetLength(1); y += 2)
                        for (int x = 0; x < detJ.GetLength(2); x += 2)
                        {
                            if (!lungMask[z, y, x]) continue;
                            a.Add(detJ[z, y, x] - 1.0f);
                            b.Add(vHu[z, y, x]);
                        }
                if (a.Count > 100)
                {
                    mutualRho = HuVentilationEngine.Spearman(a.ToArray(), b.ToArray());
                    _log(string.Format("Mutual consistency rho(Jac, HU) in lung = {0:F4} (reference QC metric, usable without PET; Lim 2026)",
                        mutualRho));
                }
            }

            _log("Processing completed");

            // --- Step 11: statistics ---
            double voxVol = ctSpacing[0] * ctSpacing[1] * ctSpacing[2];
            double totalLungVol = SumMask(lungMask) * voxVol / 1000.0;

            double jacobianMeanLung = 0.0;
            double jacobianStdLung = 0.0;
            double negativeJacobianRatio = 0.0;
            double lungExclGtvVol = 0.0, highVol = 0.0, interVol = 0.0, lowVol = 0.0;

            if (wantJac)
            {
                lungExclGtvVol = SumMask(funcMasks.LungExclGtv) * voxVol / 1000.0;
                highVol = SumMask(funcMasks.High) * voxVol / 1000.0;
                interVol = SumMask(funcMasks.Intermediate) * voxVol / 1000.0;
                lowVol = SumMask(funcMasks.Low) * voxVol / 1000.0;

                List<double> lungDetJVals = new List<double>();
                for (int z = 0; z < detJ.GetLength(0); z++)
                    for (int y = 0; y < detJ.GetLength(1); y++)
                        for (int x = 0; x < detJ.GetLength(2); x++)
                            if (lungMask[z, y, x])
                                lungDetJVals.Add(detJ[z, y, x]);

                if (lungDetJVals.Count > 0)
                {
                    double sum = 0.0;
                    int negCount = 0;
                    foreach (double v in lungDetJVals)
                    {
                        sum += v;
                        if (v < 0) negCount++;
                    }
                    jacobianMeanLung = sum / lungDetJVals.Count;
                    negativeJacobianRatio = (double)negCount / lungDetJVals.Count;

                    double varSum = 0.0;
                    foreach (double v in lungDetJVals)
                        varSum += (v - jacobianMeanLung) * (v - jacobianMeanLung);
                    jacobianStdLung = Math.Sqrt(varSum / lungDetJVals.Count);
                }
            }

            double huMeanLung = 0.0, huStdLung = 0.0;
            double huHighVol = 0.0, huInterVol = 0.0, huLowVol = 0.0;
            if (wantHu)
            {
                huHighVol = SumMask(huMasks.High) * voxVol / 1000.0;
                huInterVol = SumMask(huMasks.Intermediate) * voxVol / 1000.0;
                huLowVol = SumMask(huMasks.Low) * voxVol / 1000.0;

                double sum = 0.0; long count = 0;
                for (int z = 0; z < vHu.GetLength(0); z++)
                    for (int y = 0; y < vHu.GetLength(1); y++)
                        for (int x = 0; x < vHu.GetLength(2); x++)
                            if (lungMask[z, y, x]) { sum += vHu[z, y, x]; count++; }
                if (count > 0)
                {
                    huMeanLung = sum / count;
                    double varSum = 0.0;
                    for (int z = 0; z < vHu.GetLength(0); z++)
                        for (int y = 0; y < vHu.GetLength(1); y++)
                            for (int x = 0; x < vHu.GetLength(2); x++)
                                if (lungMask[z, y, x])
                                    varSum += (vHu[z, y, x] - huMeanLung) * (vHu[z, y, x] - huMeanLung);
                    huStdLung = Math.Sqrt(varSum / count);
                }
            }

            JObject stats = new JObject();
            stats["metric"] = metric;
            stats["dvf_type"] = dvfType;
            stats["total_lung_volume_cc"] = new JValue(totalLungVol);
            if (wantJac)
            {
                stats["lung_excl_gtv_volume_cc"] = new JValue(lungExclGtvVol);
                stats["high_functional_volume_cc"] = new JValue(highVol);
                stats["intermediate_functional_volume_cc"] = new JValue(interVol);
                stats["low_functional_volume_cc"] = new JValue(lowVol);
                stats["jacobian_mean_lung"] = new JValue(jacobianMeanLung);
                stats["jacobian_std_lung"] = new JValue(jacobianStdLung);
                stats["negative_jacobian_ratio"] = new JValue(negativeJacobianRatio);
            }
            if (wantHu)
            {
                stats["hu_mean_lung"] = new JValue(huMeanLung);
                stats["hu_std_lung"] = new JValue(huStdLung);
                stats["hu_high_functional_volume_cc"] = new JValue(huHighVol);
                stats["hu_intermediate_functional_volume_cc"] = new JValue(huInterVol);
                stats["hu_low_functional_volume_cc"] = new JValue(huLowVol);
                stats["hu_mass_factor"] = new JValue(huMassFactor);
            }
            if (!double.IsNaN(mutualRho))
                stats["mutual_rho_jac_hu"] = new JValue(mutualRho);

            JObject result = new JObject();
            result["success"] = JToken.FromObject(true);
            result["message"] = "Processing completed";

            JObject outputsObj = new JObject();
            foreach (KeyValuePair<string, object> kvp in outputs)
            {
                if (kvp.Value is string)
                    outputsObj[kvp.Key] = kvp.Value.ToString();
            }
            result["outputs"] = outputsObj;
            result["stats"] = stats;

            if (dirQa != null)
            {
                JObject dirQaObj = new JObject();
                dirQaObj["whole_image"] = SerializeDirQaRegion(dirQa.WholeImage);
                JToken lungQa = (dirQa.Lung != null) ? (JToken)SerializeDirQaRegion(dirQa.Lung) : (JToken)JValue.CreateNull();
                dirQaObj["lung"] = lungQa;
                result["dir_qa"] = dirQaObj;
            }

            // --- Step 12: manifest (reproducibility record per case) ---
            if (opts.OutputManifest)
            {
                try
                {
                    WriteManifest(config, dvfType, metric, dvfPathForManifest, ctSeries, outputs, stats, outputDir);
                }
                catch (Exception manifestErr)
                {
                    _log("Warning: manifest could not be written: " + manifestErr.Message);
                }
            }

            return result;
        }

        // ================================================================
        //  CT reading
        // ================================================================

        private class DicomCtSeries
        {
            public float[,,] Array { get; set; }
            public List<DicomDataset> Datasets { get; set; }
            public double[] Spacing { get; set; }
        }

        private DicomCtSeries ReadCtSeries(string ctDir, string rtstructPath)
        {
            string[] files = Directory.GetFiles(ctDir, "*.dcm");
            if (files.Length == 0)
                throw new FileNotFoundException("No DICOM files in " + ctDir);

            // Read all CT slices (metadata only initially).
            List<CtSliceInfo> slices = new List<CtSliceInfo>();
            int skipped = 0;

            foreach (string f in files)
            {
                try
                {
                    DicomFile df = DicomFile.Open(f, FileReadOption.Default);
                    DicomDataset ds = df.Dataset;
                    string modality = ds.Get<string>(DicomTag.Modality);
                    if (modality != "CT")
                    {
                        skipped++;
                        continue;
                    }

                    double[] ipp = GetDoubleArray(ds, DicomTag.ImagePositionPatient, 3);
                    double sliceLoc = ipp[2];

                    slices.Add(new CtSliceInfo
                    {
                        FilePath = f,
                        SliceLocation = sliceLoc,
                        Dataset = ds,
                        SeriesInstanceUID = ds.Get<string>(DicomTag.SeriesInstanceUID)
                    });
                }
                catch
                {
                    skipped++;
                }
            }

            if (skipped > 0)
                _log("Warning: skipped " + skipped + " invalid/non-CT DICOM file(s)");

            if (slices.Count == 0)
                throw new Exception("No CT slices found in " + ctDir);

            // Filter to the RTSTRUCT-referenced series when an RTSTRUCT is given.
            string targetUid = null;
            if (!string.IsNullOrEmpty(rtstructPath) && File.Exists(rtstructPath))
            {
                targetUid = GetReferencedSeriesUid(rtstructPath);
                if (!string.IsNullOrEmpty(targetUid))
                {
                    int nRef = slices.FindAll(s => s.SeriesInstanceUID == targetUid).Count;
                    if (nRef >= 10)
                    {
                        _log("Filtering CT series to RTSTRUCT-referenced UID " + targetUid);
                        slices = slices.FindAll(s => s.SeriesInstanceUID == targetUid);
                    }
                    else
                    {
                        _log("Warning: RTSTRUCT-referenced CT series has only " + nRef + " slice(s); falling back to largest series");
                        targetUid = null;
                    }
                }
            }

            // Without a target UID, use the series with the most slices.
            if (string.IsNullOrEmpty(targetUid))
            {
                Dictionary<string, int> seriesCounts = new Dictionary<string, int>();
                foreach (CtSliceInfo s in slices)
                {
                    if (!seriesCounts.ContainsKey(s.SeriesInstanceUID))
                        seriesCounts[s.SeriesInstanceUID] = 0;
                    seriesCounts[s.SeriesInstanceUID]++;
                }

                string bestUid = null;
                int bestCount = 0;
                foreach (KeyValuePair<string, int> kvp in seriesCounts)
                {
                    if (kvp.Value > bestCount)
                    {
                        bestCount = kvp.Value;
                        bestUid = kvp.Key;
                    }
                }
                slices = slices.FindAll(s => s.SeriesInstanceUID == bestUid);
            }

            slices.Sort((a, b) => a.SliceLocation.CompareTo(b.SliceLocation));

            // Read the full pixel data for the sorted slices.
            List<DicomDataset> datasets = new List<DicomDataset>();
            List<float[]> pixelData = new List<float[]>();

            foreach (CtSliceInfo si in slices)
            {
                DicomFile df = DicomFile.Open(si.FilePath, FileReadOption.ReadAll);
                DicomDataset ds = df.Dataset;
                datasets.Add(ds);

                ushort[] pixels = ds.Get<ushort[]>(DicomTag.PixelData);
                int rows = ds.Get<int>(DicomTag.Rows);
                int cols = ds.Get<int>(DicomTag.Columns);

                double slope = ds.Get<double>(DicomTag.RescaleSlope);
                double intercept = ds.Get<double>(DicomTag.RescaleIntercept);

                float[] slice = new float[rows * cols];
                for (int i = 0; i < pixels.Length; i++)
                    slice[i] = (float)(pixels[i] * slope + intercept);

                pixelData.Add(slice);
            }

            int nz = slices.Count;
            int ny = datasets[0].Get<int>(DicomTag.Rows);
            int nx = datasets[0].Get<int>(DicomTag.Columns);

            float[,,] huArray = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
            {
                float[] slice = pixelData[z];
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        huArray[z, y, x] = slice[y * nx + x];
            }

            double[] ps = GetDoubleArray(datasets[0], DicomTag.PixelSpacing, 2);
            double spacingZ = 0.0;
            if (nz > 1)
            {
                spacingZ = Math.Abs(slices[1].SliceLocation - slices[0].SliceLocation);
            }
            if (spacingZ <= 0)
            {
                try { spacingZ = datasets[0].Get<double>(DicomTag.SliceThickness); }
                catch { }
                if (spacingZ <= 0) spacingZ = 2.5;
            }

            DicomCtSeries series = new DicomCtSeries();
            series.Array = huArray;
            series.Datasets = datasets;
            series.Spacing = new double[] { ps[0], ps[1], spacingZ };

            return series;
        }

        private class CtSliceInfo
        {
            public string FilePath { get; set; }
            public double SliceLocation { get; set; }
            public DicomDataset Dataset { get; set; }
            public string SeriesInstanceUID { get; set; }
        }

        // ================================================================
        //  RTSTRUCT / JSON mask loading
        // ================================================================

        private Dictionary<string, bool[,,]> LoadRtstructMasks(string rtstructPath, DicomCtSeries ctSeries)
        {
            DicomFile df = DicomFile.Open(rtstructPath, FileReadOption.Default);
            DicomDataset ds = df.Dataset;
            int nz = ctSeries.Array.GetLength(0);
            int ny = ctSeries.Array.GetLength(1);
            int nx = ctSeries.Array.GetLength(2);

            // Build the slice Z locations.
            double[] sliceLocs = new double[nz];
            for (int i = 0; i < ctSeries.Datasets.Count && i < nz; i++)
            {
                double[] ipp = GetDoubleArray(ctSeries.Datasets[i], DicomTag.ImagePositionPatient, 3);
                sliceLocs[i] = ipp[2];
            }

            double sliceSpacing = 0.0;
            if (nz > 1)
            {
                List<double> diffs = new List<double>();
                for (int i = 1; i < nz; i++)
                    diffs.Add(Math.Abs(sliceLocs[i] - sliceLocs[i - 1]));
                diffs.Sort();
                sliceSpacing = diffs[diffs.Count / 2]; // median approximation
            }

            double zTolerance = 0.5 + 0.5 * Math.Abs(sliceSpacing);

            // Read the ROI names. Guarded: fo-dicom 4 throws on missing tags
            // and Varian RTSTRUCTs may omit optional sequences.
            DicomSequence ssRoiSeq = ds.Contains(DicomTag.StructureSetROISequence)
                ? ds.Get<DicomSequence>(DicomTag.StructureSetROISequence) : null;
            Dictionary<int, string> roiNames = new Dictionary<int, string>();
            if (ssRoiSeq != null)
            {
                foreach (DicomDataset roiDs in ssRoiSeq.Items)
                {
                    int num = int.Parse(roiDs.Get<string>(DicomTag.ROINumber));
                    string name = roiDs.Get<string>(DicomTag.ROIName);
                    roiNames[num] = name;
                }
            }

            DicomSequence roiContourSeq = ds.Contains(DicomTag.ROIContourSequence)
                ? ds.Get<DicomSequence>(DicomTag.ROIContourSequence) : null;
            Dictionary<string, bool[,,]> result = new Dictionary<string, bool[,,]>();

            if (roiContourSeq == null)
                return result;

            foreach (DicomDataset rc in roiContourSeq.Items)
            {
                int refRoiNum = int.Parse(rc.Get<string>(DicomTag.ReferencedROINumber));
                string roiName;
                if (!roiNames.TryGetValue(refRoiNum, out roiName))
                    continue;

                string canonicalName = CanonicalRoiName(roiName);
                bool[,,] mask = new bool[nz, ny, nx];

                if (!rc.Contains(DicomTag.ContourSequence))
                    continue;

                DicomSequence contourSeq = rc.Get<DicomSequence>(DicomTag.ContourSequence);

                // Each contour item is one closed loop along a single CT slice
                // (occasionally two loops on one slice, e.g. when a lung lobe is
                // split by the mediastinum). Every loop is rasterized
                // independently and OR-filled into the 3-D mask.

                foreach (DicomDataset contour in contourSeq.Items)
                {
                    double[] contourData = GetDoubleArray(contour, DicomTag.ContourData, -1);
                    if (contourData == null || contourData.Length < 9)
                        continue;

                    int nPts = contourData.Length / 3;
                    double z = contourData[2];

                    // Find the nearest CT slice.
                    int bestIdx = 0;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < nz; i++)
                    {
                        double d = Math.Abs(sliceLocs[i] - z);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestIdx = i;
                        }
                    }

                    if (bestDist > zTolerance)
                        continue;

                    DicomDataset refDs = ctSeries.Datasets[bestIdx];
                    double[] ipp = GetDoubleArray(refDs, DicomTag.ImagePositionPatient, 3);
                    double[] ps = GetDoubleArray(refDs, DicomTag.PixelSpacing, 2);
                    double[] iop = GetDoubleArray(refDs, DicomTag.ImageOrientationPatient, 6);

                    double[] rowDir = new double[] { iop[0], iop[1], iop[2] };
                    double[] colDir = new double[] { iop[3], iop[4], iop[5] };

                    // Convert every point to (row, col) pixel coordinates.
                    List<double> verts = new List<double>(nPts * 2);
                    for (int p = 0; p < nPts; p++)
                    {
                        double wx = contourData[p * 3];
                        double wy = contourData[p * 3 + 1];
                        double wz = contourData[p * 3 + 2];

                        double dx = wx - ipp[0];
                        double dy = wy - ipp[1];
                        double dz = wz - ipp[2];

                        double row = (dx * rowDir[0] + dy * rowDir[1] + dz * rowDir[2]) / ps[0];
                        double col = (dx * colDir[0] + dy * colDir[1] + dz * colDir[2]) / ps[1];

                        verts.Add(row);
                        verts.Add(col);
                    }

                    // Drop a duplicate closing vertex if present.
                    if (nPts >= 4)
                    {
                        double r0 = verts[0];
                        double c0 = verts[1];
                        int lastIdx = verts.Count - 2;
                        if (Math.Abs(verts[lastIdx] - r0) < 1e-9 &&
                            Math.Abs(verts[lastIdx + 1] - c0) < 1e-9)
                        {
                            verts.RemoveAt(lastIdx);
                            verts.RemoveAt(lastIdx);
                        }
                    }

                    int nVerts = verts.Count / 2;
                    if (nVerts < 3)
                        continue;

                    double[] rArr = new double[nVerts];
                    double[] cArr = new double[nVerts];
                    for (int v = 0; v < nVerts; v++)
                    {
                        rArr[v] = verts[v * 2];
                        cArr[v] = verts[v * 2 + 1];
                    }

                    bool[,] slice = new bool[ny, nx];
                    PolygonFiller.Fill(rArr, cArr, slice);

                    for (int y = 0; y < ny; y++)
                        for (int x = 0; x < nx; x++)
                            if (slice[y, x])
                                mask[bestIdx, y, x] = true;
                }

                result[canonicalName] = mask;
            }

            return result;
        }

        private Dictionary<string, bool[,,]> LoadContoursJsonMasks(string jsonPath, DicomCtSeries ctSeries)
        {
            string json = File.ReadAllText(jsonPath);
            JObject data = JObject.Parse(json);

            int nz = ctSeries.Array.GetLength(0);
            int ny = ctSeries.Array.GetLength(1);
            int nx = ctSeries.Array.GetLength(2);

            double[] sliceLocs = new double[nz];
            for (int i = 0; i < ctSeries.Datasets.Count && i < nz; i++)
            {
                double[] ipp = GetDoubleArray(ctSeries.Datasets[i], DicomTag.ImagePositionPatient, 3);
                sliceLocs[i] = ipp[2];
            }

            double sliceSpacing = 0.0;
            if (nz > 1)
            {
                List<double> diffs = new List<double>();
                for (int i = 1; i < nz; i++)
                    diffs.Add(Math.Abs(sliceLocs[i] - sliceLocs[i - 1]));
                diffs.Sort();
                sliceSpacing = diffs[diffs.Count / 2];
            }
            double zTolerance = 0.5 + 0.5 * Math.Abs(sliceSpacing);

            JToken structures = data["structures"];
            Dictionary<string, bool[,,]> result = new Dictionary<string, bool[,,]>();

            if (structures == null)
                return result;

            foreach (JProperty prop in structures.Children<JProperty>())
            {
                string name = prop.Name;
                string canonical = CanonicalRoiName(name);
                bool[,,] mask = new bool[nz, ny, nx];
                JToken slices = prop.Value;

                foreach (JProperty zProp in slices.Children<JProperty>())
                {
                    int zIdx = int.Parse(zProp.Name);
                    if (zIdx < 0 || zIdx >= nz)
                        continue;

                    DicomDataset refDs = ctSeries.Datasets[zIdx];
                    double[] ipp = GetDoubleArray(refDs, DicomTag.ImagePositionPatient, 3);
                    double[] ps = GetDoubleArray(refDs, DicomTag.PixelSpacing, 2);
                    double[] iop = GetDoubleArray(refDs, DicomTag.ImageOrientationPatient, 6);
                    double[] rowDir = new double[] { iop[0], iop[1], iop[2] };
                    double[] colDir = new double[] { iop[3], iop[4], iop[5] };

                    JArray loops = (JArray)zProp.Value;

                    // Aggregate every loop on this slice into one vertex buffer;
                    // all of them are OR-filled. A temporary 2-D mask per slice
                    // keeps this simple even when the JSON stores multiple
                    // disconnected loops per slice.
                    bool[,] slice = new bool[ny, nx];

                    foreach (JArray loop in loops)
                    {
                        if (loop.Count < 3)
                            continue;

                        int nPts = loop.Count;
                        double[] rArr = new double[nPts];
                        double[] cArr = new double[nPts];

                        for (int p = 0; p < nPts; p++)
                        {
                            JToken pt = loop[p];
                            double wx = (double)pt[0];
                            double wy = (double)pt[1];
                            double wz = (double)pt[2];

                            double dx = wx - ipp[0];
                            double dy = wy - ipp[1];
                            double dz = wz - ipp[2];

                            double row = (dx * rowDir[0] + dy * rowDir[1] + dz * rowDir[2]) / ps[0];
                            double col = (dx * colDir[0] + dy * colDir[1] + dz * colDir[2]) / ps[1];

                            rArr[p] = row;
                            cArr[p] = col;
                        }

                        // Drop a duplicate closing vertex if the loop is closed.
                        if (nPts >= 4 &&
                            Math.Abs(rArr[nPts - 1] - rArr[0]) < 1e-9 &&
                            Math.Abs(cArr[nPts - 1] - cArr[0]) < 1e-9)
                        {
                            double[] rTrim = new double[nPts - 1];
                            double[] cTrim = new double[nPts - 1];
                            Array.Copy(rArr, rTrim, nPts - 1);
                            Array.Copy(cArr, cTrim, nPts - 1);
                            rArr = rTrim;
                            cArr = cTrim;
                        }

                        PolygonFiller.Fill(rArr, cArr, slice);
                    }

                    for (int y = 0; y < ny; y++)
                        for (int x = 0; x < nx; x++)
                            if (slice[y, x])
                                mask[zIdx, y, x] = true;
                }

                result[canonical] = mask;
            }

            return result;
        }

        /// <summary>
        /// Build the union lung mask (Lung_L ∪ Lung_R) from a reference
        /// RTSTRUCT on its own CT grid, plus that grid's geometry — the
        /// Plastimatch runner uses this to write the fixed_roi label image.
        /// Spacing follows the DICOM convention of the rest of the pipeline:
        /// spacingRow/spacingCol are PixelSpacing[0]/[1].
        /// </summary>
        public static bool[,,] BuildLungMask(string ctDir, string rtstructPath, Action<string> log,
            out int nz, out int ny, out int nx,
            out double spacingRow, out double spacingCol, out double spacingZ,
            out double originX, out double originY, out double originZ)
        {
            BdfProcessor processor = new BdfProcessor(log);
            DicomCtSeries ct = processor.ReadCtSeries(ctDir, rtstructPath);
            Dictionary<string, bool[,,]> masks = processor.LoadRtstructMasks(rtstructPath, ct);

            nz = ct.Array.GetLength(0);
            ny = ct.Array.GetLength(1);
            nx = ct.Array.GetLength(2);

            bool[,,] lungL, lungR;
            GetMaskOrEmpty(masks, "Lung_L", nz, ny, nx, out lungL);
            GetMaskOrEmpty(masks, "Lung_R", nz, ny, nx, out lungR);
            bool[,,] lung = OrMasks(lungL, lungR);
            if (!AnyTrue(lung))
                throw new InvalidOperationException(
                    "No lung contours (Lung_L / Lung_R) found in " + rtstructPath +
                    "; the lung fixed_roi cannot be built.");

            double[] ipp = GetDoubleArray(ct.Datasets[0], DicomTag.ImagePositionPatient, 3);
            originX = ipp[0];
            originY = ipp[1];
            originZ = GetSliceZ(ct.Datasets[0]);
            spacingRow = ct.Spacing[0];
            spacingCol = ct.Spacing[1];
            spacingZ = ct.Spacing[2];
            return lung;
        }

        // ================================================================
        //  DVF helpers
        // ================================================================

        /// <summary>
        /// Wrap a parsed Velocity BDF into the engine-agnostic DvfField. The
        /// BDF grid shares the reference CT geometry, so its origin is the CT
        /// origin and the pre-matrix is the identity.
        /// </summary>
        private static DvfField WrapBdf(float[,,,] dvf, BdfHeader header, DicomCtSeries ctSeries)
        {
            double[] ipp = GetDoubleArray(ctSeries.Datasets[0], DicomTag.ImagePositionPatient, 3);
            DvfField f = new DvfField();
            f.Vectors = dvf;
            f.Nx = header.Nx;
            f.Ny = header.Ny;
            f.Nz = header.Nz;
            f.Sx = header.Sx;
            f.Sy = header.Sy;
            f.Sz = header.Sz;
            f.Ox = ipp != null ? ipp[0] : 0.0;
            f.Oy = ipp != null && ipp.Length > 1 ? ipp[1] : 0.0;
            f.Oz = GetSliceZ(ctSeries.Datasets[0]);
            f.SourceType = "BDF";
            f.SourcePath = null;
            return f;
        }

        private static double GetSliceZ(DicomDataset ds)
        {
            double[] ipp = GetDoubleArray(ds, DicomTag.ImagePositionPatient, 3);
            return (ipp != null && ipp.Length >= 3) ? ipp[2] : 0.0;
        }

        /// <summary>
        /// Trilinear resampling of detJ from a DVF grid with its own origin
        /// (Eclipse DR crops to a bounding box) onto the reference CT grid,
        /// through DICOM world coordinates. Edge values are clamped.
        /// </summary>
        private static float[,,] ResampleDetJWorld(float[,,] detJ, DvfField field, DicomCtSeries ctSeries)
        {
            int nzT = ctSeries.Array.GetLength(0);
            int nyT = ctSeries.Array.GetLength(1);
            int nxT = ctSeries.Array.GetLength(2);

            int nzS = detJ.GetLength(0);
            int nyS = detJ.GetLength(1);
            int nxS = detJ.GetLength(2);

            double[] ctZ = new double[nzT];
            for (int i = 0; i < nzT; i++)
                ctZ[i] = GetSliceZ(ctSeries.Datasets[i]);
            double cox = GetDoubleArray(ctSeries.Datasets[0], DicomTag.ImagePositionPatient, 3)[0];
            double coy = GetDoubleArray(ctSeries.Datasets[0], DicomTag.ImagePositionPatient, 3)[1];
            double cpr = ctSeries.Spacing[0];
            double cpc = ctSeries.Spacing[1];

            // Pre-computed fractional coordinates per axis (clamped, matching
            // the Python cross-check pipeline's n-1.001 edge handling).
            double[] zs = new double[nzT];
            for (int zt = 0; zt < nzT; zt++)
            {
                double v = (ctZ[zt] - field.Oz) / field.Sz;
                if (v < 0) v = 0;
                if (v > nzS - 1.001) v = nzS - 1.001;
                zs[zt] = v;
            }
            double[] ys = new double[nyT];
            for (int yt = 0; yt < nyT; yt++)
            {
                double v = (coy + yt * cpr - field.Oy) / field.Sy;
                if (v < 0) v = 0;
                if (v > nyS - 1.001) v = nyS - 1.001;
                ys[yt] = v;
            }
            double[] xs = new double[nxT];
            for (int xt = 0; xt < nxT; xt++)
            {
                double v = (cox + xt * cpc - field.Ox) / field.Sx;
                if (v < 0) v = 0;
                if (v > nxS - 1.001) v = nxS - 1.001;
                xs[xt] = v;
            }

            float[,,] result = new float[nzT, nyT, nxT];
            for (int zt = 0; zt < nzT; zt++)
                for (int yt = 0; yt < nyT; yt++)
                    for (int xt = 0; xt < nxT; xt++)
                        result[zt, yt, xt] = HuVentilationEngine.Trilinear(detJ, zs[zt], ys[yt], xs[xt]);
            return result;
        }

        /// <summary>
        /// Reproducibility record for one processing run: inputs (with SHA256
        /// of the DVF file), parameters, key statistics and produced outputs.
        /// </summary>
        private void WriteManifest(BdfProcessingConfig config, string dvfType, string metric,
            string dvfPath, DicomCtSeries ctSeries, Dictionary<string, object> outputs,
            JObject stats, string outputDir)
        {
            JObject m = new JObject();
            m["tool"] = "AutoFLC";
            m["dvf_source_type"] = dvfType;
            m["metric"] = metric;
            m["dvf_path"] = dvfPath;
            try
            {
                m["dvf_sha256"] = Sha256File(dvfPath);
            }
            catch { }

            JObject ctInfo = new JObject();
            try
            {
                ctInfo["dir"] = config.CtDir;
                ctInfo["series_uid"] = ctSeries.Datasets[0].Get<string>(DicomTag.SeriesInstanceUID, null);
                ctInfo["study_uid"] = ctSeries.Datasets[0].Get<string>(DicomTag.StudyInstanceUID, null);
                ctInfo["slices"] = ctSeries.Datasets.Count;
                ctInfo["spacing"] = new JArray(ctSeries.Spacing[0], ctSeries.Spacing[1], ctSeries.Spacing[2]);
            }
            catch { }
            m["reference_ct"] = ctInfo;

            if (!string.IsNullOrEmpty(config.InspCtDir))
                m["insp_ct_dir"] = config.InspCtDir;
            if (!string.IsNullOrEmpty(config.RtstructPath))
                m["rtstruct_path"] = config.RtstructPath;
            if (!string.IsNullOrEmpty(config.LungContoursJson))
                m["lung_contours_json"] = config.LungContoursJson;

            JObject p = new JObject();
            p["dvf_smooth_sigma_mm"] = config.Options.SmoothSigmaMm;
            p["ct_sigma_mm2"] = config.Options.CtSigmaMm2;
            p["percentile_threshold"] = config.Options.PercentileThreshold;
            p["exclude_gtv"] = config.Options.ExcludeGtv;
            m["parameters"] = p;

            JObject outs = new JObject();
            foreach (KeyValuePair<string, object> kvp in outputs)
            {
                if (kvp.Value is string)
                    outs[kvp.Key] = kvp.Value.ToString();
            }
            m["outputs"] = outs;
            m["stats"] = stats;

            string path = Path.Combine(outputDir, "manifest.json");
            File.WriteAllText(path, m.ToString(Newtonsoft.Json.Formatting.Indented));
            _log("Manifest written: " + path);
        }

        private static string Sha256File(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ================================================================
        //  Resampling (detJ from the BDF grid to the CT grid)
        // ================================================================

        private static float[,,] ResampleDetJ(float[,,] detJ, BdfHeader bdfHeader, DicomCtSeries ctSeries)
        {
            int nzT = ctSeries.Array.GetLength(0);
            int nyT = ctSeries.Array.GetLength(1);
            int nxT = ctSeries.Array.GetLength(2);

            int nzS = detJ.GetLength(0);
            int nyS = detJ.GetLength(1);
            int nxS = detJ.GetLength(2);

            double sx = bdfHeader.Sx;
            double sy = bdfHeader.Sy;
            double sz = bdfHeader.Sz;

            double tx = ctSeries.Spacing[0];
            double ty = ctSeries.Spacing[1];
            double tz = ctSeries.Spacing[2];

            float[,,] result = new float[nzT, nyT, nxT];

            // Trilinear interpolation.
            for (int zt = 0; zt < nzT; zt++)
            {
                double zs = (double)zt * tz / sz;
                if (zs < 0) zs = 0;
                if (zs >= nzS - 1) zs = nzS - 1.001;

                for (int yt = 0; yt < nyT; yt++)
                {
                    double ys = (double)yt * ty / sy;
                    if (ys < 0) ys = 0;
                    if (ys >= nyS - 1) ys = nyS - 1.001;

                    for (int xt = 0; xt < nxT; xt++)
                    {
                        double xs = (double)xt * tx / sx;
                        if (xs < 0) xs = 0;
                        if (xs >= nxS - 1) xs = nxS - 1.001;

                        result[zt, yt, xt] = TrilinearInterpolate(detJ, xs, ys, zs);
                    }
                }
            }

            return result;
        }

        private static float TrilinearInterpolate(float[,,] vol, double x, double y, double z)
        {
            int x0 = (int)x;
            int y0 = (int)y;
            int z0 = (int)z;
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            double fx = x - x0;
            double fy = y - y0;
            double fz = z - z0;

            int nx = vol.GetLength(2);
            int ny = vol.GetLength(1);
            int nz = vol.GetLength(0);

            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (z0 < 0) z0 = 0;
            if (x1 >= nx) x1 = nx - 1;
            if (y1 >= ny) y1 = ny - 1;
            if (z1 >= nz) z1 = nz - 1;

            double v000 = vol[z0, y0, x0];
            double v100 = vol[z0, y0, x1];
            double v010 = vol[z0, y1, x0];
            double v110 = vol[z0, y1, x1];
            double v001 = vol[z1, y0, x0];
            double v101 = vol[z1, y0, x1];
            double v011 = vol[z1, y1, x0];
            double v111 = vol[z1, y1, x1];

            // Interpolate along X, then Y, then Z.
            double v00 = v000 * (1 - fx) + v100 * fx;
            double v01 = v001 * (1 - fx) + v101 * fx;
            double v10 = v010 * (1 - fx) + v110 * fx;
            double v11 = v011 * (1 - fx) + v111 * fx;

            double v0 = v00 * (1 - fy) + v10 * fy;
            double v1 = v01 * (1 - fy) + v11 * fy;

            return (float)(v0 * (1 - fz) + v1 * fz);
        }

        // ================================================================
        //  Helpers
        // ================================================================

        private static string CanonicalRoiName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            string n = name.ToUpperInvariant()
                .Replace(" ", "").Replace("_", "").Replace("-", "");

            // Normalize the lung naming variants seen in different Eclipse
            // environments (e.g. "Lung_L", "OR Lung-L", "LeftLung").
            if (n == "LUNGL" || n == "LEFTLUNG" || n == "LUNGLEFT" || n == "ORLUNGL" || n == "ORLUNGLEFT")
                return "Lung_L";
            if (n == "LUNGR" || n == "RIGHTLUNG" || n == "LUNGRIGHT" || n == "ORLUNGR" || n == "ORLUNGRIGHT")
                return "Lung_R";
            return name;
        }

        private static string GetReferencedSeriesUid(string rtstructPath)
        {
            DicomFile df = DicomFile.Open(rtstructPath, FileReadOption.Default);
            DicomDataset ds = df.Dataset;

            DicomSequence refForSeq = ds.Contains(DicomTag.ReferencedFrameOfReferenceSequence)
                ? ds.Get<DicomSequence>(DicomTag.ReferencedFrameOfReferenceSequence) : null;
            if (refForSeq == null) return null;

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
                        try { return series.Get<string>(DicomTag.SeriesInstanceUID); }
                        catch { }
                    }
                }
            }
            return null;
        }

        private static bool[,,] OrMasks(bool[,,] a, bool[,,] b)
        {
            int nz = a.GetLength(0);
            int ny = a.GetLength(1);
            int nx = a.GetLength(2);
            bool[,,] result = new bool[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        result[z, y, x] = a[z, y, x] || b[z, y, x];
            return result;
        }

        private static bool AnyTrue(bool[,,] mask)
        {
            foreach (bool v in mask)
                if (v) return true;
            return false;
        }

        private static long SumMask(bool[,,] mask)
        {
            long sum = 0;
            foreach (bool v in mask)
                if (v) sum++;
            return sum;
        }

        private static double[] GetDoubleArray(DicomDataset ds, DicomTag tag, int expectedLength)
        {
            if (!ds.Contains(tag))
                return expectedLength > 0 ? new double[expectedLength] : null;

            try
            {
                DicomElement de = ds.GetDicomItem<DicomElement>(tag);
                if (de == null) return new double[expectedLength > 0 ? expectedLength : 1];

                int count = de.Count;
                if (expectedLength > 0 && count > expectedLength)
                    count = expectedLength;

                double[] result = new double[count];
                for (int i = 0; i < count; i++)
                    result[i] = de.Get<double>(i);
                return result;
            }
            catch
            {
                return expectedLength > 0 ? new double[expectedLength] : null;
            }
        }

        private static void GetMaskOrEmpty(Dictionary<string, bool[,,]> masks, string key,
            int nz, int ny, int nx, out bool[,,] result)
        {
            if (masks.TryGetValue(key, out result))
                return;
            result = new bool[nz, ny, nx];
        }

        private static float[,,] ExtractComponent3D(float[,,,] dvf, int comp)
        {
            int nz = dvf.GetLength(0);
            int ny = dvf.GetLength(1);
            int nx = dvf.GetLength(2);
            float[,,] result = new float[nz, ny, nx];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        result[z, y, x] = dvf[z, y, x, comp];
            return result;
        }

        private static JObject SerializeDirQaRegion(DirQaRegionStats stats)
        {
            JObject obj = new JObject();
            obj["total_voxels"] = stats.TotalVoxels;
            obj["folding_count"] = stats.FoldingCount;
            obj["folding_percent"] = stats.FoldingPercent;
            obj["contraction_count"] = stats.ContractionCount;
            obj["contraction_percent"] = stats.ContractionPercent;
            obj["identity_count"] = stats.IdentityCount;
            obj["identity_percent"] = stats.IdentityPercent;
            obj["expansion_count"] = stats.ExpansionCount;
            obj["expansion_percent"] = stats.ExpansionPercent;
            obj["nonfinite_count"] = stats.NonfiniteCount;
            obj["nonfinite_percent"] = stats.NonfinitePercent;
            obj["min_detj"] = stats.MinDetJ;
            obj["max_detj"] = stats.MaxDetJ;
            obj["mean_detj"] = stats.MeanDetJ;
            obj["std_detj"] = stats.StdDetJ;
            return obj;
        }
    }
}
