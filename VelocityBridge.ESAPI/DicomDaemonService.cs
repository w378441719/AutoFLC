using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dicom;
using Dicom.Network;

// The DicomClient API used here lives in the Dicom.Network namespace in
// fo-dicom 4.x and is marked obsolete (it moved in 5.x); the 4.x version is
// fully functional.
#pragma warning disable CS0618

namespace AutoFLC
{
    /// <summary>
    /// C-FIND / C-MOVE / C-STORE wrapper around the Varian DICOM DB Daemon.
    /// </summary>
    public class DicomDaemonService
    {
        private readonly string _daemonAETitle;
        private readonly string _daemonIP;
        private readonly int _daemonPort;
        private readonly string _localAETitle;
        private readonly int _localPort;
        private readonly Action<string> _log;

        public DicomDaemonService(string daemonAETitle, string daemonIP, int daemonPort, string localAETitle, int localPort, Action<string> log = null)
        {
            _daemonAETitle = daemonAETitle;
            _daemonIP = daemonIP;
            _daemonPort = daemonPort;
            _localAETitle = localAETitle;
            _localPort = localPort;
            _log = log ?? delegate { };
        }

        public async Task<bool> TestConnectionAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                var client = new DicomClient();
                var echo = new DicomCEchoRequest();
                bool success = false;
                echo.OnResponseReceived += (req, resp) => { success = resp.Status == DicomStatus.Success; };
                client.AddRequest(echo);
                await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 10000);
                _log(string.Format("DICOM Daemon C-ECHO: {0}", success ? "succeeded" : "failed"));
                return success;
            }
            catch (SocketException sex)
            {
                string reason;
                switch (sex.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused:
                        reason = "Connection refused. Please check the daemon IP/port and ensure the daemon is running.";
                        break;
                    case SocketError.HostNotFound:
                    case SocketError.TryAgain:
                        reason = "Host not found. Please verify the daemon IP address.";
                        break;
                    case SocketError.TimedOut:
                        reason = "Connection timed out. Please check network/Daemon status.";
                        break;
                    default:
                        reason = string.Format("Network error ({0}).", sex.SocketErrorCode);
                        break;
                }
                _log(string.Format("DICOM Daemon C-ECHO failed: {0}", reason));
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("DICOM Daemon C-ECHO exception: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Query the StudyInstanceUID for a given PatientID + SeriesInstanceUID.
        /// </summary>
        public async Task<string> FindStudyUidAsync(string patientId, string seriesUid, CancellationToken ct = default(CancellationToken))
        {
            var request = CreateSeriesFindRequest(patientId, seriesUid, null, null);
            string found = null;
            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Dataset != null)
                {
                    string studyUid = resp.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
                    if (!string.IsNullOrEmpty(studyUid) && string.IsNullOrEmpty(found))
                        found = studyUid;
                }
            };

            await SendFindAsync(request, ct);
            return found;
        }

        /// <summary>
        /// Count image instances of a series via an IMAGE-level C-FIND
        /// filtered by SeriesInstanceUID (used to verify the C-STORE of the
        /// ExpRef series in the Eclipse branch).
        /// </summary>
        public async Task<int> FindSeriesInstanceCountAsync(string patientId, string seriesUid, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(seriesUid))
                return -1;

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image, DicomPriority.Medium);
            request.Dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, "IMAGE");
            if (!string.IsNullOrEmpty(patientId))
                request.Dataset.AddOrUpdate(DicomTag.PatientID, patientId);
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
            request.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, string.Empty);

            int count = 0;
            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Dataset != null &&
                    !string.IsNullOrEmpty(resp.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "")))
                {
                    count++;
                }
            };

            await SendFindAsync(request, ct);
            return count;
        }

        /// <summary>
        /// Locate the SeriesInstanceUID of the RTSTRUCT associated with a phase
        /// (0% or 50%):
        /// 1. List all studies for the patient via PatientID.
        /// 2. List all series in each study (any modality).
        /// 3. If the StructureSet UID matches a SeriesInstanceUID directly, return it.
        /// 4. Otherwise fall back to an IMAGE-level C-FIND by SOPInstanceUID.
        /// </summary>
        public async Task<string> FindRtstructSeriesUidAsync(string patientId, string studyUid, string rtstructUid, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(studyUid))
            {
                _log("Find RTSTRUCT: StudyInstanceUID is empty, cannot query.");
                return null;
            }

            if (string.IsNullOrEmpty(rtstructUid))
            {
                _log("Find RTSTRUCT: StructureSet UID is empty, cannot query.");
                return null;
            }

            _log(string.Format("Find RTSTRUCT: Study={0}, StructureSet UID={1}...", studyUid, rtstructUid));

            var studyUids = new List<string>();
            if (!string.IsNullOrEmpty(patientId))
            {
                _log(string.Format("  Querying studies by PatientID={0}...", patientId));
                studyUids = await FindStudyUidsByPatientAsync(patientId, ct);
                if (studyUids.Count == 0)
                {
                    _log("  No studies found by PatientID, falling back to ESAPI StudyInstanceUID.");
                    studyUids.Add(studyUid);
                }
                else if (!studyUids.Contains(studyUid))
                {
                    _log("  ESAPI StudyInstanceUID not in PatientID query results, appending for retry.");
                    studyUids.Add(studyUid);
                }
            }
            else
            {
                studyUids.Add(studyUid);
            }

            foreach (string currentStudyUid in studyUids)
            {
                _log(string.Format("  Querying series under Study={0}...", currentStudyUid));
                List<string> seriesUids = await FindSeriesUidsByStudyAsync(currentStudyUid, ct);
                if (seriesUids.Count == 0)
                {
                    _log(string.Format("  No series found under Study={0}.", currentStudyUid));
                    continue;
                }

                if (seriesUids.Contains(rtstructUid))
                {
                    _log(string.Format("  StructureSet.UID is SeriesInstanceUID: {0}", rtstructUid));
                    return rtstructUid;
                }

                _log("  StructureSet.UID is not SeriesInstanceUID, trying SOPInstanceUID lookup...");
                foreach (string seriesUid in seriesUids)
                {
                    var imgRequest = new DicomCFindRequest(DicomQueryRetrieveLevel.Image, DicomPriority.Medium);
                    imgRequest.Dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, "IMAGE");
                    imgRequest.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, currentStudyUid);
                    imgRequest.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
                    imgRequest.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, rtstructUid);

                    bool found = false;
                    imgRequest.OnResponseReceived += (req, resp) =>
                    {
                        if (resp.Dataset != null)
                            found = true;
                    };

                    await SendFindAsync(imgRequest, ct);
                    if (found)
                    {
                        _log(string.Format("  Matched RTSTRUCT SeriesInstanceUID={0}", seriesUid));
                        return seriesUid;
                    }
                }
            }

            _log("  Associated RTSTRUCT series not found.");
            return null;
        }

        private async Task<List<string>> FindStudyUidsByPatientAsync(string patientId, CancellationToken ct = default(CancellationToken))
        {
            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study, DicomPriority.Medium);
            request.Dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, "STUDY");
            if (!string.IsNullOrEmpty(patientId))
                request.Dataset.AddOrUpdate(DicomTag.PatientID, patientId);
            // Request StudyInstanceUID as a return key.
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);

            var studyUids = new List<string>();
            request.OnResponseReceived += (req, resp) =>
            {
                _log(string.Format("    C-FIND Study response: {0}, hasDataset={1}", resp.Status, resp.Dataset != null));
                if (resp.Dataset == null) return;
                string suid = resp.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
                if (!string.IsNullOrEmpty(suid))
                    studyUids.Add(suid);
            };

            await SendFindAsync(request, ct);
            _log(string.Format("    Study-level C-FIND finished, {0} study(s).", studyUids.Count));
            return studyUids;
        }

        private async Task<List<string>> FindSeriesUidsByStudyAsync(string studyUid, CancellationToken ct = default(CancellationToken))
        {
            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series, DicomPriority.Medium);
            request.Dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, "SERIES");
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            // Request SeriesInstanceUID as a return key.
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);

            var seriesUids = new List<string>();
            request.OnResponseReceived += (req, resp) =>
            {
                _log(string.Format("    C-FIND Series response: {0}, hasDataset={1}", resp.Status, resp.Dataset != null));
                if (resp.Dataset == null) return;
                string suid = resp.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "");
                if (!string.IsNullOrEmpty(suid))
                    seriesUids.Add(suid);
            };

            await SendFindAsync(request, ct);
            _log(string.Format("    Series-level C-FIND finished, {0} series.", seriesUids.Count));
            return seriesUids;
        }

        /// <summary>
        /// C-MOVE a single series to the local SCP. A fresh SCP is started and
        /// stopped around the move.
        /// </summary>
        public async Task<List<string>> MoveSeriesAsync(string patientId, string studyUid, string seriesUid, string outputFolder, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(studyUid))
                throw new ArgumentException("StudyInstanceUID cannot be empty", "studyUid");
            if (string.IsNullOrEmpty(seriesUid))
                throw new ArgumentException("SeriesInstanceUID cannot be empty", "seriesUid");

            Directory.CreateDirectory(outputFolder);

            var context = CreateContext(outputFolder);
            DicomStoreContext.SetCurrent(context);

            IDicomServer server = null;
            try
            {
                server = await StartServerAsync(ct);
                return await ExecuteMoveAsync(server, patientId, studyUid, seriesUid, outputFolder, ct);
            }
            finally
            {
                if (server != null)
                {
                    try { server.Stop(); } catch { }
                    try { ((IDisposable)server).Dispose(); } catch { }
                }
                DicomStoreContext.SetCurrent(null);
            }
        }

        /// <summary>
        /// Execute a C-MOVE using an already-running local SCP, so that multiple
        /// phases can be exported on the same port without restarting it.
        /// </summary>
        public async Task<List<string>> MoveSeriesWithServerAsync(IDicomServer server, string patientId, string studyUid, string seriesUid, string outputFolder, CancellationToken ct = default(CancellationToken))
        {
            if (server == null)
                throw new ArgumentNullException("server");
            if (string.IsNullOrEmpty(studyUid))
                throw new ArgumentException("StudyInstanceUID cannot be empty", "studyUid");
            if (string.IsNullOrEmpty(seriesUid))
                throw new ArgumentException("SeriesInstanceUID cannot be empty", "seriesUid");

            Directory.CreateDirectory(outputFolder);

            var context = CreateContext(outputFolder);
            DicomStoreContext.SetCurrent(context);
            try
            {
                return await ExecuteMoveAsync(server, patientId, studyUid, seriesUid, outputFolder, ct);
            }
            finally
            {
                DicomStoreContext.SetCurrent(null);
            }
        }

        /// <summary>
        /// Fallback that pulls the whole study with a STUDY-level C-MOVE into a
        /// temporary folder and picks out the RTSTRUCT files. Used only when the
        /// targeted queries above fail; it can transfer a lot of data.
        /// </summary>
        public async Task<List<string>> MoveStudyAndPickRtstructAsync(IDicomServer server, string patientId, string studyUid, string outputFolder, CancellationToken ct = default(CancellationToken))
        {
            if (server == null)
                throw new ArgumentNullException("server");
            if (string.IsNullOrEmpty(studyUid))
                throw new ArgumentException("StudyInstanceUID cannot be empty", "studyUid");

            Directory.CreateDirectory(outputFolder);
            string tempFolder = Path.Combine(outputFolder, "_study_move_temp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);

            var context = CreateContext(tempFolder);
            DicomStoreContext.SetCurrent(context);
            try
            {
                var client = new DicomClient();
                client.NegotiateAsyncOps();

                var move = new DicomCMoveRequest(_localAETitle, studyUid, DicomPriority.Medium);
                int completed = 0;
                int remaining = int.MaxValue;
                int failures = 0;
                string lastError = null;

                move.OnResponseReceived += (req, resp) =>
                {
                    completed = resp.Completed;
                    remaining = resp.Remaining;
                    failures = resp.Failures;
                    if (resp.Status.State != DicomState.Pending)
                    {
                        if (resp.Status.State == DicomState.Failure)
                            lastError = resp.Status.ToString();
                    }
                    _log(string.Format("  Study C-MOVE progress: completed {0}, remaining {1}, failed {2} - {3}", resp.Completed, resp.Remaining, resp.Failures, resp.Status));
                };

                client.AddRequest(move);
                _log(string.Format("Sending Study-level C-MOVE: Study={0} -> {1}", studyUid, tempFolder));
                await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 600000);

                int poll = 0;
                while (poll < 100 && (remaining > 0 || GetReceivedCount(context) < completed + failures) && !ct.IsCancellationRequested)
                {
                    await Task.Delay(200, ct);
                    poll++;
                }

                if (!string.IsNullOrEmpty(lastError))
                    throw new InvalidOperationException("Study-level C-MOVE failed: " + lastError);

                _log(string.Format("Study-level C-MOVE complete, {0} file(s) in temp folder", GetReceivedCount(context)));

                var rtstructFiles = new List<string>();
                foreach (string file in GetReceivedFiles(context))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith("RTSTRUCT_", StringComparison.OrdinalIgnoreCase))
                    {
                        string dest = Path.Combine(outputFolder, name);
                        File.Copy(file, dest, true);
                        rtstructFiles.Add(dest);
                    }
                }

                try { Directory.Delete(tempFolder, true); } catch { }
                _log(string.Format("Extracted {0} RTSTRUCT file(s) from Study-level C-MOVE", rtstructFiles.Count));
                return rtstructFiles;
            }
            finally
            {
                DicomStoreContext.SetCurrent(null);
            }
        }

        private DicomStoreContext CreateContext(string outputFolder)
        {
            return new DicomStoreContext
            {
                OutputFolder = outputFolder,
                Log = _log
            };
        }

        private static int GetReceivedCount(DicomStoreContext context)
        {
            if (context == null) return 0;
            lock (context.Lock)
            {
                return context.ReceivedFiles.Count;
            }
        }

        private static List<string> GetReceivedFiles(DicomStoreContext context)
        {
            if (context == null) return new List<string>();
            lock (context.Lock)
            {
                return new List<string>(context.ReceivedFiles);
            }
        }

        public async Task<IDicomServer> StartServerAsync(CancellationToken ct = default(CancellationToken))
        {
            _log(string.Format("Starting local SCP: {0}@{1}", _localAETitle, _localPort));
            var server = DicomServer.Create<DicomStoreService>(_localPort, null, new DicomServiceOptions(), null, null);

            int wait = 0;
            while (!server.IsListening && wait < 50)
            {
                await Task.Delay(100, ct);
                wait++;
            }
            if (!server.IsListening)
                throw new InvalidOperationException("Local SCP failed to start.");
            return server;
        }

        private async Task<List<string>> ExecuteMoveAsync(IDicomServer server, string patientId, string studyUid, string seriesUid, string outputFolder, CancellationToken ct)
        {
            var context = DicomStoreContext.Current;
            if (context == null)
                throw new InvalidOperationException("C-MOVE context not set.");

            var client = new DicomClient();
            client.NegotiateAsyncOps();

            var move = new DicomCMoveRequest(_localAETitle, studyUid, seriesUid, DicomPriority.Medium);
            int completed = 0;
            int remaining = int.MaxValue;
            int failures = 0;
            string lastError = null;

            move.OnResponseReceived += (req, resp) =>
            {
                completed = resp.Completed;
                remaining = resp.Remaining;
                failures = resp.Failures;
                if (resp.Status.State != DicomState.Pending)
                {
                    if (resp.Status.State == DicomState.Failure)
                        lastError = resp.Status.ToString();
                }
                _log(string.Format("  C-MOVE progress: completed {0}, remaining {1}, failed {2} - {3}", resp.Completed, resp.Remaining, resp.Failures, resp.Status));
            };

            client.AddRequest(move);
            _log(string.Format("Sending C-MOVE: Study={0}, Series={1}", studyUid, seriesUid));
            await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 600000);

            // Give the last few C-STORE transfers time to reach disk.
            int poll = 0;
            while (poll < 100 && (remaining > 0 || GetReceivedCount(context) < completed + failures) && !ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct);
                poll++;
            }

            if (!string.IsNullOrEmpty(lastError))
                throw new InvalidOperationException("C-MOVE failed: " + lastError);

            _log(string.Format("C-MOVE complete, received {0} file(s) to {1}", GetReceivedCount(context), outputFolder));
            return GetReceivedFiles(context);
        }

        /// <summary>
        /// C-STORE a single DICOM file to the daemon.
        /// </summary>
        /// <summary>
        /// Bulk C-STORE of a CT series on a single association, ordered by
        /// InstanceNumber so ARIA/Eclipse assembles the slices into one
        /// reconstructable series (same approach as the hospital's
        /// DicomAutoImport tool). Returns the number of successful stores.
        /// </summary>
        public async Task<int> SendSeriesInOrderAsync(List<string> files, CancellationToken ct = default(CancellationToken))
        {
            if (files == null || files.Count == 0) return 0;

            // Sort by (0020,0013) InstanceNumber; unreadable files go last.
            var entries = new List<KeyValuePair<int, string>>();
            foreach (string f in files)
            {
                int num = int.MaxValue;
                try
                {
                    DicomFile df = await DicomFile.OpenAsync(f, FileReadOption.ReadLargeOnDemand);
                    num = df.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, int.MaxValue);
                }
                catch { }
                entries.Add(new KeyValuePair<int, string>(num, f));
            }
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));

            var client = new DicomClient();
            client.NegotiateAsyncOps();

            int sent = 0;
            foreach (var entry in entries)
            {
                try
                {
                    DicomFile df = await DicomFile.OpenAsync(entry.Value, FileReadOption.ReadLargeOnDemand);
                    var request = new DicomCStoreRequest(df);
                    string name = Path.GetFileName(entry.Value);
                    request.OnResponseReceived += (req, resp) =>
                    {
                        if (resp.Status == DicomStatus.Success) sent++;
                        else _log(string.Format("C-STORE failed ({0}): {1}", resp.Status, name));
                    };
                    client.AddRequest(request);
                }
                catch (Exception ex)
                {
                    _log(string.Format("C-STORE read error {0}: {1}", entry.Value, ex.Message));
                }
            }

            await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 120000);
            return sent;
        }

        public async Task<bool> SendSingleFileAsync(string filePath, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                var file = await DicomFile.OpenAsync(filePath, FileReadOption.ReadLargeOnDemand);
                var client = new DicomClient();
                client.NegotiateAsyncOps();

                var request = new DicomCStoreRequest(file);
                bool success = false;
                request.OnResponseReceived += (req, resp) =>
                {
                    success = resp.Status == DicomStatus.Success;
                    if (!success)
                        _log(string.Format("C-STORE response: {0}", resp.Status));
                };

                client.AddRequest(request);
                await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 60000);
                _log(string.Format("C-STORE {0}: {1}", Path.GetFileName(filePath), success ? "succeeded" : "failed"));
                return success;
            }
            catch (Exception ex)
            {
                _log(string.Format("C-STORE exception: {0}", ex.Message));
                return false;
            }
        }

        private DicomCFindRequest CreateSeriesFindRequest(string patientId, string seriesUid, string studyUid, string modality)
        {
            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series, DicomPriority.Medium);
            // The constructor already sets QueryRetrieveLevel; AddOrUpdate avoids
            // a duplicate-key exception.
            request.Dataset.AddOrUpdate(DicomTag.QueryRetrieveLevel, "SERIES");
            if (!string.IsNullOrEmpty(patientId))
                request.Dataset.AddOrUpdate(DicomTag.PatientID, patientId);
            if (!string.IsNullOrEmpty(studyUid))
                request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            if (!string.IsNullOrEmpty(seriesUid))
                request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
            if (!string.IsNullOrEmpty(modality))
                request.Dataset.AddOrUpdate(DicomTag.Modality, modality);

            // Request the key UIDs as return keys.
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);
            return request;
        }

        private async Task SendFindAsync(DicomCFindRequest request, CancellationToken ct)
        {
            var client = new DicomClient();
            client.NegotiateAsyncOps();
            client.AddRequest(request);
            await client.SendAsync(_daemonIP, _daemonPort, false, _localAETitle, _daemonAETitle, 60000);
        }

    }
}
