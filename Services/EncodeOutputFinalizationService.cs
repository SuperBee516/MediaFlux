using MediaFlux.Models;

namespace MediaFlux.Services
{
    public interface IEncodeOutputPromoter
    {
        void Promote(string stagingPath, string finalOutputPath);
        string TryRestoreToStaging(string finalOutputPath, string stagingPath);
    }

    public sealed class FileEncodeOutputPromoter : IEncodeOutputPromoter
    {
        public void Promote(string stagingPath, string finalOutputPath)
        {
            if (!File.Exists(stagingPath))
                throw new FileNotFoundException("The validated staged output no longer exists.", stagingPath);
            if (File.Exists(finalOutputPath))
            {
                throw new IOException(
                    "The intended final output was created by another process. " +
                    "MediaFlux will not overwrite it.");
            }

            File.Move(stagingPath, finalOutputPath, overwrite: false);
        }

        public string TryRestoreToStaging(
            string finalOutputPath,
            string stagingPath)
        {
            try
            {
                if (File.Exists(stagingPath))
                    return stagingPath;
                if (!File.Exists(finalOutputPath))
                    return "";

                File.Move(finalOutputPath, stagingPath, overwrite: false);
                return stagingPath;
            }
            catch
            {
                return File.Exists(finalOutputPath) ? finalOutputPath : "";
            }
        }
    }

    public interface IEncodeOutputFinalizationService
    {
        Task<EncodeFinalizationResult> FinalizeAsync(
            EncodeOutputValidationRequest request,
            Action<string>? statusCallback = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class EncodeOutputFinalizationService :
        IEncodeOutputFinalizationService
    {
        private readonly IEncodeOutputValidationService _validationService;
        private readonly IEncodeOutputPromoter _promoter;

        public EncodeOutputFinalizationService(
            IEncodeOutputValidationService validationService,
            IEncodeOutputPromoter? promoter = null)
        {
            _validationService = validationService ??
                throw new ArgumentNullException(nameof(validationService));
            _promoter = promoter ?? new FileEncodeOutputPromoter();
        }

        public async Task<EncodeFinalizationResult> FinalizeAsync(
            EncodeOutputValidationRequest request,
            Action<string>? statusCallback = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            statusCallback?.Invoke("Verifying output");
            EncodeOutputValidationResult staged =
                await _validationService.ValidateStagedAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
            if (!staged.Success || staged.Evidence == null)
            {
                return Failed(
                    EncodeFinalizationFailureKind.Validation,
                    $"Output validation failed: {staged.ErrorMessage}",
                    request,
                    File.Exists(request.OutputPath) ? request.OutputPath : "");
            }

            cancellationToken.ThrowIfCancellationRequested();
            statusCallback?.Invoke("Finalizing");
            try
            {
                _promoter.Promote(request.OutputPath, request.FinalOutputPath);
            }
            catch (Exception ex)
            {
                return Failed(
                    EncodeFinalizationFailureKind.Promotion,
                    $"Output promotion failed: {ex.Message}",
                    request,
                    File.Exists(request.OutputPath) ? request.OutputPath : "");
            }

            statusCallback?.Invoke("Verifying final output");
            EncodeOutputValidationResult promoted;
            try
            {
                promoted = await _validationService.ValidatePromotedAsync(
                    request,
                    staged.Evidence,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string recoverablePath = _promoter.TryRestoreToStaging(
                    request.FinalOutputPath,
                    request.OutputPath);
                throw new EncodeFinalizationCanceledException(
                    Failed(
                        EncodeFinalizationFailureKind.FinalVerification,
                        "Final output verification was canceled.",
                        request,
                        recoverablePath),
                    cancellationToken);
            }
            if (!promoted.Success || promoted.Evidence == null)
            {
                string recoverablePath = _promoter.TryRestoreToStaging(
                    request.FinalOutputPath,
                    request.OutputPath);
                return Failed(
                    EncodeFinalizationFailureKind.FinalVerification,
                    $"Final output verification failed: {promoted.ErrorMessage}",
                    request,
                    recoverablePath);
            }

            return new EncodeFinalizationResult
            {
                Success = true,
                FinalOutputPath = request.FinalOutputPath,
                StagingPath = request.OutputPath,
                ValidationSummary =
                    $"{staged.Summary} {promoted.Summary}".Trim(),
                FinalOutputSizeBytes = promoted.Evidence.OutputSizeBytes,
                FinalOutputLastWriteUtcTicks =
                    promoted.Evidence.OutputLastWriteUtcTicks
            };
        }

        private static EncodeFinalizationResult Failed(
            EncodeFinalizationFailureKind kind,
            string message,
            EncodeOutputValidationRequest request,
            string recoverablePath) => new()
        {
            Success = false,
            FailureKind = kind,
            ErrorMessage = message,
            FinalOutputPath = request.FinalOutputPath,
            StagingPath = request.OutputPath,
            RecoverableOutputPath = recoverablePath
        };
    }

    public sealed class EncodeFinalizationException : InvalidOperationException
    {
        public EncodeFinalizationException(EncodeFinalizationResult result)
            : base(BuildMessage(result))
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public EncodeFinalizationResult Result { get; }

        private static string BuildMessage(EncodeFinalizationResult result)
        {
            string message = result.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(result.RecoverableOutputPath))
            {
                message +=
                    $" Recoverable staged media was retained at " +
                    $"'{result.RecoverableOutputPath}'.";
            }

            return message + " The original source was retained.";
        }
    }

    public sealed class EncodeFinalizationCanceledException :
        OperationCanceledException
    {
        public EncodeFinalizationCanceledException(
            EncodeFinalizationResult result,
            CancellationToken cancellationToken)
            : base(
                "Output finalization was canceled. The original source was retained.",
                cancellationToken)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public EncodeFinalizationResult Result { get; }
    }
}
