using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoFLC
{
    /// <summary>
    /// Input for one Plastimatch registration run (Step 1, third engine
    /// branch). Fixed = expiration (50%) CT, moving = inspiration (0%) CT,
    /// mirroring the validated TCIA three-engine pipeline.
    /// </summary>
    public class PlastimatchRunArgs
    {
        /// <summary>Path to plastimatch.exe.</summary>
        public string ExecutablePath { get; set; }

        /// <summary>Expiration (50% phase) CT DICOM folder = fixed image.</summary>
        public string FixedCtDir { get; set; }

        /// <summary>Inspiration (0% phase) CT DICOM folder = moving image.</summary>
        public string MovingCtDir { get; set; }

        /// <summary>Reference (50% phase) RTSTRUCT used to build the lung fixed_roi.</summary>
        public string RoiRtstructPath { get; set; }

        /// <summary>Limit the registration to the lung ROI (validated default).</summary>
        public bool UseRoi { get; set; }

        /// <summary>B-spline regularization lambda (Lim 2026 baseline: 1.0).</summary>
        public double Lambda { get; set; }

        /// <summary>Stage preset index (see StagesForPreset).</summary>
        public int PresetIndex { get; set; }

        /// <summary>Destination folder for vf.mha and the run artifacts.</summary>
        public string OutputDir { get; set; }

        /// <summary>Progress/log callback (thread-safe).</summary>
        public Action<string> Log { get; set; }
    }

    /// <summary>Artifacts of a completed registration run.</summary>
    public class PlastimatchResult
    {
        public string VfPath { get; set; }
        public string CmdPath { get; set; }
        public string RoiPath { get; set; }
        public double ElapsedSeconds { get; set; }
        public int VfNx { get; set; }
        public int VfNy { get; set; }
        public int VfNz { get; set; }
    }

    /// <summary>
    /// Drives the Plastimatch command-line tool for the Step 1 "Plastimatch
    /// (local DIR)" engine branch:
    ///
    ///   P1  copy the exported phase DICOM folders into an ASCII-only temp
    ///       directory (plastimatch cannot read paths with non-ASCII
    ///       characters, e.g. Chinese folder names);
    ///   P2  plastimatch convert: fixed/moving DICOM -&gt; .mha;
    ///   P3  rasterize the reference RTSTRUCT lung contours -&gt; roi.mha
    ///       (fixed_roi, validated configuration);
    ///   P4  plastimatch register: six-stage B-spline DIR (Lim 2026
    ///       baseline) -&gt; vf.mha on the fixed CT grid;
    ///   P5  archive vf.mha + register_cmd.txt + roi.mha + a run manifest
    ///       into the output folder for Step 2 and reproducibility.
    /// </summary>
    public static class PlastimatchRunner
    {
        /// <summary>Hard cap for the registration process (six validated stages take ~2-4 min per case).</summary>
        private static readonly TimeSpan RegisterTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(5);

        private static readonly string[] DefaultProbePaths =
        {
            @"C:\Program Files\Plastimatch\bin\plastimatch.exe",
            @"C:\Program Files (x86)\Plastimatch\bin\plastimatch.exe"
        };

        /// <summary>
        /// Resolve plastimatch.exe: keep a valid configured path, otherwise
        /// probe the default install locations.
        /// </summary>
        public static string FindDefaultExecutable(string configuredPath)
        {
            if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
                return configuredPath;
            foreach (string p in DefaultProbePaths)
                if (File.Exists(p))
                    return p;
            return null;
        }

        /// <summary>
        /// Stage presets (grid spacing mm, res_vox, max iterations). Index 0
        /// is the Lim 2026 / VESPIR baseline validated on the TCIA cohort.
        /// </summary>
        public static int[][] StagesForPreset(int presetIndex)
        {
            if (presetIndex == 1)
                return new int[][]
                {
                    new int[] { 60, 3, 40 },
                    new int[] { 40, 2, 50 },
                    new int[] { 20, 1, 80 },
                    new int[] { 15, 1, 100 }
                };
            if (presetIndex == 2)
                return new int[][]
                {
                    new int[] { 40, 2, 50 },
                    new int[] { 20, 1, 80 },
                    new int[] { 15, 1, 100 }
                };
            return new int[][]
            {
                new int[] { 80, 4, 30 },
                new int[] { 60, 3, 40 },
                new int[] { 40, 2, 50 },
                new int[] { 30, 1, 60 },
                new int[] { 20, 1, 80 },
                new int[] { 15, 1, 100 }
            };
        }

        public static PlastimatchResult Run(PlastimatchRunArgs args)
        {
            Action<string> log = args.Log ?? delegate { };
            Stopwatch sw = Stopwatch.StartNew();

            string exe = FindDefaultExecutable(args.ExecutablePath);
            if (exe == null)
                throw new InvalidOperationException(
                    "plastimatch.exe was not found. Install Plastimatch (validated version: 1.9.0) " +
                    "or set the correct path in the engine settings. Expected locations: " +
                    string.Join("; ", DefaultProbePaths));

            if (string.IsNullOrEmpty(args.FixedCtDir) || !Directory.Exists(args.FixedCtDir))
                throw new DirectoryNotFoundException("Fixed (expiration 50%) CT folder not found: " + args.FixedCtDir);
            if (string.IsNullOrEmpty(args.MovingCtDir) || !Directory.Exists(args.MovingCtDir))
                throw new DirectoryNotFoundException("Moving (inspiration 0%) CT folder not found: " + args.MovingCtDir);
            if (string.IsNullOrEmpty(args.OutputDir))
                throw new ArgumentException("Plastimatch output folder is not set.");

            Directory.CreateDirectory(args.OutputDir);

            string workDir = CreateAsciiWorkDir(log);
            PlastimatchResult result = new PlastimatchResult();
            try
            {
                // P1: ASCII-safe copies of the two phase folders.
                log("P1: staging DICOM into an ASCII work directory (plastimatch cannot read non-ASCII paths):");
                log("    " + workDir);
                string fixedDicom = CopyDicomOrdered(args.FixedCtDir, Path.Combine(workDir, "fixed_dicom"));
                string movingDicom = CopyDicomOrdered(args.MovingCtDir, Path.Combine(workDir, "moving_dicom"));
                log(string.Format("    fixed {0} slice(s), moving {1} slice(s)",
                    CountFiles(fixedDicom), CountFiles(movingDicom)));

                // P2: DICOM -> .mha
                string fixedMha = Path.Combine(workDir, "fixed.mha");
                string movingMha = Path.Combine(workDir, "moving.mha");
                log("P2: converting phase CT series to .mha (plastimatch convert)...");
                RunTool(log, exe, "convert --input \"" + fixedDicom + "\" --output-img \"" + fixedMha + "\"", ConvertTimeout);
                RunTool(log, exe, "convert --input \"" + movingDicom + "\" --output-img \"" + movingMha + "\"", ConvertTimeout);

                // P3: lung fixed_roi from the reference RTSTRUCT.
                string roiMha = null;
                if (args.UseRoi)
                {
                    if (!string.IsNullOrEmpty(args.RoiRtstructPath) && File.Exists(args.RoiRtstructPath))
                    {
                        log("P3: building the lung fixed_roi from the reference RTSTRUCT...");
                        int nz, ny, nx;
                        double spRow, spCol, spZ, oX, oY, oZ;
                        bool[,,] lung = BdfProcessor.BuildLungMask(
                            args.FixedCtDir, args.RoiRtstructPath, log,
                            out nz, out ny, out nx,
                            out spRow, out spCol, out spZ,
                            out oX, out oY, out oZ);
                        roiMha = Path.Combine(workDir, "roi.mha");
                        WriteRoiMha(lung, nx, ny, nz, spCol, spRow, spZ, oX, oY, oZ, roiMha);
                        log(string.Format("    lung ROI: {0}x{1}x{2}, spacing ({3:F4}, {4:F4}, {5:F4}) mm",
                            nx, ny, nz, spCol, spRow, spZ));
                    }
                    else
                    {
                        log("P3: reference RTSTRUCT not found; registering the full field without the lung ROI.");
                    }
                }

                // P4: six-stage B-spline registration.
                int[][] stages = StagesForPreset(args.PresetIndex);
                string vfMha = Path.Combine(workDir, "vf.mha");
                string cmdFile = Path.Combine(workDir, "register_cmd.txt");
                WriteRegisterCmd(fixedMha, movingMha, roiMha, vfMha, args.Lambda, stages, cmdFile);
                log(string.Format("P4: plastimatch register ({0} B-spline stage(s), lambda={1})...",
                    stages.Length, args.Lambda.ToString("0.###", CultureInfo.InvariantCulture)));
                RunTool(log, exe, "register \"" + cmdFile + "\"", RegisterTimeout);
                if (!File.Exists(vfMha))
                    throw new InvalidOperationException(
                        "plastimatch register finished but produced no vf.mha; see the log above.");

                // P5: archive into the output folder + integrity check.
                int vnx, vny, vnz;
                ReadMhaDims(vfMha, out vnx, out vny, out vnz);
                result.VfNx = vnx;
                result.VfNy = vny;
                result.VfNz = vnz;
                result.VfPath = Path.Combine(args.OutputDir, "vf.mha");
                File.Copy(vfMha, result.VfPath, true);
                result.CmdPath = Path.Combine(args.OutputDir, "register_cmd.txt");
                File.Copy(cmdFile, result.CmdPath, true);
                if (roiMha != null && File.Exists(roiMha))
                {
                    result.RoiPath = Path.Combine(args.OutputDir, "roi.mha");
                    File.Copy(roiMha, result.RoiPath, true);
                }

                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                WriteRunManifest(args, result, exe);
                log(string.Format(
                    "P5: DVF archived ({0}x{1}x{2}, {3:F0} s): {4}",
                    result.VfNx, result.VfNy, result.VfNz, result.ElapsedSeconds, result.VfPath));
                return result;
            }
            finally
            {
                try { Directory.Delete(workDir, true); }
                catch { log("Warning: could not clean up the temp work directory: " + workDir); }
            }
        }

        /// <summary>
        /// Plastimatch's DICOM reader rejects paths containing non-ASCII
        /// characters, so the work directory must be ASCII-only. %TEMP% is
        /// used when its full path is ASCII, otherwise a folder on the
        /// system drive.
        /// </summary>
        private static string CreateAsciiWorkDir(Action<string> log)
        {
            string baseDir = Path.GetTempPath();
            if (!IsAscii(baseDir))
            {
                baseDir = @"C:\AutoFLC_Temp";
                log("Temp path contains non-ASCII characters; using " + baseDir + " instead.");
            }

            for (int attempt = 0; ; attempt++)
            {
                string candidate = Path.Combine(baseDir, "AutoFLC_Plasti",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                    (attempt == 0 ? "" : "_" + attempt));
                try
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch (Exception ex)
                {
                    if (attempt >= 3)
                        throw new InvalidOperationException(
                            "Could not create the Plastimatch work directory: " + ex.Message);
                }
            }
        }

        private static bool IsAscii(string s)
        {
            foreach (char c in s)
                if (c > 127)
                    return false;
            return true;
        }

        /// <summary>
        /// Copy *.dcm into an ASCII folder with sequential names (sorted for
        /// determinism; plastimatch derives geometry from the tags).
        /// </summary>
        private static string CopyDicomOrdered(string srcDir, string dstDir)
        {
            string[] files = Directory.GetFiles(srcDir, "*.dcm");
            if (files.Length == 0)
                throw new FileNotFoundException("No .dcm files in " + srcDir);
            Array.Sort(files, StringComparer.Ordinal);

            Directory.CreateDirectory(dstDir);
            for (int i = 0; i < files.Length; i++)
                File.Copy(files[i], Path.Combine(dstDir, string.Format(
                    CultureInfo.InvariantCulture, "{0:00000}.dcm", i)), true);
            return dstDir;
        }

        private static int CountFiles(string dir)
        {
            return Directory.GetFiles(dir, "*.dcm").Length;
        }

        private static void RunTool(Action<string> log, string exe, string arguments, TimeSpan timeout)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.ASCII;
            psi.StandardErrorEncoding = Encoding.ASCII;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);

            StringBuilder errTail = new StringBuilder();
            using (Process proc = new Process())
            {
                proc.StartInfo = psi;
                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        log("    " + e.Data);
                };
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        log("    " + e.Data);
                        errTail.AppendLine(e.Data);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(); }
                    catch { }
                    throw new TimeoutException(string.Format(
                        "plastimatch ({0}) timed out after {1} minutes.", arguments.Split(' ')[0], timeout.TotalMinutes));
                }

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException(string.Format(
                        "plastimatch failed with exit code {0}:\n{1}{2}",
                        proc.ExitCode, arguments, errTail.Length > 0 ? "\n" + errTail : ""));
            }
        }

        /// <summary>
        /// Write the register command file, mirroring the validated TCIA
        /// configuration (MSE / LBFGSB / B-spline, fixed_roi, vf_out).
        /// </summary>
        private static void WriteRegisterCmd(string fixedMha, string movingMha, string roiMha,
            string vfOut, double lambda, int[][] stages, string cmdFile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[GLOBAL]");
            sb.AppendLine("fixed=" + fixedMha);
            sb.AppendLine("moving=" + movingMha);
            if (roiMha != null)
                sb.AppendLine("fixed_roi=" + roiMha);
            sb.AppendLine("vf_out=" + vfOut);
            foreach (int[] stage in stages)
            {
                sb.AppendLine("[STAGE]");
                sb.AppendLine("xform=bspline");
                sb.AppendLine("impl=plastimatch");
                sb.AppendLine("optim=lbfgsb");
                sb.AppendLine("metric=mse");
                sb.AppendLine("regularization_lambda=" + lambda.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "grid_spac={0} {0} {0}", stage[0]));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "res_vox={0} {0} {0}", stage[1]));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "max_its={0}", stage[2]));
                sb.AppendLine("threading=openmp");
            }
            File.WriteAllText(cmdFile, sb.ToString(), Encoding.ASCII);
        }

        /// <summary>
        /// Write the boolean lung mask as a MetaIO uint8 label image with
        /// the exact header shape plastimatch consumed in the validated
        /// pipeline (ElementSpacing/DimSize ordered x y z, Offset = DICOM
        /// LPS origin, x fastest in the raw block).
        /// </summary>
        private static void WriteRoiMha(bool[,,] mask, int nx, int ny, int nz,
            double sx, double sy, double sz, double ox, double oy, double oz, string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ObjectType = Image");
            sb.AppendLine("NDims = 3");
            sb.AppendLine("BinaryData = True");
            sb.AppendLine("BinaryDataByteOrderMSB = False");
            sb.AppendLine("CompressedData = False");
            sb.AppendLine("TransformMatrix = 1 0 0 0 1 0 0 0 1");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Offset = {0:R} {1:R} {2:R}", ox, oy, oz));
            sb.AppendLine("CenterOfRotation = 0 0 0");
            sb.AppendLine("AnatomicalOrientation = RAI");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "ElementSpacing = {0:R} {1:R} {2:R}", sx, sy, sz));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "DimSize = {0} {1} {2}", nx, ny, nz));
            sb.AppendLine("ElementType = MET_UCHAR");
            sb.AppendLine("ElementDataFile = LOCAL");

            byte[] raw = new byte[(long)nx * ny * nz];
            long idx = 0;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        raw[idx++] = mask[z, y, x] ? (byte)1 : (byte)0;

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                fs.Write(head, 0, head.Length);
                fs.Write(raw, 0, raw.Length);
            }
        }

        /// <summary>Read DimSize (nx, ny, nz) from an .mha header.</summary>
        private static void ReadMhaDims(string path, out int nx, out int ny, out int nz)
        {
            nx = ny = nz = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader sr = new StreamReader(fs, Encoding.ASCII, false, 4096))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    if (!string.Equals(key, "DimSize", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Substring(eq + 1).Trim().Split(' ');
                    if (parts.Length == 3)
                    {
                        nx = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        ny = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        nz = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    }
                    break;
                }
            }
            if (nx <= 0 || ny <= 0 || nz <= 0)
                throw new InvalidDataException("Could not read DimSize from " + path);
        }

        private static void WriteRunManifest(PlastimatchRunArgs args, PlastimatchResult result, string exe)
        {
            try
            {
                JObject m = new JObject();
                m["tool"] = "AutoFLC PlastimatchRunner";
                m["executable"] = exe;
                m["fixed_ct_dir"] = args.FixedCtDir;
                m["moving_ct_dir"] = args.MovingCtDir;
                m["roi_rtstruct_path"] = args.RoiRtstructPath;
                m["use_roi"] = args.UseRoi;
                m["lambda"] = args.Lambda;
                m["preset_index"] = args.PresetIndex;
                m["vf_grid"] = new JArray(result.VfNx, result.VfNy, result.VfNz);
                m["elapsed_seconds"] = Math.Round(result.ElapsedSeconds, 1);
                m["vf_path"] = result.VfPath;
                m["cmd_path"] = result.CmdPath;
                m["roi_path"] = result.RoiPath;
                File.WriteAllText(Path.Combine(args.OutputDir, "plastimatch_run.json"),
                    m.ToString(Formatting.Indented));
            }
            catch (Exception)
            {
                // The manifest is a convenience record; never fail the run.
            }
        }
    }
}
