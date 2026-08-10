using BoloPay.Web.Models;

namespace BoloPay.Web.Services;

public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes a short spoken command.
    /// </summary>
    /// <param name="model">
    /// Which ASR model to use. Both passes normally use the same model.
    /// </param>
    /// <param name="temperature">
    /// Decoding temperature. The pipeline runs one greedy pass (0) and one
    /// sampled pass (non-zero); disagreement between them indicates ambiguous
    /// audio rather than a difference in model capability.
    /// </param>
    Task<TranscriptionPass> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        string model,
        float temperature = 0f,
        CancellationToken cancellationToken = default);
}
