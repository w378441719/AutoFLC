using Newtonsoft.Json;

namespace AutoFLC
{
    /// <summary>
    /// Configuration passed to the DVF processing pipeline (BDF and DICOM DR
    /// sources share it; legacy field names are kept for compatibility).
    /// </summary>
    public class BdfProcessingConfig
    {
        /// <summary>Legacy BDF path field; still the primary JSON key "bdf_path".</summary>
        [JsonProperty("bdf_path")]
        public string BdfPath { get; set; }

        /// <summary>Generic alias "dvf_path"; takes precedence when both are set.</summary>
        [JsonProperty("dvf_path")]
        public string DvfPath { get; set; }

        /// <summary>"auto" (default, detected by content), "bdf", "dr" or "vf" (Plastimatch .mha).</summary>
        [JsonProperty("dvf_type")]
        public string DvfType { get; set; }

        /// <summary>Ventilation metric: "jacobian" (default), "hu" or "both".</summary>
        [JsonProperty("metric")]
        public string Metric { get; set; }

        /// <summary>Expiration (reference) CT directory.</summary>
        [JsonProperty("ct_dir")]
        public string CtDir { get; set; }

        /// <summary>Inspiration CT directory (required for the HU metric).</summary>
        [JsonProperty("insp_ct_dir")]
        public string InspCtDir { get; set; }

        [JsonProperty("rtstruct_path")]
        public string RtstructPath { get; set; }

        [JsonProperty("lung_contours_json")]
        public string LungContoursJson { get; set; }

        [JsonProperty("output_dir")]
        public string OutputDir { get; set; }

        [JsonProperty("options")]
        public BdfOptions Options { get; set; }

        public BdfProcessingConfig()
        {
            DvfType = "auto";
            Metric = "jacobian";
            Options = new BdfOptions();
        }

        /// <summary>Effective DVF file path (DvfPath wins over legacy BdfPath).</summary>
        [JsonIgnore]
        public string EffectiveDvfPath
        {
            get { return string.IsNullOrEmpty(DvfPath) ? BdfPath : DvfPath; }
        }
    }

    /// <summary>
    /// Processing options for the DVF pipeline.
    /// </summary>
    public class BdfOptions
    {
        [JsonProperty("smooth_sigma_mm")]
        public double SmoothSigmaMm { get; set; }

        /// <summary>HU-metric CT pre-smoothing variance in mm^2 (Yamamoto: 1.5).</summary>
        [JsonProperty("ct_sigma_mm2")]
        public double CtSigmaMm2 { get; set; }

        [JsonProperty("exclude_gtv")]
        public bool ExcludeGtv { get; set; }

        [JsonProperty("functional_method")]
        public string FunctionalMethod { get; set; }

        [JsonProperty("percentile_threshold")]
        public int PercentileThreshold { get; set; }

        [JsonProperty("output_rtstruct")]
        public bool OutputRtstruct { get; set; }

        [JsonProperty("output_jacobian_nifti")]
        public bool OutputJacobianNifti { get; set; }

        [JsonProperty("output_ventilation_nifti")]
        public bool OutputVentilationNifti { get; set; }

        /// <summary>Write hu_ventilation.nii.gz for the HU metric (default on).</summary>
        [JsonProperty("output_hu_nifti")]
        public bool OutputHuNifti { get; set; }

        /// <summary>Write a manifest.json describing all inputs/outputs (default on).</summary>
        [JsonProperty("output_manifest")]
        public bool OutputManifest { get; set; }

        public BdfOptions()
        {
            SmoothSigmaMm = 2.0;
            CtSigmaMm2 = 1.5;
            ExcludeGtv = true;
            FunctionalMethod = "percentile";
            PercentileThreshold = 75;
            OutputRtstruct = true;
            OutputJacobianNifti = true;
            OutputVentilationNifti = true;
            OutputHuNifti = true;
            OutputManifest = true;
        }
    }
}
