using BoloPay.Web.Models;

namespace BoloPay.Web.Services;

public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes a short spoken command. <paramref name="model"/> lets the
    /// caller run the same audio through a second model for cross-checking.
    /// </summary>
    Task<TranscriptionPass> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        string model,
        CancellationToken cancellationToken = default);
}
