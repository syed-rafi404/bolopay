# BoloPay — voice-first Bangla transaction safety layer

A prototype that takes a spoken Bangla money-transfer command, transcribes it, and
**refuses to proceed silently when it isn't confident about what it heard**. When the
amount or recipient is uncertain, that field becomes an editable input pre-filled with
the model's best guess, and the confirm button changes to make the check unmissable.

**This is a prototype, not a bank.** No real money, accounts, or payment rails. Not
affiliated with, endorsed by, or connected to Banglalink, Mukto Pay, or any bank.

## Why it exists

Voice is a more natural input than a banking UI for first-time digital-finance users.
But ASR makes mistakes, and in a financial context a misheard digit sends the wrong
amount to the wrong person. Most voice interfaces treat transcription as ground truth.
This one treats it as a claim that may need checking.

The underlying idea — using a transcription model's own quality signals to decide when
to distrust its output — is repointed here from transcript-quality flagging to
transaction safety.

## How the safety net works

Two independent signals. Either one is enough to flag.

**1. Segment-level quality metrics.** Whisper's `verbose_json` returns `avg_logprob`,
`no_speech_prob`, and `compression_ratio` per segment. Outside configured bounds, the
utterance is flagged.

The honest limitation: these are only available per *segment*, and a 2–4 second command
usually comes back as a single segment. So the realistic behaviour is "flag the whole
utterance", not "highlight the misheard digit". In a financial context, over-flagging
when uncertain is the defensible choice.

A second limitation, which is why signal 2 exists: `avg_logprob` is averaged across
every token in the segment, so one slurred digit barely moves it. Bangla also sits well
below Whisper's average WER, so a threshold picked on English intuition risks separating
"this is Bangla" from "this is English" rather than "clear" from "mumbled".

**2. Cross-pass agreement.** The same audio goes through two different Whisper models.
Both transcripts are parsed, and if the extracted **amount** or **recipient** disagree,
the utterance is flagged. This targets exactly the two fields where being wrong costs
money, and it works regardless of Bangla's absolute logprob range.

Verified against live Groq: on an unintelligible input, `whisper-large-v3` returned a
4-character transcript while `whisper-large-v3-turbo` hallucinated 103 characters from
the same audio — while signal 1 reported a healthy `avg_logprob` of `-0.31`, well inside
the `-0.5` floor. Signal 1 alone would have passed that through. This is the case the
disagreement signal exists to catch.

## Pipeline

```
Browser (MediaRecorder or sample clip)
  ↓ audio blob
POST /api/process-voice-command
  ├─ 1. Groq Whisper, pass A (whisper-large-v3)      → transcript + segments
  ├─ 2. Groq Whisper, pass B (whisper-large-v3-turbo) → second transcript
  ├─ 3. Groq gpt-oss-120b, strict json_schema        → { intent, amount, recipient }
  │      (pass B is only re-extracted when its transcript differs)
  ├─ 4. Confidence scoring: metrics + cross-pass agreement
  ├─ 5. Fuzzy contact match against mock contacts
  └─ → ProcessResult
Browser
  ├─ Confirm screen (plain, or editable + amber when flagged)
  └─ On confirm: mock balance update in browser state, receipt
```

## Stack

- ASP.NET Core 10, Razor Pages + one minimal API endpoint
- Tailwind CSS v4 via the standalone CLI (no Node required)
- Alpine.js, vendored locally
- Groq for both ASR and extraction
- `FuzzySharp` for contact matching
- Built-in `Microsoft.AspNetCore.RateLimiting` — no Redis, no external service
- No database. No auth. Balance lives in browser state and resets on refresh.

## Running it

```bash
dotnet run --project src/BoloPay.Web
```

Then open the HTTPS URL printed in the console.

**It runs with no API key.** Without one, stub implementations serve canned Bangla
transcripts with fabricated segment metadata, so the full UI — including the flagged
state — is clickable offline. Those numbers are invented and must never be used to
calibrate thresholds.

### With a real Groq key

