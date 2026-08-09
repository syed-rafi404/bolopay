/**
 * Demo state machine.
 *
 * States: idle -> recording -> processing -> (confirm | receipt | error states)
 * All money is mock and lives here in the browser; there is no backend ledger.
 */
function boloApp(config) {
  return {
    // --- state ------------------------------------------------------------
    state: 'idle',
    balance: config.startingBalance,
    contacts: config.contacts,
    account: config.account,
    samples: config.samples,

    micSupported: false,
    micError: null,
    errorMessage: null,

    result: null,
    receipt: null,

    // Editable fields, pre-filled with the ASR's best guess.
    editedAmount: '',
    editedRecipient: '',

    recorder: null,
    recordingSeconds: 0,
    recordingTimer: null,
    activeSample: null,

    // --- lifecycle --------------------------------------------------------
    init() {
      this.micSupported = window.BoloRecorder.isSupported();
    },

    get isBusy() {
      return this.state === 'processing';
    },

    get formattedBalance() {
      return this.formatAmount(this.balance);
    },

    formatAmount(value) {
      const n = Number(value);
      if (!isFinite(n)) return '0';
      return n.toLocaleString('en-IN', { maximumFractionDigits: 0 });
    },

    // --- recording --------------------------------------------------------
    async toggleRecording() {
      if (this.state === 'recording') {
        this.stopRecording();
        return;
      }
      if (this.isBusy) return;

      this.reset();

      try {
        this.recorder = await window.BoloRecorder.start({ maxMs: 15000 });
        this.state = 'recording';
        this.recordingSeconds = 0;
        this.recordingTimer = setInterval(() => {
          this.recordingSeconds += 1;
        }, 1000);

        const { blob, mimeType, fileName } = await this.recorder.finished;
        this.clearTimer();

        if (!blob || blob.size < 1200) {
          this.state = 'idle';
          this.errorMessage = "That was too short to hear. Hold the button while you speak.";
          return;
        }

        await this.send(blob, fileName, mimeType);
      } catch (err) {
        this.clearTimer();
        this.state = 'idle';

        if (err.message === 'permission-denied') {
          this.micError =
            "Mic access was blocked. You can still try a sample command below.";
        } else if (err.message === 'unsupported') {
          this.micError =
            "This browser can't record audio. Try a sample command below.";
        } else {
          this.micError = "Couldn't access the mic. Try a sample command below.";
        }
      }
    },

    stopRecording() {
      if (this.recorder) this.recorder.stop();
      this.clearTimer();
    },

    clearTimer() {
      if (this.recordingTimer) {
        clearInterval(this.recordingTimer);
        this.recordingTimer = null;
      }
    },

    // --- sample clips -----------------------------------------------------
    async playSample(sample) {
      if (this.isBusy) return;

      this.reset();
      this.activeSample = sample.file;

      try {
        const response = await fetch(sample.url);
        if (!response.ok) throw new Error('missing');

        const blob = await response.blob();
        await this.send(blob, sample.file, blob.type || 'audio/wav');
      } catch {
        this.state = 'idle';
        this.errorMessage =
          "That sample clip hasn't been recorded yet. Try the mic instead.";
      } finally {
        this.activeSample = null;
      }
    },

    // --- pipeline ---------------------------------------------------------
    async send(blob, fileName, mimeType) {
      this.state = 'processing';

      const form = new FormData();
      form.append('audio', blob, fileName);

      try {
        const response = await fetch('/api/process-voice-command', {
          method: 'POST',
          body: form,
        });

        const data = await response.json().catch(() => null);

        if (!response.ok) {
          this.state = 'idle';
          this.errorMessage =
            (data && data.message) || 'Something went wrong. Please try again.';
          return;
        }

        this.result = data;
        this.editedAmount = data.amountBdt != null ? String(data.amountBdt) : '';
        this.editedRecipient = data.recipientName || '';

        switch (data.status) {
          case 'confirm':
            this.state = 'confirm';
            break;
          case 'balance':
            this.state = 'balance';
            break;
          case 'unknown_recipient':
            this.state = 'unknownRecipient';
            break;
          case 'over_balance':
            this.state = 'overBalance';
            break;
          case 'no_speech':
            this.state = 'noSpeech';
            break;
          default:
            this.state = 'unrecognized';
        }
      } catch {
        this.state = 'idle';
        this.errorMessage = 'Network problem. Please try again.';
      }
    },

    // --- confirmation -----------------------------------------------------
    get parsedAmount() {
      const n = Number(this.editedAmount);
      return isFinite(n) ? Math.round(n) : NaN;
    },

    get amountValid() {
      const n = this.parsedAmount;
      return !isNaN(n) && n > 0 && n <= this.balance;
    },

    get amountError() {
      const n = this.parsedAmount;
      if (isNaN(n) || n <= 0) return 'Enter a valid amount.';
      if (n > this.balance) return `That's more than your balance of ৳${this.formattedBalance}.`;
      return null;
    },

    confirm() {
      if (!this.amountValid) return;

      const amount = this.parsedAmount;
      this.balance -= amount;

      this.receipt = {
        id: this.newTransactionId(),
        amount: amount,
        recipient: this.editedRecipient || this.result.recipientName,
        phone: this.result.recipientPhone,
        timestamp: new Date(),
      };

      this.state = 'receipt';
    },

    newTransactionId() {
      const stamp = Date.now().toString(36).toUpperCase();
      const rand = Math.random().toString(36).slice(2, 6).toUpperCase();
      return `BP${stamp}${rand}`;
    },

    formatTimestamp(date) {
      return date.toLocaleString('en-GB', {
        day: '2-digit',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    },

    // --- reset ------------------------------------------------------------
    reset() {
      this.state = 'idle';
      this.result = null;
      this.receipt = null;
      this.errorMessage = null;
      this.micError = null;
      this.editedAmount = '';
      this.editedRecipient = '';
      this.recordingSeconds = 0;
      this.clearTimer();
    },
  };
}
