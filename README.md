# AutoFLC

Automated Functional Lung Contouring (AutoFLC) is a Windows desktop tool for
radiation therapy departments that generates **functional lung contours** from
4DCT deformable image registration (DIR) data.

It runs as a Varian **Eclipse Scripting API (ESAPI)** plugin and works in two
steps:

1. **Data preparation** — connect to Eclipse, locate the 0% (end-inspiration)
   and 50% (end-expiration) phases of a 4DCT, and retrieve their CT series and
   structure sets from the Varian DICOM DB Daemon via DICOM C-FIND / C-MOVE.
   Three DIR-engine branches are supported:
   - **Velocity AI**: export both phases for manual DIR in Velocity and BDF
     export (the original workflow).
   - **Eclipse DIR**: one-click *Prepare Exp reference* builds an **ExpRef**
     series (new Series/SOP/Frame-of-Reference UIDs, uniform target slice
     thickness, world geometry unchanged), pushes it back to Eclipse via
     C-STORE and verifies it with C-FIND, so a rigid + deformable
     registration to the inspiration series can be run inside Eclipse and
     exported as a DICOM DR object.
   - **Plastimatch (local)**: one-click *Run Registration* exports both
     phases, then runs the open-source Plastimatch B-spline DIR locally
     (settings follow the published Plastimatch baseline of Lim 2026:
     six-stage schedule, MSE/LBFGSB, lung fixed_roi, lambda = 1). The stage preset,
     regularization lambda, lung-ROI restriction and plastimatch.exe path are
     exposed in the UI with the validated defaults; the run produces
     `<output>/<patient>/Plastimatch/vf.mha` (plus `register_cmd.txt`,
     `roi.mha` and a `plastimatch_run.json` manifest) and hands the DVF to
     Step 2 automatically. Requires Plastimatch 1.9.0 installed (probed at
     `C:\Program Files\Plastimatch\bin\plastimatch.exe`); DICOM is staged
     through an ASCII temp directory because plastimatch cannot read
     non-ASCII (e.g. Chinese) paths.
2. **AutoFLC & Import** — parse the **DVF** file (Velocity BDF, DICOM
   DR **or** Plastimatch vf `.mha`, auto-detected), compute the ventilation map with a selectable metric —
   **Jacobian**, **HU (density, Guerrero/Yamamoto with lung mass
   correction)**, or **both** (adds the PET-free rho(Jac, HU) consistency QC)
   — derive ventilation-weighted lung sub-volumes, and write a DICOM RTSTRUCT
   that can be pushed back to Eclipse via C-STORE. Every run also writes a
   `manifest.json` reproducibility record.

The analysis core is engine-agnostic: BDF, DR and the Plastimatch vf `.mha`
are just DVF carrier formats consumed by adapters (`BdfParser` / `DrParser` /
`VfMhaParser`); the DR adapter applies the
embedded pre-deformation rigid matrix, rejects non-identity post matrices, and
resamples the determinant from the cropped DR grid onto the original
expiration CT grid through DICOM world coordinates (bit-exact against the
reference Python pipeline, max |dJ| = 4.8e-7). The Plastimatch
adapter (same target->source semantics as BDF, mm, LPS origin from the MHA
header) was validated end-to-end against the same reference pipeline:
mean detJ in lung agrees to 5.6e-8 (sigma=0) / 6.9e-7 (sigma=2 mm).

All processing is self-contained in a single .NET Framework executable; no
Python runtime is required.

## How it works

The ventilation surrogate is the **Jacobian determinant** of the deforming
registration. A voxel with `det(J) > 1` has expanded between the reference and
moving phase, which corresponds to local inhalation; `det(J) < 1` corresponds
to local exhalation. Lung voxels are classified into three functional tiers by
percentile thresholds of `det(J) - 1`:

| Structure               | Definition                  |
| ----------------------- | --------------------------- |
| `FunctionalLung_High`   | ventilation >= upper percentile |
| `FunctionalLung_Intermediate` | between the percentiles     |
| `FunctionalLung_Low`    | ventilation <= lower percentile |

The pipeline steps are:

1. Read the reference (50% phase) CT series and, when available, the
   associated lung/GTV contours from an RTSTRUCT.
