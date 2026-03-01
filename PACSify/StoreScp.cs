using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;

namespace PACSify;

// ReSharper disable once ClassNeverInstantiated.Global
public partial class StoreScp : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCStoreProvider
{
    private readonly HashSet<string> _seriesList = new();
    private readonly string _workdir;
    private string _senderAe = string.Empty;
    private string _senderIp = string.Empty;
    private string _ourAe = string.Empty;

    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxList =
    [
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    ];

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxList =
    [
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
    ];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abort from {Source} due to {Reason}")]
    private static partial void LogAbort(ILogger logger, DicomAbortSource source, DicomAbortReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection closed, about to process: {SeriesList}")]
    private static partial void LogConnectionClosed(ILogger logger, Exception? exception, HashSet<string> seriesList);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finished processing {Exe} {Series}")]
    private static partial void LogFinishedProcessing(ILogger logger, string exe, string series);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection from {CallingAe} calling us {CalledAe}")]
    private static partial void LogConnection(ILogger logger, string callingAe, string calledAe);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Requested abstract syntax {AbstractSyntax} from {CallingAe} not supported")]
    private static partial void LogUnsupportedSyntax(ILogger logger, DicomUID abstractSyntax, string callingAe);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed saving {Uid}")]
    private static partial void LogSaveError(ILogger logger, Exception exception, string uid);

    [LoggerMessage(Level = LogLevel.Error, Message = "C-STORE request exception: {Error}")]
    private static partial void LogStoreRequestError(ILogger logger, string error);

    public StoreScp(INetworkStream stream, Encoding enc, ILogger log, DicomServiceDependencies deps) : base(stream, enc, log, deps)
    {
        _workdir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_workdir);
    }

    /// <inheritdoc />
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        LogAbort(Logger, source, reason);
    }

    /// <inheritdoc />
    public void OnConnectionClosed(Exception exception)
    {
        LogConnectionClosed(Logger, exception, _seriesList);
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
            LogFinishedProcessing(Logger, Program.Exe, series);
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

        Directory.Delete(_workdir, true);
    }

    /// <inheritdoc />
    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        _senderAe = association.CallingAE;
        _senderIp = association.RemoteHost;
        _ourAe = association.CalledAE;
        LogConnection(Logger, association.CallingAE, association.CalledAE);
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
                LogUnsupportedSyntax(Logger, pc.AbstractSyntax, association.CallingAE);
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
#pragma warning disable CS1998
    public async Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
#pragma warning restore CS1998
    {
        return new DicomCEchoResponse(request, DicomStatus.Success);
    }

    /// <inheritdoc />
    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            _seriesList.Add(request.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
            var inst = request.SOPInstanceUID.UID;
            await request.File.SaveAsync(Path.Combine(_workdir, $"{inst}.dcm"));
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception e)
        {
            LogSaveError(Logger, e, request.SOPInstanceUID.UID);
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    /// <inheritdoc />
#pragma warning disable CS1998
    public async Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
#pragma warning restore CS1998
    {
        LogStoreRequestError(Logger, e.ToString());
    }
}
