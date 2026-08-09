/**
 * Mic capture.
 *
 * MediaRecorder output differs by browser: Chrome/Firefox emit WebM/Opus,
 * Safari emits MP4/AAC. Both are accepted upstream, so rather than hardcoding
 * "audio/webm" we ask the browser what it can actually produce and carry that
 * MIME type through to the upload — hardcoding is the usual cause of Safari
 * recordings arriving mislabelled.
 */
window.BoloRecorder = (function () {
  const PREFERRED_TYPES = [
    'audio/webm;codecs=opus',
    'audio/webm',
    'audio/ogg;codecs=opus',
    'audio/mp4',
    'audio/aac',
  ];

  function pickMimeType() {
    if (typeof MediaRecorder === 'undefined') return null;

    for (const type of PREFERRED_TYPES) {
      if (MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(type)) {
        return type;
      }
    }
    // Empty string lets the browser choose its own default.
    return '';
  }

  function extensionFor(mimeType) {
    if (!mimeType) return 'webm';
    if (mimeType.includes('webm')) return 'webm';
    if (mimeType.includes('ogg')) return 'ogg';
    if (mimeType.includes('mp4')) return 'mp4';
    if (mimeType.includes('aac')) return 'aac';
    return 'webm';
  }

  function isSupported() {
    return !!(
      navigator.mediaDevices &&
      navigator.mediaDevices.getUserMedia &&
      typeof MediaRecorder !== 'undefined'
    );
  }

  /**
   * Records until stop() is called or maxMs elapses.
   * Resolves with { blob, mimeType, fileName, durationMs }.
   */
  async function start({ maxMs = 15000, onStop } = {}) {
    if (!isSupported()) {
      throw new Error('unsupported');
    }

    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: false, // preserve the signal the confidence layer reads
          autoGainControl: true,
        },
      });
    } catch (err) {
      const denied =
        err && (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError');
      throw new Error(denied ? 'permission-denied' : 'mic-unavailable');
    }

    const mimeType = pickMimeType();
    let recorder;
    try {
      recorder = mimeType
        ? new MediaRecorder(stream, { mimeType })
        : new MediaRecorder(stream);
    } catch {
      recorder = new MediaRecorder(stream);
    }

    const chunks = [];
    const startedAt = Date.now();
    let timeoutId = null;

    const finished = new Promise((resolve, reject) => {
      recorder.ondataavailable = (e) => {
        if (e.data && e.data.size > 0) chunks.push(e.data);
      };

      recorder.onerror = () => {
        cleanup();
        reject(new Error('record-failed'));
      };

      recorder.onstop = () => {
        cleanup();

        // Use the recorder's actual type, which may differ from what we asked for.
        const actualType = recorder.mimeType || mimeType || 'audio/webm';
        const blob = new Blob(chunks, { type: actualType });

        resolve({
          blob,
          mimeType: actualType,
          fileName: `command.${extensionFor(actualType)}`,
          durationMs: Date.now() - startedAt,
        });
      };
    });

    function cleanup() {
      if (timeoutId) clearTimeout(timeoutId);
      stream.getTracks().forEach((t) => t.stop());
      if (typeof onStop === 'function') onStop();
    }

    recorder.start();
    timeoutId = setTimeout(() => {
      if (recorder.state === 'recording') recorder.stop();
    }, maxMs);

    return {
      stop() {
        if (recorder.state === 'recording') recorder.stop();
      },
      finished,
    };
  }

  return { isSupported, start };
})();
