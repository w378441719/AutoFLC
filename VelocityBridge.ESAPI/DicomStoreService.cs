using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Dicom;
using Dicom.Log;
using Dicom.Network;

namespace AutoFLC
{
    /// <summary>
    /// Local DICOM C-STORE SCP that receives images pushed by the DB Daemon via
    /// C-MOVE. DicomDaemonService sets the CurrentContext before each C-MOVE.
    /// </summary>
    public class DicomStoreService : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
        {
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian
        };

        public DicomStoreService(INetworkStream stream, Encoding fallbackEncoding, Logger log)
            : base(stream, fallbackEncoding, log)
        {
        }

        private static void LogSafe(string msg)
        {
            var ctx = DicomStoreContext.Current;
            if (ctx != null && ctx.Log != null)
                ctx.Log(msg);
        }

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            LogSafe(string.Format("Association request received: {0} -> {1}", association.CallingAE, association.CalledAE));

            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification)
                {
                    pc.AcceptTransferSyntaxes(DicomTransferSyntax.ImplicitVRLittleEndian);
                }
                else
                {
                    pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                }
            }

            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            return SendAssociationReleaseResponseAsync();
        }

        public void OnConnectionClosed(Exception exception)
        {
            if (exception != null)
                LogSafe(string.Format("SCP connection closed: {0}", exception.Message));
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            LogSafe(string.Format("Association abort received: {0} / {1}", source, reason));
        }

        public DicomCEchoResponse OnCEchoRequest(DicomCEchoRequest request)
        {
            return new DicomCEchoResponse(request, DicomStatus.Success);
        }

        public DicomCStoreResponse OnCStoreRequest(DicomCStoreRequest request)
        {
            try
            {
                var ctx = DicomStoreContext.Current;
                if (ctx == null)
                    throw new InvalidOperationException("C-STORE context not set.");

                string folder = ctx.OutputFolder;
                if (string.IsNullOrEmpty(folder))
                    throw new InvalidOperationException("C-STORE output folder not set.");

                Directory.CreateDirectory(folder);

                DicomDataset ds = request.File != null ? request.File.Dataset : request.Dataset;
                if (ds == null)
                    throw new InvalidOperationException("No dataset in C-STORE request.");

                string sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, DicomUID.Generate().UID);
                string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "UN");
                string fileName = string.Format("{0}_{1}.dcm", modality, sopUid.Replace(".", ""));
                string path = Path.Combine(folder, fileName);

                if (request.File != null)
                    request.File.Save(path);
                else
                    new DicomFile(ds).Save(path);

                lock (ctx.Lock)
                {
                    ctx.ReceivedFiles.Add(path);
                }
                LogSafe(string.Format("  Received: {0} [{1}]", fileName, request.SOPInstanceUID.UID));

                return new DicomCStoreResponse(request, DicomStatus.Success);
            }
            catch (Exception ex)
            {
                LogSafe(string.Format("  C-STORE receive failed: {0}", ex.Message));
                return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
            }
        }

        public void OnCStoreRequestException(string tempFileName, Exception e)
        {
            LogSafe(string.Format("C-STORE parse exception: {0}", e.Message));
        }
    }

    /// <summary>
    /// Receive context for the local SCP. Only one C-MOVE is processed at a time.
    /// </summary>
    public class DicomStoreContext
    {
        private static DicomStoreContext _current;
        private static readonly object _contextLock = new object();

        public string OutputFolder { get; set; }
        public Action<string> Log { get; set; }
        public List<string> ReceivedFiles { get; private set; }
        public object Lock { get; private set; }

        public DicomStoreContext()
        {
            ReceivedFiles = new List<string>();
            Lock = new object();
        }

        public static DicomStoreContext Current
        {
            get { lock (_contextLock) { return _current; } }
        }

        public static void SetCurrent(DicomStoreContext context)
        {
            lock (_contextLock) { _current = context; }
        }
    }
}
