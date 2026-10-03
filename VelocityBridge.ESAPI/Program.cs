using System;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoFLC
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Headless mode: when invoked with --config <json-path> the BDF
            // pipeline runs without ESAPI or a GUI. The config JSON uses the
            // same fields as BdfProcessingConfig + BdfOptions:
            //
            // {
            //   "bdf_path": "...",            // DVF: Velocity .bdf, Eclipse DICOM DR .dcm
            //                                  // or Plastimatch vf .mha ("dvf_type": "vf")
            //   "dvf_type": "auto",           // auto | bdf | dr | vf
            //   "ct_dir": "...",
            //   "rtstruct_path": "...",
            //   "output_dir": "...",
            //   "options": {
            //     "smooth_sigma_mm": 2.0,
            //     "exclude_gtv": true,
            //     "percentile_threshold": 75,
            //     "output_rtstruct": true,
            //     "output_jacobian_nifti": true,
            //     "output_ventilation_nifti": true
            //   }
            // }
            if (args.Length >= 2 &&
                (args[0] == "--config" || args[0] == "-c"))
            {
                string configPath = args[1];
                if (!File.Exists(configPath))
                {
                    Console.Error.WriteLine("Config file not found: " + configPath);
                    Environment.Exit(1);
                    return;
                }

                try
                {
                    string json = File.ReadAllText(configPath);
                    BdfProcessingConfig config = JsonConvert.DeserializeObject<BdfProcessingConfig>(json);
                    if (config == null)
                    {
                        Console.Error.WriteLine("Failed to parse config JSON.");
                        Environment.Exit(1);
                        return;
                    }

                    BdfProcessor processor = new BdfProcessor(msg =>
                    {
                        Console.WriteLine(msg);
                    });

                    JObject result = processor.Run(config);

                    // Write the result JSON next to the output directory.
                    string resultDir = config.OutputDir;
                    if (!string.IsNullOrEmpty(resultDir) && !Directory.Exists(resultDir))
                        Directory.CreateDirectory(resultDir);

                    string resultPath = Path.Combine(
                        resultDir ?? ".",
                        "result.json");
                    File.WriteAllText(resultPath, result.ToString(Formatting.Indented));

                    Console.WriteLine(result.ToString(Formatting.Indented));

                    bool success = result["success"] != null && result["success"].Value<bool>();
                    Environment.Exit(success ? 0 : 1);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FATAL: " + ex.Message);
                    Console.Error.WriteLine(ex.StackTrace);
                    Environment.Exit(2);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (sender, e) =>
            {
                MessageBox.Show(string.Format("Unhandled UI error: {0}", e.Exception.Message), "AutoFLC Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                string msg = ex != null ? ex.Message : "Unknown fatal error";
                MessageBox.Show(string.Format("Unhandled error: {0}", msg), "AutoFLC Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }
}