2. Parse the Velocity BDF (deformation vector field in mm).
3. Smooth each DVF component with a 3D Gaussian filter.
4. Compute `det(J) = det(I + du/dx)` with central differences.
5. Align `det(J)` from the BDF grid to the CT grid (trilinear resampling).
6. Build the functional lung masks (optionally excluding the GTV region).
7. Write `jacobian.nii.gz`, `ventilation.nii.gz`, and
   `functional_lung_for_eclipse.rtstruct.dcm`.
8. Compute **DIR QA** statistics from `det(J)`: folding (`detJ < 0`),
   contraction (`0 <= detJ < 1`), identity (`detJ == 1`), expansion
   (`detJ > 1`), and non-finite values, reported for the whole image and the
   lung region. The UI shows a warning when folding or non-finite values occur
   inside the lungs.

## Requirements

- Windows 10/11 x64
- [.NET SDK](https://dotnet.microsoft.com/download) (for building; the app
  targets .NET Framework 4.6.1)
- Varian Eclipse with the **Eclipse Scripting API (ESAPI)** installed and the
  ESAPI DLLs under `C:\esapi\API\`
- Varian **DICOM DB Daemon** running on the Aria/Eclipse workstation

The app is a standalone ESAPI executable: it launches outside Eclipse, but it
must run on a workstation where the ESAPI runtime and the DB Daemon are
reachable.

## Build

The project uses an SDK-style csproj targeting `net461` with C# 5. Dependencies
are pulled from NuGet (`fo-dicom.Desktop` 4.0.8, `Newtonsoft.Json`).

```
cd VelocityBridge.ESAPI
build.bat
```

or with Visual Studio / `dotnet`:

```
dotnet build VelocityBridge.ESAPI.csproj -c Debug
```

The output executable is `bin\Debug\AutoFLC.exe`. The ESAPI references point at
`C:\esapi\API\`; change the `<HintPath>` entries in
`VelocityBridge.ESAPI.csproj` if your ESAPI installation lives elsewhere.

## Usage

### GUI workflow

1. Open `AutoFLC.exe` on the Eclipse workstation.
2. In **Step 1: Export 4DCT**, enter the patient ID, connect to Eclipse,
   select a patient, and configure the DICOM DB Daemon (AE title, IP, port,
   local AE). Export the 0% and 50% phases.
3. In **Step 2: Process BDF & Import**, point at the Velocity BDF file, choose
   the reference data (default: the Step 1 export folder), adjust the options,
   and run. The generated RTSTRUCT is pushed back to Eclipse automatically via
   C-STORE.
4. Review the DIR QA panel. A red warning appears if registration folding
   (`detJ < 0`) or non-finite values are found within the lung region.

### Headless mode

The same pipeline can run without ESAPI or the GUI:

```
AutoFLC.exe --config config.json
```

The config file follows the `BdfProcessingConfig` schema:

```json
{
  "bdf_path": "C:\\data\\Phase50_4DCT.bdf",
  "ct_dir": "C:\\data\\Phase_50\\CT",
  "rtstruct_path": "C:\\data\\Phase_50\\RTSTRUCT\\RTSTRUCT_1.dcm",
  "output_dir": "C:\\data\\BDF_Output",
  "options": {
    "smooth_sigma_mm": 2.0,
    "exclude_gtv": true,
    "percentile_threshold": 75,
    "output_rtstruct": true,
    "output_jacobian_nifti": true,
    "output_ventilation_nifti": true
  }
}
```

`rtstruct_path` is optional; when absent, lung masks are taken from the JSON
contour file (`lung_contours_json`) or, in the GUI, auto-fetched from the
Eclipse 50% phase structure set. A `result.json` containing outputs,
statistics, and the DIR QA block is written into the output directory.

## Output

| File                                     | Description                                      |
| ---------------------------------------- | ------------------------------------------------ |
| `jacobian.nii.gz`                        | Jacobian determinant volume (float32 NIfTI-1)    |
| `ventilation.nii.gz`                     | `det(J) - 1` ventilation surrogate volume        |
| `functional_lung_for_eclipse.rtstruct.dcm` | RTSTRUCT with FunctionalLung_High/Intermediate/Low and the lung contours |
| `result.json`                            | Pipeline result with outputs, statistics, DIR QA |
| `processing_log.txt`                     | Activity log written by the GUI workflow         |

## Project layout

```
AutoFLC/
├── VelocityBridge.sln
├── VelocityBridge.ESAPI/
│   ├── MainForm.cs            # WinForms UI (Step 1 / Step 2 tabs)
│   ├── Program.cs             # entry point + headless --config mode
│   ├── BdfProcessor.cs        # pipeline orchestrator
│   ├── BdfParser.cs           # Velocity BDF reader
│   ├── JacobianEngine.cs      # gradients, det(J), functional masks, DIR QA
│   ├── GaussianFilter3D.cs    # separable 3D Gaussian smoothing
│   ├── CtResampler.cs         # slice-spacing uniformity + resampling
│   ├── RtstructWriter.cs      # mask -> RTSTRUCT writer
│   ├── ContourTracer.cs       # marching-squares contour tracing
│   ├── PolygonFiller.cs       # scan-line polygon rasterization
│   ├── NiftiWriter.cs         # minimal NIfTI-1 writer
│   ├── DicomDaemonService.cs  # C-FIND / C-MOVE / C-STORE client
│   ├── DicomStoreService.cs   # local C-STORE SCP
│   └── PhaseDetector.cs       # 0%/50% phase detection from image IDs
└── LICENSE
```

## Notes and limitations

- **ESAPI licensing.** The ESAPI assemblies are proprietary Varian libraries
  and are not distributed with this repository; you build against your own
  `C:\esapi\API\` installation.
- **Slice thickness.** Some Eclipse 4DCT protocols alternate slice thickness,
  which breaks Velocity registration. AutoFLC detects non-uniform spacing and
  resamples the exported CT to a uniform grid before registration, then C-STOREs
  the resampled data back.
- **Trademarks.** Eclipse, ESAPI, and Velocity are trademarks of Varian Medical
  Systems. This project is an independent community tool; it is not affiliated
  with, endorsed by, or sponsored by Varian.

## Disclaimer

**Not a medical device.** AutoFLC is research software, not a certified medical
device. It has not been reviewed or approved by any regulatory authority
(e.g. the U.S. FDA, or the competent authorities under the EU MDR), and it does
not provide medical advice, diagnosis, or treatment recommendations.

**Research use only.** The software is intended for research, teaching, and
internal evaluation. It computes research-grade contours from deformable
registration surrogates; the DIR QA panel and the resulting structure set must
be carefully reviewed by a qualified professional before any use in a
treatment workflow.

**Institutional responsibility.** Whether, and under what conditions, any
output of this software may be used in clinical practice is the sole
responsibility of the institution and its qualified users, subject to their
local laws, regulations, and institutional review processes. The authors make
no warranty of fitness for any particular purpose and accept no liability for
any use of the software or its outputs. The full disclaimer is provided by the
MIT license in [LICENSE](LICENSE).

## Development notes

- **Language level.** The project targets .NET Framework 4.6.1 with **C# 5**.
  Do not use C# 6+ syntax (string interpolation, `out var`, expression-bodied
  members, null-conditional `?.`, `is` pattern matching). `async`/`await` is
  allowed.
- **ESAPI thread affinity.** Every ESAPI call must run on the same UI thread
  that called `CreateApplication()`; calling from a worker thread aborts the
  process. Long operations run on a `BackgroundWorker`, but they must not touch
  ESAPI objects.
- **fo-dicom 4.0.8 is pinned intentionally.** Some `Get<T>` helpers are marked
  obsolete in 4.x; the warning is suppressed via `NoWarn CS0618`.
- **Array conventions.** The BDF parser returns the DVF as `[z, y, x, 3]` with
  components `(dx, dy, dz)` in mm. `JacobianEngine.Gradient3D` returns the
  (z, y, x) derivative components, matching a gradient call with spacing
  `(sz, sy, sx)`.
- **ESAPI references** use `HintPath C:\esapi\API\`; adjust if your Eclipse
  installation lives elsewhere.
- **Validation.** There is no test suite; build with `build.bat` and run the
  headless `--config` mode as a smoke test.

## License

MIT — see [LICENSE](LICENSE).
