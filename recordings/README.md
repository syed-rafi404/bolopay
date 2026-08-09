# Sample audio clips

Drop recorded Bangla clips here, then copy them to
`src/BoloPay.Web/wwwroot/sample-audio/`.

Format: **16 kHz, mono, 16-bit WAV**. Whisper downsamples to exactly this, so
supplying it directly avoids a conversion step. 2-5 seconds each, with roughly
half a second of silence at both ends.

| File | Say |
|---|---|
| `01-clean-adiba-500.wav` | আদিবার নাম্বারে পাঁচশো টাকা পাঠাও |
| `02-clean-tanvir-900.wav` | তানভিরকে নয়শো টাকা পাঠাও |
| `03a-mumble-mild.wav` | Same as 01, slur only পাঁচশো |
| `03b-mumble-heavy.wav` | Same as 01, badly slurred and trailing off |
| `03c-noisy.wav` | Same as 01, spoken clearly, background noise |
| `03d-stutter.wav` | পাঁ... পাঁচশো... ইয়ে... পাঁচশো টাকা পাঠাও |
| `04-balance.wav` | আমার ব্যালেন্স কত? |
| `05-nonsense.wav` | আজকে আবহাওয়া খুব সুন্দর |
| `06-unknown-recipient.wav` | রাকিবকে তিনশো টাকা পাঠাও *(optional)* |
| `07-over-balance.wav` | আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও *(optional)* |

## Rules that matter for calibration

- **Same device, room, and distance** for 01 and every 03 variant. If the clean
  and degraded clips differ in mic or room, the thresholds end up measuring the
  room rather than the speech.
- **No noise reduction, normalisation, or "enhance".** Those filters exist to
  remove exactly the signal being measured.
- Natural speaking volume. Do not over-enunciate the clean takes — an
  artificially crisp baseline exaggerates the gap.
- Multiple takes are welcome; more data points make calibration easier.

If you deviate from a script, note what was actually said in `notes.txt` — the
ground truth is needed to judge whether the ASR got it right.
