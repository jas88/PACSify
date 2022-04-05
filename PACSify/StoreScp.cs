using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Log;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace PACSify
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class StoreScp : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCStoreProvider
    {
        private readonly HashSet<string> _seriesList=new();
        private readonly string _workdir;
        private string _senderAe;
        private string _senderIp;
        private string _ourAe;

        private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxList = new DicomTransferSyntax[]
        {
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian
        };


        private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxList = new DicomTransferSyntax[]
        {
            // Lossless
            DicomTransferSyntax.JPEGLSLossless,
            DicomTransferSyntax.JPEG2000Lossless,
            DicomTransferSyntax.JPEGProcess14SV1,
            DicomTransferSyntax.JPEGProcess14,
            DicomTransferSyntax.RLELossless,

            // Lossy
            DicomTransferSyntax.JPEGLSNearLossless,
            DicomTransferSyntax.JPEG2000Lossy,
            DicomTransferSyntax.JPEGProcess1,
            DicomTransferSyntax.JPEGProcess2_4,

            // Uncompressed
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian
        };

        public StoreScp(INetworkStream stream, Encoding enc, ILogger log, ILogManager logManager, INetworkManager netManager,
            ITranscoderManager transcoder) : base(stream, enc, log, logManager, netManager, transcoder)
        {
            _workdir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_workdir);
        }

        /// <inheritdoc />
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            Logger.Warn($"Abort from {source.ToString()} due to {reason.ToString()}");
        }

        /// <inheritdoc />
        public void OnConnectionClosed(Exception exception)
        {
            Logger.Info($"Connection closed {exception}\nAbout to process: {_seriesList}");
            var existing = Directory.EnumerateFiles(_workdir).ToImmutableHashSet();

            // Run analysis script for each series in dataset
            foreach (var series in _seriesList)
            {
                using var p = new Process();
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.FileName = Program.Exe;
                p.StartInfo.Arguments = series;
                p.StartInfo.WorkingDirectory = _workdir;
                p.Start();
                p.WaitForExit();
                Logger.Info($"Finished processing {Program.Exe} {series}");
            }

            // Upload newly added files only
            var client = DicomClientFactory.Create(_senderIp, 104, false, _ourAe, _senderAe);
            client.NegotiateAsyncOps();
            foreach (var enumerateFile in Directory.EnumerateFiles(_workdir))
            {
                if (existing.Contains(enumerateFile))
                    continue;
                client.AddRequestAsync(new DicomCStoreRequest(enumerateFile));
                client.SendAsync();
            }
            Directory.Delete(_workdir,true);
        }

        /// <inheritdoc />
        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            _senderAe = association.CallingAE;
            _senderIp = association.RemoteHost;
            _ourAe = association.CalledAE;
            Logger.Info($"Connection from {association.CallingAE} calling us {association.CalledAE}");
            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification
                    || pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelFind
                    || pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove
                    || pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelFind
                    || pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove)
                {
                    pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxList);
                }
                else if (pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet
                         || pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet)
                {
                    pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxList);
                }
                else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                {
                    pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxList);
                }
                else
                {
                    Logger.Warn($"Requested abstract syntax {pc.AbstractSyntax} from {association.CallingAE} not supported");
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            return SendAssociationAcceptAsync(association);
        }

        /// <inheritdoc />
        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            return SendAssociationReleaseResponseAsync();
        }

        /// <inheritdoc />
#pragma warning disable 1998
        public async Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
#pragma warning restore 1998
        {
            return new DicomCEchoResponse(request, DicomStatus.Success);
        }

        /// <inheritdoc />
        public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            try
            {
                //var study = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID).Trim();
                _seriesList.Add(request.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
                var inst = request.SOPInstanceUID.UID;
                //var path = Path.Combine(Path.GetFullPath("output"), study);
                //Directory.CreateDirectory(path);
                //path = Path.Combine(path, $"{inst}.dcm");
                await request.File.SaveAsync(Path.Combine(_workdir,$"{inst}.dcm"));
                return new DicomCStoreResponse(request, DicomStatus.Success);
            }
            catch (Exception e)
            {
                Logger.Error($"Failed saving {request.SOPInstanceUID.UID} due to {e}");
                return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
            }
        }

        /// <inheritdoc />
#pragma warning disable 1998
        public async Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
#pragma warning restore 1998
        {
            Logger.Error($"Error {e}");
        }
    }
}