Free tier is sufficient: `whisper-large-v3` allows 20 req/min and 2,000 req/day,
`gpt-oss-120b` allows 1,000 req/day. One interaction costs 2 ASR calls and 1–2 LLM
calls, so the practical ceiling is roughly 500 interactions/day.

**The binding limit is tokens per minute, not requests per day.** Measured against
the live API: `gpt-oss-120b` on the free tier allows 8,000 TPM, and the extraction
system prompt alone is ~1,900 tokens. That caps sustained throughput at roughly
four extractions per minute regardless of the daily quota. Interactive demo use
never approaches it, but batch work does — the integration tests pace themselves
for this reason, which is why they take ~2.5 minutes.

```bash
cd src/BoloPay.Web
dotnet user-secrets init
dotnet user-secrets set "Groq:ApiKey" "gsk_your_key_here"
```

Never commit the key. `appsettings.json` ships with an empty `ApiKey` on purpose.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Groq:ApiKey` | *(empty)* | Empty ⇒ stub mode |
| `Groq:TranscriptionModel` | `whisper-large-v3` | 10.3% WER; `-turbo` is 12% but same free-tier limits |
| `Groq:CrossCheckModel` | `whisper-large-v3-turbo` | Second pass for the agreement signal |
| `Groq:ExtractionModel` | `openai/gpt-oss-120b` | Only gpt-oss models support `strict: true` on Groq |
| `Groq:EnableCrossCheck` | `true` | Set `false` to halve quota usage |
| `Confidence:AvgLogprobFloor` | `-0.5` | **Uncalibrated.** See below. |
| `Confidence:NoSpeechProbCeiling` | `0.4` | **Uncalibrated.** |
| `Confidence:CompressionRatioMax` | `2.2` | **Uncalibrated.** |
| `Confidence:CompressionRatioMin` | `1.0` | **Uncalibrated.** |
| `Confidence:ContactMatchThreshold` | `70` | Fuzzy score below which a name is "not a contact" |
| `Confidence:ContactMatchConfidentThreshold` | `85` | Fuzzy scores between 70 and 85 are matched, but flagged as uncertain — "probably Adiba" isn't presented as fact |
| `Confidence:ExposeDiagnostics` | `true` in Development | Shows measured values in the response |

## Threshold calibration — required before trusting signal 1

The four threshold defaults are **starting points derived from Groq's documented
"healthy" example, not measurements.** They have never been checked against real Bangla
audio. Until calibrated, signal 1 should not be trusted; signal 2 works regardless.

1. Record clips into `recordings/` (see below) and copy them to
   `src/BoloPay.Web/wwwroot/sample-audio/`.
2. Set a real `Groq:ApiKey` and run in Development.
3. Play each clip. Every segment's actual `avg_logprob`, `no_speech_prob`, and
   `compression_ratio` is logged at Information level and shown in the
   "Confidence diagnostics" panel.
4. Compare the clean clip against the mumbled one and move the thresholds in
   `appsettings.json` to where they actually separate.

If clean and mumbled turn out indistinguishable, that is a real finding, not a bug —
it is precisely why signal 2 was built.

## Sample clips

Drop into `src/BoloPay.Web/wwwroot/sample-audio/`. The picker degrades gracefully when
a file is absent, so the app runs fine before they exist.

| File | Content |
|---|---|
| `01-clean-adiba-500.wav` | আদিবার নাম্বারে পাঁচশো টাকা পাঠাও |
| `02-clean-tanvir-900.wav` | তানভিরকে নয়শো টাকা পাঠাও |
| `03b-mumble-heavy.wav` | Same as 01, number deliberately slurred |
| `04-balance.wav` | আমার ব্যালেন্স কত? |
| `05-nonsense.wav` | আজকে আবহাওয়া খুব সুন্দর |
| `07-over-balance.wav` | আম্মাকে পঞ্চাশ হাজার টাকা পাঠাও *(blocked: exceeds balance)* |

Record at 16 kHz mono WAV — Whisper downsamples to exactly that. Keep the mic, room,
and distance identical between the clean and mumbled takes, and disable any noise
reduction or normalisation: those filters remove the signal being measured.

## Deployment

**Live: https://bolopay.onrender.com**

Check `/healthz` for `{"status":"ok","transcription":"groq"}` — a `"stub"` value
means the Groq key did not reach the container.

Containerised, so it runs anywhere that takes a Dockerfile. `render.yaml` targets
Render's free tier: no credit card, Docker-native, TLS and a subdomain included.

```bash
docker build -t bolopay .
docker run -p 8080:8080 -e Groq__ApiKey=gsk_... bolopay
```

`tools/deploy-azure.sh` runs the same image on Azure Container Apps. It is kept as a
second target rather than the canonical link: `.github/workflows/build-image.yml`
publishes to GHCR because ACR Tasks are not permitted on Student subscriptions and
Cloud Shell has no Docker daemon.

On Render: connect the repo, and the blueprint is picked up automatically. Set
`Groq__ApiKey` in the dashboard — `render.yaml` marks it `sync: false` so it is never
committed. Note the double underscore: that is how .NET configuration nesting maps to
environment variables (`Groq__ApiKey` → `Groq:ApiKey`).

**Cold starts.** Render spins a free instance down after 15 minutes idle, and spinning
back up takes about a minute. For a link on a CV that is a real problem — a recruiter
opening it cold waits on a loading page. Point a free uptime pinger at `/healthz` every
10 minutes to keep it warm: 24/7 for a 31-day month is 744 instance-hours, just inside
the 750-hour free allowance. `/healthz` is deliberately exempt from rate limiting and
makes no external calls, so keeping the instance alive costs no Groq quota.

**Behind the proxy.** The host terminates TLS and forwards plain HTTP, so
`UseForwardedHeaders` runs in Production only. Without it `UseHttpsRedirection`
redirect-loops, and the rate limiter partitions every visitor under the proxy's single
IP — turning a per-visitor limit into a global one. Client IP is read from
`RemoteIpAddress` rather than the raw `X-Forwarded-For` header, since a caller-supplied
header would otherwise let anyone pick their own rate-limit bucket.

**Tailwind in the container.** `wwwroot/css/app.css` is committed and the image uses it
as-is; the MSBuild target is Windows-only and skips itself. If you change
`Styles/app.css`, rebuild locally and commit the generated CSS, or the deployed styles
will be stale.

## Abuse protection

The endpoint is public and unauthenticated by design, and it calls a metered API. It is
protected by a fixed-window limiter of 20 requests/IP/hour, a 2 MB upload cap, a content-type
allowlist, and a 15-second client-side recording cap. The point is not cost — that is
negligible — but preventing a bot from exhausting the daily free quota and leaving the
demo broken for a real visitor.

## Testing

```bash
dotnet test tests/BoloPay.Tests        # unit tests: 46 (confidence scoring, contact matching)
powershell -File tools/scenario-test.ps1  # end-to-end: every pipeline branch, stub transcriber
powershell -File tools/guard-test.ps1     # input guards: 400/413/415 status codes
```

The unit tests pin the parts with real logic: confidence flags, message wording
(field-specific only when uncertainty is actually localisable), suffix-stripped
contact matching, and the weak-match band. The HTTP scripts exercise the deployed
behaviour end to end against the stub transcriber.

## Notes

- `MediaRecorder` output differs by browser: Chrome emits WebM/Opus, Safari MP4/AAC. The
  client reads the recorder's actual MIME type and carries it through rather than
  hardcoding `.webm` — the usual cause of Safari uploads arriving mislabelled.
- Bangla webfont (Hind Siliguri) is loaded explicitly; Windows and older Android don't
  reliably ship a Bangla font.
- No TTS readback: `speechSynthesis` support for `bn-BD` is inconsistent, and the input
  is already voice.

## Not implemented, deliberately

No payment rails, accounts, KYC, login, model training, native app, persistence, or
full dialect coverage.
